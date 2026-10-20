# IntroDB Plugin for Jellyfin and Emby

Fetches crowdsourced **intro, recap and end-credits** timestamps from [IntroDB](https://introdb.app) for TV episodes and movies, and exposes them as Jellyfin **Media Segments** so clients can show *Skip* buttons.

Lookups use the IMDb id (the series' for episodes, falling back to the episode's or a `tt…` id in the file path) plus the season/episode number. IntroDB doesn't support TVDB/TMDB ids or alternate episode orders, so DVD/absolute-ordered shows may match the wrong episode.

## Features

- **Intros, recaps and end credits** (stored as Intro, Recap and Outro segments), each switchable.
- **Post-credits scenes stay watchable:** when IntroDB knows of one, the credits segment ends where the scene starts.
- **Movies** as well as TV episodes.
- **Scan scope:** limit fetching to chosen libraries, shows or movies.
- **On-demand fetch:** segments for new items (after a 2-minute wait for metadata) and for items that start playing without segments.
- **Skip items with existing segments** (optional, off by default).
- **Not-found cache** that survives restarts, with a clear button.
- **Remove IntroDB segments** from a library, show or movie, keeping other providers' segments (Jellyfin 10.11+).
- Automatic **rate-limit handling** (see below).

## Supported versions

| Server | Build | .NET |
|---|---|---|
| Jellyfin 12.2 | `jellyfin-12.2` (repository install) | 10.0 |
| Jellyfin 10.11 | `jellyfin-10.11` (manual install) | 9.0 |
| Jellyfin 10.10 | `jellyfin-10.10` (manual install) | 8.0 |
| Emby 4.9 | `emby` (manual install) | netstandard2.0 |

## Installation

### Option A: Add repository (Jellyfin 12.2, automatic updates)

1. In Jellyfin: **Dashboard → Plugins → Repositories → Add**.
2. Add this repository URL:
   `https://raw.githubusercontent.com/asportnoy/introdb-plugin/main/manifest.json`
3. Go to **Catalog** and install **IntroDB**, then restart Jellyfin.
4. Enable **IntroDB** under each library's **Media Segment Providers** (Dashboard → Libraries → ⋯ → Manage library).
5. Optionally adjust settings at **Dashboard → Plugins → IntroDB**.
6. Run **Dashboard → Scheduled Tasks → Media Segment Scan** (▶) to populate segments.

Segments are filled by the **Media Segment Scan** task, and (with on-demand fetch on) when items are added or start playing. On-demand fetch runs every enabled segment provider for that item, not just IntroDB.

### Emby

The Emby build adds a **IntroDB Segment Fetcher** scheduled task (daily at 3am) that writes IntroStart/IntroEnd and CreditsStart markers for episodes and movies. Emby has no recap marker, and there's no settings page: the intro/credits toggles can only be changed in the plugin's XML config.

### Option B: Manual install

1. Download the zip for your server from the [Releases](https://github.com/asportnoy/introdb-plugin/releases) page.
2. Extract it into a new folder (e.g. `IntroDB`) inside your server's plugins directory:
   - **Linux:** `~/.local/share/jellyfin/plugins/` (or `/var/lib/jellyfin/plugins/`)
   - **macOS:** `~/Library/Application Support/jellyfin/plugins/`
   - **Windows:** `%LocalAppData%\jellyfin\plugins\`
3. Restart the server and run the **Media Segment Scan** task.

## Rate limiting

The plugin handles API throttling on its own. When IntroDB answers `429` or `503`, all requests pause for the `Retry-After` header time, or the `reset_at` time in the response body (default 10s, growing on repeated throttles, max 5 min). The item is then retried up to 3 times. Requests are spaced at least 100 ms apart.

Items IntroDB has nothing for are remembered in `notfound-cache.json` in the plugin's data folder: for 24 hours, or 14 days when the community has confirmed there's nothing to skip. Clear it from the settings page.

## Building

Requires the .NET SDK matching the target (see table above).

```sh
./build.sh                    # all targets (skips any whose SDK is missing)
./build.sh --jellyfin-12.2    # a single target
VERSION=1.2.0.0 ./build.sh    # stamp a version
```

Outputs go to `build/`, one folder and one `introdb_<version>_<target>.zip` (+ `.md5`) per target.

Run tests for a given Jellyfin version:

```sh
dotnet test introdb-plugin/IntroDbPlugin.Tests -p:JellyfinVersion=12.2
```

## Releasing

Publishing a GitHub release tagged `vX.Y.Z.W` runs `.github/workflows/publish.yaml`, which builds every target, attaches the zips and checksums to the release, and adds the Jellyfin 12.2 build to `manifest.json` on `main`.

## License

GPL-3.0-only. See [LICENSE](LICENSE).
