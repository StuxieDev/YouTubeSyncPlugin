using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeSync.Playback;

/// <summary>
/// Represents the yt-dlp-resolved input URLs used for managed transcoding.
/// A managed session may use either a combined input URL or separate video and audio URLs.
/// The HTTP headers are the ones yt-dlp negotiated for each format; googlevideo URLs issued
/// to some YouTube clients return 403 unless ffmpeg sends the same headers.
/// </summary>
public sealed record ManagedPlaybackInput(
    string VideoUrl,
    string? AudioUrl,
    IReadOnlyDictionary<string, string>? VideoHeaders = null,
    IReadOnlyDictionary<string, string>? AudioHeaders = null)
{
    /// <summary>Gets a value indicating whether the input uses separate audio and video URLs.</summary>
    public bool HasSeparateAudio => !string.IsNullOrWhiteSpace(AudioUrl);
}