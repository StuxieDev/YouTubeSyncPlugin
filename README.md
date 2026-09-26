<p align="center">
  <img src="assets/logo.svg" alt="YouTubeSync logo" width="128">
</p>

# YouTubeSync – Jellyfin Plugin

Watch YouTube channels and playlists directly in Jellyfin — no downloading required.

The plugin syncs metadata and artwork from YouTube into your library and streams videos on demand using [yt-dlp](https://github.com/yt-dlp/yt-dlp).

## Installation

Add the plugin repository in Jellyfin under **Dashboard → Plugins → Repositories → +** :

```
https://github.com/StuxieDev/YouTubeSyncPlugin/releases/latest/download/manifest.json
```

Then install **YouTubeSync** from the plugin catalogue and restart Jellyfin.

### Requirements

- **Jellyfin 12.x** (12.0 or newer). Jellyfin 10.11 is no longer supported; the repository only lists v1.0.0 and later.
- **yt-dlp** available on PATH (or configured in plugin settings), plus a JavaScript runtime such as [Deno](https://deno.com) so yt-dlp can solve YouTube's player challenges. See [`local-testing/Dockerfile`](local-testing/Dockerfile) for a working setup.
- **ffmpeg** for Enhanced playback mode (recommended, see below). Jellyfin's bundled `jellyfin-ffmpeg` works.

> **Upgrading from Jellyfin 10.11?** Jellyfin's 12.0 release notes ask you to remove third-party plugins before migrating. Remove YouTubeSync, upgrade Jellyfin, then install YouTubeSync from the repository above. Your plugin settings and synced library folder are kept.

## Getting started

1. Open **Dashboard → YouTube Sync** (in the sidebar's Plugins section), or **Dashboard → Plugins → YouTubeSync**.
2. Set the **Library folder** to a path inside one of your Jellyfin libraries (e.g. `/media/youtube`).
3. Set the **Jellyfin address** to the URL your playback devices use to reach the server.
4. Set **Playback mode** to **Enhanced mode** (see below).
5. Click **+ Add Channel or Playlist**, paste a YouTube URL, give it a name, and save.
6. The **Sync from YouTube** task (under **YouTube Sync** in **Dashboard → Scheduled Tasks**) runs every 6 hours. You can also run it from there by hand.

After sync, your YouTube content appears in Jellyfin organised by channel, season (year), and episode — complete with artwork and metadata.

## Subtitles

YouTube subtitles are saved next to each video as `<video>.<language>.srt`, and Jellyfin lists them as external subtitle tracks. Choose the languages under **Subtitles** in the settings (default `en`; `en` also matches `en-GB` and similar). If a video has no uploaded subtitles in a language, YouTube's auto-generated captions are used instead, unless you turn that off. Caption links come with each video's details, so subtitles add no extra requests to YouTube.

## Rate limiting and cookies

YouTube rate-limits servers that make a lot of requests, answering with HTTP 429 or "Sign in to confirm you're not a bot". While that lasts, **playback fails too**, because it uses the same IP. The plugin tries to stay under the limit:

- Each video is looked up once, then reused from `.youtubesync-metadata.json`, so only new uploads are fetched on later syncs.
- Lookups run two at a time with a pause after each (**Pause between video lookups**, default 2 seconds).
- If YouTube starts blocking, the sync stops making requests for 30 minutes, **keeps every existing video**, and logs a warning. The next sync carries on.

The first sync of a large channel can take a few hours.

**Recommended: a PO-token provider (no account, nothing expires).** YouTube's bot check mostly targets clients without a "proof of origin" token. [bgutil-ytdlp-pot-provider](https://github.com/Brainicism/bgutil-ytdlp-pot-provider) generates them:

1. Run the provider next to Jellyfin:
   `docker run -d --name bgutil-provider --init --restart unless-stopped -p 127.0.0.1:4416:4416 brainicism/bgutil-ytdlp-pot-provider`
2. Put `bgutil-ytdlp-pot-provider.zip` from its latest release in a folder the Jellyfin user can read, e.g. `/opt/yt-dlp-plugins`.
3. Set **Extra yt-dlp arguments** to `--plugin-dirs /opt/yt-dlp-plugins`.

Check it with `yt-dlp -v --plugin-dirs /opt/yt-dlp-plugins --skip-download <video URL>`: the output should list `PO Token Providers: bgutil:http`.

**Optional: cookies.** For a heavy library, set **YouTube cookies file** to a `cookies.txt` exported from a browser signed in to YouTube (for example with a "Get cookies.txt" browser extension). This makes blocks much rarer and also lets age-restricted videos play. The Jellyfin service user must be able to read the file. Use an account you don't mind yt-dlp using. Exported cookies don't last forever: YouTube rotates them in any browser that keeps the session open, so export from a private window and then close it (this usually lasts weeks to months), and re-export when blocks come back.

## Playback modes

| Mode | What it does | Needs ffmpeg? |
|---|---|---|
| **Enhanced** (recommended) | Muxes YouTube's separate video and audio streams through a local ffmpeg process into HLS, up to 1080p. Falls back to Simple automatically if anything goes wrong. | Yes |
| **Simple** (default) | Hands Jellyfin a single direct YouTube stream URL. Lightweight, but see the limitation below. | No |

You can switch between modes in the plugin settings at any time.

## Known limitations

- **Simple mode is unreliable.** YouTube now serves most videos only as separate video and audio streams. The only combined stream left (360p) is offered intermittently, so Simple mode often returns "no compatible playback URL". Use Enhanced mode.
- Enhanced mode currently outputs a single 1080p HLS profile.
- Age-restricted videos need a cookies file (see above). Members-only videos also need that account to be a member.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet publish Jellyfin.Plugin.YouTubeSync/Jellyfin.Plugin.YouTubeSync.csproj \
  -c Release --no-self-contained -o publish/
```

Copy the resulting DLL and `meta.json` into a `YouTubeSync` folder in your Jellyfin plugins directory and restart. For a Docker-based test server, see [`local-testing/README.md`](local-testing/README.md).

## Releasing

See [CONTRIBUTING.md](CONTRIBUTING.md). In short: update `CHANGELOG.md`, bump `VERSION.md`, run `commit.sh` / `commit.bat`, then push the tag. The release workflow builds the plugin and publishes a GitHub Release with the plugin zip and the repository `manifest.json` Jellyfin reads.

## Credits

Based on [jellyfin-youtube-plugin](https://github.com/kingschnulli/jellyfin-youtube-plugin) by kingschnulli. Maintained by [StuxieDev](https://github.com/StuxieDev) as a standalone project, ported to Jellyfin 12.

© Leo Ridgwell (StuxieDev) for changes made since the original.
