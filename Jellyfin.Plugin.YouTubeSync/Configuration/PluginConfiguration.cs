using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTubeSync.Configuration;

/// <summary>Named playback targets exposed in the plugin configuration UI.</summary>
public static class PlaybackTargets
{
    /// <summary>Prefer broadly compatible 720p progressive MP4 when available.</summary>
    public const string BroadCompatibility720p = "BroadCompatibility720p";

    /// <summary>Prefer up to 1080p MP4 and allow manifest playback when needed.</summary>
    public const string Balanced1080p = "Balanced1080p";

    /// <summary>Let yt-dlp choose the highest-quality combined stream it can expose.</summary>
    public const string MaximumQuality = "MaximumQuality";
}

/// <summary>Named hardware acceleration modes for the managed transcoding pipeline.</summary>
public static class ManagedTranscodeHardwareModes
{
    /// <summary>Use software encoding.</summary>
    public const string None = "None";

    /// <summary>Use Intel Quick Sync if available.</summary>
    public const string Qsv = "Qsv";

    /// <summary>Use NVIDIA NVENC if available.</summary>
    public const string Nvenc = "Nvenc";

    /// <summary>Use VAAPI if available.</summary>
    public const string Vaapi = "Vaapi";

    /// <summary>Use AMD AMF if available.</summary>
    public const string Amf = "Amf";
}

/// <summary>Holds all user-configurable settings for the YouTubeSync plugin.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the list of YouTube sources (channels/playlists) to sync.</summary>
    public List<SourceDefinition> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets the path to the yt-dlp executable.
    /// Defaults to "yt-dlp" (expects it to be on PATH).
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Gets or sets the base directory where .strm/.nfo files are written.
    /// This directory must be inside a Jellyfin library.
    /// </summary>
    public string LibraryBasePath { get; set; } = "/media/youtube";

    /// <summary>
    /// Gets or sets the externally accessible base URL of this Jellyfin instance.
    /// Used to build resolver URLs written into .strm files.
    /// For remote clients this must be the public URL (e.g. https://jellyfin.example.com).
    /// </summary>
    public string JellyfinBaseUrl { get; set; } = "http://localhost:8096";

    /// <summary>
    /// Gets or sets how many minutes a resolved CDN URL stays in the in-memory cache.
    /// </summary>
    public int CacheMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets the playback target used when asking yt-dlp for a playback URL.
    /// </summary>
    public string PlaybackTarget { get; set; } = PlaybackTargets.BroadCompatibility720p;

    /// <summary>
    /// Gets or sets how many days of videos to keep per source.
    /// Set to 0 to keep all available videos.
    /// </summary>
    public int VideoRetentionDays { get; set; } = 300;

    /// <summary>
    /// Gets or sets how many of the most recent playlists are kept for channel sources using the playlist feed.
    /// Set to 0 to keep all discovered playlists.
    /// </summary>
    public int RecentPlaylistsToKeep { get; set; } = 20;

    /// <summary>
    /// Gets or sets the legacy maximum number of videos to sync per source.
    /// Retained for backward compatibility with existing saved plugin configuration.
    /// </summary>
    public int MaxVideosPerSource { get; set; } = 200;

    /// <summary>
    /// Gets or sets a value indicating whether managed ffmpeg transcoding is enabled.
    /// When disabled, the plugin always uses the lightweight redirect-only path.
    /// </summary>
    public bool AllowManagedTranscoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether playback first uses YouTube's own HLS playlists (complete and
    /// seekable, no local transcoding). Enhanced and Simple modes are the fallback for videos without HLS formats.
    /// </summary>
    public bool UseYouTubeHls { get; set; } = true;

    /// <summary>
    /// Gets or sets the audio language to play, e.g. <c>en</c> or <c>de</c>. YouTube can offer several audio
    /// tracks per video (the original and dubs, its AI "dubbed-auto" ones included); this one is played when
    /// it's there, otherwise the video's original audio. A code also matches its regional variants
    /// (<c>en</c> matches <c>en-US</c>). Blank: always the original audio.
    /// </summary>
    public string AudioLanguage { get; set; } = "en";

    /// <summary>
    /// Gets or sets the tallest YouTube HLS video to play: 1080 (default), 720 or 480. Jellyfin usually
    /// transcodes these streams for TV-style clients, and 720p is less than half the work of 1080p - the
    /// difference between keeping up and buffering on a small GPU, or with several streams at once.
    /// </summary>
    public int HlsMaxHeight { get; set; } = 1080;

    /// <summary>
    /// Gets or sets the path to the ffmpeg executable used for managed transcoding.
    /// Defaults to "ffmpeg" and expects it to be available on PATH.
    /// </summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Gets or sets the managed transcoding hardware acceleration mode.
    /// </summary>
    public string ManagedTranscodeHardwareMode { get; set; } = ManagedTranscodeHardwareModes.None;

    /// <summary>
    /// Gets or sets how many minutes an idle managed transcoding session is kept alive.
    /// </summary>
    public int ManagedTranscodeSessionIdleMinutes { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of concurrent managed transcoding sessions.
    /// </summary>
    public int MaxConcurrentManagedTranscodes { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether YouTube subtitles are saved next to each synced video
    /// as <c>&lt;video&gt;.&lt;language&gt;.srt</c>, where Jellyfin picks them up as external subtitles.
    /// </summary>
    public bool DownloadSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets the comma-separated subtitle languages to download, e.g. <c>en</c> or <c>en,de</c>.
    /// A language also matches its regional variants (<c>en</c> matches <c>en-GB</c>).
    /// </summary>
    public string SubtitleLanguages { get; set; } = "en";

    /// <summary>
    /// Gets or sets a value indicating whether YouTube's automatically generated captions are used
    /// for a language when the video has no uploaded subtitles in it.
    /// </summary>
    public bool IncludeAutoGeneratedSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional Netscape-format cookies file passed to yt-dlp with <c>--cookies</c>.
    /// A signed-in YouTube session makes rate limiting ("confirm you're not a bot") much rarer and allows
    /// age-restricted videos. The Jellyfin service user must be able to read the file.
    /// </summary>
    public string CookiesFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets extra arguments added to every yt-dlp call, e.g.
    /// <c>--plugin-dirs /path/to/yt-dlp-plugins</c> to load a PO-token provider plugin.
    /// Separate arguments with spaces; wrap an argument containing spaces in double quotes.
    /// </summary>
    public string ExtraYtDlpArguments { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the comma-separated device IDs of clients whose selected subtitles Jellyfin should burn into
    /// the video. Jellyfin 12 delivers subtitles separately unless a client asks otherwise, and players that only
    /// play the video (such as the NostalgiaTV pseudo-TV app, device ID <c>nostalgiatv-docker</c>) then show none.
    /// Applies to all of the server's videos, not just synced ones. Empty turns it off.
    /// </summary>
    public string BurnInSubtitleDeviceIds { get; set; } = "nostalgiatv-docker";

    /// <summary>
    /// Gets or sets the pause, in seconds, after each per-video lookup during a sync. Spacing requests out
    /// stops large first syncs from getting the server's IP rate-limited by YouTube.
    /// </summary>
    public double SyncRequestDelaySeconds { get; set; } = 2;
}
