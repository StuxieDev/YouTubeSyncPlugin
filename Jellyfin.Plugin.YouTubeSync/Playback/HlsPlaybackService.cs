using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Playback;

/// <summary>
/// Resolves and caches YouTube's HLS playlists per video. Pseudo-TV apps and seeking make Jellyfin restart
/// its transcode often, and each restart re-opens the resolve URL, so caching avoids a yt-dlp call (and a
/// YouTube request) every time.
/// </summary>
public sealed partial class HlsPlaybackService
{
    private static readonly TimeSpan MaxCacheLifetime = TimeSpan.FromHours(3);
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (HlsPlaybackInput? Input, DateTime ExpiresUtc)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<HlsPlaybackService> _logger;

    /// <summary>Initializes a new instance of the <see cref="HlsPlaybackService"/> class.</summary>
    public HlsPlaybackService(YtDlpService ytDlpService, ILogger<HlsPlaybackService> logger)
    {
        _ytDlpService = ytDlpService;
        _logger = logger;
    }

    /// <summary>
    /// Returns YouTube's HLS playlists for a video, from the cache when still valid. Returns <c>null</c> when the
    /// video has no usable HLS formats (that answer is cached briefly too).
    /// </summary>
    public async Task<HlsPlaybackInput?> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        if (TryGetCached(videoId, out var cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(videoId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(videoId, out cached))
            {
                return cached;
            }

            var input = await _ytDlpService.GetHlsPlaybackInputAsync(videoId, cancellationToken).ConfigureAwait(false);
            var expires = input is null
                ? DateTime.UtcNow.AddMinutes(10)
                : GetExpiry(input.VideoUrl, input.AudioUrl);
            _cache[videoId] = (input, expires);
            PruneExpired();
            return input;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetCached(string videoId, out HlsPlaybackInput? input)
    {
        if (_cache.TryGetValue(videoId, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            input = entry.Input;
            return true;
        }

        input = null;
        return false;
    }

    /// <summary>Caches until shortly before the earliest <c>expire</c> in YouTube's URLs, at most three hours.</summary>
    private static DateTime GetExpiry(params string?[] urls)
    {
        var expires = DateTime.UtcNow + MaxCacheLifetime;
        foreach (var url in urls.Where(u => !string.IsNullOrEmpty(u)))
        {
            var match = ExpireRegex().Match(url!);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                var urlExpiry = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime - ExpirySafetyMargin;
                if (urlExpiry < expires)
                {
                    expires = urlExpiry;
                }
            }
        }

        return expires;
    }

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _cache.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToList())
        {
            _cache.TryRemove(key, out _);
            _locks.TryRemove(key, out _);
        }
    }

    [GeneratedRegex(@"[/?&]expire[/=](\d+)")]
    private static partial Regex ExpireRegex();
}
