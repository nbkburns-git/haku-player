# HDR Haku on WinUI 3: plan

Goal: a fully HDR Haku. On an HDR display:
- HDR episodes play in real HDR.
- SDR episodes look exactly as they do today, at the brightness Windows' "SDR content brightness" slider sets.
- The glass, subtitles and the UI all sit right next to the video.

On an SDR display nothing changes. Every feature Haku has today keeps working, at today's speed.

**Status (2026-11-08):**
- **Phase 0 (go/no-go test): passed everything it could check.** HDR on screen is still to check, with the tools ready.
- **Phase 1: done**, in the WPF app (tests pass). What changed:
  - `Player/MpvCore.cs` now holds mpv and Direct3D 11; `MpvVideo` is only the D3DImage side.
  - New `Player/DisplayInfo.cs` (HDR state per monitor, shown in the player stats).
  - The library scan moved to `Library/Scan.cs`. Covers decode through two small per-app hooks (`DecodeImage`, `SaveSmallCover`).
  - The running-time cache moved to `Library/Durations.cs`.
  - Thumbnails split into `Thumbs.cs` (shared) and `Thumbs.Wpf.cs`. `Watch.cs` no longer uses WPF.
  - `Sync.cs` and `Cover.cs` turned out to be UI, so they get ported instead.
- **Phase 2: first cut working** on the `winui3` branch (`HakuWinUI\`). It plays a video passed on the command line or
  dropped on the window. What it has:
  - Video in HDR10 on an HDR monitor, SDR otherwise (`VideoView`). Swaps when the window changes monitor.
  - A glass pill: play/pause, timeline, time, volume, mute, fullscreen.
  - Keys go to mpv; fullscreen mirrors mpv; the drop screen shows when mpv quits.

  New shared pieces: `PlayerArgs` (the WPF app uses it too), `MpvIpc`, `GlassParams`, `Library/Entry.cs`, and `gwhite` in
  `glass.glsl`. `tests\winui-tests.ps1` runs 24 checks in SDR and forced HDR (`HAKU_HDR=force`), all passing: first frame
  in ~450–575 ms, no dropped frames, never takes focus, exact window size.

  **Second cut (built, not run yet: launching WinUI test copies can take focus, see below):**
  - It plays dropped or opened files and folders as one playlist, with episode names and numbers taken from the file names.
  - A top bar with back and the title; previous/next buttons.
  - Timeline marks for openings, endings and chapters, plus a hover preview (the shared `Thumbs`, decoded on the UI
    thread in `Thumbs.WinUI.cs`).
  - The Skip opening/ending card and Up next with its countdown, ported from `PlayerSegments`. Openings and endings come
    from chapters, then online times, then comparing with the next episode. `Segments`, `Info` and `Natural` are now shared.
  - Watch progress (`Watch.Tick`), and an audio/subtitles/speed menu.
  - Glass for the top bar and the card (rectangles b and d).

  **Phase 2 done (2026-11-08)**, all run and checked (`tests\winui-tests.ps1`: 21 checks pass):
  - The second cut above works: skip opening, top bar, Up next playing the next episode, watch progress, and the glass
    for the top bar and card.
  - **AI upscaling** is now shared: `Upscaling\UpscaleSession.cs` does tiers, engines, the AI buffer, the safety net and
    RTX Video for both apps, through `IUpscaleView`. The WPF upscaling tests all pass on it. In WinUI: the AI button,
    badge, loading panel, green buffer, toasts and Ctrl+U. Starts paused and plays upscaled to 4K in about 1.7 s with no
    dropped frames.
  - Fixed: the segment lookup ran before the playlist position was known, so it never ran. mpv reports the length first.
  - Still to check: HDR on a real HDR screen.
- **Phase 3: core pages working (2026-11-08).** The library logic moved to `Library\LibraryLogic.cs`, shared and with no UI
  in it: lib/Libs, favourites, filters and sorting, Continue watching, seasons, what's missing, the resume episode, the
  sync message, and background details and covers. The WPF library tests all pass on it.
  - **WinUI pages** (`HakuWinUI\Library.WinUI.cs`, `Detail.WinUI.cs`):
    - Home: the wordmark, a tile per library with a drifting wall of its covers, and the Continue cards.
    - The library grid (virtualized `ItemsRepeater`), with search and filter chips. Cards lift on hover and have stars
      and a right-click menu (play/resume, favourite, mark watched, rename).
    - The show page: banner, cover, facts, genres, description, seasons and episodes with ticks, stars and progress,
      what's missing, and Mark all watched.
    - Sync, with a folder watcher.
  - Home appears about 510 ms after launch (WPF: about 650).
  - Covers: `Covers.WinUI.cs` makes the small copies off the UI thread with Windows' imaging, and pages show them with
    `BitmapImage`.
  - **Test pipe** (`TestHooks.cs`, test runs only): `press <name>` and `text <name>|<text>`, pressed in-process, so a test
    never brings the window forward. `tests\winui-tests.ps1` now goes through home, the library, a show page, playing
    and back: 28 checks pass. `tests\winui-shots.ps1` takes screenshots of the pages.
  - Fixed: a rare deadlock when a resize waited for the render thread while that thread's first Present needed the UI thread.
  - Not done yet: paging and drag-to-reorder of home tiles, + Library, the cover glow on show pages, the drifting
    background blobs, and "Wrong show info?" (phase 4, with Settings).
- **Test windows and focus** (`tests\focus-watch.ps1` launches a test copy and logs the foreground window):
  - **WPF: fixed.** The test window came to the front about 0.4 s after launch. WPF made it on the main monitor (150%) and
    moved it to the test one (100%), and its DPI-change handling activates the window. `Player\TestFocus.cs` now makes
    the window right where `HAKU_TEST_WINDOW` puts it, using a thread CBT hook in test runs only. Your window keeps the
    foreground the whole time. The fix is on the `winui3` branch and needs to reach `main` too.
  - **WinUI: fixed.** WinUI shows and activates its window while making it (inside `Window`'s constructor), and that
    sometimes took the foreground: a program started by the one in front may come to the front once. A test copy now
    gives that up first (`LockSetForegroundWindow`, released as it exits). Things that didn't work: `WS_EX_NOACTIVATE`,
    making the window hidden, `SWP_NOACTIVATE` on its moves, and refusing the activation (that left no foreground window
    at all, which a fullscreen game can take as alt-tab).
  - `tests\safe-to-launch.ps1`: both test scripts now refuse to launch a test copy while WoW runs, a fullscreen window is
    in front, or your own Haku is in front.
  - Pressing a WinUI button through UI Automation also brings its window to the front, so the WinUI tests drive actions
    through mpv's pipe and only read the window.

**Look parity (2026-11-09):** the WinUI app now copies the WPF app's look, not just its features. Checked side by side
with `tests\compare-shots.ps1` (same pages in both apps on the test monitor, captured with PrintWindow; pairs land in
`%TEMP%\haku-compare`). What changed to get there:
- `Look.WinUI.cs`: WPF's App.xaml styles (Flat, Glass, Back, Caption, Icon, Chip, Row), the glass brushes, a WrapPanel,
  the logo drawn from the same shapes, text glow.
- No Windows caption buttons: the header and the player's top bar have Haku's own, as in WPF.
- `Backdrop.WinUI.cs`: thin desktop acrylic (matches WPF's DWM acrylic exactly), the tint, drifting colour blobs, the
  cover glow on show pages, the bottom shade.
- `Home.WinUI.cs`: WPF tiles (paging, arrows, dots, drag to reorder, + Library) and Continue watching cards.
- Player: WPF's 68 px pill and 56 px top bar (badge, pop-out, window buttons), its glass menus for tracks, speed and settings.
- `Popups.WinUI.cs`: glass right-click menu, rename box, "Find the right show", the banner faded with a composition mask.
- `Splash.WinUI.cs`: the loading screen. Tooltips use the glass look (App.xaml).
- A video opened at launch waits for the library (as WPF), so it plays as that show's episode.

Not copied: WinUI has no blur or drop shadow on arbitrary elements, so text shadows over video, the AI button's green
glow and card drop shadows are left out; scrollbars are WinUI's own.

## Contents

1. [What "fully HDR" means](#what-fully-hdr-means)
2. [Phase 0 results](#phase-0-results-2026-11-08)
3. [Key decisions](#key-decisions)
4. [How HDR output works](#how-hdr-output-works)
5. [Phases](#phases)
6. [File-by-file](#file-by-file)
7. [Tests](#tests)
8. [Risks](#risks)
9. [Effort](#effort)

## What "fully HDR" means

| Feature | How |
|---|---|
| HDR10 (PQ) video in HDR | mpv outputs PQ / BT.2020 into a 10-bit swap chain shown by WinUI's `SwapChainPanel` |
| SDR video unchanged | Same swap chain; SDR white placed at Windows' slider level (mpv fixes it at 203 nits, so Haku's shader scales it: below) |
| Right brightness for the display | `target-peak` = the display's peak (DXGI `MaxLuminance`). mpv tone-maps brighter HDR down to it (BT.2390) |
| SDR display or HDR off | 8-bit sRGB swap chain, as today. HDR video is tone-mapped to SDR by mpv, as today |
| Moving between monitors, HDR toggled | Watch the window's display. Rebuild the swap chain and mpv's targets when it changes |
| Glass over HDR video | `glass.glsl` with `gpq=1` (done and tested: within ~3% of SDR) |
| Subtitles | Checked on screen. If too dim in PQ (mpv draws them at 203 nits), scaled with the SDR white |
| HDR badge and info | Player stats show HDR in/out and peak; a small HDR badge on HDR video |
| Setting | Settings → Video → HDR: **Auto** (default) / Off (always SDR output) |
| Extra (optional) | NVIDIA RTX Video HDR: SDR anime shown as HDR (`d3d11vpp` `nvidia-true-hdr`, present in our libmpv) |
| Later | AI upscaler for HDR video (it skips HDR today); separate project |

## Phase 0 results (2026-11-08)

A throwaway WinUI 3 app (Windows App SDK 2.6.5, unpackaged, self-contained) with Haku's own `libmpv-2.dll` drawing into a
`SwapChainPanel`. It now lives in `tools\hdr-spike` (run `make-clips.ps1` first, then `run-spike.ps1`).

| Check | Result |
|---|---|
| 10-bit PQ / BT.2020 output | **Works.** HDR clip reaches the display peak; mid-tones match SDR mode |
| SDR video in PQ mode | Matches today's output (×0.997, ~1% colour difference) with SDR white at 203 nits. Needs scaling to the Windows slider (240 nits here) |
| FP16 scRGB output | 2.54× too dark (mpv 1.0 = 203 nits, scRGB 1.0 = 80). **Use PQ** |
| Glass shader | **Fixed in today's Haku too** (it never loaded under the libmpv render API). Works in PQ (`gpq=1`), within ~3% of SDR. Moving it costs ~0.5 ms a frame |
| Resize | Drawn in the layout pass: 2–3 ms behind the layout |
| Startup | First video frame in ~388 ms (a bare WPF window: ~345 ms) |
| Size | +90–100 MB (the 164 MB runtime package isn't needed) |
| No focus stealing | `AppWindow.Show(false)` keeps the foreground window |
| UI automation | Finds and presses buttons by name |
| AI upscaler, RTX Video | Both run in PQ mode |
| XAML over video | Works |
| **HDR on screen** | **Not checked yet.** HDR is on for DISPLAY1 (peak 417, SDR white 240). `hdr-screen.ps1` + `screen-analyze.py` capture what Windows actually shows (FP16 Desktop Duplication). Needs WoW closed; opens on the main monitor |

## Key decisions

1. **WinUI 3, unpackaged, self-contained** (Windows App SDK 2.6.x, .NET 9). Runs from a folder like today: no installer,
   no Store, nothing to install system-wide.
2. **A new project next to the old one, on a `winui3` branch.** `HakuWinUI\HakuWinUI.csproj` builds `HakuPlayer.exe`. The
   WPF project stays buildable on `main` until the switch-over, so your Debug/Release copies keep coming from WPF until then.
3. **Shared code is linked, not moved.** The WinUI project includes the UI-free files from where they are
   (`<Compile Include="..\Library\Info.cs" />` and so on). No big folder reshuffle, and both apps get the same fixes.
4. **One video core, two screens.** `MpvVideo` is split into the mpv/Direct3D part (shared) and a small "where the frame goes" part:
   `D3DImage` for WPF, `SwapChainPanel` for WinUI. That's done in phase 1, in WPF, so the risky part is shared and already tested.
5. **PQ for HDR, sRGB 8-bit otherwise.** Never FP16. When the format changes, the swap chain is recreated with
   `SetSwapChain(null)` first (microsoft-ui-xaml #2761, #2933).
6. **Same data folder, same settings.** `%LOCALAPPDATA%\Haku Player` and `settings.json` are unchanged. You can switch back and forth.
7. **The look stays.** Glass, colours, fonts and animations are ported to match. Changing the design isn't part of this.

## How HDR output works

```
display info (DXGI Output6 + DisplayConfig)        mpv (libmpv, D3D11 render API)                 WinUI
  HDR on?  peak nits?  SDR white nits?   ──►   target-trc=pq  target-prim=bt.2020        ──►  swap chain R10G10B10A2
                                               target-peak=<peak>                            colour space G2084_P2020
                                               glass.glsl: gpq=1, gwhite=<sdr white / 203>   SwapChainPanel (+ XAML on top)
```

- **SDR white.** mpv's renderer hard-wires SDR white at 203 nits (`MP_REF_WHITE`; there's no option for it outside gpu-next).
  Windows shows SDR content (and Haku's own UI) at the slider level, 240 nits here. So `glass.glsl` gets one more param,
  `gwhite` = slider ÷ 203. Its OUTPUT pass scales SDR videos' light by that, in PQ. HDR videos keep their absolute nits.
  The pass also runs with no glass showing, while gwhite ≠ 1.
- **Display changes.** Recheck on window move, `WM_DISPLAYCHANGE`, and the display's advanced-colour change
  (`DisplayInformation.AdvancedColorInfoChanged`). On a change: new swap chain, new mpv target options (set as properties, no restart).
- **Glass frost for pop-ups** (`GlassFrost`) keeps grabbing the screen through GDI. In HDR that grab is an SDR picture, which is fine for a blurred backdrop.

## Phases

Each phase ends with the relevant tests passing. Phases 1 and 7a improve today's WPF app too.

### Before starting
- Commit the pending work on `main` (the speed-ups, the glass fix, `tools\hdr-spike`). Then branch `winui3`.
- Optional: run the HDR-on-screen check (`tools\hdr-spike\hdr-screen.ps1`) when WoW is closed. It confirms the SDR-white numbers above.

### 1. Prepare in WPF (on `main`, ships as a normal update)
- **Split `MpvVideo`** into `MpvCore` (mpv handle, render context, D3D11 device, draw call, resize) and `IVideoTarget`
  (gives it a texture each frame; `D3DImageTarget` today). Same behaviour, same tests.
- **`DisplayInfo`**: HDR state, peak and SDR white for a window's monitor (the spike's code, tidied). WPF uses it for one thing:
  a note in the player stats ("HDR video shown as SDR: HDR output needs the new version").
- **Keep image building in one place.** Covers, thumbnails and the cover cache return bytes or decoded pixels from background
  threads, and one helper turns them into images on the UI thread. In WinUI, images can only be made on the UI thread, so the
  startup gains (cover cache, early scan) carry over.
- **Take WPF out of the UI-free files**: `Watch`, `Sync`, `Stats` (data part), `Thumbs` (cache part), `Cover` (lookup part).
  Their UI bits move to `*Ui.cs` files, as `WatchUi.cs` already does.

### 2. WinUI shell and player
- New project, App, a main window with the custom title bar (`AppWindow`, `ExtendsContentIntoTitleBar`, caption buttons),
  `HAKU_TEST_WINDOW` and quiet show.
- `SwapChainTarget` for `MpvCore`, drawing during the layout pass on resize, PQ/sRGB by display.
- **Player controls** (`PlayerControls.xaml`): pill, top bar, timeline with thumbnails, volume, menus, AI button,
  segments (skip intro), the Up next / Skip card, keyboard forwarding, fullscreen.
- Opening a file from the command line and drag-and-drop, so the player can be tried on its own.

### 3. Library
- Home (tiles, continue row, drift background), library grid with filter chips, show page with episodes, card menus,
  fix match, rename, stats page.
- Cover loading per the phase 1 helper; measure startup against today's ~650 ms.

### 4. Glass, settings, the rest of the UI
- `GlassPanel` as a WinUI style (frost grab, tint, sheen, rim); `GlassSurface` over the video (tint, rims, shadow; the blur stays in mpv).
- Tooltips, toasts, the splash, Settings (every tab), Discord status UI, theme accent (live colour changes by updating shared brushes).

### 5. Window features
- Pop-out (always on top, shaped to the video, remembered spot), window zoom transitions, maximize/restore, placement,
  single instance, Windows media keys and the volume mixer name.

### 6. Tests and switch-over
- Port `tests\app-tests.ps1` (most of it drives mpv's pipe and finds controls by name, so mainly the window-finding changes)
  and `native-tests.ps1`.
- Walk a feature checklist against the WPF app, side by side.
- Merge `winui3` into `main`. Your Debug/Release copies switch to the WinUI build, and the WPF project is archived (kept in history).

### 7. HDR features
- a. `gwhite` in `glass.glsl` and the HDR **Auto / Off** setting.
- b. HDR badge, player stats lines (input: PQ/HLG, peak; output: PQ 417 nits / SDR).
- c. Subtitle brightness checked in PQ; scaled if needed.
- d. Optional: RTX Video HDR toggle (SDR → HDR) for NVIDIA cards, next to the AI upscaling setting.
- e. Later, separate plan: the AI upscaler for HDR video (10-bit input to haku_sr).

## File-by-file

How much each file changes. **Shared** = linked into both apps unchanged. **Port** = rewritten for WinUI (mostly namespaces,
then the API differences listed under the table).

| File | Lines | Change |
|---|---|---|
| Library/Names, Crawl, Info · Settings/Settings · Player/Segments, Segments.Ui, MpvJson · Discord/Discord, DiscordUi · Upscaling/Upscaler | ~1,900 | **Shared**, unchanged |
| Library/Watch, Sync, Stats · Player/Thumbs, Cover | ~830 | Phase 1: split data from UI, then the data part is **shared** |
| Player/MpvVideo | 271 | Phase 1: `MpvCore` (shared) + `D3DImageTarget`; phase 2: `SwapChainTarget` |
| Upscaling/UpscalePlayer | 350 | Shared after removing its 3 WPF calls (screen height, dispatcher) |
| MainWindow.xaml + .cs | 2,190 | **Port**: biggest piece (pages, player hosting, mpv pipe, window) |
| Player/PlayerControls.xaml + .cs | 1,235 | **Port** (animations, glass hand-off, timeline) |
| Settings/GlobalSettings | 653 | **Port** (UI built in code: mostly namespaces) |
| App.xaml (styles) | 406 | **Port**: 20 keyed styles, 33 triggers → visual states |
| Library/HomeTiles, ContinueRow, Library, CardMenu, FixMatch, WatchUi | ~1,085 | **Port** (UI in code) |
| Glass/GlassSurface, GlassFrost, GlassHandoff, Theme, Drift | ~490 | **Port** (blur/shadow effects → Composition) |
| Player/PlayerSegments, PopOut, DropPlay | ~330 | **Port** (small) |

API swaps that recur:

| WPF | WinUI 3 |
|---|---|
| `Storyboard` / `BeginAnimation` (~200) | Storyboards (different target syntax), or Composition implicit animations for fades |
| Style triggers (33) | `VisualStateManager` |
| `DynamicResource` | update the shared brush's `Color` in place |
| `DropShadowEffect` (28), `BlurEffect` (12) | `ThemeShadow` / Composition `DropShadow`, Composition `GaussianBlurEffect` |
| `VisualBrush` (6), adorners (3) | `CreateVisualSurface` / `RenderTargetBitmap`; an overlay `Canvas` |
| `Dispatcher` + priorities | `DispatcherQueue`; layout timing via `LayoutUpdated` / `CompositionTarget.Rendering` |
| `WindowChrome`, window messages | `AppWindow`, `OverlappedPresenter`, `FullScreenPresenter`; a subclass proc where still needed |
| `Freeze()`d bitmaps from worker threads | bytes from workers, images made on the UI thread |

## Tests

- **Every phase**: the existing tests that cover what moved (per the "run only what's relevant" rule), on the second monitor.
- **New, phase 2**: `hdr-output`. On an SDR display: the swap chain is sRGB and mpv's `target-trc` is auto. On an HDR display:
  PQ, `target-peak` = the display's peak.
- **New, phase 7**: `hdr-screen`. On the HDR monitor (only when you say so: it opens there), a Desktop Duplication capture
  checks that HDR goes above SDR white and SDR white matches the slider within 3%. It's the spike's
  `hdr-screen.ps1`/`screen-analyze.py`, moved into the tests.
- **Startup**: measured each phase against today's ~650 ms to the home page.
- **Switch-over checklist**: every feature, both apps side by side.

## Risks

| Risk | Plan |
|---|---|
| Startup slower | Measured in phases 2 and 3. The cover cache and early scan carry over. Images are made on the UI thread in batches |
| SwapChainPanel bugs (#5888 wrong position at some DPI scales, #5021 thin edges) | Test at 100/125/150% in phase 2; known workarounds (matrix transform, pixel snapping) |
| Glass looks different | Side-by-side screenshots of every glass surface in phase 4 |
| SDR white off in some setups | `hdr-screen` test, and the HDR "Off" setting as an escape hatch |
| Pop-out lag over games | Not a goal; checked in phase 5 so it doesn't get worse |
| Long gap with two apps | The WPF app keeps shipping from `main`; the fixes in shared files reach both |

## Effort

Rough, in working sessions like today's:

| Phase | Sessions |
|---|---|
| 1. Prepare in WPF | 1–2 |
| 2. Shell and player | 2–3 |
| 3. Library | 2–3 |
| 4. Glass, settings, UI | 2–3 |
| 5. Window features | 1–2 |
| 6. Tests and switch-over | 1–2 |
| 7. HDR features | 1–2 |
| **Total** | **~10–17** |
