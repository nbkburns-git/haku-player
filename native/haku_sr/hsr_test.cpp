// Self-check and benchmark for haku_sr.dll, no mpv needed.
//   hsr_test.exe                      bilinear test mode: checks colours
//   hsr_test.exe <model.onnx> <dir>   also builds (or reuses) an engine in <dir> and times the AI path
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>
#include "haku_sr.h"

#pragma comment(lib, "d3d11.lib")

static int failures = 0;
#define CHECK(cond, ...) do { if (!(cond)) { printf("FAIL: " __VA_ARGS__); printf("\n"); failures++; } } while (0)

// limited-range BT.709 -> RGB for UNORM8-normalised values
static hsr_yuv2rgb bt709_limited_8bit()
{
    const double kr = 0.2126, kb = 0.0722, kg = 1 - kr - kb;
    const double ys = 255.0 / 219.0, cs = 255.0 / 224.0, yo = 16.0 / 255.0, co = 128.0 / 255.0;
    double cr_r = 2 * (1 - kr), cb_b = 2 * (1 - kb), cb_g = -2 * (1 - kb) * kb / kg, cr_g = -2 * (1 - kr) * kr / kg;
    hsr_yuv2rgb t = {};
    double m[9] = {ys, 0, cr_r * cs, ys, cb_g * cs, cr_g * cs, ys, cb_b * cs, 0};
    for (int i = 0; i < 9; i++) t.m[i] = (float)m[i];
    for (int r = 0; r < 3; r++) t.c[r] = (float)(-(m[r * 3] * yo + m[r * 3 + 1] * co + m[r * 3 + 2] * co));
    return t;
}

static float half_to_float(uint16_t h)
{
    uint32_t s = (h >> 15) & 1, e = (h >> 10) & 31, m = h & 1023;
    float v = e == 0 ? std::ldexp((float)m, -24) : std::ldexp((float)(m | 1024), (int)e - 25);
    return s ? -v : v;
}

int main(int argc, char **argv)
{
    printf("%s\n", hsr_version());
    ID3D11Device *dev = nullptr;
    ID3D11DeviceContext *ctx = nullptr;
    D3D_FEATURE_LEVEL fl[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                 fl, 2, D3D11_SDK_VERSION, &dev, nullptr, &ctx))) { printf("no D3D11 device\n"); return 2; }
    ID3D11Multithread *mt = nullptr;
    dev->QueryInterface(__uuidof(ID3D11Multithread), (void **)&mt);
    mt->SetMultithreadProtected(TRUE); // as Haku's device

    // 1920x1080 video in a 1920x1088 decoder-style NV12 texture: left half one colour, right half another
    const int W = 1920, H = 1080, TH = 1088;
    std::vector<uint8_t> nv12((size_t)W * TH * 3 / 2);
    // left: Y=81 U=90 V=240 (BT.709 limited red-ish); right: Y=126 U=V=128 (mid grey)
    for (int y = 0; y < TH; y++) for (int x = 0; x < W; x++) nv12[(size_t)y * W + x] = x < W / 2 ? 81 : 126;
    uint8_t *uv = nv12.data() + (size_t)W * TH;
    for (int y = 0; y < TH / 2; y++) for (int x = 0; x < W / 2; x++) {
        uv[(size_t)y * W + 2 * x] = x < W / 4 ? 90 : 128;
        uv[(size_t)y * W + 2 * x + 1] = x < W / 4 ? 240 : 128;
    }
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = W; td.Height = TH; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_DECODER;
    D3D11_SUBRESOURCE_DATA sd = {nv12.data(), (UINT)W, 0};
    ID3D11Texture2D *in = nullptr;
    if (FAILED(dev->CreateTexture2D(&td, &sd, &in))) { printf("couldn't create the NV12 test frame\n"); return 2; }

    auto run = [&](const wchar_t *engine, const char *label, int vw) {
        hsr *s = hsr_create(dev, engine, vw, H);
        CHECK(s, "%s: hsr_create: %s", label, hsr_last_error());
        if (!s) return;
        int ow, oh;
        hsr_output_size(s, &ow, &oh);
        CHECK(ow == 2 * vw && oh == 2 * H, "%s: output %dx%d", label, ow, oh);
        D3D11_TEXTURE2D_DESC od = {};
        od.Width = ow; od.Height = oh; od.MipLevels = 1; od.ArraySize = 1; od.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        od.SampleDesc.Count = 1; od.Usage = D3D11_USAGE_DEFAULT; od.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        ID3D11Texture2D *out = nullptr;
        dev->CreateTexture2D(&od, nullptr, &out);
        hsr_yuv2rgb csp = bt709_limited_8bit();
        CHECK(hsr_process(s, in, 0, &csp, out) == 0, "%s: hsr_process: %s", label, hsr_last_error());

        // read back two pixels away from the edge between the halves
        D3D11_TEXTURE2D_DESC rd = od;
        rd.Usage = D3D11_USAGE_STAGING; rd.BindFlags = 0; rd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D *rb = nullptr;
        dev->CreateTexture2D(&rd, nullptr, &rb);
        ctx->CopyResource(rb, out);
        D3D11_MAPPED_SUBRESOURCE m;
        ctx->Map(rb, 0, D3D11_MAP_READ, 0, &m);
        auto px = [&](int x, int y, int c) { return half_to_float(((uint16_t *)((uint8_t *)m.pData + (size_t)y * m.RowPitch))[x * 4 + c]); };
        // expected: left = BT.709 limited (81, 90, 240), right = grey 126 -> 0.502
        float lr = px(400, 1000, 0), lg = px(400, 1000, 1), lb = px(400, 1000, 2);
        float gr = px(ow - 200, 1000, 0), gg = px(ow - 200, 1000, 1), gb = px(ow - 200, 1000, 2);
        printf("%s: left %.3f %.3f %.3f | right %.3f %.3f %.3f\n", label, lr, lg, lb, gr, gg, gb);
        const float tol = engine ? 0.05f : 0.01f; // a model may shift flat colours slightly
        CHECK(std::fabs(gr - 0.5023f) < tol && std::fabs(gg - 0.5023f) < tol && std::fabs(gb - 0.5023f) < tol, "%s: grey half wrong", label);
        CHECK(lr > 0.9f && lg < 0.15f && lb < 0.15f, "%s: red half wrong", label);
        ctx->Unmap(rb, 0);

        // timing: 200 frames, waiting for the GPU at the end
        ID3D11Query *q = nullptr;
        D3D11_QUERY_DESC qd = {D3D11_QUERY_EVENT, 0};
        dev->CreateQuery(&qd, &q);
        for (int i = 0; i < 10; i++) hsr_process(s, in, 0, &csp, out);
        ctx->End(q);
        while (ctx->GetData(q, nullptr, 0, 0) == S_FALSE) {}
        LARGE_INTEGER f, t0, t1;
        QueryPerformanceFrequency(&f);
        QueryPerformanceCounter(&t0);
        const int N = 200;
        for (int i = 0; i < N; i++) hsr_process(s, in, 0, &csp, out);
        ctx->End(q);
        while (ctx->GetData(q, nullptr, 0, 0) == S_FALSE) {}
        QueryPerformanceCounter(&t1);
        double ms = (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart / N;
        printf("%s: %.2f ms per frame (%.1f fps)\n", label, ms, 1000.0 / ms);
        q->Release(); rb->Release(); out->Release();
        hsr_destroy(s);
    };

    run(nullptr, "bilinear", W);

    if (argc >= 3) {
        std::wstring onnx(argv[1], argv[1] + strlen(argv[1])), dir(argv[2], argv[2] + strlen(argv[2]));
        std::wstring name = onnx.substr(onnx.find_last_of(L"\\/") + 1);
        std::wstring engine = dir + L"\\" + name.substr(0, name.rfind(L'.')) + L".engine"; // same name Haku uses
        if (GetFileAttributesW(engine.c_str()) == INVALID_FILE_ATTRIBUTES) {
            printf("building engine...\n");
            ULONGLONG t = GetTickCount64();
            CHECK(hsr_build_engine(onnx.c_str(), engine.c_str(), W, H) == 0, "build: %s", hsr_last_error());
            printf("built in %.1f s\n", (GetTickCount64() - t) / 1000.0);
        }
        run(engine.c_str(), "ai", W);
        run(engine.c_str(), "ai 1440x1080", 1440); // same engine, another size (4:3 shows)
    }
    in->Release(); mt->Release(); ctx->Release(); dev->Release();
    printf(failures ? "%d FAILED\n" : "all checks passed\n", failures);
    return failures ? 1 : 0;
}
