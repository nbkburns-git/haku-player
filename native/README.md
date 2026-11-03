# Native parts: real-time AI upscaling

Haku Player's AI upscaling has two native pieces. Both are needed, along with the files they load at runtime.

| Piece | What it is | Built with |
|---|---|---|
| `mpv/vf_hakusr.c` | An mpv video filter. It takes D3D11 decoder frames, calls `haku_sr.dll`, and outputs RGBA16F frames at 2x size. If anything is missing it passes frames through unchanged. | Compiled into Haku's own `libmpv-2.dll` (shinchiro's recipe plus `mpv/apply.sh`) |
| `haku_sr/` | `haku_sr.dll`: D3D11 compute shaders around TensorRT 11, with every frame kept on the GPU. Also `hsr_test.exe`, a self-check and benchmark. | MSVC + CMake, CUDA 13.4 headers, TensorRT 11.4 headers |

At runtime everything lives in `%LOCALAPPDATA%\Haku Upscaler`:

```text
haku_sr.dll
nvinfer_11.dll  nvonnxparser_11.dll  nvinfer_plugin_11.dll
nvinfer_builder_resource_{ptx,sm86,sm89,sm120}_11.dll   (engine building for RTX 30/40/50)
models\2x_AnimeJaNai_HD_V3[Sharp1]_{SuperUltraCompact,UltraCompact,Compact}_fp16.onnx
engines\<model>.engine     (built on first use, about 15 s; one per model, any size up to 1920x1088)
```

The models must be **FP16** files. TensorRT 11 is strongly typed, so an FP32 ONNX file runs in FP32, about 2.5x slower. Convert them with `onnxconverter_common.float16.convert_float_to_float16(m, keep_io_types=False)`.

Speeds on an RTX 4070, through the whole pipeline (`hsr_test.exe`):

| Tier | 1920x1080 | 1440x1080 |
|---|---|---|
| Fast (SuperUltraCompact) | 89 fps | 121 fps |
| Balanced (UltraCompact) | 32 fps | 42 fps |
| Quality (Compact) | 17 fps | 23 fps |

## Building haku_sr.dll

The tools sit in `deps/`, which is git-ignored:

- `deps/cuda/` holds the CUDA 13.4.3 redistributables (`cuda_nvcc`, `cuda_cudart`, `cuda_crt`, `libnvvm`, `cccl`, `cuda_profiler_api`) from `developer.download.nvidia.com/compute/cuda/redist/`, each checked against `redistrib_13.4.3.json`.
- `deps/tensorrt/bin` holds the DLLs from the `tensorrt-cu13-libs==11.4.0.106` Windows wheel (pypi.nvidia.com).
- `deps/tensorrt/include` holds the headers from github.com/NVIDIA/TensorRT, tag `v11.4`.

`haku_sr.dll` loads TensorRT at runtime, so it needs no import libraries. Then:

```bat
native\haku_sr\build.cmd
native\haku_sr\out\hsr_test.exe <model_fp16.onnx> <engine folder>
```

## Building libmpv-2.dll with the filter

This uses shinchiro/mpv-winbuild-cmake, **clang** route, x86_64, under WSL2 Ubuntu 24.04 as root. mpv is pinned with `GIT_TAG <commit>` in `packages/mpv.cmake`. Don't run the recipe's `update` step, because it would move mpv to master and drop the filter.

The Ubuntu fixes this needed (2026-11), each researched:

1. **libc++ dev packages.** `apt install libc++-dev libc++abi-dev`. The host LLVM build uses `LLVM_ENABLE_LIBCXX=ON` and otherwise falls back to libstdc++, which can't link with the recipe's `-ftls-model=local-exec`.
2. **nasm 3.02.** Build it from nasm.us into `/usr/local` (Arch has the same version). Ubuntu's 2.16.01 crashes with "stack smashing detected" on x265's `ipfilter8.asm`.
3. **curl 8.22.0.** In `packages/curl.cmake`, add `GIT_REMOTE_NAME origin` and `GIT_RESET curl-8_22_0`. Newer curl assumes an OpenSSL 4.1.0 that has `ASN1_STRING_get_length`, but the recipe pins a July 4.1.0-dev snapshot without it. (shinchiro's CI only stays green because of its build cache.)
4. **davs2 and xvidcore.** `CONFIGURE_COMMAND ${EXEC} "cd <SOURCE_DIR>/build/linux &&" CONF=1 ./configure ...`, with the `cd ... &&` as one quoted argument. That way the `cd` reaches the exec wrapper whether ExternalProject runs the step through a shell or through a log script.
5. **rst2pdf.** `pip3 install --break-system-packages rst2pdf mako jsonschema meson`. mpv's meson needs it for the PDF manual.
6. **The GCC route** currently fails at ngtcp2, including in shinchiro's CI: ld.bfd and static library order. Use clang.

Then:

```sh
bash native/mpv/apply.sh <recipe>/src_packages/mpv
# remove build_x86_64/packages/mpv-prefix/src/mpv-stamp/mpv-{build,install,copy-binary,copy-package-dir,strip-binary,done}
ninja -C build_x86_64 mpv
```

The new DLL lands in `build_x86_64/mpv-dev-x86_64-<date>-git-<hash>/`, or in a folder nested inside it if one with that name already existed. Copy it to `lib/libmpv-2.dll`. The previous shinchiro build is kept as `lib/libmpv-2.shinchiro.dll`.
