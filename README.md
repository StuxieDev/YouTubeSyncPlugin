<p align="center">
  <img src="assets/logo.svg" alt="YouTubeSync logo" width="128">
</p>

# YouTubeSync – Jellyfin Plugin

Watch YouTube channels and playlists directly in Jellyfin — no downloading required.

The plugin syncs metadata and artwork from YouTube into your library and streams videos on demand using [yt-dlp](https://github.com/yt-dlp/yt-dlp).

## Installation

Add the plugin repository in Jellyfin under **Dashboard → Plugins → Repositories → +** :

```
https://raw.githubusercontent.com/StuxieDev/YouTubeSyncPlugin/main/manifest.json
```

Then install **YouTubeSync** from the plugin catalogue and restart Jellyfin.

### Requirements

- **Jellyfin 12.x** (12.0 or newer). Jellyfin 10.11 is no longer supported; the repository only lists v1.0.0 and later.
- **yt-dlp** available on PATH (or configured in plugin settings), plus a JavaScript runtime such as [Deno](https://deno.com) so yt-dlp can solve YouTube's player challenges. See [`local-testing/Dockerfile`](local-testing/Dockerfile) for a working setup.
- **ffmpeg** for Enhanced playback mode (recommended, see below). Jellyfin's bundled `jellyfin-ffmpeg` works.

> **Upgrading from Jellyfin 10.11?** Jellyfin's 12.0 release notes ask you to remove third-party plugins before migrating. Remove YouTubeSync, upgrade Jellyfin, then install YouTubeSync from the repository above. Your plugin settings and synced library folder are kept.

## Getting started

1. Open **Dashboard → Plugins → YouTubeSync → Settings**.
2. Set the **Library folder** to a path inside one of your Jellyfin libraries (e.g. `/media/youtube`).
3. Set the **Jellyfin address** to the URL your playback devices use to reach the server.
4. Set **Playback mode** to **Enhanced mode** (see below).
5. Click **+ Add Channel or Playlist**, paste a YouTube URL, give it a name, and save.
6. The sync task runs automatically every 6 hours. You can also trigger it manually from **Dashboard → Scheduled Tasks**.

After sync, your YouTube content appears in Jellyfin organised by channel, season (year), and episode — complete with artwork and metadata.

## Playback modes

| Mode | What it does | Needs ffmpeg? |
|---|---|---|
| **Enhanced** (recommended) | Muxes YouTube's separate video and audio streams through a local ffmpeg process into HLS, up to 1080p. Falls back to Simple automatically if anything goes wrong. | Yes |
| **Simple** (default) | Hands Jellyfin a single direct YouTube stream URL. Lightweight, but see the limitation below. | No |

You can switch between modes in the plugin settings at any time.

## Known limitations

- **Simple mode is unreliable.** YouTube now serves most videos only as separate video and audio streams. The only combined stream left (360p) is offered intermittently, so Simple mode often returns "no compatible playback URL". Use Enhanced mode.
- Enhanced mode currently outputs a single 1080p HLS profile.
- Age-restricted or members-only videos will not play (no cookie support yet).

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet publish Jellyfin.Plugin.YouTubeSync/Jellyfin.Plugin.YouTubeSync.csproj \
  -c Release --no-self-contained -o publish/
```

Copy the resulting DLL and `meta.json` into a `YouTubeSync` folder in your Jellyfin plugins directory and restart. For a Docker-based test server, see [`local-testing/README.md`](local-testing/README.md).

## Releasing

See [CONTRIBUTING.md](CONTRIBUTING.md). In short: update `CHANGELOG.md`, bump `VERSION.md`, run `commit.sh` / `commit.bat`, then push the tag. The release workflow builds the plugin, publishes the GitHub Release and updates `manifest.json`.

## Credits

Based on [jellyfin-youtube-plugin](https://github.com/kingschnulli/jellyfin-youtube-plugin) by kingschnulli. Maintained by [StuxieDev](https://github.com/StuxieDev) as a standalone project, ported to Jellyfin 12.

© Leo Ridgwell (StuxieDev) for changes made since the original.
