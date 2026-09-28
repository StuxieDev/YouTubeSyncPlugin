# Changelog

All notable changes to YouTubeSync are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## v1.3.0

### Added
- Backdrops (the background image in Jellyfin). Each synced video's own thumbnail is now also its backdrop, set through a `fanart` entry in its NFO, because Jellyfin reads no local backdrop files for episodes. Each channel also gets a `backdrop` image from its full 2560x1440 channel art, or for a playlist its thumbnail. Before, YouTube shows and videos had no background at all. Existing videos pick this up on the next sync, with no extra YouTube requests.

## v1.2.1

### Fixed
- With a cookies file set, videos no longer lose YouTube's own HLS playback. Signed in, yt-dlp's default YouTube clients return no HLS formats, so every video fell back to a managed transcode (slower to start, and harder to seek). The HLS lookup now also asks the `web_safari` client, which still offers H.264 HLS up to 1080p when signed in, including for age-restricted videos.

## v1.2.0

### Added
- `scripts/youtube-cookies.sh` keeps the **YouTube cookies file** fresh on its own. Sign in to YouTube once in a dedicated Firefox profile (`youtube-cookies.sh login`), and a schedule (`youtube-cookies.sh install`, every 6 hours by default) keeps that session alive in a headless Firefox. It then exports the cookies, checks they're signed in and work, and swaps the file in. If a refresh fails, the old file is kept. No more exporting `cookies.txt` by hand.

### Changed
- Each yt-dlp run now gets its own temporary copy of the cookies file. yt-dlp writes cookies back to its `--cookies` file when it exits, so runs happening at the same time could overwrite each other's copy of the shared file. The configured file is now only ever read, and it can be replaced while syncing or playing.

### Fixed
- Synced episodes now have thumbnails in Jellyfin. Jellyfin only picks up an episode's image when it's named after the video file (`<video>-thumb`), so the `poster` and `folder` images next to each episode were ignored and every series episode showed no picture. The thumbnail is now also saved as `<video>-thumb`, copied from the existing poster so no extra downloads are needed. Episode NFOs no longer point at `poster.webp`, which Jellyfin logged as "not a valid URL or file path".

## v1.1.1

### Fixed
- Channels synced by playlist no longer lose videos when a playlist can't be listed. Before, a failed or rate-limited playlist request left that playlist's videos out, and the sync then deleted them. Playlists are now listed with the **Pause between video lookups** delay between them, listing stops while YouTube is rate-limiting, and nothing is removed from a sync where any playlist couldn't be listed.
- A video that moves to a new folder (a changed title, or switching a channel between year and playlist seasons) keeps its subtitles and artwork: they're copied from the old folder, and renamed if the title changed. Before, cached videos lost their subtitles when their folder moved.
- A cached video whose folder is recreated (for example after widening a date range) gets its subtitles again on the next sync, instead of never.

## v1.1.0

### Added
- Per-source publish date range: **Only videos published from** and **Only videos published until** (both optional, inclusive) on each channel or playlist.
  - **From:** older videos aren't synced, and ones synced earlier are removed. Use it to skip a channel's back catalogue from years ago.
  - **Until:** newer videos aren't added, but videos already synced are kept. Set it to today to stop a source getting new videos without deleting anything.
- The sources list shows each source's date range.

### Fixed
- Editing a channel or playlist no longer drops fields the form doesn't show, such as a custom thumbnail URL.

## v1.0.8

### Added
- A **Burn in subtitles for these clients** setting (default `nostalgiatv-docker`). Since Jellyfin 12, a video stream request that selects a subtitle but doesn't say how to deliver it gets `External` delivery, meaning the player must load the subtitle file itself. Players that only play the video, such as the NostalgiaTV pseudo-TV app, then showed no subtitles for any video. For the listed device IDs, the plugin adds `SubtitleMethod=Encode` to those requests so Jellyfin burns the chosen subtitles into the picture. This applies to every video on the server, and other clients are unaffected. Leave the setting empty to turn it off.

## v1.0.7

### Fixed
- Heavy buffering when a client asked for "original" quality. Synced videos' stream details had no bitrate, so Jellyfin re-encoded every YouTube stream at the client's maximum (40 Mbit/s for NostalgiaTV), about ten times YouTube's own bitrate. The details now include a typical YouTube bitrate for the resolution (for example 6 Mbit/s at 1080p), the H.264 level and BT.709 colour. With these, Jellyfin copies the video untouched when the client supports H.264, so there's no GPU encode and a fraction of the bandwidth, and caps any transcode it still needs at that bitrate. Stream details written by earlier versions are upgraded on the next library scan.

## v1.0.6

### Added
- Playback now uses YouTube's own HLS streams first (new **Use YouTube's own HLS streams** setting, on by default). The resolve URL points Jellyfin at a small master playlist listing YouTube's H.264 video (up to 1080p) and its audio. These playlists are complete and seekable, so playback starts anywhere in a video within seconds. The plugin runs no ffmpeg for them, and the result is cached per video until shortly before YouTube's links expire. Videos without HLS streams fall back to Enhanced or Simple mode.

### Fixed
- Live-TV apps such as NostalgiaTV could stay on standby or buffer forever when tuning into a YouTube programme part-way through. Enhanced mode always transcodes from 0:00 and publishes a still-growing playlist, so Jellyfin had to wait for the transcode to reach the tune-in point. It restarted and retried every few seconds meanwhile. YouTube's HLS streams are seekable, so this no longer happens.

## v1.0.5

### Fixed
- Synced videos now have video and audio stream details in Jellyfin. Jellyfin never probes `.strm` files, so they had none, and apps that check an item's media streams before scheduling it treated them as unplayable. In NostalgiaTV, for example, a channel made of YouTube shows scheduled almost nothing and showed standby. After each library scan, videos without a probed video stream get an H.264 video stream at the size Enhanced playback outputs and an AAC stereo audio stream. Existing streams such as subtitles are kept, and videos Jellyfin has really probed are left alone.
- NFOs now record the video codec, output size and audio in `fileinfo/streamdetails`. The size is taken from YouTube's best format, capped at 1920 wide as Enhanced playback does. Videos synced before this release use 1920×1080 until their details are next refreshed.

## v1.0.4

### Fixed
- Enhanced-mode streams that nobody is watching any more no longer block new ones. When the **Concurrent enhanced streams** limit is reached, the least recently used stream that no player has fetched from for 15 seconds is stopped to make room. Before, an abandoned stream (for example after changing channel in a pseudo-TV app) held its slot for up to 2 minutes, and new videos fell back to Simple mode, which usually fails.
- Streams whose ffmpeg has already finished no longer count against the limit.

## v1.0.3

### Changed
- When a sync finishes, it queues a Jellyfin library scan. Jellyfin's file watcher adds new videos straight away, but runtimes and removals are only applied by a full scan. Without one, new videos had no length, and apps like pseudo-TV schedulers didn't see them until Jellyfin's own scan ran.

### Fixed
- Subtitles are saved right after each video is looked up, instead of after the whole channel. YouTube's caption links expire after about 7 hours, which a large channel's lookup phase can outlast, so its first videos could miss their subtitles.

## v1.0.2

### Added
- An **Extra yt-dlp arguments** setting, added to every yt-dlp call. Use it to load yt-dlp plugins without touching the server's yt-dlp install, for example `--plugin-dirs /path/to/yt-dlp-plugins` for a PO-token provider such as `bgutil-ytdlp-pot-provider`. That avoids most of YouTube's "confirm you're not a bot" blocks without cookies. The README explains the setup.

## v1.0.1

### Added
- **YouTube subtitles.** Each video's subtitles are saved next to it as `<video>.<language>.srt`, and Jellyfin lists them as external subtitle tracks. New settings: **Download subtitles**, **Subtitle languages** (default `en`, which also matches regional variants like `en-GB`) and **Use auto-generated captions** (used when a video has no uploaded subtitles in a language). Caption links come with each video's details, so this adds no extra YouTube requests.
- A **YouTube Sync** link to the plugin's settings in the Plugins section of the admin dashboard sidebar.
- A banner image for the plugin, in Jellyfin's plugin catalogue and on the installed plugin's page.
- An optional **YouTube cookies file** setting, passed to yt-dlp. A signed-in session makes YouTube's rate limiting much rarer and lets age-restricted videos play.
- A **Pause between video lookups** setting (default 2 seconds) that spaces out per-video requests during a sync.

### Changed
- The scheduled task is now **Sync from YouTube**, in a **YouTube Sync** category. Its schedule and history carry over.
- Per-video lookups run two at a time instead of four.
- The repository manifest is published with each GitHub release (`releases/latest/download/manifest.json`) instead of being committed to `main` by a bot, so every commit in the repository is its author's own. Update the repository URL in Jellyfin to the new address in the README.

### Fixed
- **Syncs no longer delete videos when YouTube rate-limits the server.** Before, every video whose details couldn't be fetched was dropped, and its folder was deleted at the end of the sync. A large channel could lose most of its videos during an HTTP 429 / "confirm you're not a bot" block. Now videos that are still listed keep their existing files. When YouTube starts blocking, the sync stops making requests for 30 minutes and logs a warning, so playback, which uses the same IP, can recover.
- A channel listing that comes back empty (usually a failed request) no longer deletes every video in that channel; the sync leaves its files alone.

## v1.0.0

First release of YouTubeSync as a standalone StuxieDev project, for Jellyfin 12.x. It is based on [jellyfin-youtube-plugin](https://github.com/kingschnulli/jellyfin-youtube-plugin) by kingschnulli.

### Added
- After every library scan, synced videos get their runtime from their NFO file. Jellyfin ignores NFO runtimes for videos and never probes remote `.strm` streams, so without this synced videos have no length, and pseudo-TV schedulers such as NostalgiaTV can't place them. It only reads local files.
- NFOs include each video's exact length in seconds (Kodi's `fileinfo/streamdetails/video/durationinseconds`) as well as whole minutes.
- Each video's details are cached in `.youtubesync-metadata.json` in its channel folder, so a sync only calls yt-dlp for new videos. A cached video is refreshed if it was uploaded in the last 3 days, if its date or duration is missing, or once its entry is 30–60 days old, spread per video.
- Enhanced mode passes yt-dlp's negotiated HTTP headers to ffmpeg for each input. Stream URLs issued to some YouTube clients return 403 without them.
- When ffmpeg fails, the last lines of its output are logged as a warning.
- `CHANGELOG.md`, `VERSION.md`, `CONTRIBUTING.md`, `commit.sh` / `commit.bat` and a project logo.

### Changed
- Targets **Jellyfin 12.x**: built against Jellyfin 12.0.0 on .NET 10, with `targetAbi` 12.0.0.0. Jellyfin 10.11 is not supported.
- The config page fetches channel and playlist details through `ApiClient`, which sends the modern `Authorization` header. Jellyfin 12 disables legacy authorization (`X-Emby-Token`) by default.
- StuxieDev is the plugin's owner (shown in Jellyfin's plugin catalogue) and the assembly's author.
- Versions use plain semantic versioning (`1.0.0`) in `meta.json`, `manifest.json`, the release workflow and in Jellyfin.
- The local test container is built from `jellyfin/jellyfin:12`, and the release workflow uses the .NET 10 SDK.
- The README recommends Enhanced mode and explains why Simple mode is unreliable: YouTube now serves most videos only as separate video and audio streams.

### Fixed
- Enhanced mode no longer cuts off playback when ffmpeg finishes transcoding. Finished sessions are kept until they go idle instead of being deleted while the client is still watching.
- Enhanced mode no longer returns HTTP 500 ("No process is associated with this object") when ffmpeg exits during startup; it falls back to Simple mode.
- Very short videos that finish transcoding before the first readiness check are served instead of discarded.
- Videos shorter than 30 seconds no longer get `<runtime>0</runtime>`, and videos with a fractional yt-dlp duration no longer lose their duration.

### Security
- `GET /YouTubeSync/source-info` requires an administrator. Before, anyone who could reach the server could make it run yt-dlp against any URL they chose.
