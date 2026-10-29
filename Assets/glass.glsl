// Haku Player liquid glass: blurs, brightens and lens-bends the live video inside up to three
// rounded rectangles (the player's floating bars), every frame on the GPU.
// The app moves the rectangles with --glsl-shader-opts (values in 0..1 of the window).

//!PARAM ax
//!TYPE DYNAMIC float
0.0
//!PARAM ay
//!TYPE DYNAMIC float
0.0
//!PARAM aw
//!TYPE DYNAMIC float
0.0
//!PARAM ah
//!TYPE DYNAMIC float
0.0
//!PARAM ar
//!TYPE DYNAMIC float
0.0
//!PARAM aa
//!TYPE DYNAMIC float
0.0

//!PARAM bx
//!TYPE DYNAMIC float
0.0
//!PARAM by
//!TYPE DYNAMIC float
0.0
//!PARAM bw
//!TYPE DYNAMIC float
0.0
//!PARAM bh
//!TYPE DYNAMIC float
0.0
//!PARAM br
//!TYPE DYNAMIC float
0.0
//!PARAM ba
//!TYPE DYNAMIC float
0.0

//!PARAM cx
//!TYPE DYNAMIC float
0.0
//!PARAM cy
//!TYPE DYNAMIC float
0.0
//!PARAM cw
//!TYPE DYNAMIC float
0.0
//!PARAM ch
//!TYPE DYNAMIC float
0.0
//!PARAM cr
//!TYPE DYNAMIC float
0.0
//!PARAM ca
//!TYPE DYNAMIC float
0.0

//!PARAM dx
//!TYPE DYNAMIC float
0.0
//!PARAM dy
//!TYPE DYNAMIC float
0.0
//!PARAM dw
//!TYPE DYNAMIC float
0.0
//!PARAM dh
//!TYPE DYNAMIC float
0.0
//!PARAM dr
//!TYPE DYNAMIC float
0.0
//!PARAM da
//!TYPE DYNAMIC float
0.0

//!PARAM gblur
//!TYPE DYNAMIC float
18.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC Haku liquid glass
//!WHEN aa ba + ca + da + 0 >

// signed distance to a rounded rectangle (pixels; negative inside)
float rrect(vec2 p, vec2 c, vec2 half_size, float r)
{
    vec2 q = abs(p - c) - (half_size - vec2(r));
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

vec4 hook()
{
    vec4 base = HOOKED_tex(HOOKED_pos);
    vec2 size = HOOKED_size;
    vec2 p = HOOKED_pos * size;

    // pick the rectangle this pixel is in (they never overlap)
    vec4 rect = vec4(0.0); float rad = 0.0; float alpha = 0.0;
    if (aa > 0.0 && aw > 0.0) { vec2 c = vec2(ax + aw * 0.5, ay + ah * 0.5) * size; if (rrect(p, c, vec2(aw, ah) * size * 0.5, ar * size.y) < 1.0) { rect = vec4(ax, ay, aw, ah); rad = ar; alpha = aa; } }
    if (alpha == 0.0 && ba > 0.0 && bw > 0.0) { vec2 c = vec2(bx + bw * 0.5, by + bh * 0.5) * size; if (rrect(p, c, vec2(bw, bh) * size * 0.5, br * size.y) < 1.0) { rect = vec4(bx, by, bw, bh); rad = br; alpha = ba; } }
    if (alpha == 0.0 && ca > 0.0 && cw > 0.0) { vec2 c = vec2(cx + cw * 0.5, cy + ch * 0.5) * size; if (rrect(p, c, vec2(cw, ch) * size * 0.5, cr * size.y) < 1.0) { rect = vec4(cx, cy, cw, ch); rad = cr; alpha = ca; } }
    if (alpha == 0.0 && da > 0.0 && dw > 0.0) { vec2 c = vec2(dx + dw * 0.5, dy + dh * 0.5) * size; if (rrect(p, c, vec2(dw, dh) * size * 0.5, dr * size.y) < 1.0) { rect = vec4(dx, dy, dw, dh); rad = dr; alpha = da; } }
    if (alpha == 0.0) return base;

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
        acc += HOOKED_tex((q + o) / size).rgb;
    }
    vec3 col = acc / float(N);

    // vivid, slightly brighter content behind the glass (Apple's glass lifts, never muddies)
    float gray = dot(col, vec3(0.299, 0.587, 0.114));
    col = mix(vec3(gray), col, 1.3) * 1.03 + 0.008;
    // a whisper of specular brightening along the top rim, only where there is light
    col += col * 0.18 * rim * smoothstep(center.y, center.y - half_size.y, p.y); // scales with the picture: black stays black

    return mix(base, vec4(clamp(col, 0.0, 1.0), base.a), inside * alpha);
}
