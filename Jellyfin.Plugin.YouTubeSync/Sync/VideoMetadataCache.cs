using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Metadata;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

/// <summary>
/// Per-source cache of the per-video details yt-dlp returns, so a sync only calls yt-dlp for new videos.
/// <para>
/// Fetching one video's details takes around 1.5 seconds, which made every sync of a large channel take
/// hours. Entries are refetched when the video is very new (creators often retitle new uploads), when a
/// date or duration is missing, or once they age past a per-video refresh window of 30–60 days.
/// </para>
/// The file is a dot-file in the source folder, which Jellyfin's scanner ignores.
/// </summary>
internal sealed class VideoMetadataCache
{
    internal const string FileName = ".youtubesync-metadata.json";

    private const int SaveEveryNewEntries = 100;
    private static readonly TimeSpan RecentUploadWindow = TimeSpan.FromDays(3);
    private static readonly TimeSpan MinimumRefreshAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CachedVideo> _entries;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private int _unsavedChanges;

    private VideoMetadataCache(string path, ILogger logger, ConcurrentDictionary<string, CachedVideo> entries)
    {
        _path = path;
        _logger = logger;
        _entries = entries;
    }

    /// <summary>Gets the number of cached videos.</summary>
    public int Count => _entries.Count;

    /// <summary>Loads the cache for a source folder, starting empty if the file is missing or unreadable.</summary>
    public static async Task<VideoMetadataCache> LoadAsync(string sourceDir, ILogger logger, CancellationToken cancellationToken)
    {
        var path = Path.Combine(sourceDir, FileName);
        var entries = new ConcurrentDictionary<string, CachedVideo>(StringComparer.Ordinal);

        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var loaded = await JsonSerializer.DeserializeAsync<List<CachedVideo>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                foreach (var entry in loaded ?? new List<CachedVideo>())
                {
                    if (!string.IsNullOrWhiteSpace(entry.VideoId))
                    {
                        entries[entry.VideoId] = entry;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                logger.LogWarning(ex, "Ignoring unreadable YouTubeSync metadata cache {Path}; it will be rebuilt.", path);
                entries.Clear();
            }
        }

        return new VideoMetadataCache(path, logger, entries);
    }

    /// <summary>
    /// Returns a copy of the cached details for a video when they are still fresh enough to reuse.
    /// </summary>
    /// <param name="videoId">The YouTube video ID.</param>
    /// <param name="utcNow">The current time.</param>
    /// <param name="requireSubtitleCheck">
    /// When subtitles are enabled, a video whose subtitles were never checked is treated as a miss, because
    /// caption URLs only come with a fresh yt-dlp fetch.
    /// </param>
    public VideoMetadata? TryGetFresh(string videoId, DateTime utcNow, bool requireSubtitleCheck = false)
    {
        if (!_entries.TryGetValue(videoId, out var cached)
            || cached.PublishedUtc is null
            || cached.DurationSeconds is null or <= 0
            || (requireSubtitleCheck && cached.SubtitlesCheckedUtc is null))
        {
            return null;
        }

        if (utcNow - cached.PublishedUtc.Value < RecentUploadWindow)
        {
            return null;
        }

        if (utcNow - cached.FetchedUtc > GetRefreshAge(videoId))
        {
            return null;
        }

        return new VideoMetadata
        {
            VideoId = cached.VideoId,
            Title = cached.Title,
            Description = cached.Description,
            ThumbnailUrl = cached.ThumbnailUrl,
            ChannelName = cached.ChannelName,
            PublishedUtc = cached.PublishedUtc,
            DurationSeconds = cached.DurationSeconds
        };
    }

    /// <summary>
    /// Returns cached details regardless of age. Used while YouTube is rate-limiting, when stale details
    /// are better than dropping a video that is still on the channel.
    /// </summary>
    public VideoMetadata? TryGetAny(string videoId)
    {
        return _entries.TryGetValue(videoId, out var cached) && cached.PublishedUtc is not null
            ? new VideoMetadata
            {
                VideoId = cached.VideoId,
                Title = cached.Title,
                Description = cached.Description,
                ThumbnailUrl = cached.ThumbnailUrl,
                ChannelName = cached.ChannelName,
                PublishedUtc = cached.PublishedUtc,
                DurationSeconds = cached.DurationSeconds
            }
            : null;
    }

    /// <summary>Stores freshly fetched details, saving the cache periodically so interrupted syncs keep progress.</summary>
    public async Task StoreAsync(VideoMetadata metadata, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadata.VideoId))
        {
            return;
        }

        _entries.TryGetValue(metadata.VideoId, out var previous);
        _entries[metadata.VideoId] = new CachedVideo
        {
            VideoId = metadata.VideoId,
            Title = metadata.Title,
            Description = metadata.Description,
            ThumbnailUrl = metadata.ThumbnailUrl,
            ChannelName = metadata.ChannelName,
            PublishedUtc = metadata.PublishedUtc,
            DurationSeconds = metadata.DurationSeconds,
            FetchedUtc = utcNow,
            SubtitlesCheckedUtc = previous?.SubtitlesCheckedUtc
        };

        if (Interlocked.Increment(ref _unsavedChanges) % SaveEveryNewEntries == 0)
        {
            await SaveAsync(null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records that a video's subtitles were fetched (or confirmed unavailable).</summary>
    public void MarkSubtitlesChecked(string videoId, DateTime utcNow)
    {
        if (_entries.TryGetValue(videoId, out var cached))
        {
            cached.SubtitlesCheckedUtc = utcNow;
        }
    }

    /// <summary>
    /// Writes the cache to disk. When <paramref name="keepVideoIds"/> is given, entries for videos that are no
    /// longer in the source are dropped first.
    /// </summary>
    public async Task SaveAsync(IReadOnlyCollection<string>? keepVideoIds, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (keepVideoIds is not null)
            {
                var keep = keepVideoIds as ISet<string> ?? new HashSet<string>(keepVideoIds, StringComparer.Ordinal);
                foreach (var videoId in _entries.Keys.Where(id => !keep.Contains(id)).ToList())
                {
                    _entries.TryRemove(videoId, out _);
                }
            }

            var snapshot = _entries.Values.OrderBy(v => v.VideoId, StringComparer.Ordinal).ToList();
            var tempPath = _path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save YouTubeSync metadata cache {Path}", _path);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Spreads refreshes over 30–60 days per video (stable per ID) so a whole channel doesn't expire in the same sync.
    /// </summary>
    private static TimeSpan GetRefreshAge(string videoId)
    {
        var hash = 0u;
        foreach (var c in videoId)
        {
            hash = (hash * 31) + c;
        }

        return MinimumRefreshAge + TimeSpan.FromHours(hash % (30 * 24));
    }

    internal sealed class CachedVideo
    {
        public string VideoId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string ThumbnailUrl { get; set; } = string.Empty;

        public string ChannelName { get; set; } = string.Empty;

        public DateTime? PublishedUtc { get; set; }

        public int? DurationSeconds { get; set; }

        public DateTime FetchedUtc { get; set; }

        public DateTime? SubtitlesCheckedUtc { get; set; }
    }
}
