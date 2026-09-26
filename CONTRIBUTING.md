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

1. Finish the `## vX.Y.Z` section in `CHANGELOG.md`. The release workflow copies it into the plugin's changelog in Jellyfin.
2. Put the new version in `VERSION.md` as `X.Y.Z`, and in the `version` field of `Jellyfin.Plugin.YouTubeSync/meta.json` as `X.Y.Z.0` (the release workflow sets this anyway).
3. Update `README.md` if anything user-facing changed.
4. Run `./commit.sh` (or `commit.bat` on Windows). It commits everything and creates the tag `vX.Y.Z` from `VERSION.md`.
5. Push with `git push origin main --tags`.

Pushing the tag runs `.github/workflows/release.yml`. It builds the plugin and publishes a GitHub Release containing the plugin zip and the repository `manifest.json`. That manifest combines the plugin details in `manifest.template.json` with the version list from the previous release, so Jellyfin always reads it from `releases/latest/download/manifest.json`. Nothing is committed back to the repo.

**Why `X.Y.Z.0` in Jellyfin's files:** tags, releases, `VERSION.md` and the changelog use plain `X.Y.Z`. Jellyfin's API always reports installed plugin versions with four parts, and its plugin-image endpoint matches versions exactly, so `meta.json` and the manifest must use `X.Y.Z.0` or the plugin's banner won't load.
