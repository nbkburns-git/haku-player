# Real-time AI upscaling: plan

Goal: the best-quality real-time anime upscaling Haku Player can do. A real AI model (AnimeJaNai, AnimeSharpV4 Fast
and the like) runs with TensorRT on every frame, from 1080p to 4K, keeping every frame on the GPU, before subtitles and
the glass shader are drawn.

**Status (2026-11-03): phases 0 to 3 are done, and working in Haku's Debug and Release copies.**
- Haku's own `libmpv-2.dll` is built, with `vf_hakusr` in it (see `native/README.md`).
- `haku_sr.dll` runs AnimeJaNai V3 with TensorRT 11.4. Balanced reaches 32 fps at 1080p to 4K on an RTX 4070.
- Haku chooses a tier per video, builds engines in the background, shows an AI badge, and turns itself off if frames drop.
- UI:
  - its own Settings tab (toggle, tier and look; this PC's estimated speed per tier; prepared status with "Prepare all now"; when it runs; credits);
  - a sparkle button in the player bar (accent while upscaling, with a tooltip explaining what it's doing);
  - a section in the player's quick-settings menu;
  - Ctrl+U to turn it on or off.
- Engines use a size range (one about 1 MB engine per model, any size up to 1920x1088). A fixed size made 2.4 GB engines.
- Still to come: phase 4 (two frames in flight, CUDA graphs, FP8/INT8) and phase 5 (temporal models, HDR, DirectML).

## Why a native filter

- **Speed decides quality.** On an RTX 4070, the best real-time models sit right at the 24 fps line (AnimeJaNai Compact
  is about 17 fps and UltraCompact about 30, at 1080p to 4K). Every millisecond saved lets a better model fit.
- **Copying through system memory wastes that time.** The VapourSynth route (`vf=vapoursynth`) copies each frame to the
  CPU and back. AnimeJaNai used it for years, then moved to a GPU-only native filter in 3.4 for speed.
- **AnimeJaNai's filter isn't public.** It's called `vf_animejanai`, but there's no fork, no source and no libmpv. Its
  users also report that it has no D3D11 path (it runs on NVIDIA's decoder and OpenGL). Haku's player is D3D11, so we
  build our own, designed for D3D11 from the start.
- **mpv filters can't be plugins.** Filters are a compiled-in list (`filters/user_filters.c`), and C plugins can't
  register one. So this needs a small mpv patch and a self-built `libmpv-2.dll`.

## Architecture

```
decoder (d3d11va, NV12/P010 texture)          all on the GPU, on mpv's own D3D11 device (Haku's device)
   │
   ▼
vf_hakusr  (new mpv filter, C, ~300 lines, modelled on vf_d3d11vpp.c)
   │  frame plumbing only: takes IMGFMT_D3D11 frames, allocates 2x output frames, calls into haku_sr.dll
   ▼
haku_sr.dll  (new, C++, MSVC + CUDA runtime + TensorRT 11, loaded with LoadLibrary, small C interface)
   1. copy the decoder's texture slice to our own texture (decoder textures often can't be read by shaders)
   2. D3D11 compute shader: YUV -> RGB FP16, planar (NCHW), with the right matrix, range and chroma siting
   3. buffer shared with CUDA -> TensorRT engine (FP16, strongly typed) -> 2x FP16 planar output
   4. D3D11 compute shader: planar -> R16G16B16A16_FLOAT texture (mpv displays this format directly: RGBAF16)
   ▼
mpv renderer: subtitles drawn at full 4K, Haku's glass shader (OUTPUT hook), final scaling to the window
```

Why split mpv and the DLL:
- The mpv patch stays tiny (frame plumbing only), so moving to a new mpv version means re-applying one small file
  and a two-line registration, not maintaining a fork.
- All the TensorRT and CUDA work lives in our own DLL, built with ordinary Windows tools and independent of the mpv
  build. TensorRT has no C interface, so this part has to be C++.
- If the DLL or the TensorRT runtime is missing, the filter passes frames through unchanged and Haku hides the option.

Facts this design relies on (from mpv master):
- `vf_d3d11vpp.c` shows the pattern: an `mp_refqueue` taking `IMGFMT_D3D11` frames; the device from
  `hwdec_devices_get_by_imgfmt_and_type(..., IMGFMT_D3D11, AV_HWDEVICE_TYPE_D3D11VA)`; the input texture at
  `planes[0]` with its array slice at `planes[1]`; output frames from `mp_update_av_hw_frames_pool` +
  `av_hwframe_get_buffer`.
- The D3D11 renderer (`hwdec_d3d11va.c`) can display `RGBAF16` (`R16G16B16A16_FLOAT`), `X2BGR10`, `BGRA` and the YUV
  formats, so FP16 model output can be shown without losing precision.
- Haku already creates its D3D11 device with video support and multithread protection (`MpvVideo.cs`). The filter
  runs on mpv's thread on that same device.

## Haku side

- Settings → Playback → **Upscaling**: Off · AI (model picker) · RTX Video. The RTX Video option comes almost for
  free from the existing `d3d11vpp` filter.
- AI upscaling only switches on when the video is smaller than the picture on screen (for example 1080p in a 4K
  window). 4K files, like your own upscales, play untouched.
- Hardware decoding is forced to `d3d11va` (frames stay on the GPU), and the filter is added as
  `vf add @haku-sr:hakusr=model=...`.
- **First use of a model:** building its TensorRT engine takes a few minutes. It builds in the background while the
  video plays normally, with a glass toast showing progress, and switches on when ready. Engines are cached in
  `%LOCALAPPDATA%\Haku Player\engines`, keyed by GPU, TensorRT version, model and resolution.
- **Safety net:** if the filter can't keep up (frames dropping), Haku says so and falls back to a lighter model or
  turns upscaling off for that video.
- Models live in `%LOCALAPPDATA%\Haku Player\models`, and any 2x ONNX model can be added. The default is picked from
  the benchmark results.

## Phases

0. **Tools, and a self-built libmpv identical to today's.** Rebuild the current `libmpv-2.dll` from source with no
   changes, and check Haku runs exactly the same on it. This proves the build before anything else is touched.
   - Preferred: shinchiro's `mpv-winbuild-cmake` under WSL2. It's the same recipe as the DLL Haku uses now
     (one self-contained file with the same features: VapourSynth, d3d11vpp, libplacebo).
   - Fallback: an MSYS2 clang64 native build (easier to set up, but it ships about 30 extra DLLs).
1. **Plumbing with no AI.** `vf_hakusr` does a plain 2x upscale with a compute shader. This checks frames, formats,
   colours (screenshots compared with the filter off), seeking, pause, 10-bit, subtitles and the glass shader, all
   without any AI involved.
2. **TensorRT.** `haku_sr.dll` loads an ONNX model, builds or loads its engine and upscales. Measure fps in the player
   with AnimeJaNai V3 UltraCompact first.
3. **Haku integration.** The setting, the model picker, auto on/off, engine-build progress, the fallback and RTX Video.
4. **Speed, to fit the best model.**
   - CUDA graphs, and D3D11 ↔ CUDA fences instead of blocking map/unmap.
   - Two frames in flight on two TensorRT contexts (this nearly doubled Real-CUGAN's speed on a 4090).
   - Then try FP8 or INT8 on Ada.
   - Target: AnimeJaNai Compact or AnimeSharpV4 Fast at 24 fps or more.
5. **Extras, later.**
   - Temporal models like TSPAN: the filter keeps a few frames of history, which adds a frame or two of latency.
   - HDR sources (models are trained on SDR).
   - A DirectML backend for AMD and Intel cards.

## What has to be installed (each one on your OK)

| Tool | For | Size (approx.) |
|---|---|---|
| WSL2 + Ubuntu | building libmpv with shinchiro's recipe | ~2 GB, plus ~20 GB for the build |
| Visual Studio 2022 Build Tools (C++) | `haku_sr.dll` | ~6 GB |
| CMake | building `haku_sr.dll` | ~100 MB |
| CUDA Toolkit 13 | CUDA runtime headers and libraries | ~3 GB |
| TensorRT 11 SDK (Windows zip) | headers, libraries and runtime DLLs | ~2 GB |

Nothing touches Vapourkit. It has its own TensorRT copy, which we only read for benchmarks.

## Shipping it

- The TensorRT runtime DLLs are large (about 1 GB across all GPU generations, much less for RTX 30 and 40 cards only)
  and over GitHub's file limit. Like libmpv today, they become a separate optional download: an "AI upscaling pack"
  with the DLLs and the default models. Their license permits shipping them with an app; check its terms at release.
- **Licenses:**
  - The mpv patch is under mpv's license (the shinchiro build is GPL).
  - `haku_sr.dll` is our own code, loaded at runtime.
  - AnimeJaNai and AnimeSharpV4 models are CC-BY-NC-SA-4.0: fine for a free app, with credit, but they can't be
    used commercially.

## Risks

- **Building mpv for the first time** is the riskiest step, which is why phase 0 does it with no changes.
- **The 4070 is at the edge for the best models.** If Compact can't reach 24 fps even after phase 4, the best
  real-time choice is UltraCompact / V3 Sharp1, or AnimeSharpV4 Fast if the benchmark shows it fits.
- **One D3D11 device is shared** by the decoder, our filter and Haku's renderer on the UI thread. It's already
  multithread-protected; heavy filter work will run on CUDA's own stream so the UI isn't blocked.
- **Seeking and size changes** have to flush in-flight frames cleanly (the filter's `reset`).

## Open items

- The 4070 benchmark of all candidate models (queued; it runs once the GPU is free). That picks the default model
  and decides how much phase 4 matters.
- Side-by-side quality on your own shows, done offline in Vapourkit: V3 Sharp1, AnimeSharpV4 Fast, Compact and
  TSPAN against your Real-CUGAN look.
