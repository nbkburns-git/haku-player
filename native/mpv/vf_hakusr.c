/*
 * This file is part of mpv.
 *
 * mpv is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * mpv is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with mpv.  If not, see <http://www.gnu.org/licenses/>.
 */

// vf_hakusr: Haku Player's AI upscaler. Takes D3D11 decoder frames (NV12/P010), hands them to haku_sr.dll (loaded at
// runtime: D3D11 compute + TensorRT, all on the GPU) and outputs RGBA16F frames at the model's size, before mpv's
// renderer draws subtitles, the OSD and shaders on top. If the DLL, the engine or the format isn't usable, frames pass
// through untouched, so playback never breaks.
//
//   --vf=hakusr=lib=C\:/path/haku_sr.dll:engine=C\:/path/model.1920x1080.engine
//   (no engine: plain bilinear 2x, for testing the plumbing)

#include <windows.h>
#include <d3d11.h>

#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libplacebo/colorspace.h>

#include "common/common.h"
#include "filters/filter.h"
#include "filters/filter_internal.h"
#include "filters/user_filters.h"
#include "options/m_option.h"
#include "osdep/io.h"
#include "refqueue.h"
#include "video/hwdec.h"
#include "video/mp_image.h"
#include "video/mp_image_pool.h"

// haku_sr.dll's C interface (native/haku_sr/haku_sr.h)
typedef struct hsr hsr;
typedef struct hsr_yuv2rgb { float m[9]; float c[3]; } hsr_yuv2rgb;
typedef const char *(*hsr_str_fn)(void);
typedef hsr *(*hsr_create_fn)(ID3D11Device *, const wchar_t *, int, int);
typedef void (*hsr_output_size_fn)(hsr *, int *, int *);
typedef int (*hsr_process_fn)(hsr *, ID3D11Texture2D *, int, const hsr_yuv2rgb *, ID3D11Texture2D *);
typedef void (*hsr_destroy_fn)(hsr *);

struct opts {
    char *lib;
    char *engine;
};

struct priv {
    struct opts *opts;
    struct mp_refqueue *queue;
    AVBufferRef *av_device_ref;
    AVBufferRef *hw_pool;
    ID3D11Device *dev;

    HMODULE lib;
    hsr_str_fn last_error;
    hsr_create_fn create;
    hsr_output_size_fn output_size;
    hsr_process_fn process;
    hsr_destroy_fn destroy;

    hsr *sr;
    bool usable;          // this stream can be upscaled (format, colour, sizes all fine)
    bool failed;          // gave up on this stream: pass frames through
    struct mp_image_params params, out_params;
    hsr_yuv2rgb csp;
};

static void stop_sr(struct priv *p)
{
    if (p->sr && p->destroy)
        p->destroy(p->sr);
    p->sr = NULL;
}

static void give_up(struct mp_filter *vf, const char *why)
{
    struct priv *p = vf->priv;
    const char *detail = p->last_error ? p->last_error() : "";
    MP_ERR(vf, "AI upscaling off for this video: %s%s%s\n", why, detail && detail[0] ? ": " : "", detail ? detail : "");
    p->failed = true;
    stop_sr(p);
}

static bool load_lib(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    const char *path = p->opts->lib && p->opts->lib[0] ? p->opts->lib : "haku_sr.dll";
    wchar_t *wpath = mp_from_utf8(NULL, path);
    p->lib = LoadLibraryExW(wpath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    talloc_free(wpath);
    if (!p->lib) {
        MP_ERR(vf, "Could not load %s.\n", path);
        return false;
    }
    hsr_str_fn version = (hsr_str_fn)GetProcAddress(p->lib, "hsr_version");
    p->last_error = (hsr_str_fn)GetProcAddress(p->lib, "hsr_last_error");
    p->create = (hsr_create_fn)GetProcAddress(p->lib, "hsr_create");
    p->output_size = (hsr_output_size_fn)GetProcAddress(p->lib, "hsr_output_size");
    p->process = (hsr_process_fn)GetProcAddress(p->lib, "hsr_process");
    p->destroy = (hsr_destroy_fn)GetProcAddress(p->lib, "hsr_destroy");
    if (!version || !p->last_error || !p->create || !p->output_size || !p->process || !p->destroy) {
        MP_ERR(vf, "%s is missing functions.\n", path);
        return false;
    }
    MP_VERBOSE(vf, "Loaded %s.\n", version());
    return true;
}

// New stream format: decide whether we can upscale it, and with what colour conversion.
static void reinit(struct mp_filter *vf, struct mp_image *fmt)
{
    struct priv *p = vf->priv;
    stop_sr(p);
    av_buffer_unref(&p->hw_pool);
    p->failed = false;
    p->usable = false;
    p->params = fmt->params;
    mp_image_params_guess_csp(&p->params);

    int sub = p->params.hw_subfmt;
    if (sub != IMGFMT_NV12 && sub != IMGFMT_P010) {
        MP_WARN(vf, "AI upscaling needs NV12 or P010 hardware frames (got %s), passing through.\n", mp_imgfmt_to_name(sub));
        return;
    }
    if (pl_color_space_is_hdr(&p->params.color)) {
        MP_WARN(vf, "AI upscaling is SDR-only for now, passing HDR video through.\n");
        return;
    }

    struct pl_color_repr repr = p->params.repr;
    // P010 keeps its 10 bits at the top of each 16-bit value, so it reads like 16-bit (limited range maps exactly)
    int depth = sub == IMGFMT_P010 ? 16 : 8;
    repr.bits = (struct pl_bit_encoding){.sample_depth = depth, .color_depth = depth};
    pl_transform3x3 t = pl_color_repr_decode(&repr, NULL);
    for (int r = 0; r < 3; r++) {
        for (int c = 0; c < 3; c++)
            p->csp.m[r * 3 + c] = t.mat.m[r][c];
        p->csp.c[r] = t.c[r];
    }
    p->usable = true;
}

// Start the upscaler on the first frame of a stream (it needs the device and the frame size).
static bool start_sr(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    wchar_t *engine = p->opts->engine && p->opts->engine[0] ? mp_from_utf8(NULL, p->opts->engine) : NULL;
    p->sr = p->create(p->dev, engine, p->params.w, p->params.h);
    talloc_free(engine);
    if (!p->sr) {
        give_up(vf, "couldn't start the upscaler");
        return false;
    }
    int ow, oh;
    p->output_size(p->sr, &ow, &oh);
    p->out_params = p->params;
    p->out_params.w = ow;
    p->out_params.h = oh;
    double sx = (double)ow / p->params.w, sy = (double)oh / p->params.h;
    p->out_params.crop.x0 = lrint(sx * p->params.crop.x0);
    p->out_params.crop.x1 = lrint(sx * p->params.crop.x1);
    p->out_params.crop.y0 = lrint(sy * p->params.crop.y0);
    p->out_params.crop.y1 = lrint(sy * p->params.crop.y1);
    p->out_params.hw_subfmt = IMGFMT_RGBAF16;
    p->out_params.repr = pl_color_repr_rgb;   // full-range RGB; primaries and transfer stay the video's
    p->out_params.chroma_location = PL_CHROMA_UNKNOWN;
    mp_image_params_guess_csp(&p->out_params);
    MP_INFO(vf, "AI upscaling %dx%d -> %dx%d.\n", p->params.w, p->params.h, ow, oh);
    return true;
}

// Output frames: single RGBA16F textures the upscaler can write with a compute shader.
static struct mp_image *alloc_out(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    if (!p->hw_pool) {
        p->hw_pool = av_hwframe_ctx_alloc(p->av_device_ref);
        if (!p->hw_pool)
            return NULL;
        AVHWFramesContext *fc = (void *)p->hw_pool->data;
        fc->format = AV_PIX_FMT_D3D11;
        fc->sw_format = AV_PIX_FMT_RGBAF16;
        fc->width = p->out_params.w;
        fc->height = p->out_params.h;
        fc->initial_pool_size = 0; // one texture per frame, not a decoder-style array
        AVD3D11VAFramesContext *dfc = fc->hwctx;
        dfc->BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        if (av_hwframe_ctx_init(p->hw_pool) < 0) {
            av_buffer_unref(&p->hw_pool);
            return NULL;
        }
    }
    AVFrame *av_frame = av_frame_alloc();
    MP_HANDLE_OOM(av_frame);
    struct mp_image *img = NULL;
    if (av_hwframe_get_buffer(p->hw_pool, av_frame, 0) >= 0)
        img = mp_image_from_av_frame(av_frame);
    av_frame_free(&av_frame);
    return img;
}

static struct mp_image *render(struct mp_filter *vf, struct mp_image *in)
{
    struct priv *p = vf->priv;
    if (!p->sr && !start_sr(vf))
        return NULL;
    struct mp_image *out = alloc_out(vf);
    if (!out) {
        give_up(vf, "couldn't allocate output frames");
        return NULL;
    }
    mp_image_copy_attributes(out, in);
    out->params = p->out_params;
    if (p->process(p->sr, (ID3D11Texture2D *)in->planes[0], (int)(intptr_t)in->planes[1], &p->csp,
                   (ID3D11Texture2D *)out->planes[0]) < 0)
    {
        talloc_free(out);
        give_up(vf, "upscaling a frame failed");
        return NULL;
    }
    return out;
}

static void vf_hakusr_process(struct mp_filter *vf)
{
    struct priv *p = vf->priv;

    struct mp_image *in_fmt = mp_refqueue_execute_reinit(p->queue);
    if (in_fmt)
        reinit(vf, in_fmt);

    if (!mp_refqueue_can_output(p->queue))
        return;

    struct mp_image *in = mp_refqueue_get(p->queue, 0);
    struct mp_image *out = NULL;
    if (p->usable && !p->failed && p->lib)
        out = render(vf, in);
    if (!out)
        out = mp_image_new_ref(in); // pass through
    if (!out) {
        mp_filter_internal_mark_failed(vf);
        return;
    }
    mp_refqueue_write_out_pin(p->queue, out);
}

static void flush_frames(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    mp_refqueue_flush(p->queue);
}

static void uninit(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    stop_sr(p);
    flush_frames(vf);
    talloc_free(p->queue);
    av_buffer_unref(&p->hw_pool);
    av_buffer_unref(&p->av_device_ref);
    if (p->dev)
        ID3D11Device_Release(p->dev);
    // the DLL stays loaded: TensorRT's runtime doesn't like being unloaded and reloaded in one process
}

static const struct mp_filter_info vf_hakusr_filter = {
    .name = "hakusr",
    .process = vf_hakusr_process,
    .reset = flush_frames,
    .destroy = uninit,
    .priv_size = sizeof(struct priv),
};

static struct mp_filter *vf_hakusr_create(struct mp_filter *parent, void *options)
{
    struct mp_filter *f = mp_filter_create(parent, &vf_hakusr_filter);
    if (!f) {
        talloc_free(options);
        return NULL;
    }
    mp_filter_add_pin(f, MP_PIN_IN, "in");
    mp_filter_add_pin(f, MP_PIN_OUT, "out");

    struct priv *p = f->priv;
    p->opts = talloc_steal(p, options);
    p->queue = mp_refqueue_alloc(f);

    struct mp_stream_info *info = mp_filter_find_stream_info(f);
    if (!info || !info->hwdec_devs)
        goto fail;
    struct hwdec_imgfmt_request params = {.imgfmt = IMGFMT_D3D11, .probing = false};
    hwdec_devices_request_for_img_fmt(info->hwdec_devs, &params);
    struct mp_hwdec_ctx *hwctx =
        hwdec_devices_get_by_imgfmt_and_type(info->hwdec_devs, IMGFMT_D3D11, AV_HWDEVICE_TYPE_D3D11VA);
    if (!hwctx || !hwctx->av_device_ref) {
        MP_ERR(f, "AI upscaling needs D3D11 hardware decoding (--hwdec=d3d11va).\n");
        goto fail;
    }
    p->av_device_ref = av_buffer_ref(hwctx->av_device_ref);
    AVD3D11VADeviceContext *d3dctx = ((AVHWDeviceContext *)p->av_device_ref->data)->hwctx;
    p->dev = d3dctx->device;
    ID3D11Device_AddRef(p->dev);

    if (!load_lib(f))
        p->failed = true; // still a working (pass-through) filter

    mp_refqueue_add_in_format(p->queue, IMGFMT_D3D11, 0);
    mp_refqueue_set_refs(p->queue, 0, 0);
    mp_refqueue_set_mode(p->queue, 0);
    return f;

fail:
    talloc_free(f);
    return NULL;
}

#define OPT_BASE_STRUCT struct opts
static const m_option_t vf_opts_fields[] = {
    {"lib", OPT_STRING(lib)},
    {"engine", OPT_STRING(engine)},
    {0}
};

const struct mp_user_filter_entry vf_hakusr = {
    .desc = {
        .description = "Haku Player AI upscaler (D3D11 + TensorRT, via haku_sr.dll)",
        .name = "hakusr",
        .priv_size = sizeof(OPT_BASE_STRUCT),
        .options = vf_opts_fields,
    },
    .create = vf_hakusr_create,
};
