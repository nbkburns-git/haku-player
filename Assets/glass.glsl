// Haku Player liquid glass: blurs, brightens and lens-bends the live video inside up to four
// rounded rectangles (the player's floating bars), every frame on the GPU.
// The app moves the rectangles with --glsl-shader-opts. mpv's renderer for the libmpv render API takes at most 16
// plain float params, so each param carries two numbers: round((u + 1) / 3 * 4095) * 4096 + the same for v,
// with u and v in -1..2 (exact in a float; steps of 1/1365 of the picture). Per rectangle: _p = x,y  _s = w,h
// _r = radius,alpha (0..1 of the picture). gpq = 1 when mpv outputs HDR (PQ / BT.2020): the glass then works on the
// same SDR-like values it does in SDR, so it looks the same (capped at SDR white, like SDR).

//!PARAM a_p
//!TYPE float
0.0
//!PARAM a_s
//!TYPE float
0.0
//!PARAM a_r
//!TYPE float
0.0
//!PARAM b_p
//!TYPE float
0.0
//!PARAM b_s
//!TYPE float
0.0
//!PARAM b_r
//!TYPE float
0.0
//!PARAM c_p
//!TYPE float
0.0
//!PARAM c_s
//!TYPE float
0.0
//!PARAM c_r
//!TYPE float
0.0
//!PARAM d_p
//!TYPE float
0.0
//!PARAM d_s
//!TYPE float
0.0
//!PARAM d_r
//!TYPE float
0.0
//!PARAM gblur
//!TYPE float
18.0
//!PARAM gpq
//!TYPE float
0.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC Haku liquid glass

// two numbers in one param (see the top)
vec2 unpack2(float v)
{
    float hi = floor(v / 4096.0);
    float lo = v - hi * 4096.0;
    return vec2(hi, lo) / 4095.0 * 3.0 - 1.0;
}

// HDR output: PQ to linear light relative to SDR white (203 nits), then a 2.2 gamma, so the maths below sees SDR-like values
vec3 to_work(vec3 c)
{
    if (gpq < 0.5) return c;
    vec3 e = pow(max(c, 0.0), vec3(1.0 / 78.84375));
    vec3 lin = 10000.0 * pow(max(e - 0.8359375, 0.0) / (18.8515625 - 18.6875 * e), vec3(1.0 / 0.1593017578125));
    return pow(lin / 203.0, vec3(1.0 / 2.2));
}
vec3 from_work(vec3 w)
{
    if (gpq < 0.5) return clamp(w, 0.0, 1.0);
    vec3 y = pow(clamp(w, 0.0, 1.0), vec3(2.2)) * (203.0 / 10000.0); // capped at SDR white, as in SDR: the bars stay readable over HDR highlights
    vec3 ym = pow(min(y, 1.0), vec3(0.1593017578125));
    return pow((0.8359375 + 18.8515625 * ym) / (1.0 + 18.6875 * ym), vec3(78.84375));
}

// signed distance to a rounded rectangle (pixels; negative inside)
float rrect(vec2 p, vec2 c, vec2 half_size, float r)
{
    vec2 q = abs(p - c) - (half_size - vec2(r));
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// is p inside this packed rectangle? fills rect/rad/alpha if so
bool pick(vec2 p, vec2 size, float pp, float ps, float pr, inout vec4 rect, inout float rad, inout float alpha)
{
    if (pr == 0.0) return false; // never set: alpha 0
    vec2 xy = unpack2(pp), wh = unpack2(ps), ra = unpack2(pr);
    if (ra.y <= 0.002 || wh.x <= 0.0) return false;
    vec2 c = (xy + wh * 0.5) * size;
    if (rrect(p, c, wh * size * 0.5, ra.x * size.y) >= 1.0) return false;
    rect = vec4(xy, wh); rad = ra.x; alpha = min(ra.y, 1.0);
    return true;
}

vec4 hook()
{
    vec4 base = HOOKED_tex(HOOKED_pos);
    vec2 size = HOOKED_size;
    vec2 p = HOOKED_pos * size;

    // pick the rectangle this pixel is in (they never overlap)
    vec4 rect = vec4(0.0); float rad = 0.0; float alpha = 0.0;
    if (!pick(p, size, a_p, a_s, a_r, rect, rad, alpha) &&
        !pick(p, size, b_p, b_s, b_r, rect, rad, alpha) &&
        !pick(p, size, c_p, c_s, c_r, rect, rad, alpha) &&
        !pick(p, size, d_p, d_s, d_r, rect, rad, alpha))
        return base;

    vec2 center = (rect.xy + rect.zw * 0.5) * size;
    vec2 half_size = rect.zw * size * 0.5;
    float r = rad * size.y;
    float d = rrect(p, center, half_size, r);
    float inside = clamp(0.5 - d, 0.0, 1.0);          // anti-aliased edge
    float scale = size.y / 1440.0;                       // keep the look the same at any window size

    // lens: near the rim, sample from further in, so the edge bends and magnifies like thick glass
    float rim = 1.0 - smoothstep(0.0, 22.0 * scale, -d);
    vec2 toward = (center - p) / max(half_size, vec2(1.0));
    vec2 q = p + toward * rim * rim * 18.0 * scale;

    // frosted blur: golden-angle spiral of taps
    const int N = 48;
    float R = gblur * scale;
    vec3 acc = vec3(0.0);
    for (int i = 0; i < N; i++)
    {
        float t = (float(i) + 0.5) / float(N);
        float a = float(i) * 2.39996323;
        vec2 o = sqrt(t) * R * vec2(cos(a), sin(a));
        acc += to_work(HOOKED_tex((q + o) / size).rgb);
    }
    vec3 col = acc / float(N);

    // vivid, slightly brighter content behind the glass (Apple's glass lifts, never muddies)
    float gray = dot(col, gpq < 0.5 ? vec3(0.299, 0.587, 0.114) : vec3(0.2627, 0.6780, 0.0593));
    col = mix(vec3(gray), col, 1.3) * 1.03 + 0.008;
    // a whisper of specular brightening along the top rim, only where there is light
    col += col * 0.18 * rim * smoothstep(center.y, center.y - half_size.y, p.y); // scales with the picture: black stays black

    return mix(base, vec4(from_work(col), base.a), inside * alpha);
}
