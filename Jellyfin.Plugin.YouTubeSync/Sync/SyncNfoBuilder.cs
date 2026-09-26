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

    /// <summary>
    /// Builds the runtime tags: whole minutes in <c>runtime</c>, plus exact seconds in the Kodi-standard
    /// <c>fileinfo/streamdetails</c> block. Jellyfin ignores NFO runtimes for videos, so
    /// <see cref="RuntimePostScanTask"/> applies the seconds after each library scan.
    /// </summary>
    private static string BuildRuntimeTags(int? durationSeconds)
    {
        if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return string.Empty;
        }

        var seconds = durationSeconds.Value;
        var minutes = Math.Max(1, (int)Math.Round(seconds / 60d, MidpointRounding.AwayFromZero));
        return $"\n  <runtime>{minutes}</runtime>"
            + $"\n  <fileinfo><streamdetails><video><durationinseconds>{seconds}</durationinseconds></video></streamdetails></fileinfo>";
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
        var runtime = BuildRuntimeTags(video.DurationSeconds);
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
        var runtime = BuildRuntimeTags(video.DurationSeconds);
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