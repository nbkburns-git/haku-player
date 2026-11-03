// haku_sr: see haku_sr.h.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define HAKU_SR_EXPORTS
#include <windows.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi.h>

#include <cuda_runtime_api.h>
#include <cuda_d3d11_interop.h>
#include <NvInfer.h>
#include <NvOnnxParser.h>

#include <cstdio>
#include <cstring>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "haku_sr.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "dxgi.lib")

#define HSR_VERSION 1

// ---------- errors ----------

static std::mutex g_err_lock;
static std::string g_err;

static void set_err(const char *fmt, ...)
{
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    std::lock_guard<std::mutex> l(g_err_lock);
    g_err = buf;
    OutputDebugStringA("haku_sr: ");
    OutputDebugStringA(buf);
    OutputDebugStringA("\n");
}

extern "C" HSR_API const char *hsr_last_error(void)
{
    static thread_local std::string copy;
    std::lock_guard<std::mutex> l(g_err_lock);
    copy = g_err;
    return copy.c_str();
}

#define HR(x, what) do { HRESULT hr_ = (x); if (FAILED(hr_)) { set_err("%s failed: 0x%08lx", what, (unsigned long)hr_); goto fail; } } while (0)
#define CU(x, what) do { cudaError_t e_ = (x); if (e_ != cudaSuccess) { set_err("%s failed: %s", what, cudaGetErrorString(e_)); goto fail; } } while (0)

template <class T> static void release(T *&p) { if (p) { p->Release(); p = nullptr; } }

// ---------- TensorRT, loaded at runtime from this DLL's folder ----------

namespace {

class Logger : public nvinfer1::ILogger {
public:
    void log(Severity s, nvinfer1::AsciiChar const *msg) noexcept override
    {
        if (s <= Severity::kWARNING) {
            OutputDebugStringA("haku_sr/TensorRT: ");
            OutputDebugStringA(msg);
            OutputDebugStringA("\n");
        }
        if (s <= Severity::kERROR)
            set_err("TensorRT: %s", msg);
    }
};
Logger g_logger;

typedef void *(*create_fn)(void *, int32_t);
typedef void *(*create_parser_fn)(void *, void *, int);
std::once_flag g_trt_once;
create_fn g_create_runtime, g_create_builder;
create_parser_fn g_create_parser;

std::wstring module_dir()
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)&module_dir, &self);
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(self, path, MAX_PATH);
    std::wstring s(path, n);
    return s.substr(0, s.find_last_of(L"\\/") + 1);
}

bool load_trt(bool need_builder)
{
    std::call_once(g_trt_once, [] {
        std::wstring dir = module_dir();
        // nvinfer finds its builder resources and cudart next to itself
        SetDllDirectoryW(dir.c_str());
        HMODULE infer = LoadLibraryExW((dir + L"nvinfer_11.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        HMODULE onnx = LoadLibraryExW((dir + L"nvonnxparser_11.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        SetDllDirectoryW(nullptr);
        if (infer) {
            g_create_runtime = (create_fn)GetProcAddress(infer, "createInferRuntime_INTERNAL");
            g_create_builder = (create_fn)GetProcAddress(infer, "createInferBuilder_INTERNAL");
        }
        if (onnx)
            g_create_parser = (create_parser_fn)GetProcAddress(onnx, "createNvOnnxParser_INTERNAL");
    });
    if (!g_create_runtime) { set_err("couldn't load nvinfer_11.dll next to haku_sr.dll"); return false; }
    if (need_builder && (!g_create_builder || !g_create_parser)) { set_err("couldn't load the TensorRT builder / ONNX parser"); return false; }
    return true;
}

// ---------- shaders ----------

// YUV (NV12 / P010 planes as UNORM) -> planar RGB (NCHW) in a typed buffer. Chroma is left-sited (MPEG-2 / H.264 default).
const char *kInShader = R"(
Texture2D<float> Yp : register(t0);
Texture2D<float2> UVp : register(t1);
SamplerState S : register(s0);
RWBuffer<float> O : register(u0);
cbuffer C : register(b0) { float4 r0; float4 r1; float4 r2; uint W; uint H; float CW; float CH; };
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= W || id.y >= H) return;
    float y = Yp.Load(int3(id.xy, 0));
    float2 c = UVp.SampleLevel(S, float2((id.x * 0.5 + 0.5) / CW, (id.y * 0.5 + 0.25) / CH), 0);
    float3 v = float3(y, c.x, c.y);
    float3 rgb = saturate(float3(dot(r0.xyz, v) + r0.w, dot(r1.xyz, v) + r1.w, dot(r2.xyz, v) + r2.w));
    uint i = id.y * W + id.x, n = W * H;
    O[i] = rgb.r; O[n + i] = rgb.g; O[2 * n + i] = rgb.b;
}
)";

// Planar RGB buffer -> RGBA16F texture. mode 0: the buffer is already output-sized (model output);
// mode 1: bilinear upscale from the input-sized buffer (test mode, no AI).
const char *kOutShader = R"(
Buffer<float> I : register(t0);
RWTexture2D<float4> O : register(u0);
cbuffer C : register(b0) { uint OW; uint OH; uint IW; uint IH; uint MODE; uint pad0; uint pad1; uint pad2; };
float3 px(uint x, uint y, uint w, uint n) { uint i = y * w + x; return float3(I[i], I[n + i], I[2 * n + i]); }
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= OW || id.y >= OH) return;
    float3 rgb;
    if (MODE == 0) {
        rgb = px(id.x, id.y, OW, OW * OH);
    } else {
        float fx = (id.x + 0.5) * IW / OW - 0.5, fy = (id.y + 0.5) * IH / OH - 0.5;
        int x0 = (int)floor(fx), y0 = (int)floor(fy);
        float ax = fx - x0, ay = fy - y0;
        uint xa = (uint)clamp(x0, 0, (int)IW - 1), xb = (uint)clamp(x0 + 1, 0, (int)IW - 1);
        uint ya = (uint)clamp(y0, 0, (int)IH - 1), yb = (uint)clamp(y0 + 1, 0, (int)IH - 1);
        uint n = IW * IH;
        rgb = lerp(lerp(px(xa, ya, IW, n), px(xb, ya, IW, n), ax), lerp(px(xa, yb, IW, n), px(xb, yb, IW, n), ax), ay);
    }
    O[id.xy] = float4(saturate(rgb), 1);
}
)";

ID3D11ComputeShader *compile_cs(ID3D11Device *dev, const char *src, const char *name)
{
    ID3DBlob *code = nullptr, *errs = nullptr;
    ID3D11ComputeShader *cs = nullptr;
    HRESULT hr = D3DCompile(src, strlen(src), name, nullptr, nullptr, "main", "cs_5_0",
                            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errs);
    if (FAILED(hr)) {
        set_err("shader %s: %s", name, errs ? (const char *)errs->GetBufferPointer() : "compile failed");
    } else if (FAILED(dev->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &cs))) {
        set_err("CreateComputeShader %s failed", name);
    }
    release(code);
    release(errs);
    return cs;
}

struct InCB { float r0[4], r1[4], r2[4]; uint32_t w, h; float cw, ch; };
struct OutCB { uint32_t ow, oh, iw, ih, mode, pad[3]; };

// RAII for the device's multithread lock: the renderer on the UI thread uses the same immediate context.
struct DeviceLock {
    ID3D10Multithread *mt;
    explicit DeviceLock(ID3D10Multithread *m) : mt(m) { if (mt) mt->Enter(); }
    ~DeviceLock() { if (mt) mt->Leave(); }
};

} // namespace

// ---------- the upscaler ----------

struct hsr {
    ID3D11Device *dev = nullptr;
    ID3D11DeviceContext *ctx = nullptr;
    ID3D10Multithread *mt = nullptr;
    int w = 0, h = 0, ow = 0, oh = 0;

    // input: our copy of the decoder slice (decoder textures usually can't be bound to shaders)
    ID3D11Texture2D *in_copy = nullptr;
    D3D11_TEXTURE2D_DESC in_desc = {};
    ID3D11ShaderResourceView *srv_y = nullptr, *srv_uv = nullptr;

    // planar RGB buffers shared with CUDA
    ID3D11Buffer *buf_in = nullptr, *buf_out = nullptr;
    ID3D11UnorderedAccessView *uav_in = nullptr;
    ID3D11ShaderResourceView *srv_in = nullptr, *srv_out = nullptr;
    bool in_half = false, out_half = false;

    ID3D11ComputeShader *cs_in = nullptr, *cs_out = nullptr;
    ID3D11Buffer *cb_in = nullptr, *cb_out = nullptr;
    ID3D11SamplerState *samp = nullptr;
    std::unordered_map<ID3D11Texture2D *, ID3D11UnorderedAccessView *> out_uavs;

    // TensorRT
    bool ai = false;
    nvinfer1::IRuntime *rt = nullptr;
    nvinfer1::ICudaEngine *eng = nullptr;
    nvinfer1::IExecutionContext *ec = nullptr;
    std::string in_name, out_name;
    cudaStream_t stream = nullptr;
    cudaGraphicsResource *res[2] = {};
    int cuda_dev = 0;
};

static bool make_buffer(hsr *s, size_t count, bool half, ID3D11Buffer **buf, ID3D11ShaderResourceView **srv,
                        ID3D11UnorderedAccessView **uav)
{
    UINT es = half ? 2 : 4;
    DXGI_FORMAT f = half ? DXGI_FORMAT_R16_FLOAT : DXGI_FORMAT_R32_FLOAT;
    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = (UINT)(count * es);
    bd.Usage = D3D11_USAGE_DEFAULT;
    bd.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    if (FAILED(s->dev->CreateBuffer(&bd, nullptr, buf))) { set_err("CreateBuffer (%zu values) failed", count); return false; }
    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
    sd.Format = f;
    sd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    sd.Buffer.NumElements = (UINT)count;
    if (srv && FAILED(s->dev->CreateShaderResourceView(*buf, &sd, srv))) { set_err("buffer SRV failed"); return false; }
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
    ud.Format = f;
    ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    ud.Buffer.NumElements = (UINT)count;
    if (uav && FAILED(s->dev->CreateUnorderedAccessView(*buf, &ud, uav))) { set_err("buffer UAV failed"); return false; }
    return true;
}

static bool load_engine(hsr *s, const wchar_t *path)
{
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) { set_err("engine file not found"); return false; }
    std::vector<char> blob((size_t)f.tellg());
    f.seekg(0);
    f.read(blob.data(), (std::streamsize)blob.size());

    s->rt = static_cast<nvinfer1::IRuntime *>(g_create_runtime(&g_logger, NV_TENSORRT_VERSION));
    if (!s->rt) { set_err("createInferRuntime failed"); return false; }
    s->eng = s->rt->deserializeCudaEngine(blob.data(), blob.size());
    if (!s->eng) { set_err("couldn't load the engine (built for another GPU or TensorRT version?)"); return false; }
    s->ec = s->eng->createExecutionContext();
    if (!s->ec) { set_err("createExecutionContext failed"); return false; }

    for (int i = 0; i < s->eng->getNbIOTensors(); i++) {
        const char *n = s->eng->getIOTensorName(i);
        bool in = s->eng->getTensorIOMode(n) == nvinfer1::TensorIOMode::kINPUT;
        nvinfer1::DataType t = s->eng->getTensorDataType(n);
        if (t != nvinfer1::DataType::kHALF && t != nvinfer1::DataType::kFLOAT) { set_err("model tensor %s isn't FP16/FP32", n); return false; }
        (in ? s->in_name : s->out_name) = n;
        (in ? s->in_half : s->out_half) = t == nvinfer1::DataType::kHALF;
    }
    if (s->in_name.empty() || s->out_name.empty()) { set_err("model needs one input and one output"); return false; }

    nvinfer1::Dims in_dims{4, {1, 3, s->h, s->w}};
    if (!s->ec->setInputShape(s->in_name.c_str(), in_dims)) { set_err("engine doesn't take %dx%d input", s->w, s->h); return false; }
    nvinfer1::Dims od = s->ec->getTensorShape(s->out_name.c_str());
    if (od.nbDims != 4 || od.d[1] != 3) { set_err("unexpected model output shape"); return false; }
    s->oh = (int)od.d[2];
    s->ow = (int)od.d[3];
    return true;
}

extern "C" HSR_API const char *hsr_version(void)
{
    static char v[64];
    snprintf(v, sizeof(v), "haku_sr %d / TensorRT %d.%d.%d", HSR_VERSION, NV_TENSORRT_MAJOR, NV_TENSORRT_MINOR, NV_TENSORRT_PATCH);
    return v;
}

extern "C" HSR_API hsr *hsr_create(ID3D11Device *dev, const wchar_t *engine_path, int in_w, int in_h)
{
    hsr *s = new hsr();
    s->dev = dev;
    dev->AddRef();
    dev->GetImmediateContext(&s->ctx);
    dev->QueryInterface(__uuidof(ID3D10Multithread), (void **)&s->mt);
    s->w = in_w;
    s->h = in_h;
    s->ow = in_w * 2;
    s->oh = in_h * 2;
    s->ai = engine_path && engine_path[0];

    if (s->ai) {
        if (!load_trt(false))
            goto fail;
        // CUDA on the same GPU as the D3D11 device
        {
            IDXGIDevice *dxgi = nullptr;
            IDXGIAdapter *adapter = nullptr;
            HR(dev->QueryInterface(__uuidof(IDXGIDevice), (void **)&dxgi), "IDXGIDevice");
            HRESULT hr = dxgi->GetAdapter(&adapter);
            release(dxgi);
            if (FAILED(hr)) { set_err("GetAdapter failed"); goto fail; }
            cudaError_t e = cudaD3D11GetDevice(&s->cuda_dev, adapter);
            release(adapter);
            if (e != cudaSuccess) { set_err("this GPU has no CUDA device: %s", cudaGetErrorString(e)); goto fail; }
        }
        CU(cudaSetDevice(s->cuda_dev), "cudaSetDevice");
        CU(cudaStreamCreateWithFlags(&s->stream, cudaStreamNonBlocking), "cudaStreamCreate");
        if (!load_engine(s, engine_path))
            goto fail;
    } else {
        s->in_half = s->out_half = true;
    }

    {
        size_t in_n = (size_t)3 * s->w * s->h, out_n = (size_t)3 * s->ow * s->oh;
        if (!make_buffer(s, in_n, s->in_half, &s->buf_in, &s->srv_in, &s->uav_in))
            goto fail;
        if (s->ai && !make_buffer(s, out_n, s->out_half, &s->buf_out, &s->srv_out, nullptr))
            goto fail;
        if (s->ai) {
            CU(cudaGraphicsD3D11RegisterResource(&s->res[0], s->buf_in, cudaGraphicsRegisterFlagsNone), "register input buffer");
            CU(cudaGraphicsD3D11RegisterResource(&s->res[1], s->buf_out, cudaGraphicsRegisterFlagsNone), "register output buffer");
        }
    }

    s->cs_in = compile_cs(dev, kInShader, "hsr_in");
    s->cs_out = compile_cs(dev, kOutShader, "hsr_out");
    if (!s->cs_in || !s->cs_out)
        goto fail;
    {
        D3D11_BUFFER_DESC cd = {};
        cd.Usage = D3D11_USAGE_DEFAULT;
        cd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cd.ByteWidth = sizeof(InCB);
        HR(dev->CreateBuffer(&cd, nullptr, &s->cb_in), "constant buffer");
        cd.ByteWidth = sizeof(OutCB);
        HR(dev->CreateBuffer(&cd, nullptr, &s->cb_out), "constant buffer");
        D3D11_SAMPLER_DESC sd = {};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        HR(dev->CreateSamplerState(&sd, &s->samp), "sampler");
    }
    return s;

fail:
    hsr_destroy(s);
    return nullptr;
}

extern "C" HSR_API void hsr_output_size(hsr *s, int *out_w, int *out_h)
{
    *out_w = s->ow;
    *out_h = s->oh;
}

// (Re)make our copy texture and its plane views when the decoder's texture size or format changes.
static bool ensure_input(hsr *s, ID3D11Texture2D *in_tex)
{
    D3D11_TEXTURE2D_DESC d;
    in_tex->GetDesc(&d);
    if (s->in_copy && d.Width == s->in_desc.Width && d.Height == s->in_desc.Height && d.Format == s->in_desc.Format)
        return true;
    release(s->srv_y);
    release(s->srv_uv);
    release(s->in_copy);
    DXGI_FORMAT fy, fuv;
    if (d.Format == DXGI_FORMAT_NV12) { fy = DXGI_FORMAT_R8_UNORM; fuv = DXGI_FORMAT_R8G8_UNORM; }
    else if (d.Format == DXGI_FORMAT_P010 || d.Format == DXGI_FORMAT_P016) { fy = DXGI_FORMAT_R16_UNORM; fuv = DXGI_FORMAT_R16G16_UNORM; }
    else { set_err("unsupported decoder format %d (NV12 and P010 only)", (int)d.Format); return false; }
    if ((int)d.Width < s->w || (int)d.Height < s->h) { set_err("decoder texture smaller than the video"); return false; }

    D3D11_TEXTURE2D_DESC c = {};
    c.Width = d.Width;
    c.Height = d.Height;
    c.MipLevels = 1;
    c.ArraySize = 1;
    c.Format = d.Format;
    c.SampleDesc.Count = 1;
    c.Usage = D3D11_USAGE_DEFAULT;
    c.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(s->dev->CreateTexture2D(&c, nullptr, &s->in_copy))) { set_err("couldn't create the input copy texture"); return false; }
    D3D11_SHADER_RESOURCE_VIEW_DESC v = {};
    v.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    v.Texture2D.MipLevels = 1;
    v.Format = fy;
    if (FAILED(s->dev->CreateShaderResourceView(s->in_copy, &v, &s->srv_y))) { set_err("luma view failed"); return false; }
    v.Format = fuv;
    if (FAILED(s->dev->CreateShaderResourceView(s->in_copy, &v, &s->srv_uv))) { set_err("chroma view failed"); return false; }
    s->in_desc = d;
    return true;
}

static ID3D11UnorderedAccessView *out_uav(hsr *s, ID3D11Texture2D *t)
{
    auto it = s->out_uavs.find(t);
    if (it != s->out_uavs.end())
        return it->second;
    ID3D11UnorderedAccessView *u = nullptr;
    if (FAILED(s->dev->CreateUnorderedAccessView(t, nullptr, &u))) { set_err("output texture has no UAV (needs D3D11_BIND_UNORDERED_ACCESS)"); return nullptr; }
    t->AddRef(); // keep the pointer valid as a map key while we hold its view
    s->out_uavs[t] = u;
    return u;
}

extern "C" HSR_API int hsr_process(hsr *s, ID3D11Texture2D *in_tex, int in_slice, const hsr_yuv2rgb *csp,
                                   ID3D11Texture2D *out_tex)
{
    DeviceLock lock(s->mt);
    if (!ensure_input(s, in_tex))
        return -1;
    ID3D11UnorderedAccessView *ou = out_uav(s, out_tex);
    if (!ou)
        return -1;

    D3D11_BOX box = {0, 0, 0, s->in_desc.Width, s->in_desc.Height, 1};
    s->ctx->CopySubresourceRegion(s->in_copy, 0, 0, 0, 0, in_tex, (UINT)in_slice, &box);

    // YUV -> planar RGB
    InCB ic = {};
    for (int r = 0; r < 3; r++) {
        float *row = r == 0 ? ic.r0 : r == 1 ? ic.r1 : ic.r2;
        row[0] = csp->m[r * 3 + 0];
        row[1] = csp->m[r * 3 + 1];
        row[2] = csp->m[r * 3 + 2];
        row[3] = csp->c[r];
    }
    ic.w = (uint32_t)s->w;
    ic.h = (uint32_t)s->h;
    ic.cw = (float)(s->in_desc.Width / 2);
    ic.ch = (float)(s->in_desc.Height / 2);
    s->ctx->UpdateSubresource(s->cb_in, 0, nullptr, &ic, 0, 0);
    ID3D11ShaderResourceView *srvs[2] = {s->srv_y, s->srv_uv};
    s->ctx->CSSetShader(s->cs_in, nullptr, 0);
    s->ctx->CSSetShaderResources(0, 2, srvs);
    s->ctx->CSSetSamplers(0, 1, &s->samp);
    s->ctx->CSSetConstantBuffers(0, 1, &s->cb_in);
    s->ctx->CSSetUnorderedAccessViews(0, 1, &s->uav_in, nullptr);
    s->ctx->Dispatch((s->w + 15) / 16, (s->h + 15) / 16, 1);
    ID3D11ShaderResourceView *no_srv[2] = {};
    ID3D11UnorderedAccessView *no_uav = nullptr;
    s->ctx->CSSetShaderResources(0, 2, no_srv);
    s->ctx->CSSetUnorderedAccessViews(0, 1, &no_uav, nullptr);

    // the model
    if (s->ai) {
        void *pin = nullptr, *pout = nullptr;
        size_t sz = 0;
        s->ctx->Flush();
        cudaSetDevice(s->cuda_dev);
        if (cudaGraphicsMapResources(2, s->res, s->stream) != cudaSuccess) { set_err("map buffers for CUDA failed"); return -1; }
        bool ok = cudaGraphicsResourceGetMappedPointer(&pin, &sz, s->res[0]) == cudaSuccess &&
                  cudaGraphicsResourceGetMappedPointer(&pout, &sz, s->res[1]) == cudaSuccess &&
                  s->ec->setTensorAddress(s->in_name.c_str(), pin) &&
                  s->ec->setTensorAddress(s->out_name.c_str(), pout) &&
                  s->ec->enqueueV3(s->stream);
        cudaGraphicsUnmapResources(2, s->res, s->stream);
        if (!ok) { set_err("TensorRT inference failed"); return -1; }
    }

    // planar RGB -> RGBA16F output frame
    OutCB oc = {};
    oc.ow = (uint32_t)s->ow;
    oc.oh = (uint32_t)s->oh;
    oc.iw = (uint32_t)s->w;
    oc.ih = (uint32_t)s->h;
    oc.mode = s->ai ? 0 : 1;
    s->ctx->UpdateSubresource(s->cb_out, 0, nullptr, &oc, 0, 0);
    ID3D11ShaderResourceView *src = s->ai ? s->srv_out : s->srv_in;
    s->ctx->CSSetShader(s->cs_out, nullptr, 0);
    s->ctx->CSSetShaderResources(0, 1, &src);
    s->ctx->CSSetConstantBuffers(0, 1, &s->cb_out);
    s->ctx->CSSetUnorderedAccessViews(0, 1, &ou, nullptr);
    s->ctx->Dispatch((s->ow + 15) / 16, (s->oh + 15) / 16, 1);
    s->ctx->CSSetShaderResources(0, 1, no_srv);
    s->ctx->CSSetUnorderedAccessViews(0, 1, &no_uav, nullptr);
    s->ctx->CSSetShader(nullptr, nullptr, 0);
    return 0;
}

extern "C" HSR_API void hsr_destroy(hsr *s)
{
    if (!s)
        return;
    for (auto &kv : s->out_uavs) {
        kv.second->Release();
        kv.first->Release();
    }
    for (auto &r : s->res)
        if (r) cudaGraphicsUnregisterResource(r);
    if (s->stream) cudaStreamDestroy(s->stream);
    delete s->ec;
    delete s->eng;
    delete s->rt;
    release(s->srv_y);
    release(s->srv_uv);
    release(s->in_copy);
    release(s->uav_in);
    release(s->srv_in);
    release(s->srv_out);
    release(s->buf_in);
    release(s->buf_out);
    release(s->cs_in);
    release(s->cs_out);
    release(s->cb_in);
    release(s->cb_out);
    release(s->samp);
    release(s->mt);
    release(s->ctx);
    release(s->dev);
    delete s;
}

// ---------- engine building ----------

extern "C" HSR_API int hsr_build_engine(const wchar_t *onnx_path, const wchar_t *engine_path, int in_w, int in_h)
{
    if (!load_trt(true))
        return -1;
    int ret = -1;
    nvinfer1::IBuilder *b = static_cast<nvinfer1::IBuilder *>(g_create_builder(&g_logger, NV_TENSORRT_VERSION));
    nvinfer1::INetworkDefinition *net = nullptr;
    nvonnxparser::IParser *parser = nullptr;
    nvinfer1::IBuilderConfig *cfg = nullptr;
    nvinfer1::IHostMemory *blob = nullptr;
    std::string onnx;
    {
        int n = WideCharToMultiByte(CP_UTF8, 0, onnx_path, -1, nullptr, 0, nullptr, nullptr);
        onnx.resize(n > 0 ? n - 1 : 0);
        WideCharToMultiByte(CP_UTF8, 0, onnx_path, -1, onnx.data(), n, nullptr, nullptr);
    }
    if (!b) { set_err("createInferBuilder failed"); goto done; }
    net = b->createNetworkV2(0); // TensorRT 11 networks are always strongly typed: precision comes from the ONNX file
    parser = static_cast<nvonnxparser::IParser *>(g_create_parser(net, &g_logger, NV_ONNX_PARSER_VERSION));
    if (!net || !parser) { set_err("couldn't create the network / parser"); goto done; }
    if (!parser->parseFromFile(onnx.c_str(), (int)nvinfer1::ILogger::Severity::kWARNING)) {
        set_err("couldn't parse the ONNX model: %s", parser->getNbErrors() ? parser->getError(0)->desc() : "?");
        goto done;
    }
    if (net->getNbInputs() != 1) { set_err("model needs exactly one input"); goto done; }
    cfg = b->createBuilderConfig();
    cfg->setMemoryPoolLimit(nvinfer1::MemoryPoolType::kWORKSPACE, (size_t)2 << 30);
    {
        // how hard TensorRT searches for fast kernels (0-5, its default 3); HSR_OPT_LEVEL overrides, for measuring
        int level = 3;
        char env[8] = {};
        if (GetEnvironmentVariableA("HSR_OPT_LEVEL", env, sizeof(env)) > 0) level = atoi(env);
        cfg->setBuilderOptimizationLevel(level);
    }
    {
        nvinfer1::IOptimizationProfile *p = b->createOptimizationProfile();
        nvinfer1::Dims d{4, {1, 3, in_h, in_w}};
        const char *in = net->getInput(0)->getName();
        // A size range, tuned for in_w x in_h: one engine serves every video up to HSR_MAX_W x HSR_MAX_H.
        // (A single fixed size made TensorRT bake resolution-sized tensors in: 2.4 GB engines instead of 1 MB.)
        p->setDimensions(in, nvinfer1::OptProfileSelector::kMIN, nvinfer1::Dims{4, {1, 3, 64, 64}});
        p->setDimensions(in, nvinfer1::OptProfileSelector::kOPT, d);
        p->setDimensions(in, nvinfer1::OptProfileSelector::kMAX, nvinfer1::Dims{4, {1, 3, HSR_MAX_H, HSR_MAX_W}});
        cfg->addOptimizationProfile(p);
    }
    blob = b->buildSerializedNetwork(*net, *cfg);
    if (!blob) { set_err("TensorRT couldn't build the engine"); goto done; }
    {
        std::wstring tmp = std::wstring(engine_path) + L".tmp";
        std::ofstream f(tmp, std::ios::binary);
        f.write((const char *)blob->data(), (std::streamsize)blob->size());
        f.close();
        if (!f || !MoveFileExW(tmp.c_str(), engine_path, MOVEFILE_REPLACE_EXISTING)) { set_err("couldn't save the engine"); goto done; }
    }
    ret = 0;
done:
    delete blob;
    delete cfg;
    delete parser;
    delete net;
    delete b;
    return ret;
}
