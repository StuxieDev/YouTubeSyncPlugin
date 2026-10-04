using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Configuration;
using Jellyfin.Plugin.YouTubeSync.Metadata;
using Jellyfin.Plugin.YouTubeSync.Playback;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Services;

/// <summary>
/// Wraps yt-dlp invocations. All methods run yt-dlp out-of-process and parse its JSON output.
/// </summary>
public class YtDlpService
{
    private const string BroadCompatibility720pSelector = "b[protocol!*=m3u8][ext=mp4][height=720]/b[protocol!*=m3u8][ext=mp4][height<=720]/b[height=720]/b[height<=720]";
    private const string Balanced1080pSelector = "b[height=1080]/b[height=720]/b[height<=1080]/b[height<=720]/b";
    private const string MaximumQualitySelector = "b";
    private const string ManagedPlaybackInputSelector = "bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=1080]+ba/b[height<=1080]/b";

    private readonly ILogger<YtDlpService> _logger;

    /// <summary>How long background syncing pauses after YouTube starts rate-limiting this server.</summary>
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(30);

    private DateTime _rateLimitedUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Gets a value indicating whether YouTube recently rate-limited this server. Syncs stop making new
    /// requests while this is set so playback, which uses the same IP, can recover.
    /// </summary>
    public bool IsRateLimited => DateTime.UtcNow < _rateLimitedUntilUtc;

    /// <summary>Initializes a new instance of the <see cref="YtDlpService"/> class.</summary>
    public YtDlpService(ILogger<YtDlpService> logger)
    {
        _logger = logger;
    }

    private string YtDlpPath => Plugin.Instance?.Configuration.YtDlpPath ?? "yt-dlp";

    /// <summary>The audio language setting, as a plain language code (letters and hyphens) or empty.</summary>
    private static string AudioLanguage => NormalizeLanguage(Plugin.Instance?.Configuration.AudioLanguage ?? "en");

    internal static string NormalizeLanguage(string? language)
    {
        var code = (language ?? string.Empty).Trim().ToLowerInvariant();
        return code.Length is >= 2 and <= 12 && code.All(c => c is (>= 'a' and <= 'z') or '-') ? code : string.Empty;
    }

    // a language matches itself and its regional variants: "en" matches "en" and "en-US"
    private static bool IsLanguage(string formatLanguage, string language)
    {
        var lang = formatLanguage.ToLowerInvariant();
        return language.Length > 0 && (lang == language || lang.StartsWith(language + "-", StringComparison.Ordinal));
    }

    /// <summary>
    /// The managed-transcoding format selector: the configured audio language first (YouTube adds dubbed
    /// tracks, its AI "dubbed-auto" ones included), then yt-dlp's own choice, which prefers the original track.
    /// </summary>
    internal static string GetManagedPlaybackSelector(string language)
    {
        if (language.Length == 0)
        {
            return ManagedPlaybackInputSelector;
        }

        // [language^=xx] also covers its regional variants
        return $"bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a][language^={language}]/bv*[height<=1080]+ba[language^={language}]/"
            + ManagedPlaybackInputSelector;
    }

    /// <summary>
    /// Returns the final playback URL for a single video using yt-dlp's own format selection.
    /// This may be a direct media URL or an HLS manifest URL.
    /// </summary>
    public async Task<string?> GetPlaybackUrlAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var playbackTarget = GetPlaybackTarget();
        var playbackSelector = GetPlaybackFormatSelector(playbackTarget);
        var result = await RunYtDlpTextAsync(
            new[] { "-f", playbackSelector, "--get-url", "--no-playlist", url },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var lines = result
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            return null;
        }

        var manifestUrl = lines.FirstOrDefault(IsManifestUrl);
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            _logger.LogInformation(
                "Resolved playback URL for {VideoId}: manifest ({PlaybackTarget})",
                videoId,
                playbackTarget);
            return manifestUrl;
        }

        if (lines.Length > 1)
        {
            _logger.LogWarning(
                "yt-dlp returned multiple playback URLs for {VideoId} without a single manifest-style URL.",
                videoId);
            return null;
        }

        var playbackUrl = lines[0];
        _logger.LogInformation(
            "Resolved playback URL for {VideoId}: {PlaybackKind} ({PlaybackTarget})",
            videoId,
            DescribePlaybackUrl(playbackUrl),
            playbackTarget);
        return playbackUrl;
    }

    /// <summary>
    /// Returns the input URLs used by the managed transcoding pipeline.
    /// Prefers separate AVC video and AAC audio inputs up to 1080p.
    /// </summary>
    public async Task<ManagedPlaybackInput?> GetManagedPlaybackInputAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var node = await RunYtDlpJsonAsync(
            new[] { "-f", GetManagedPlaybackSelector(AudioLanguage), "-j", "--no-playlist", url },
            cancellationToken).ConfigureAwait(false);

        if (node is null)
        {
            return null;
        }

        // Merged selections (bv+ba) list each part under requested_formats; a single format sits at the top level.
        var formats = node["requested_formats"] is JsonArray requested && requested.Count > 0
            ? requested.OfType<JsonNode>().ToList()
            : new List<JsonNode> { node };

        var videoUrl = GetString(formats[0], "url");
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            return null;
        }

        if (formats.Count == 1)
        {
            _logger.LogInformation("Resolved managed playback input for {VideoId}: combined", videoId);
            return new ManagedPlaybackInput(videoUrl, null, GetHttpHeaders(formats[0]));
        }

        _logger.LogInformation("Resolved managed playback input for {VideoId}: separate video+audio", videoId);
        return new ManagedPlaybackInput(
            videoUrl,
            GetString(formats[1], "url"),
            GetHttpHeaders(formats[0]),
            GetHttpHeaders(formats[1]));
    }

    private static Dictionary<string, string> GetHttpHeaders(JsonNode format)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format["http_headers"] is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                var text = value?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    headers[key] = text;
                }
            }
        }

        return headers;
    }

    /// <summary>
    /// Finds YouTube's own HLS playlists for a video: the best H.264 video up to 1080p and the best audio.
    /// Returns <c>null</c> when YouTube offers no suitable HLS formats for the video.
    /// </summary>
    public async Task<HlsPlaybackInput?> GetHlsPlaybackInputAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";

        // With a cookies file, yt-dlp's default YouTube clients return no HLS formats at all, so every video
        // fell back to a managed transcode. The web_safari client still offers HLS when signed in.
        var node = await RunYtDlpJsonAsync(
                new[] { "-J", "--no-playlist", "--extractor-args", "youtube:player_client=default,web_safari", url },
                cancellationToken)
            .ConfigureAwait(false);
        if (node?["formats"] is not JsonArray formats)
        {
            return null;
        }

        var hls = formats.OfType<JsonObject>()
            .Where(f => GetString(f, "protocol").Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(GetString(f, "url")))
            .ToList();

        static bool HasCodec(string codec) => !string.IsNullOrEmpty(codec) && codec != "none";

        var video = PickHlsVideo(hls, HlsMaxHeight, AudioLanguage);
        if (video is null)
        {
            _logger.LogInformation("YouTube offers no H.264 HLS video for {VideoId}.", videoId);
            return null;
        }

        var videoHasAudio = HasCodec(GetString(video, "acodec"));

        // YouTube's HLS audio-only formats report vcodec "none" but often leave acodec unknown (null), so an
        // audio format is one with no video that isn't explicitly marked as having no audio.
        // A video can carry several audio tracks: the original plus dubs (YouTube's AI "dubbed-auto" ones
        // included), each in a low and a high variant, usually with no bitrate reported - see PickAudioTrack.
        var audio = videoHasAudio
            ? null
            : PickAudioTrack(hls.Where(f => GetString(f, "vcodec") == "none" && GetString(f, "acodec") != "none"), AudioLanguage);
        if (!videoHasAudio && audio is null)
        {
            _logger.LogInformation("YouTube offers no HLS audio for {VideoId}.", videoId);
            return null;
        }

        var height = GetPositiveInt(video["height"]) ?? 1080;
        var width = GetPositiveInt(video["width"]) ?? (int)System.Math.Round(height * 16d / 9d);
        var videoCodecs = GetString(video, "vcodec");
        var audioCodecs = videoHasAudio ? GetString(video, "acodec") : GetString(audio!, "acodec");
        var videoBandwidth = (long)((GetPositiveInt(video["tbr"]) ?? 2500) * 1000L);
        var audioBandwidth = audio is null ? 0 : (long)((GetPositiveInt(audio["tbr"]) ?? GetPositiveInt(audio["abr"]) ?? 128) * 1000L);

        _logger.LogInformation(
            "Resolved YouTube HLS for {VideoId}: {Height}p {VideoCodecs}{Audio}",
            videoId,
            height,
            videoCodecs,
            audio is null ? $" (muxed audio: {GetString(video, "language")}, {GetString(video, "format_note")})" : $" + separate audio ({GetString(audio, "language")}, {GetString(audio, "format_note")})");

        return new HlsPlaybackInput(
            GetString(video, "url"),
            videoCodecs,
            videoBandwidth,
            width,
            height,
            audio is null ? null : GetString(audio, "url"),
            string.IsNullOrEmpty(audioCodecs) || audioCodecs == "none" ? "mp4a.40.2" : audioCodecs,
            audioBandwidth);
    }

    /// <summary>The "YouTube HLS maximum resolution" setting: 480, 720 or 1080 (anything else is 1080).</summary>
    private static int HlsMaxHeight => Plugin.Instance?.Configuration.HlsMaxHeight is 480 or 720 ? Plugin.Instance.Configuration.HlsMaxHeight : 1080;

    /// <summary>
    /// Picks the H.264 HLS video to play: the tallest up to <paramref name="maxHeight"/> (if a video has
    /// nothing that small, the smallest it has up to 1080p), then by audio, then the highest bitrate.
    /// Signed in (with cookies), YouTube offers only muxed HLS - one copy of each resolution per audio track,
    /// dubs included, a few bytes apart in bitrate - so the audio has to be chosen here, the same way as
    /// <see cref="PickAudioTrack"/>: a muxed copy in <paramref name="language"/> first, then a video-only
    /// stream (whose audio PickAudioTrack chooses), then any other muxed copy, original track first.
    /// </summary>
    internal static JsonObject? PickHlsVideo(IEnumerable<JsonObject> hlsFormats, int maxHeight, string language)
    {
        static string Lower(JsonObject f, string key) => GetString(f, key).ToLowerInvariant();
        bool IsMuxed(JsonObject f) => Lower(f, "acodec") is { Length: > 0 } and not "none";
        int AudioRank(JsonObject f) => !IsMuxed(f) ? 1 : IsLanguage(GetString(f, "language"), language) ? 2 : 0;

        var avc = hlsFormats
            .Where(f => GetString(f, "vcodec").StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
                && GetPositiveInt(f["height"]) is > 0 and <= 1080)
            .ToList();
        var fitting = avc.Where(f => GetPositiveInt(f["height"]) <= maxHeight).ToList();
        var height = fitting.Count > 0
            ? fitting.Max(f => GetPositiveInt(f["height"]))
            : avc.Count > 0 ? avc.Min(f => GetPositiveInt(f["height"])) : null;
        return avc.Where(f => GetPositiveInt(f["height"]) == height)
            .OrderByDescending(AudioRank)
            .ThenByDescending(f => GetInt(f["language_preference"]) >= 10 || Lower(f, "format_note").Contains("original", StringComparison.Ordinal))
            .ThenBy(f => Lower(f, "format_note").Contains("descriptive", StringComparison.Ordinal))
            .ThenByDescending(f => GetPositiveInt(f["tbr"]) ?? 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// Picks the audio track to play from a video's HLS audio formats. YouTube lists one per language
    /// (the original and any dubs, including its AI "dubbed-auto" ones) in a low and a high variant, and
    /// usually reports no bitrate, so ordering by bitrate alone picked whichever came first - often a dub,
    /// and the low variant. In order: the configured language (<paramref name="language"/>, e.g. "en"),
    /// then YouTube's original track, never audio description, then the higher-quality variant.
    /// </summary>
    internal static JsonObject? PickAudioTrack(IEnumerable<JsonObject> audioFormats, string language)
    {
        static string Lower(JsonObject f, string key) => GetString(f, key).ToLowerInvariant();

        return audioFormats
            .OrderByDescending(f => IsLanguage(GetString(f, "language"), language))
            .ThenByDescending(f => GetInt(f["language_preference"]) >= 10 || Lower(f, "format_note").Contains("original", StringComparison.Ordinal))
            .ThenBy(f => Lower(f, "format_note").Contains("descriptive", StringComparison.Ordinal))
            .ThenByDescending(f => AudioQualityRank(f))
            .ThenByDescending(f => GetPositiveInt(f["tbr"]) ?? GetPositiveInt(f["abr"]) ?? 0)
            .FirstOrDefault();
    }

    // YouTube's HLS audio itags: 234 is the high (~128 kbps) variant, 233 the low (~48 kbps) one;
    // the format note says so too ("Default, high").
    private static int AudioQualityRank(JsonObject format)
    {
        var note = GetString(format, "format_note").ToLowerInvariant();
        if (note.Contains("high", StringComparison.Ordinal))
        {
            return 2;
        }

        if (note.Contains("low", StringComparison.Ordinal))
        {
            return 0;
        }

        var itag = GetString(format, "format_id").Split('-')[0];
        return itag == "234" ? 2 : itag == "233" ? 0 : 1;
    }

    // any whole number, negative too (language_preference is -1 or -10 for some tracks)
    private static int? GetInt(JsonNode? node)
    {
        try
        {
            var value = node?.GetValue<double>();
            return value is null ? null : (int)Math.Round(value.Value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Gets the currently configured playback target.</summary>
    public string GetPlaybackTarget()
    {
        var configured = Plugin.Instance?.Configuration.PlaybackTarget;
        return configured switch
        {
            PlaybackTargets.BroadCompatibility720p => PlaybackTargets.BroadCompatibility720p,
            PlaybackTargets.Balanced1080p => PlaybackTargets.Balanced1080p,
            PlaybackTargets.MaximumQuality => PlaybackTargets.MaximumQuality,
            _ => PlaybackTargets.BroadCompatibility720p
        };
    }

    private static string GetPlaybackFormatSelector(string playbackTarget)
    {
        return playbackTarget switch
        {
            PlaybackTargets.Balanced1080p => Balanced1080pSelector,
            PlaybackTargets.MaximumQuality => MaximumQualitySelector,
            _ => BroadCompatibility720pSelector
        };
    }

    /// <summary>
    /// Returns the flat-playlist JSON entry list for a channel or playlist URL.
    /// Each entry contains at minimum <c>id</c>, <c>title</c>, and <c>url</c>.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode>> GetPlaylistEntriesAsync(
        string playlistUrl,
        int videoRetentionDays,
        int maxEntryScanCount,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "--flat-playlist", "-J" };
        if (maxEntryScanCount > 0)
        {
            args.Add("--playlist-end");
            args.Add(maxEntryScanCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (videoRetentionDays > 0)
        {
            args.Add("--dateafter");
            args.Add(DateTime.UtcNow.AddDays(-videoRetentionDays).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add(playlistUrl);

        var result = await RunYtDlpJsonAsync(args, cancellationToken).ConfigureAwait(false);
        var entries = result?["entries"]?.AsArray();
        if (entries is null)
        {
            return Array.Empty<JsonNode>();
        }

        var list = new List<JsonNode>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is not null)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    /// <summary>
    /// Fetches full metadata for a single YouTube video.
    /// </summary>
    public async Task<VideoMetadata?> GetVideoMetadataAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var result = await RunYtDlpJsonAsync(new[] { "-J", "--no-playlist", url }, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        // yt-dlp may report fractional seconds, which GetValue<int> rejects.
        int? durationSeconds = null;
        try
        {
            var duration = result["duration"]?.GetValue<double>();
            if (duration is > 0)
            {
                durationSeconds = (int)Math.Round(duration.Value, MidpointRounding.AwayFromZero);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
        }

        return new VideoMetadata
        {
            VideoId = GetString(result, "id"),
            Title = GetString(result, "title"),
            Description = GetString(result, "description"),
            ThumbnailUrl = GetBestVideoThumbnailUrl(result),
            ChannelName = GetString(result, "channel"),
            PublishedUtc = ParsePublishedDate(result),
            DurationSeconds = durationSeconds,
            Width = GetPositiveInt(result["width"]),
            Height = GetPositiveInt(result["height"]),
            SubtitleTracks =ParseSubtitleTracks(result["subtitles"], isAutomatic: false)
                .Concat(ParseSubtitleTracks(result["automatic_captions"], isAutomatic: true))
                .ToList()
        };
    }

    private static int? GetPositiveInt(JsonNode? node)
    {
        try
        {
            var value = node?.GetValue<double>();
            return value is > 0 ? (int)Math.Round(value.Value) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads yt-dlp's <c>subtitles</c> / <c>automatic_captions</c> maps into one track per language,
    /// preferring SRT (YouTube's auto-caption VTT repeats every line karaoke-style) and skipping
    /// <c>m3u8</c> entries, which are HLS playlists rather than caption files.
    /// </summary>
    private static IEnumerable<SubtitleTrack> ParseSubtitleTracks(JsonNode? node, bool isAutomatic)
    {
        if (node is not JsonObject languages)
        {
            yield break;
        }

        foreach (var (language, formats) in languages)
        {
            if (formats is not JsonArray list)
            {
                continue;
            }

            var candidates = list
                .OfType<JsonObject>()
                .Select(f => (Ext: GetString(f, "ext"), Url: GetString(f, "url"), Protocol: GetString(f, "protocol")))
                .Where(f => !string.IsNullOrWhiteSpace(f.Url) && !f.Protocol.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var best = candidates.FirstOrDefault(f => f.Ext == "srt");
            if (string.IsNullOrEmpty(best.Url))
            {
                best = candidates.FirstOrDefault(f => f.Ext == "vtt");
            }

            if (!string.IsNullOrEmpty(best.Url))
            {
                yield return new SubtitleTrack(language, best.Url, best.Ext, isAutomatic);
            }
        }
    }

    /// <summary>
    /// Fetches just the published date fields for a video as a lightweight fallback when the full metadata lookup lacks a usable date.
    /// </summary>
    public async Task<DateTime?> GetVideoPublishedDateAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var result = await RunYtDlpTextAsync(
            new[]
            {
                "--no-playlist",
                "--print", "%(upload_date)s",
                "--print", "%(release_date)s",
                "--print", "%(timestamp)s",
                "--print", "%(release_timestamp)s",
                "--print", "%(release_year)s",
                url
            },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var lines = result
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return ParsePublishedDateFromLines(lines);
    }

    /// <summary>
    /// Fetches metadata (title, description, thumbnail URL, detected type) for a channel or playlist URL.
    /// Only the first playlist entry is requested so the call is fast.
    /// </summary>
    public async Task<SourceInfo?> GetSourceInfoAsync(string url, CancellationToken cancellationToken)
    {
        // --playlist-end 1 limits video retrieval but the container metadata is always present.
        var args = new[] { "--flat-playlist", "-J", "--playlist-end", "1", url };
        var result = await RunYtDlpJsonAsync(args, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var title = result["title"]?.GetValue<string>() ?? string.Empty;
        var description = result["description"]?.GetValue<string>() ?? string.Empty;

        var thumbnailUrl = GetBestSourceAvatarUrl(result);
        var posterUrl = GetBestSourcePosterUrl(result);

        // Detect source type from yt-dlp's extractor key or URL pattern.
        var extractorKey = result["extractor_key"]?.GetValue<string>() ?? string.Empty;
        var isPlaylist = url.Contains("playlist?list=", StringComparison.OrdinalIgnoreCase)
                         || extractorKey.Equals("YoutubePlaylist", StringComparison.OrdinalIgnoreCase);
        var backdropUrl = GetSourceBackdropUrl(result, isPlaylist);

        // A playlist only has a small video still (336x188) as artwork. Use the owning channel's avatar and
        // channel art instead, so the show looks like the channel it comes from.
        if (isPlaylist)
        {
            var channelUrl = GetOwningChannelUrl(result);
            if (!string.IsNullOrWhiteSpace(channelUrl))
            {
                var channel = await RunYtDlpJsonAsync(new[] { "--flat-playlist", "-J", "--playlist-end", "1", channelUrl }, cancellationToken)
                    .ConfigureAwait(false);
                var avatar = GetBestSourceAvatarUrl(channel);
                if (!string.IsNullOrWhiteSpace(avatar))
                {
                    thumbnailUrl = avatar;
                    posterUrl = GetBestSourcePosterUrl(channel);
                }

                var channelArt = GetSourceBackdropUrl(channel, isPlaylist: false);
                if (!string.IsNullOrWhiteSpace(channelArt))
                {
                    backdropUrl = channelArt;
                }
            }
        }

        return new SourceInfo
        {
            Title = title,
            Description = description,
            ThumbnailUrl = thumbnailUrl,
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
            Type = isPlaylist ? SourceType.Playlist : SourceType.Channel
        };
    }

    /// <summary>
    /// Returns the URL of the channel that owns a playlist. Older playlists don't carry it at the top level, so
    /// the first entry's channel is used as a fallback.
    /// </summary>
    internal static string GetOwningChannelUrl(JsonNode? playlist)
    {
        var candidates = new[] { playlist, playlist?["entries"]?.AsArray().FirstOrDefault() };
        foreach (var node in candidates)
        {
            var url = GetString(node, "channel_url");
            if (string.IsNullOrWhiteSpace(url))
            {
                url = GetString(node, "uploader_url");
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return string.Empty;
    }

    private async Task<JsonNode?> RunYtDlpJsonAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var output = await RunYtDlpAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(output);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(ex, "yt-dlp returned invalid JSON");
            return null;
        }
    }

    private async Task<string?> RunYtDlpTextAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var output = await RunYtDlpAsync(arguments, cancellationToken).ConfigureAwait(false);
        return output?.Trim();
    }

    /// <summary>
    /// Runs yt-dlp and returns stdout, or <c>null</c> on failure. Adds the configured cookies file, and
    /// records a rate-limit window when YouTube answers with HTTP 429 or a "confirm you're not a bot" wall.
    /// </summary>
    private async Task<string?> RunYtDlpAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var cookiesCopy = CopyCookiesFile(Plugin.Instance?.Configuration.CookiesFilePath);
        if (cookiesCopy is not null)
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesCopy);
        }

        try
        {
            return await RunProcessAsync(psi, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (cookiesCopy is not null)
            {
                TryDelete(cookiesCopy);
            }
        }
    }

    /// <summary>
    /// Copies the configured cookies file to a private temporary file for one yt-dlp run, or returns
    /// <c>null</c> when none is configured or it can't be read.
    /// <para>
    /// yt-dlp writes its cookie jar back to the <c>--cookies</c> file when it exits. Several runs at once
    /// (sync lookups plus playback) would then overwrite each other's copy of the shared file, and a
    /// read-only file makes the write fail. Each run gets its own copy, so the configured file is only ever
    /// read and can be replaced safely by whatever keeps it fresh (for example <c>scripts/youtube-cookies.sh</c>).
    /// </para>
    /// </summary>
    private string? CopyCookiesFile(string? cookiesPath)
    {
        if (string.IsNullOrWhiteSpace(cookiesPath))
        {
            return null;
        }

        try
        {
            var copy = Path.Combine(Path.GetTempPath(), $"youtubesync-cookies-{Guid.NewGuid():N}.txt");
            File.Copy(cookiesPath, copy);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Can't read the YouTube cookies file {Path}: {Message}. Running yt-dlp without cookies.", cookiesPath, ex.Message);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file in the temp folder is harmless.
        }
    }

    private async Task<string?> RunProcessAsync(ProcessStartInfo psi, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        foreach (var arg in SplitArguments(Plugin.Instance?.Configuration.ExtraYtDlpArguments))
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Running yt-dlp with arguments: {Arguments}", string.Join(" ", psi.ArgumentList));

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                if (IsRateLimitError(error))
                {
                    var until = DateTime.UtcNow + RateLimitBackoff;
                    if (_rateLimitedUntilUtc < until)
                    {
                        _rateLimitedUntilUtc = until;
                        _logger.LogWarning(
                            "YouTube is rate-limiting this server (HTTP 429 / bot check). Background syncing pauses until {Until:u}. A cookies file in the plugin settings makes this much rarer.",
                            until);
                    }
                }

                _logger.LogError(
                    "yt-dlp exited with code {ExitCode}. Stderr: {Error}",
                    process.ExitCode,
                    error);
                return null;
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run yt-dlp");
            return null;
        }
    }

    /// <summary>Splits an argument string on spaces, keeping double-quoted parts (which may contain spaces) together.</summary>
    internal static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static bool IsRateLimitError(string stderr)
    {
        return stderr.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("confirm you’re not a bot", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribePlaybackUrl(string playbackUrl)
    {
        if (IsManifestUrl(playbackUrl))
        {
            return "manifest";
        }

        return "direct media";
    }

    private static bool IsManifestUrl(string playbackUrl)
        => playbackUrl.Contains("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase)
           || playbackUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
           || playbackUrl.Contains("/api/manifest/", StringComparison.OrdinalIgnoreCase)
           || playbackUrl.Contains(".mpd", StringComparison.OrdinalIgnoreCase);

    internal static string GetBestVideoThumbnailUrl(JsonNode? node)
    {
        return SelectThumbnailUrl(node?["thumbnails"]?.AsArray(), ThumbnailPreference.Video);
    }

    internal static string GetBestSourceAvatarUrl(JsonNode? node)
    {
        return SelectThumbnailUrl(node?["thumbnails"]?.AsArray(), ThumbnailPreference.Avatar);
    }

    internal static string GetBestSourcePosterUrl(JsonNode? node)
    {
        var banner = SelectThumbnailUrl(node?["thumbnails"]?.AsArray(), ThumbnailPreference.Banner);
        return string.IsNullOrWhiteSpace(banner) ? GetBestSourceAvatarUrl(node) : banner;
    }

    /// <summary>
    /// Returns the channel's full channel art (yt-dlp's <c>banner_uncropped</c>, 2560x1440), which suits a
    /// 16:9 backdrop, unlike the cropped banner strip. Playlists have no channel art, so their thumbnail is used.
    /// </summary>
    internal static string GetSourceBackdropUrl(JsonNode? node, bool isPlaylist)
    {
        var uncropped = node?["thumbnails"]?.AsArray()
            .FirstOrDefault(t => string.Equals(GetString(t, "id"), "banner_uncropped", StringComparison.OrdinalIgnoreCase));
        if (uncropped is not null && !string.IsNullOrWhiteSpace(GetString(uncropped, "url")))
        {
            return GetString(uncropped, "url");
        }

        return isPlaylist ? GetBestVideoThumbnailUrl(node) : string.Empty;
    }

    private static string SelectThumbnailUrl(JsonArray? thumbnails, ThumbnailPreference preference)
    {
        if (thumbnails is null || thumbnails.Count == 0)
        {
            return string.Empty;
        }

        var best = thumbnails
            .Where(t => t is not null)
            .Select(t => new
            {
                Node = t!,
                Width = GetInt(t, "width"),
                Height = GetInt(t, "height"),
                Preference = GetInt(t, "preference"),
                Url = GetString(t, "url"),
                Id = GetString(t, "id")
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.Url))
            .OrderByDescending(t => ScoreThumbnail(t.Width, t.Height, t.Preference, t.Id, t.Url, preference))
            .ThenByDescending(t => t.Preference)
            .ThenByDescending(t => t.Width)
            .FirstOrDefault();

        return best?.Url ?? string.Empty;
    }

    private static double ScoreThumbnail(int width, int height, int sourcePreference, string id, string url, ThumbnailPreference preference)
    {
        var inferredSize = InferThumbnailSize(width, height, id, url, preference);
        width = inferredSize.width;
        height = inferredSize.height;

        if (width <= 0 || height <= 0)
        {
            return sourcePreference;
        }

        var ratio = (double)width / height;
        var area = width * height;
        var idLower = id.ToLowerInvariant();
        var urlLower = url.ToLowerInvariant();

        return preference switch
        {
            ThumbnailPreference.Avatar => (1000 - Math.Abs(ratio - 1.0) * 500) + area / 1000.0 + ScoreId(idLower, urlLower, "avatar", "profile", "channel") - ScoreId(idLower, urlLower, "banner", "hero"),
            ThumbnailPreference.Banner => (1000 - Math.Abs(ratio - 1.77) * 300) + area / 1000.0 + ScoreId(idLower, urlLower, "banner", "hero", "header") - ScoreId(idLower, urlLower, "avatar", "profile"),
            _ => (1000 - Math.Abs(ratio - 1.77) * 250) + area / 1000.0 + ScoreId(idLower, urlLower, "mq", "hq", "sd", "maxres", "hq720", "video") - ScoreId(idLower, urlLower, "avatar", "profile", "banner")
        };
    }

    private static (int width, int height) InferThumbnailSize(int width, int height, string id, string url, ThumbnailPreference preference)
    {
        if (width > 0 && height > 0)
        {
            return (width, height);
        }

        var key = string.Concat(id, " ", url).ToLowerInvariant();

        if (key.Contains("maxres"))
        {
            return (1920, 1080);
        }

        if (key.Contains("hq720"))
        {
            return (1280, 720);
        }

        if (key.Contains("sddefault") || key.Contains("sd1") || key.Contains("sd2") || key.Contains("sd3"))
        {
            return (640, 480);
        }

        if (key.Contains("hqdefault") || key.Contains("hq1") || key.Contains("hq2") || key.Contains("hq3"))
        {
            return (480, 360);
        }

        if (key.Contains("mqdefault") || key.Contains("mq1") || key.Contains("mq2") || key.Contains("mq3"))
        {
            return (320, 180);
        }

        if (key.Contains("default") || key.Contains("/0.") || key.Contains("/1.") || key.Contains("/2.") || key.Contains("/3."))
        {
            return (120, 90);
        }

        if (key.Contains("banner_uncropped"))
        {
            return (2560, 424);
        }

        if (key.Contains("avatar_uncropped"))
        {
            return (900, 900);
        }

        return preference switch
        {
            ThumbnailPreference.Avatar => (900, 900),
            ThumbnailPreference.Banner => (2560, 424),
            _ => (0, 0)
        };
    }

    private static double ScoreId(string id, string url, params string[] matches)
    {
        return matches.Any(match => id.Contains(match) || url.Contains(match)) ? 200 : 0;
    }

    private static int GetInt(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<int>() ?? 0; }
        catch (InvalidOperationException) { return 0; }
    }

    internal static DateTime? ParsePublishedDate(JsonNode? node)
    {
        var directDate = ParsePublishedDateValues(
            GetScalarString(node, "upload_date"),
            GetScalarString(node, "release_date"),
            GetScalarString(node, "timestamp"),
            GetScalarString(node, "release_timestamp"),
            GetScalarString(node, "release_year"));

        if (directDate is not null)
        {
            return directDate;
        }

        return null;
    }

    private static DateTime? ParsePublishedDateFromLines(IReadOnlyList<string> lines)
    {
        var values = new string[5];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i < lines.Count ? lines[i] : string.Empty;
        }

        return ParsePublishedDateValues(values[0], values[1], values[2], values[3], values[4]);
    }

    private static DateTime? ParsePublishedDateValues(
        string uploadDate,
        string releaseDate,
        string timestamp,
        string releaseTimestamp,
        string releaseYear)
    {
        foreach (var value in new[] { uploadDate, releaseDate })
        {
            if (value.Length == 8
                && DateTime.TryParseExact(
                    value,
                    "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsedDate))
            {
                return parsedDate;
            }
        }

        foreach (var value in new[] { releaseTimestamp, timestamp })
        {
            if (long.TryParse(value, out var unixTimestamp) && unixTimestamp > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
            }
        }

        if (int.TryParse(releaseYear, out var year) && year > 0)
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }

    private enum ThumbnailPreference
    {
        Avatar,
        Banner,
        Video
    }

    private static string GetScalarString(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null)
        {
            return string.Empty;
        }

        try
        {
            return value.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return value.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return value.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return value.ToJsonString().Trim('"');
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }
}
