using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.YouTubeSync.Configuration;
using Jellyfin.Plugin.YouTubeSync.Metadata;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

internal static class SyncSeasonLayout
{
    public static Dictionary<int, Dictionary<string, int>> BuildSeasonEpisodeCounters(
        IReadOnlyList<VideoMetadata> videos,
        SourceDefinition source)
    {
        var counters = new Dictionary<int, Dictionary<string, int>>();
        if (source.Mode == SourceMode.Movies)
        {
            return counters;
        }

        foreach (var seasonGroup in videos
                     .GroupBy(video => GetSeasonNumber(video, source) ?? 0)
                     .OrderBy(group => group.Key))
        {
            var seasonEpisodes = new Dictionary<string, int>(StringComparer.Ordinal);
            var ordered = seasonGroup
                .OrderBy(video => video.PlaylistEpisodeNumber ?? int.MaxValue)
                .ThenBy(video => video.PublishedUtc ?? DateTime.MinValue)
                .ThenBy(video => video.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                seasonEpisodes[ordered[index].SyncId] = index + 1;
            }

            counters[seasonGroup.Key] = seasonEpisodes;
        }

        return counters;
    }

    public static int? GetEpisodeNumber(
        VideoMetadata video,
        IReadOnlyDictionary<int, Dictionary<string, int>> seasonEpisodeCounters,
        SourceDefinition source)
    {
        if (source.Mode == SourceMode.Movies)
        {
            return null;
        }

        var seasonNumber = GetSeasonNumber(video, source) ?? 0;
        return seasonEpisodeCounters.TryGetValue(seasonNumber, out var seasonMap)
            && seasonMap.TryGetValue(video.SyncId, out var episode)
            ? episode
            : null;
    }

    public static int? GetSeasonNumber(VideoMetadata video, SourceDefinition source)
    {
        if (source.Mode == SourceMode.Movies)
        {
            return null;
        }

        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            return video.PlaylistSeasonNumber;
        }

        return video.PublishedUtc?.Year;
    }

    public static string GetSeasonFolderName(VideoMetadata video, SourceDefinition source)
    {
        if (source.Mode == SourceMode.Movies)
        {
            return string.Empty;
        }

        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            if (video.PlaylistSeasonNumber is not int playlistSeasonNumber)
            {
                return string.Empty;
            }

            return $"Season {playlistSeasonNumber:00}";
        }

        return video.PublishedUtc is DateTime date ? $"Season {date.Year}" : string.Empty;
    }

    public static string BuildVideoFolderName(string title, string videoId)
    {
        var safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? videoId : title);
        return $"{safeTitle} [{videoId}]";
    }

    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sb.Replace(c, '_');
        }

        return sb.ToString();
    }
}