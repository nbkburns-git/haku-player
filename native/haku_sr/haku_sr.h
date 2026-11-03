// haku_sr: Haku Player's GPU upscaler. mpv's vf_hakusr filter calls it with D3D11 decoder frames; it converts them to RGB,
// runs a TensorRT super-resolution engine (or a plain bilinear 2x, for testing the plumbing) and writes an RGBA16F frame.
// Everything stays on the GPU: D3D11 compute shaders around TensorRT, with buffers shared through CUDA's D3D11 interop.
#pragma once
#include <stdint.h>

#ifdef HAKU_SR_EXPORTS
#define HSR_API __declspec(dllexport)
#else
#define HSR_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct hsr hsr;
struct ID3D11Device;
struct ID3D11Texture2D;

// Input colour: the decoder frame's YUV -> RGB transform, for values as the shader reads them (UNORM-normalised).
// rgb = m * (y, cb, cr) + c, row-major 3x3 in m[0..8], offsets in c[0..2].
typedef struct hsr_yuv2rgb {
    float m[9];
    float c[3];
} hsr_yuv2rgb;

// Version string ("haku_sr 1 / TensorRT x.y.z"), for logs.
HSR_API const char *hsr_version(void);

// Bumped when Haku needs a newer haku_sr.dll. 2: safe to run on vf_hakusr's own thread (short device lock).
#define HSR_ABI 2
HSR_API int hsr_abi(void);

// Last error on any call, for logs.
HSR_API const char *hsr_last_error(void);

// Start an upscaler for in_w x in_h frames on this device (mpv's device). engine_path: a TensorRT engine built by
// hsr_build_engine (any size up to HSR_MAX_W x HSR_MAX_H), or NULL for the plain bilinear 2x test mode. NULL on failure.
HSR_API hsr *hsr_create(struct ID3D11Device *dev, const wchar_t *engine_path, int in_w, int in_h);

// Output size (in_w/in_h times the model's scale).
HSR_API void hsr_output_size(hsr *s, int *out_w, int *out_h);

// One frame: in_tex/in_slice is the decoder's NV12 or P010 texture array slice; out_tex an RGBA16F texture of the
// output size with D3D11_BIND_UNORDERED_ACCESS. Returns 0 on success.
HSR_API int hsr_process(hsr *s, struct ID3D11Texture2D *in_tex, int in_slice, const hsr_yuv2rgb *csp,
                        struct ID3D11Texture2D *out_tex);

HSR_API void hsr_destroy(hsr *s);

// Average GPU time of the model per frame so far, in ms (profiling).
HSR_API double hsr_model_ms(hsr *s);

// Largest input an engine takes (any size from 64x64 up to this works with one engine).
#define HSR_MAX_W 2560
#define HSR_MAX_H 1728

// Build a TensorRT engine from an ONNX model (NCHW, RGB 0..1), tuned for in_w x in_h and taking any size up to
// HSR_MAX_W x HSR_MAX_H, and save it to engine_path. Slow (seconds): call it from a background thread. Returns 0 on success.
HSR_API int hsr_build_engine(const wchar_t *onnx_path, const wchar_t *engine_path, int in_w, int in_h);

// Speed of an engine on this PC: frames per second for in_w x in_h input (synthetic frames, its own device), or < 0 on
// failure. Takes a few seconds: call it from a background thread.
HSR_API double hsr_benchmark(const wchar_t *engine_path, int in_w, int in_h, int frames);

#ifdef __cplusplus
}
#endif
