namespace Jellyfin.Plugin.YouTubeSync.Playback;

/// <summary>
/// YouTube's own HLS media playlists for one video: an H.264 video playlist and, when YouTube serves audio
/// separately, an audio playlist. These are complete (VOD) playlists, so players and ffmpeg can seek straight
/// to any position.
/// </summary>
/// <param name="VideoUrl">The video media playlist URL (short-lived).</param>
/// <param name="VideoCodecs">The RFC 6381 video codec string, e.g. <c>avc1.64002A</c>.</param>
/// <param name="VideoBandwidth">The video bitrate in bits per second.</param>
/// <param name="Width">The video width.</param>
/// <param name="Height">The video height.</param>
/// <param name="AudioUrl">The audio media playlist URL, or <c>null</c> when the video playlist carries audio.</param>
/// <param name="AudioCodecs">The audio codec string, e.g. <c>mp4a.40.2</c>.</param>
/// <param name="AudioBandwidth">The audio bitrate in bits per second.</param>
public sealed record HlsPlaybackInput(
    string VideoUrl,
    string VideoCodecs,
    long VideoBandwidth,
    int Width,
    int Height,
    string? AudioUrl,
    string AudioCodecs,
    long AudioBandwidth)
{
    /// <summary>
    /// Builds an HLS master playlist with the video variant listed <em>before</em> the audio rendition. ffmpeg
    /// numbers streams in the order it meets playlists, and Jellyfin maps input streams 0 and 1 as video and
    /// audio, so this order is what makes Jellyfin pick the right streams.
    /// </summary>
    public string BuildMasterPlaylist()
    {
        var bandwidth = System.Math.Max(1, VideoBandwidth + AudioBandwidth);
        var codecs = string.IsNullOrEmpty(AudioCodecs) ? VideoCodecs : $"{VideoCodecs},{AudioCodecs}";
        var audioAttribute = AudioUrl is null ? string.Empty : ",AUDIO=\"audio\"";
        var master = "#EXTM3U\n#EXT-X-INDEPENDENT-SEGMENTS\n"
            + $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={Width}x{Height},CODECS=\"{codecs}\"{audioAttribute}\n"
            + VideoUrl + "\n";
        if (AudioUrl is not null)
        {
            master += $"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Audio\",DEFAULT=YES,AUTOSELECT=YES,URI=\"{AudioUrl}\"\n";
        }

        return master;
    }
}
