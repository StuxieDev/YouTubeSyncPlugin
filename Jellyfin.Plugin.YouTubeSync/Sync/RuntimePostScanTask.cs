using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

/// <summary>
/// Copies each synced video's duration and stream details from its NFO onto the Jellyfin item after every
/// library scan.
/// <para>
/// Jellyfin never probes remote <c>.strm</c> streams and ignores NFO runtimes for videos, so without this a
/// synced video has no length and no video or audio stream. Clients then show no length, and apps that
/// check media streams before scheduling (for example pseudo-TV apps such as NostalgiaTV) treat the videos as
/// unplayable. The streams added describe what Enhanced playback produces: H.264 at the video's size (at most
/// 1920 wide) and AAC stereo. Items Jellyfin has really probed are left alone. This task only reads local
/// NFO files; it makes no YouTube requests.
/// </para>
/// </summary>
public class RuntimePostScanTask : ILibraryPostScanTask
{
    private const int DefaultWidth = 1920;
    private const int DefaultHeight = 1080;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly ILogger<RuntimePostScanTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="RuntimePostScanTask"/> class.</summary>
    public RuntimePostScanTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaStreamRepository mediaStreamRepository,
        ILogger<RuntimePostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaStreamRepository = mediaStreamRepository;
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
            _logger.LogInformation("YouTubeSync folder {Path} is not a Jellyfin library folder; skipping video details.", fullBase);
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

        var runtimesSet = 0;
        var streamsAdded = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            var changed = false;

            var info = ReadNfoInfo(Path.ChangeExtension(item.Path, ".nfo"));
            if (info.DurationSeconds is > 0)
            {
                var ticks = TimeSpan.FromSeconds(info.DurationSeconds.Value).Ticks;
                if (item.RunTimeTicks != ticks)
                {
                    item.RunTimeTicks = ticks;
                    changed = true;
                    runtimesSet++;
                }
            }

            var width = info.Width ?? DefaultWidth;
            var height = info.Height ?? DefaultHeight;
            if (AddStreamsIfMissing(item, width, height, cancellationToken))
            {
                streamsAdded++;
                if (item is Video video)
                {
                    video.Width = width;
                    video.Height = height;
                }

                // Also bumps DateLastSaved, so apps that sync incrementally pick up the new details.
                changed = true;
            }

            if (changed)
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            progress.Report(100d * (i + 1) / items.Count);
        }

        if (runtimesSet > 0 || streamsAdded > 0)
        {
            _logger.LogInformation(
                "YouTubeSync updated {Total} synced videos: set {Runtimes} runtime(s) and added stream details to {Streams}.",
                items.Count,
                runtimesSet,
                streamsAdded);
        }
    }

    /// <summary>
    /// Adds an H.264 video stream and an AAC stereo audio stream when the item has no video stream, keeping any
    /// existing streams (such as external subtitles) and their indexes.
    /// </summary>
    private bool AddStreamsIfMissing(BaseItem item, int width, int height, CancellationToken cancellationToken)
    {
        var existing = _mediaSourceManager.GetMediaStreams(item.Id);
        if (existing.Any(s => s.Type == MediaStreamType.Video))
        {
            return false;
        }

        var next = existing.Count == 0 ? 0 : existing.Max(s => s.Index) + 1;
        var streams = existing.ToList();
        streams.Add(new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = next,
            Codec = "h264",
            Profile = "High",
            IsAVC = true,
            Width = width,
            Height = height,
            PixelFormat = "yuv420p",
            BitDepth = 8,
            IsInterlaced = false,
            IsDefault = true
        });
        streams.Add(new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = next + 1,
            Codec = "aac",
            Channels = 2,
            ChannelLayout = "stereo",
            SampleRate = 48000,
            BitRate = 192000,
            IsDefault = true
        });

        _mediaStreamRepository.SaveMediaStreams(item.Id, streams, cancellationToken);
        return true;
    }

    /// <summary>
    /// Reads the duration and output size from an NFO: exact <c>durationinseconds</c> when present, else
    /// whole-minute <c>runtime</c>; <c>width</c>/<c>height</c> from <c>fileinfo/streamdetails/video</c>.
    /// </summary>
    internal static (int? DurationSeconds, int? Width, int? Height) ReadNfoInfo(string nfoPath)
    {
        if (!File.Exists(nfoPath))
        {
            return (null, null, null);
        }

        int? minutes = null;
        int? seconds = null;
        int? width = null;
        int? height = null;
        try
        {
            using var reader = XmlReader.Create(nfoPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                // ReadElementContentAsString already advances past the element, so only call Read otherwise.
                switch (reader.Name)
                {
                    case "durationinseconds":
                        seconds = ParsePositive(reader.ReadElementContentAsString()) ?? seconds;
                        break;
                    case "runtime" when reader.Depth == 1:
                        minutes = ParsePositive(reader.ReadElementContentAsString()) ?? minutes;
                        break;
                    case "width" when reader.Depth > 1:
                        width = ParsePositive(reader.ReadElementContentAsString()) ?? width;
                        break;
                    case "height" when reader.Depth > 1:
                        height = ParsePositive(reader.ReadElementContentAsString()) ?? height;
                        break;
                    default:
                        reader.Read();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return (null, null, null);
        }

        return (seconds ?? minutes * 60, width, height);
    }

    private static int? ParsePositive(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }
}
