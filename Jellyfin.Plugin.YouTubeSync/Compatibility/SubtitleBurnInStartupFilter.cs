using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.YouTubeSync.Compatibility;

/// <summary>
/// Makes Jellyfin burn subtitles into the video for clients that select a subtitle track but never say how
/// it should be delivered.
/// <para>
/// Since Jellyfin 12, an HLS video request with a <c>SubtitleStreamIndex</c> but no <c>SubtitleMethod</c>
/// defaults to <c>External</c>: the player is expected to fetch the subtitle file separately. Clients that only
/// play the video playlist, such as the NostalgiaTV pseudo-TV app, then show no subtitles at all. For the
/// configured device IDs this adds <c>SubtitleMethod=Encode</c> to <c>/Videos/…</c> stream requests that
/// select a subtitle and don't specify a method. Requests from every other client are left untouched.
/// </para>
/// </summary>
public class SubtitleBurnInStartupFilter : IStartupFilter
{
    private readonly ILogger<SubtitleBurnInStartupFilter> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleBurnInStartupFilter"/> class.</summary>
    public SubtitleBurnInStartupFilter(ILogger<SubtitleBurnInStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Registered ahead of Jellyfin's pipeline so model binding sees the added parameter.
            app.Use(async (context, nextMiddleware) =>
            {
                ApplyBurnIn(context.Request);
                await nextMiddleware(context).ConfigureAwait(false);
            });
            next(app);
        };
    }

    private void ApplyBurnIn(HttpRequest request)
    {
        var configured = Plugin.Instance?.Configuration.BurnInSubtitleDeviceIds;
        if (string.IsNullOrWhiteSpace(configured) || !IsVideoStreamPath(request.Path))
        {
            return;
        }

        var query = request.Query;
        if (!query.TryGetValue("SubtitleStreamIndex", out var indexValue)
            || !int.TryParse(indexValue.ToString(), out var index)
            || index < 0
            || query.ContainsKey("SubtitleMethod"))
        {
            return;
        }

        var deviceId = query.TryGetValue("DeviceId", out var device) ? device.ToString() : string.Empty;
        var deviceIds = configured.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (deviceId.Length == 0 || !deviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var updated = new Dictionary<string, StringValues>(query, StringComparer.OrdinalIgnoreCase)
        {
            ["SubtitleMethod"] = "Encode"
        };
        request.QueryString = QueryString.Create(updated);
        _logger.LogDebug("Burning in subtitle stream {Index} for {DeviceId} on {Path}", index, deviceId, request.Path);
    }

    /// <summary>Matches Jellyfin's video stream endpoints: HLS playlists and segments, and progressive streams.</summary>
    private static bool IsVideoStreamPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var start = value.IndexOf("/Videos/", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var rest = value[(start + "/Videos/".Length)..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var tail = rest[(slash + 1)..];
        return tail.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || tail.StartsWith("hls1/", StringComparison.OrdinalIgnoreCase)
            || tail.StartsWith("hls/", StringComparison.OrdinalIgnoreCase)
            || tail.StartsWith("stream", StringComparison.OrdinalIgnoreCase);
    }
}
