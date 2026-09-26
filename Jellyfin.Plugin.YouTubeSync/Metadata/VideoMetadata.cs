using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeSync.Metadata;

/// <summary>Metadata used to write a synced video's files and NFO content.</summary>
public sealed class VideoMetadata
{
    /// <summary>Gets or sets the YouTube video identifier.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the video title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the video description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the thumbnail URL.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the uploader or channel name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets a sync-unique identifier used for local item identity.</summary>
    public string SyncId { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating playlist identifier when the source syncs channel playlists.</summary>
    public string PlaylistId { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating playlist title when the source syncs channel playlists.</summary>
    public string PlaylistTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the artwork URL detected for the originating playlist when available.</summary>
    public string PlaylistThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the poster-style artwork URL detected for the originating playlist when available.</summary>
    public string PlaylistPosterUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the synthetic season number assigned to the playlist.</summary>
    public int? PlaylistSeasonNumber { get; set; }

    /// <summary>Gets or sets the position of the video inside its originating playlist.</summary>
    public int? PlaylistEpisodeNumber { get; set; }

    /// <summary>Gets or sets the published date in UTC when available.</summary>
    public DateTime? PublishedUtc { get; set; }

    /// <summary>Gets or sets the runtime in seconds when available.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the subtitle tracks YouTube offers for this video. Only set when the details were
    /// fetched from yt-dlp during this sync (caption URLs expire, so they are never cached); <c>null</c> otherwise.
    /// </summary>
    public IReadOnlyList<SubtitleTrack>? SubtitleTracks { get; set; }
}

/// <summary>One downloadable subtitle track for a video.</summary>
/// <param name="Language">The YouTube language code, e.g. <c>en</c>, <c>en-GB</c> or <c>en-orig</c>.</param>
/// <param name="Url">The caption file URL (short-lived).</param>
/// <param name="Format">The file format, <c>srt</c> or <c>vtt</c>.</param>
/// <param name="IsAutomatic">Whether YouTube generated the track automatically.</param>
public sealed record SubtitleTrack(string Language, string Url, string Format, bool IsAutomatic);