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
// A worker thread upscales ahead of playback into a queue of finished frames (frames=, capped by vram= MB), so a slow
// frame or a busy GPU moment doesn't stall the video, and pausing or the start-up wait fills the queue. Haku reads how
// full it is from vf-metadata/<label>/frames and /until (the last ready frame's time), for the timeline's green bar.
//
//   --vf=@hakusr:hakusr=lib=C\:/path/haku_sr.dll:engine=C\:/path/model.engine:frames=24:vram=1600
//   (no engine: plain bilinear 2x, for testing the plumbing)
//   vf-command hakusr skip_until <seconds>: a seek is heading there (see the worker)

#include <windows.h>
#include <d3d11.h>

#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libplacebo/colorspace.h>

#include "common/common.h"
#include "common/tags.h"
#include "filters/filter.h"
#include "filters/filter_internal.h"
#include "filters/user_filters.h"
#include "options/m_option.h"
#include "osdep/io.h"
#include "osdep/threads.h"
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

#define MAX_AHEAD 120

struct opts {
    char *lib;
    char *engine;
    int frames;   // upscaled frames to keep ready ahead of playback
    int vram;     // ...but at most this many MB of them
};

struct priv {
    struct opts *opts;
    AVBufferRef *av_device_ref;
    AVBufferRef *hw_pool;
    ID3D11Device *dev;

    HMODULE lib;
    hsr_str_fn last_error;
    hsr_create_fn create;
    hsr_output_size_fn output_size;
    hsr_process_fn process;
    hsr_destroy_fn destroy;

    // Used by the worker while it upscales; the player thread only touches these while the worker is idle and nothing
    // is queued (reinit), or after the worker has stopped.
    hsr *sr;
    hsr *fast;            // plain bilinear 2x for frames mpv throws away during a seek (see skip_until)
    bool usable;          // this stream can be upscaled (format, colour, sizes all fine)
    bool failed;          // gave up on this stream: pass frames through
    struct mp_image_params in_params;   // the stream's params as they arrive (to spot format changes)
    struct mp_image_params params, out_params;
    hsr_yuv2rgb csp;

    // Shared between the player thread and the worker, under lock.
    mp_mutex lock;
    mp_cond cond;
    mp_thread thread;
    bool thread_ok, quit;
    struct mp_image *todo;              // the next decoder frame for the worker (one: decoder frames are scarce)
    bool busy;                          // the worker is upscaling one
    struct mp_image *done[MAX_AHEAD];   // upscaled, in order, waiting for playback
    int num_done;
    int cap;                            // how many to keep ahead (from frames= and vram=, once the output size is known)
    int gen;                            // bumped by a seek: frames from before it are thrown away
    double skip_until;                  // a seek is heading here: frames before it are only decoded to get there
    int skipped;

    // Player thread only.
    struct mp_image *pending_fmt;       // first frame of a new format: waits until the queue has drained
    bool have_format;
    bool eof;                           // input ended: send EOF once everything queued is out
};

static void stop_sr(struct priv *p)
{
    if (p->sr && p->destroy)
        p->destroy(p->sr);
    if (p->fast && p->destroy)
        p->destroy(p->fast);
    p->sr = p->fast = NULL;
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

// New stream format: decide whether we can upscale it, and with what colour conversion. Worker idle.
static void reinit(struct mp_filter *vf, struct mp_image *fmt)
{
    struct priv *p = vf->priv;
    stop_sr(p);
    av_buffer_unref(&p->hw_pool);
    p->failed = false;
    p->usable = false;
    p->in_params = fmt->params;
    p->have_format = true;
    p->params = fmt->params;
    mp_image_params_guess_csp(&p->params);
    mp_mutex_lock(&p->lock);
    p->cap = 2; // until the output size is known
    mp_mutex_unlock(&p->lock);

    if (fmt->imgfmt != IMGFMT_D3D11) {
        MP_WARN(vf, "AI upscaling needs D3D11 hardware frames (got %s), passing through.\n", mp_imgfmt_to_name(fmt->imgfmt));
        return;
    }
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

// Start the upscaler on the first frame of a stream (it needs the device and the frame size). Worker thread.
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

    // how many to keep ready: frames=, but no more than vram= MB of RGBA16F frames (8 bytes a pixel)
    double frame_mb = (double)ow * oh * 8 / (1024.0 * 1024.0);
    int cap = MPMIN(p->opts->frames, (int)(p->opts->vram / frame_mb));
    cap = MPCLAMP(cap, 2, MAX_AHEAD);
    mp_mutex_lock(&p->lock);
    p->cap = cap;
    mp_mutex_unlock(&p->lock);
    MP_INFO(vf, "AI upscaling %dx%d -> %dx%d, up to %d frames ahead (%.0f MB).\n", p->params.w, p->params.h, ow, oh,
            cap, cap * frame_mb);
    return true;
}

// Output frames: single RGBA16F textures the upscaler can write with a compute shader. Worker thread.
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

// One frame through the AI (or the cheap bilinear 2x when fast). NULL: pass it through. Worker thread.
static struct mp_image *render(struct mp_filter *vf, struct mp_image *in, bool fast)
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
    hsr *use = p->sr;
    if (fast) {
        if (!p->fast)
            p->fast = p->create(p->dev, NULL, p->params.w, p->params.h);
        if (p->fast)
            use = p->fast;
    }
    if (p->process(use, (ID3D11Texture2D *)in->planes[0], (int)(intptr_t)in->planes[1], &p->csp,
                   (ID3D11Texture2D *)out->planes[0]) < 0)
    {
        talloc_free(out);
        give_up(vf, "upscaling a frame failed");
        return NULL;
    }
    return out;
}

static MP_THREAD_VOID worker(void *arg)
{
    struct mp_filter *vf = arg;
    struct priv *p = vf->priv;
    mp_thread_set_name("hakusr");
    mp_mutex_lock(&p->lock);
    while (!p->quit) {
        if (!p->todo) {
            mp_cond_wait(&p->cond, &p->lock);
            continue;
        }
        struct mp_image *in = p->todo;
        p->todo = NULL;
        p->busy = true;
        int gen = p->gen;
        // during an exact seek mpv decodes from the keyframe and drops the frames before the target after the filters:
        // those get the cheap bilinear 2x (same size and format). 1000: a limit in case the target is never reached.
        bool fast = false;
        if (p->skip_until != MP_NOPTS_VALUE) {
            if (in->pts != MP_NOPTS_VALUE && in->pts < p->skip_until - 0.002 && p->skipped < 1000) {
                fast = true;
                p->skipped++;
            } else {
                MP_VERBOSE(vf, "Seek reached: %d frames skipped the AI.\n", p->skipped);
                p->skip_until = MP_NOPTS_VALUE;
            }
        }
        mp_mutex_unlock(&p->lock);

        struct mp_image *out = NULL;
        if (p->usable && !p->failed && p->lib)
            out = render(vf, in, fast);
        if (!out)
            out = mp_image_new_ref(in); // pass through
        talloc_free(in);

        mp_mutex_lock(&p->lock);
        p->busy = false;
        if (out && gen == p->gen && p->num_done < MAX_AHEAD)
            p->done[p->num_done++] = out;
        else
            talloc_free(out); // a seek happened meanwhile
        mp_cond_broadcast(&p->cond);
        mp_mutex_unlock(&p->lock);
        mp_filter_wakeup(vf);
        mp_mutex_lock(&p->lock);
    }
    mp_mutex_unlock(&p->lock);
    MP_THREAD_RETURN();
}

static void vf_hakusr_process(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    struct mp_pin *in = vf->ppins[0], *out = vf->ppins[1];

    // 1. hand playback the next finished frame
    if (mp_pin_in_needs_data(out)) {
        struct mp_image *img = NULL;
        mp_mutex_lock(&p->lock);
        bool idle = !p->todo && !p->busy;
        if (p->num_done) {
            img = p->done[0];
            memmove(&p->done[0], &p->done[1], (p->num_done - 1) * sizeof(p->done[0]));
            p->num_done--;
        }
        mp_mutex_unlock(&p->lock);
        if (img) {
            mp_pin_in_write(out, MAKE_FRAME(MP_FRAME_VIDEO, img));
            mp_filter_internal_mark_progress(vf);
        } else if (p->eof && idle && !p->pending_fmt) {
            p->eof = false;
            mp_pin_in_write(out, MP_EOF_FRAME);
        }
    }

    mp_mutex_lock(&p->lock);
    int queued = p->num_done + !!p->todo + p->busy;
    // passing through holds the decoder's own frames, which it needs back: don't run ahead then
    int cap = p->usable && !p->failed && p->lib ? p->cap : 1;
    bool room = !p->todo && queued < cap;
    mp_mutex_unlock(&p->lock);

    // 2. a new format starts once everything of the old one is out
    if (p->pending_fmt && queued == 0) {
        struct mp_image *img = p->pending_fmt;
        p->pending_fmt = NULL;
        reinit(vf, img);
        mp_mutex_lock(&p->lock);
        p->todo = img;
        mp_cond_signal(&p->cond);
        mp_mutex_unlock(&p->lock);
        mp_filter_internal_mark_progress(vf);
        return;
    }

    // 3. take the next decoded frame while there's room ahead
    if (p->eof || p->pending_fmt || !room || !mp_pin_out_request_data(in))
        return;
    struct mp_frame frame = mp_pin_out_read(in);
    if (frame.type == MP_FRAME_EOF) {
        p->eof = true;
        mp_filter_internal_mark_progress(vf);
    } else if (frame.type == MP_FRAME_VIDEO) {
        struct mp_image *img = frame.data;
        if (!p->have_format || !mp_image_params_equal(&img->params, &p->in_params)) {
            p->pending_fmt = img;
        } else {
            mp_mutex_lock(&p->lock);
            p->todo = img;
            mp_cond_signal(&p->cond);
            mp_mutex_unlock(&p->lock);
        }
        mp_filter_internal_mark_progress(vf);
    } else {
        MP_ERR(vf, "unexpected frame type\n");
        mp_frame_unref(&frame);
        mp_filter_internal_mark_failed(vf);
    }
}

// Seek or stop: throw away everything queued and wait for the frame being upscaled.
static void flush_frames(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    mp_mutex_lock(&p->lock);
    p->gen++;
    talloc_free(p->todo);
    p->todo = NULL;
    for (int n = 0; n < p->num_done; n++)
        talloc_free(p->done[n]);
    p->num_done = 0;
    while (p->busy)
        mp_cond_wait(&p->cond, &p->lock);
    mp_mutex_unlock(&p->lock);
    talloc_free(p->pending_fmt);
    p->pending_fmt = NULL;
    p->eof = false;
}

// "vf-command hakusr skip_until <seconds>", sent by Haku right before a seek; and vf-metadata/hakusr for the queue.
static bool command(struct mp_filter *vf, struct mp_filter_command *cmd)
{
    struct priv *p = vf->priv;
    if (cmd->type == MP_FILTER_COMMAND_GET_META) {
        struct mp_tags *t = talloc_zero(NULL, struct mp_tags);
        mp_mutex_lock(&p->lock);
        int n = p->num_done;
        double until = n ? p->done[n - 1]->pts : MP_NOPTS_VALUE;
        int cap = p->cap;
        mp_mutex_unlock(&p->lock);
        mp_tags_set_str(t, "frames", mp_tprintf(20, "%d", n));
        mp_tags_set_str(t, "cap", mp_tprintf(20, "%d", cap));
        mp_tags_set_str(t, "until", until == MP_NOPTS_VALUE ? "" : mp_tprintf(40, "%.3f", until));
        *(struct mp_tags **)cmd->res = t;
        return true;
    }
    if (cmd->type != MP_FILTER_COMMAND_TEXT || !cmd->cmd || strcmp(cmd->cmd, "skip_until") != 0 || !cmd->arg)
        return false;
    char *end;
    double t = strtod(cmd->arg, &end);
    if (end == cmd->arg)
        return false;
    mp_mutex_lock(&p->lock);
    p->skip_until = t;
    p->skipped = 0;
    mp_mutex_unlock(&p->lock);
    return true;
}

static void uninit(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    if (p->thread_ok) {
        mp_mutex_lock(&p->lock);
        p->quit = true;
        mp_cond_broadcast(&p->cond);
        mp_mutex_unlock(&p->lock);
        mp_thread_join(p->thread);
    }
    flush_frames(vf);
    stop_sr(p);
    av_buffer_unref(&p->hw_pool);
    av_buffer_unref(&p->av_device_ref);
    if (p->dev)
        ID3D11Device_Release(p->dev);
    mp_cond_destroy(&p->cond);
    mp_mutex_destroy(&p->lock);
    // the DLL stays loaded: TensorRT's runtime doesn't like being unloaded and reloaded in one process
}

static const struct mp_filter_info vf_hakusr_filter = {
    .name = "hakusr",
    .process = vf_hakusr_process,
    .command = command,
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
    p->skip_until = MP_NOPTS_VALUE;
    p->cap = 2;
    mp_mutex_init(&p->lock);
    mp_cond_init(&p->cond);

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

    if (mp_thread_create(&p->thread, worker, f)) {
        MP_ERR(f, "couldn't start the upscaling thread\n");
        goto fail;
    }
    p->thread_ok = true;
    return f;

fail:
    talloc_free(f); // runs uninit
    return NULL;
}

#define OPT_BASE_STRUCT struct opts
static const m_option_t vf_opts_fields[] = {
    {"lib", OPT_STRING(lib)},
    {"engine", OPT_STRING(engine)},
    {"frames", OPT_INT(frames), M_RANGE(1, MAX_AHEAD)},
    {"vram", OPT_INT(vram), M_RANGE(64, 16384)},
    {0}
};

const struct mp_user_filter_entry vf_hakusr = {
    .desc = {
        .description = "Haku Player AI upscaler (D3D11 + TensorRT, via haku_sr.dll)",
        .name = "hakusr",
        .priv_size = sizeof(OPT_BASE_STRUCT),
        .options = vf_opts_fields,
        .priv_defaults = &(const OPT_BASE_STRUCT){
            .frames = 24,
            .vram = 1600,
        },
    },
    .create = vf_hakusr_create,
};
