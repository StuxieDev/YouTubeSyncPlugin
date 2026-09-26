using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

/// <summary>
/// Copies video durations from the synced NFO files onto the Jellyfin items after each library scan.
/// <para>
/// Jellyfin ignores NFO runtimes for video items and never probes remote .strm streams, so without
/// this every synced video has no runtime. Clients then show no length, and schedulers such as
/// pseudo-TV apps can't place the videos. This task only reads local NFO files; it makes no YouTube requests.
/// </para>
/// </summary>
public class RuntimePostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RuntimePostScanTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="RuntimePostScanTask"/> class.</summary>
    public RuntimePostScanTask(ILibraryManager libraryManager, ILogger<RuntimePostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var basePath = Plugin.Instance?.Configuration.LibraryBasePath;
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
        {
            return;
        }

        // Normalise so the lookup matches the path Jellyfin stored (separators, trailing slash).
        var fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
        var root = _libraryManager.FindByPath(fullBase, isFolder: true);
        if (root is null)
        {
            _logger.LogInformation("YouTubeSync folder {Path} is not a Jellyfin library folder; skipping runtimes.", fullBase);
            return;
        }

        var prefix = fullBase + Path.DirectorySeparatorChar;
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
            AncestorIds = [root.Id],
            IsVirtualItem = false,
            Recursive = true
        })
            .Where(item => item.Path is not null
                && item.Path.StartsWith(prefix, StringComparison.Ordinal)
                && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var updated = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];

            var seconds = ReadDurationSeconds(Path.ChangeExtension(item.Path, ".nfo"));
            if (seconds is > 0)
            {
                var ticks = TimeSpan.FromSeconds(seconds.Value).Ticks;
                if (item.RunTimeTicks != ticks)
                {
                    item.RunTimeTicks = ticks;
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    updated++;
                }
            }

            progress.Report(100d * (i + 1) / items.Count);
        }

        if (updated > 0)
        {
            _logger.LogInformation("YouTubeSync set runtimes on {Updated} of {Total} synced videos.", updated, items.Count);
        }
    }

    /// <summary>
    /// Reads the duration from an NFO: exact <c>durationinseconds</c> when present, else whole-minute <c>runtime</c>.
    /// </summary>
    internal static int? ReadDurationSeconds(string nfoPath)
    {
        if (!File.Exists(nfoPath))
        {
            return null;
        }

        int? minutes = null;
        try
        {
            using var reader = XmlReader.Create(nfoPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                // ReadElementContentAsString already advances past the element, so only call Read otherwise.
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "durationinseconds")
                {
                    if (int.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                        && seconds > 0)
                    {
                        return seconds;
                    }
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Name == "runtime" && reader.Depth == 1)
                {
                    if (int.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var runtime)
                        && runtime > 0)
                    {
                        minutes = runtime;
                    }
                }
                else
                {
                    reader.Read();
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return null;
        }

        return minutes * 60;
    }
}
