# Changelog

All notable changes to YouTubeSync are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
