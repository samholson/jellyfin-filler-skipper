# Jellyfin Anime Filler Skipper Plugin

A server-side metadata and playback control plugin for Jellyfin (v10.10.x+) designed to identify, tag, and skip anime filler episodes using data fetched from AnimeFillerList.

---

## Architecture & Core Components

The plugin is built as a .NET 8.0 class library targeting the Jellyfin plugin API and exposes the following key architectural layers:

```
                  ┌───────────────────────┐
                  │   AnimeFillerService  │
                  └───────────┬───────────┘
                              │
          ┌───────────────────┼───────────────────┐
          ▼                   ▼                   ▼
┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐
│  SyncFillerTask   │ │SegmentProvider    │ │ PluginEntryPoint  │
│ (IScheduledTask)  │ │(MediaSegmentProv.)│ │ (IHostedService)  │
└─────────┬─────────┘ └─────────┬─────────┘ └─────────┬─────────┘
          │                     │                     │
          ▼                     ▼                     ▼
┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐
│ Library Metadata  │ │ Native Player OSD │ │Playback Auto-Skip │
│ (Tags & Prefixes) │ │ (Skip segments)   │ │  (Session Control)│
└───────────────────┘ └───────────────────┘ └───────────────────┘
```

### 1. Data Harvesting & Caching (`AnimeFillerService`)
* **Scraper**: Pulls HTML from `https://www.animefillerlist.com/shows/{slug}` using `HtmlAgilityPack`.
  * **Method 1 (Detailed Table)**: Parses the `.EpisodeList` table structure to map exact episode numbers to `Filler`, `Mixed`, or `Canon` classifications.
  * **Method 2 (Condensed Fallback)**: Parses the `div#Condensed` container ranges if the detailed table is missing.
* **Slug Resolver**: Normalizes Jellyfin series names to AnimeFillerList URL slugs. If a 404 is encountered, it scrapes the main `/shows` index and computes a Levenshtein distance fuzzy-match to locate the correct slug.
* **Cache Manager**: Serializes scraped data locally to `anime-filler-cache.json` in the plugin configuration directory to throttle external network calls.

### 2. Native Media Segment Provider (`FillerSegmentProvider`)
* Implements `IMediaSegmentProvider` (introduced natively in Jellyfin 10.10.x).
* Checks if an item is an `Episode`, is tagged `"Filler"`, and has a positive duration.
* Dynamically provides a `MediaSegmentDto` of the configured type (e.g. `Recap` or `Intro`) spanning the entire duration of the episode (`0` to `RunTimeTicks`).
* **Client Behavior**: Compatible clients (such as Jellyfin Web) detect this segment and render a native "Skip" prompt overlay on the OSD.

### 3. Playback Auto-Skip Controller (`PluginEntryPoint`)
* Implements `IHostedService` to listen to playback lifecycle events.
* Hooks into `ISessionManager.PlaybackStart`.
* If `AutoSkipFiller` is active and the starting episode is tagged `"Filler"`, it immediately issues a `PlaystateCommand.NextTrack` command to the user session, silently skipping the episode.

### 4. Background Sync scheduled Task (`SyncFillerTask`)
* Implements `IScheduledTask` to manage batch operations across the library.
* Scans all library series, filtering for Anime by querying tags and metadata provider IDs (`Anilist`, `Mal`, `AniDB`).
* Resolves absolute episode numbers sequentially across season boundaries (summing maximum index numbers of prior seasons).
* Appends the tag `"Filler"` to matched episodes.
* **Title Prefixing**: If enabled, updates the episode name (e.g. `[Filler] Episode Name`) to support 3rd-party apps (such as Kodi, Swiftfin, Swift, or Infuse) that do not support the Media Segments API.

---

## Configuration Settings

The plugin is configured via an HTML Dashboard page (`configPage.html`) mapping to the `PluginConfiguration` model:

| Parameter | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `AutoSkipFiller` | `bool` | `false` | Silently skips filler episodes when playback starts. |
| `CreateSkipButton` | `bool` | `true` | Exposes Media Segments to render native skip prompts in player. |
| `FillerSegmentType` | `string` | `"Recap"` | Target segment type enum (`Recap`, `Intro`, `Preview`). |
| `PrefixEpisodeTitles`| `bool` | `false` | Prepends custom string prefix to episode title names. |
| `EpisodeTitlePrefix` | `string` | `"[Filler] "` | Title prefix string if prefixing is enabled. |
| `ThrottlingDelayMs` | `int` | `2000` | Minimum delay in milliseconds between external HTTP requests. |

---

## Testing & Verification

A dedicated unit test project (`Jellyfin.Plugin.AnimeFiller.Tests`) covers all core features using xUnit and Moq:
* **Mocking**: Overrides `IXmlSerializer` and `IApplicationPaths` to isolate local file environments and abstract the singleton `Plugin.Instance` lifecycle.
* **Concurrency**: Assembly parallelization is disabled (`CollectionBehavior`) to avoid static state pollution across test runners.
* **Test Coverage**: Covers slug generation rules, HTML parsers, fuzzy slug matching, scheduled sync loops, absolute episode numbering, and Media Segment validation.

Run tests locally via the .NET CLI:
```powershell
dotnet test
```

Collect test coverage reports (cobertura format):
```powershell
dotnet test --collect:"XPlat Code Coverage"
```

---

## CI/CD Workflow

A GitHub Actions pipeline (`build.yml`) compiles, restores, builds, and publishes release-ready DLLs on push events to `main` and `master`:
* **Runner**: `ubuntu-latest`
* **SDK**: `.NET SDK 8.0.x`
* **Artifact**: Compiles to Release configuration and uploads the final binary `Jellyfin.Plugin.AnimeFiller.dll` as a build workflow artifact.