using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Metadata;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

/// <summary>
/// Saves YouTube subtitles next to a synced video as <c>&lt;video&gt;.&lt;language&gt;.&lt;ext&gt;</c>,
/// the naming Jellyfin uses to find external subtitles (it also scans next to <c>.strm</c> files).
/// </summary>
internal static class SyncSubtitleHelper
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>Parses the configured comma-separated language list into normalised codes.</summary>
    public static IReadOnlyList<string> ParseLanguages(string? configured)
    {
        return (configured ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Picks at most one track per configured language: uploaded subtitles first (an exact code, then a
    /// regional variant such as <c>en-GB</c> for <c>en</c>), then, if allowed, YouTube's auto-generated
    /// captions (the original-language track <c>xx-orig</c> before a machine translation).
    /// </summary>
    public static IReadOnlyList<(string Language, SubtitleTrack Track)> SelectTracks(
        IReadOnlyList<SubtitleTrack> tracks,
        IReadOnlyList<string> languages,
        bool includeAutomatic)
    {
        var selected = new List<(string, SubtitleTrack)>();
        foreach (var language in languages)
        {
            var track = FindTrack(tracks, language, automatic: false);
            if (track is null && includeAutomatic)
            {
                track = tracks.FirstOrDefault(t => t.IsAutomatic && t.Language.Equals(language + "-orig", StringComparison.OrdinalIgnoreCase))
                    ?? FindTrack(tracks, language, automatic: true);
            }

            if (track is not null)
            {
                selected.Add((language, track));
            }
        }

        return selected;
    }

    /// <summary>
    /// Downloads the selected tracks for a video. Returns <c>false</c> if any download failed, so the
    /// caller can retry on a later sync instead of recording the video as checked.
    /// </summary>
    public static async Task<bool> SaveAsync(
        ILogger logger,
        string videoDir,
        string baseName,
        IReadOnlyList<(string Language, SubtitleTrack Track)> selected,
        CancellationToken cancellationToken)
    {
        var allSucceeded = true;
        foreach (var (language, track) in selected)
        {
            var targetPath = Path.Combine(videoDir, $"{baseName}.{language}.{track.Format}");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DownloadTimeout);
                var content = await HttpClient.GetStringAsync(track.Url, timeout.Token).ConfigureAwait(false);

                if (!LooksLikeSubtitles(content, track.Format))
                {
                    logger.LogWarning("YouTube returned no usable {Language} subtitles for {Path}; skipping.", language, targetPath);
                    continue;
                }

                // Drop a file in the other format from an earlier sync so Jellyfin doesn't list the language twice.
                var otherFormat = track.Format == "srt" ? "vtt" : "srt";
                var otherPath = Path.Combine(videoDir, $"{baseName}.{language}.{otherFormat}");
                if (File.Exists(otherPath))
                {
                    File.Delete(otherPath);
                }

                if (!File.Exists(targetPath) || !string.Equals(await File.ReadAllTextAsync(targetPath, cancellationToken).ConfigureAwait(false), content, StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(targetPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning("Failed to download {Language} subtitles for {Path}: {Message}", language, targetPath, ex.Message);
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    private static SubtitleTrack? FindTrack(IReadOnlyList<SubtitleTrack> tracks, string language, bool automatic)
    {
        return tracks.FirstOrDefault(t => t.IsAutomatic == automatic && t.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? tracks.FirstOrDefault(t => t.IsAutomatic == automatic
                && t.Language.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)
                && !t.Language.EndsWith("-orig", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeSubtitles(string content, string format)
    {
        return format == "vtt"
            ? content.TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal) && content.Contains("-->", StringComparison.Ordinal)
            : content.Contains("-->", StringComparison.Ordinal);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        return client;
    }
}
