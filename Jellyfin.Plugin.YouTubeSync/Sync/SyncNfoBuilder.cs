using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.YouTubeSync.Configuration;
using Jellyfin.Plugin.YouTubeSync.Metadata;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

internal static class SyncNfoBuilder
{
    public static string BuildTvShowNfo(
        SourceDefinition source,
        string name,
        string description,
        string folderFileName,
        string posterFileName,
        string bannerFileName,
        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions)
    {
        var posterThumb = string.IsNullOrEmpty(posterFileName)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(posterFileName)}</thumb>";
        var folderThumb = string.IsNullOrEmpty(folderFileName)
            ? string.Empty
            : $"\n  <thumb aspect=\"folder\">{Xml(folderFileName)}</thumb>";
        var bannerThumb = string.IsNullOrEmpty(bannerFileName)
            ? string.Empty
            : $"\n  <thumb aspect=\"banner\">{Xml(bannerFileName)}</thumb>";
        var namedSeasons = playlistSeasonDefinitions.Count == 0
            ? string.Empty
            : string.Concat(
                playlistSeasonDefinitions.Select(definition =>
                    $"\n  <namedseason number=\"{definition.SeasonNumber}\">{Xml(definition.Title)}</namedseason>"));

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <tvshow>
          <title>{Xml(name)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(source.Id)}</uniqueid>{posterThumb}{folderThumb}{bannerThumb}{namedSeasons}
        </tvshow>
        """;
    }

    public static string BuildCollectionNfo(
        SourceDefinition source,
        string name,
        string description,
        string folderFileName,
        string posterFileName)
    {
        var posterThumb = string.IsNullOrEmpty(posterFileName)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(posterFileName)}</thumb>";
        var folderThumb = string.IsNullOrEmpty(folderFileName)
            ? string.Empty
            : $"\n  <thumb aspect=\"folder\">{Xml(folderFileName)}</thumb>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <movie>
          <title>{Xml(name)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(source.Id)}</uniqueid>{posterThumb}{folderThumb}
        </movie>
        """;
    }

    /// <summary>Enhanced playback scales video to at most this width, keeping the aspect ratio.</summary>
    internal const int EnhancedMaxWidth = 1920;

    /// <summary>
    /// Builds the runtime tags: whole minutes in <c>runtime</c>, plus a Kodi-standard <c>fileinfo/streamdetails</c>
    /// block with the exact duration and the stream Enhanced playback produces (H.264 at the video's size, capped
    /// at 1080p width, and AAC stereo). Jellyfin never probes <c>.strm</c> files and ignores NFO runtimes for
    /// videos, so <see cref="RuntimePostScanTask"/> applies these after each library scan.
    /// </summary>
    private static string BuildRuntimeTags(VideoMetadata video)
    {
        var durationSeconds = video.DurationSeconds;
        if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return string.Empty;
        }

        var seconds = durationSeconds.Value;
        var minutes = Math.Max(1, (int)Math.Round(seconds / 60d, MidpointRounding.AwayFromZero));
        var size = string.Empty;
        if (GetEnhancedOutputSize(video.Width, video.Height) is var (width, height))
        {
            size = $"<width>{width}</width><height>{height}</height>";
        }

        return $"\n  <runtime>{minutes}</runtime>"
            + "\n  <fileinfo><streamdetails>"
            + $"<video><codec>h264</codec>{size}<durationinseconds>{seconds}</durationinseconds></video>"
            + "<audio><codec>aac</codec><channels>2</channels></audio>"
            + "</streamdetails></fileinfo>";
    }

    /// <summary>
    /// Returns the size Enhanced playback outputs for a source size: unchanged up to <see cref="EnhancedMaxWidth"/>
    /// wide, otherwise scaled down to that width with an even height.
    /// </summary>
    internal static (int Width, int Height)? GetEnhancedOutputSize(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0)
        {
            return null;
        }

        if (width.Value <= EnhancedMaxWidth)
        {
            return (width.Value, height.Value);
        }

        var scaledHeight = (int)Math.Round(height.Value * (double)EnhancedMaxWidth / width.Value / 2d) * 2;
        return (EnhancedMaxWidth, Math.Max(2, scaledHeight));
    }

    public static string BuildEpisodeNfo(
        VideoMetadata video,
        string sourceName,
        int? seasonNumber,
        int? episodeNumber,
        string thumbFileName)
    {
        var aired = BuildDateTag("aired", video.PublishedUtc);
        var premiered = BuildDateTag("premiered", video.PublishedUtc);
        var thumb = string.IsNullOrEmpty(thumbFileName)
            ? string.Empty
            : $"\n  <thumb>{Xml(thumbFileName)}</thumb>";
        var season = seasonNumber.HasValue ? $"\n  <season>{seasonNumber.Value}</season>" : string.Empty;
        var episode = episodeNumber.HasValue ? $"\n  <episode>{episodeNumber.Value}</episode>" : string.Empty;
        var runtime = BuildRuntimeTags(video);
        var studio = string.IsNullOrWhiteSpace(video.ChannelName) ? string.Empty : $"\n  <studio>{Xml(video.ChannelName)}</studio>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <episodedetails>
          <title>{Xml(video.Title)}</title>
          <showtitle>{Xml(sourceName)}</showtitle>
          <plot>{Xml(video.Description)}</plot>
                    <uniqueid type="youtube" default="true">{Xml(video.SyncId)}</uniqueid>{aired}{premiered}{season}{episode}{runtime}{studio}{thumb}
        </episodedetails>
        """;
    }

    public static string BuildMovieVideoNfo(VideoMetadata video, string sourceName, string thumbFileName)
    {
        var premiered = BuildDateTag("premiered", video.PublishedUtc);
        var thumb = string.IsNullOrEmpty(thumbFileName)
            ? string.Empty
            : $"\n  <thumb>{Xml(thumbFileName)}</thumb>";
        var runtime = BuildRuntimeTags(video);
        var studio = string.IsNullOrWhiteSpace(video.ChannelName) ? string.Empty : $"\n  <studio>{Xml(video.ChannelName)}</studio>";
        var set = string.IsNullOrWhiteSpace(sourceName) ? string.Empty : $"\n  <set>{Xml(sourceName)}</set>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <movie>
          <title>{Xml(video.Title)}</title>
          <plot>{Xml(video.Description)}</plot>
                    <uniqueid type="youtube" default="true">{Xml(video.SyncId)}</uniqueid>{premiered}{runtime}{studio}{set}{thumb}
        </movie>
        """;
    }

    public static string BuildSeasonNfo(
        string sourceName,
        string seasonTitle,
        int? seasonNumber,
        string posterFileName,
        string folderFileName)
    {
        var season = seasonNumber.HasValue ? $"\n  <seasonnumber>{seasonNumber.Value}</seasonnumber>" : string.Empty;
        var showTitle = string.IsNullOrWhiteSpace(sourceName) ? string.Empty : $"\n  <showtitle>{Xml(sourceName)}</showtitle>";
        var posterThumb = string.IsNullOrEmpty(posterFileName)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(posterFileName)}</thumb>";
        var folderThumb = string.IsNullOrEmpty(folderFileName)
            ? string.Empty
            : $"\n  <thumb aspect=\"folder\">{Xml(folderFileName)}</thumb>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <season>
          <title>{Xml(seasonTitle)}</title>{showTitle}{season}{posterThumb}{folderThumb}
        </season>
        """;
    }

    private static string BuildDateTag(string tagName, DateTime? date)
    {
        return date is DateTime value ? $"\n  <{tagName}>{value:yyyy-MM-dd}</{tagName}>" : string.Empty;
    }

    private static string Xml(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}