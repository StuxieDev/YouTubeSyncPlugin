<p align="center">
  <img src="assets/logo.svg" alt="YouTubeSync logo" width="96">
</p>

# Contributing to YouTubeSync

Thanks for helping out! Issues and pull requests are welcome.

## Development setup

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A Jellyfin 12.x server to test against. The easiest option is the Docker setup in [`local-testing/`](local-testing/README.md), which bundles yt-dlp, Deno and ffmpeg.

Build with:

```bash
dotnet build Jellyfin.Plugin.YouTubeSync/Jellyfin.Plugin.YouTubeSync.csproj -c Release
```

Before opening a PR, check that the plugin loads and a sync plus a playback work on a real Jellyfin 12 server. There is no automated test suite yet.

## Jellyfin compatibility

The plugin compiles against the `Jellyfin.Controller` / `Jellyfin.Model` NuGet packages for the **lowest** Jellyfin version it supports (currently 12.0.0), and `meta.json`'s `targetAbi` matches. Jellyfin loads a plugin on any server at or above its `targetAbi`, so one build covers every 12.x release. Only raise these when you need an API from a newer release.

## Changelog

Every user-facing change goes into `CHANGELOG.md` under the upcoming version. Use these section headings, in this order, and leave out any that are empty:

1. `### Added`
2. `### Changed`
3. `### Fixed`
4. `### Removed`
5. `### Security`
6. `### Deprecated`

## Releasing

Versions follow [semantic versioning](https://semver.org/). To release:

1. Finish the `## vX.Y.Z` section in `CHANGELOG.md`.
2. Put the new version in `VERSION.md` and in the `version` field of `Jellyfin.Plugin.YouTubeSync/meta.json`, both as plain semantic versions (e.g. `1.0.1`, not `1.0.1.0`).
3. Update `README.md` if anything user-facing changed.
4. Run `./commit.sh` (or `commit.bat` on Windows). It commits everything and creates the tag `vX.Y.Z` from `VERSION.md`.
5. Push with `git push origin main --tags`.

Pushing the tag runs `.github/workflows/release.yml`, which builds the plugin, attaches the zip to a GitHub Release and adds the version to `manifest.json` on `main`.
