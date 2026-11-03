<div align="center">

<img src="Assets/logo.png" width="96" alt="Haku Player logo"/>

# Haku Player

**A liquid-glass media player for Windows, built for 4K anime.**

Browse your anime, TV shows and movies like a streaming service, then watch them in mpv,
built right into the window, with live frosted-glass controls floating over the picture.

[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)](#requirements) [![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/9.0) [![WPF](https://img.shields.io/badge/UI-WPF-68217A)](#how-it-works) [![mpv](https://img.shields.io/badge/player-mpv-691F69)](https://mpv.io) [![License: MIT](https://img.shields.io/badge/license-MIT-22c55e)](LICENSE)

<img src="docs/images/home.png" alt="Haku Player home screen" width="100%"/>

</div>

---

## Highlights

- **Liquid glass everywhere.** The window uses Windows 11's acrylic, with slowly drifting colour glows behind it. In the player, a GPU shader blurs the *live video* under the controls every frame, Apple-style.
- **Made for anime.** Japanese audio and full English subtitles by default (it skips "Signs & Songs" tracks). Official episode titles, and openings and endings as their own library, named the [AnimeThemes](https://animethemes.moe) way.
- **Skip opening, skip ending, Up next.** Timings come from the file's chapters or from [AniSkip](https://aniskip.com) and [TheIntroDB](https://theintrodb.org). Failing that, Haku Player finds them itself by comparing the music of two episodes.
- **Remembers what you've watched.** Watched ticks, progress bars, a *Continue watching* row and library stats.
- **Your files, untouched.** It plays the originals at full quality through mpv (GPU decoding, no re-encoding) and never renames anything.
- **Real-time AI upscaling** (NVIDIA RTX). Anime is redrawn at twice the size with [AnimeJaNai](https://github.com/the-database/mpv-upscale-2x_animejanai) models on TensorRT, live, all on the GPU, a second ahead of playback. Subtitles and controls are drawn after, so they stay sharp.
- **Drop to play.** Drag a video or a folder onto the window, or onto `HakuPlayer.exe`, and it plays straight away. With the app already open, it plays in that window.

## Tour

<table>
  <tr>
    <td width="50%"><img src="docs/images/openings.png" alt="Library grid"/></td>
    <td width="50%"><img src="docs/images/show.png" alt="Show page"/></td>
  </tr>
  <tr>
    <td><b>Library</b> · covers, search, favourites, filters by genre or kind, sorting. Anime, TV shows and movies live together; openings and endings get their own section, with a <i>Random</i> shuffle.</td>
    <td><b>Show pages</b> · details, banner and official episode names, fetched automatically. Watched ticks, progress bars, seasons, "What's missing" and "Wrong show info?" to fix a bad match.</td>
  </tr>
</table>

### The player

<img src="docs/images/glass.gif" alt="Liquid glass controls fading over the video" width="100%"/>

The controls float over the video in two glass pills. They blur, brighten and bend the moving picture underneath, and fade away after a couple of seconds.

<table>
  <tr>
    <td width="50%"><img src="docs/images/skip.gif" alt="Skip opening"/></td>
    <td width="50%"><img src="docs/images/upnext.gif" alt="Up next countdown"/></td>
  </tr>
  <tr>
    <td><b>Skip opening / ending</b> · appears for the first 8 seconds, and again whenever the controls are up. Openings, endings and chapters are marked on the timeline.</td>
    <td><b>Up next</b> · when the ending starts, the next episode plays after a 10-second countdown. <i>Play now</i> or <i>Cancel</i>.</td>
  </tr>
  <tr>
    <td><img src="docs/images/menu.gif" alt="Player quick settings"/></td>
    <td><img src="docs/images/player.png" alt="Player controls"/></td>
  </tr>
  <tr>
    <td><b>Quick settings</b> · audio and subtitle tracks, your own subtitle files with timing fixes, speed, and per-kind language defaults.</td>
    <td><b>Timeline previews</b> · exact frames on hover, decoded on the GPU in the background, starting where you hover. Plus a 4K / codec badge, fill-to-screen, fullscreen and an always-on-top pop-out.</td>
  </tr>
</table>

### Settings and stats

<table>
  <tr>
    <td width="50%"><img src="docs/images/settings.png" alt="Appearance settings"/></td>
    <td width="50%"><img src="docs/images/stats.png" alt="Library stats"/></td>
  </tr>
  <tr>
    <td><b>Make it yours</b> · five accent themes or a colour of your own (the logo follows it), glass clarity and background motion. Separate language defaults for anime and for TV and movies, several library folders, and Discord status.</td>
    <td><b>Stats</b> · library size and running time, time watched, a 14-day chart and your most-watched shows.</td>
  </tr>
</table>

## All features

<details>
<summary><b>Library</b></summary>

- Several library folders, each marked as anime, TV shows or movies. New files are picked up while the app is open.
- Anime: `Anime Shows` and `Anime Openings` folders. TV: one folder per show *or per season*; seasons of the same show are merged ("Rick.and.Morty.S01…", "Rick and Morty Season 9").
- Details from [AniList](https://anilist.co) for anime and [Cinemeta](https://v3-cinemeta.strem.io) (IMDb) for TV and movies: description, genres, year, studio or director, score, banner and cover.
- Accurate matching: openings named the AnimeThemes way identify their show exactly; otherwise a ranked search (title, year, popularity, format, episode count). Badly named folders still match: it also tries the name cleaned up ("Naruto Complete", "Monster 2004 1080p") and the series name in the episode files ("[CBM]_Monster_-_01_….mkv"). *Wrong show info?* lets you pick the right one.
- Missing covers are downloaded. Anime covers are saved as `cover.jpg` in the show folder; TV and movie covers go in the app's own data folder.
- Official episode names (shown in the app only), a season picker, and *What's missing* (episodes you don't have yet).
- Search across shows, alternative titles and episodes. Favourites, genre and kind filters, sorted A–Z, by newest or by top rated.
</details>

<details>
<summary><b>Player</b></summary>

- mpv built into the app (libmpv), drawing straight into the window, with GPU decoding and the `high-quality` profile. Resumes where you left off (openings and endings always start from the top).
- Live liquid-glass controls (a GLSL shader on mpv's output). They hide after 2, 3 or 5 seconds, together with the cursor.
- Timeline: exact-frame hover previews, chapter ticks, opening and ending marked. Previews decode outward from where you hover, on the GPU (CUDA) with a CPU fallback, and are cached.
- Skip opening, ending and recap (optionally automatic), an Up next countdown and Autoplay.
- Audio and subtitle menu that prefers full subtitles over signs-only. Add your own subtitle files and shift their timing; both are remembered per video.
- Speed, volume (wheel works anywhere), fill-to-screen for near-16:9 screens, fullscreen, and a pop-out player that stays on top.
- The window keeps the video's shape when you resize it. Keyboard shortcuts are mpv's (space, arrows, `f`, `m`, `j`, `z`/`x`…).
- Debug panel: codec, colour, decoder, frame rate, dropped frames, A/V sync and bitrate.
- AI upscaling: a sparkle button (green while it runs) and `Ctrl+U`, its own settings page with Auto / Fast / Balanced / Quality tiers and a sharp or soft look, and *Detect best for this PC*, which times each tier on your card. The badge shows what it's upscaling to (`1080p → 4K`); the timeline shows the read-ahead in blue and the frames already upscaled in green. It waits a second before playing, and after a seek, so the AI is running first; if frames start dropping, it turns itself off for that video.
</details>

<details>
<summary><b>Watching</b></summary>

- An episode counts as watched once you reach its ending (or 90% of the way in). Tick or untick by hand, per episode, season or show.
- *Continue watching* row on the home screen: every show you're part-way through, or onto the next episode of. Close a card to hide it until you next watch that show.
- Stats: library size and running time, time watched (total and this week), a 14-day chart and most-watched shows.
- Discord status, *"Watching Haku Player · Code Geass · S01E05"* with time left. Optional, and needs your own Discord application ID.
</details>

## Requirements

| | |
|---|---|
| **Windows** | Windows 11 (22H2 or later, for the acrylic backdrop) |
| **.NET** | [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or the SDK to build) |
| **libmpv** | To build: put `libmpv-2.dll` in `lib\` (see below). It's copied next to the exe, so nothing else to install |
| **FFmpeg** | `winget install Gyan.FFmpeg` (`ffmpeg` / `ffprobe` on `PATH`), for previews, opening/ending detection and stats |
| **GPU** | Any. NVIDIA cards decode timeline previews with CUDA; others fall back to the CPU |
| **AI upscaling** (optional) | An NVIDIA RTX card, Haku's own `libmpv-2.dll` (with its `vf_hakusr` filter) and the TensorRT runtime and models in `%LOCALAPPDATA%\Haku Upscaler`. How to build all three: [native/README.md](native/README.md). Without them the option just stays off |

## Getting started

```powershell
git clone https://github.com/nbkburns-git/haku-player.git
cd haku-player
# libmpv (too big for the repo): from a shinchiro "mpv-dev-x86_64" build, https://github.com/shinchiro/mpv-winbuild-cmake/releases
# extract libmpv-2.dll into lib\
dotnet build -c Release
.\bin\Release\net9.0-windows\HakuPlayer.exe
```

For a faster-starting build (ReadyToRun):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o publish
```

Then open **Settings → Library → Add library** and choose your folders.

### Expected folder layout

```text
Anime library\                 ← marked "Anime"
├─ Anime Shows\
│  └─ Code Geass\
│     ├─ cover.png              (optional; downloaded if missing)
│     └─ Season 1\S01E01 - The Day a New Demon Was Born.mkv
└─ Anime Openings\
   └─ Code Geass\
      └─ CodeGeass-OP1.webm     (AnimeThemes names: Show-OP1, Show-ED2…)

TV Shows\                      ← marked "TV shows"
├─ The Sopranos (1999)\S01E01 - Pilot.mkv
└─ Rick.and.Morty.S02.1080p…\  (season folders of one show are merged)

Movies\                        ← marked "Movies"
├─ Spirited Away (2001)\Spirited Away.mkv
└─ Akira (1988).mkv
```

Everything Haku Player stores (settings, watch history, fetched details, covers, preview cache) lives in
`%LOCALAPPDATA%\Haku Player`. Your media folders are only ever read, except for a downloaded anime `cover.jpg`.

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File tests\native-tests.ps1   # haku_sr.dll: colours, padded edges, speed per tier
powershell -ExecutionPolicy Bypass -File tests\app-tests.ps1      # the app, end to end (a few minutes, muted)
```

The app tests build a test copy with its own data folder (your settings and history aren't touched) and drive it through
mpv's pipe and UI Automation: the AI start-up buffer, exact seeks, pausing, the AI button, turning it on while paused, a
10-bit 30 fps video (no frame drops, no green edge), a 4K file, subtitle choice, the next episode, your own subtitle files,
leaving the player mid-start, show-detail matching, and no UI stalls or "not responding" anywhere. The test videos are
set at the top of `tests\app-tests.ps1`: point them at files you have.

## How it works

The short version: mpv runs inside the app as a library and draws each frame into a Direct3D texture that WPF shows like any other image, so the video, the controls and the window resize as one picture. The app drives mpv over its JSON IPC pipe, and the blur under the controls is a shader running inside mpv.
[**docs/how-it-works.md**](docs/how-it-works.md) has the details: the glass shader, why fades are drawn by one renderer only, how openings are detected from audio fingerprints, and more.

## Privacy

There's no telemetry and no account. Haku Player only goes online to fetch details and timings:
AniList, AnimeThemes, Cinemeta, IMDb's poster lookup, AniSkip, TheIntroDB and IntroDB. Discord status, when you
turn it on, talks to the Discord app on your own PC.

## Credits

- [mpv](https://mpv.io) and [libplacebo](https://code.videolan.org/videolan/libplacebo) for playback and the shader hook; [FFmpeg](https://ffmpeg.org) and Chromaprint for previews and fingerprints.
- [AniList](https://anilist.co), [AnimeThemes](https://animethemes.moe), [Cinemeta](https://v3-cinemeta.strem.io), [AniSkip](https://aniskip.com), [TheIntroDB](https://theintrodb.org) and [IntroDB](https://introdb.app) for their free APIs.
- AI upscaling: [AnimeJaNai](https://github.com/the-database/mpv-upscale-2x_animejanai) models by the-database (CC BY-NC-SA 4.0, not included), run with [NVIDIA TensorRT](https://developer.nvidia.com/tensorrt).
- [Outfit](https://github.com/Outfitio/Outfit-Fonts) by The Outfit Project Authors, under the [SIL Open Font License](Assets/Fonts/OFL-Outfit.txt).
- Artwork in the screenshots belongs to its owners. The videos shown are the author's own 4K upscales.

Haku Player plays files you already have; it doesn't include, find or download any videos.

## License

[MIT](LICENSE) © 2026 Thomas Burns
