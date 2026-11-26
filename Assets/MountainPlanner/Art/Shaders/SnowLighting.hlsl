#ifndef MP_SNOW_LIGHTING_INCLUDED
#define MP_SNOW_LIGHTING_INCLUDED

// Task 4.08 PR B (SN-13 v3): how snow takes the light. Include after Snow.hlsl (SnowSurface). A little colour tells the
// snow's family (Snow.hlsl's SnowLookTint); inside a family the type shows in three separate things (owner, 2026-11-24:
// "sparkle and shiny are two different things"):
// - shine: a glassy sheet. The sky mirrored more and more as the eye drops toward the surface, and a broad glare toward
//   the sun (SnowSheen). Ice is blue on a clear day because it mirrors the sky, and a grey sheet under cloud.
// - sparkle: a twinkle across the snow. Single crystals catching the sun, coming and going as the view moves (SnowSparkle).
// - softness: how far the sun wraps round the surface (powder pillowy, ice crisp).
// The surface itself is smooth: no type has a texture of its own (owner, 2026-11-25). A faint mottle keeps the glassy and
// spring types from reading as one even sheet from far away.
// No screen derivatives in here (no ddx, ddy, fwidth, and no sample that picks its own mip): every width comes in as a
// number, so the terrain may call this inside any branch (p4-08-findings R17) and the lit probe
// (Tests/EditMode/Shaders/SnowLitProbe.shader) can draw a point "500 m away" by passing that footprint.

float4 _SnowSkyZenith;    // the sky's own colours (linear), already greyed by the weather's grade (SceneLighting.Apply)
float4 _SnowSkyHorizon;
float4 _SnowSparkle;      // x: the twinkle's strength (0: none), y: the distance it's gone by (m), z: one pixel's angle (rad)

// How widely the crystals' facets lie about the surface: wide, so the twinkle spreads across the snow rather than gathering
// into one path toward the sun.
static const float SnowFacetSpread = 1.6;
// How much light a full glaze (gloss 1) takes off the snow under it, by channel: of the sun's (the share it throws into
// its glare instead) and of the sky's. Cooler than it is darker, never a flat tint: only in the patches.
static const float3 SnowGlazeSun = float3(0.26, 0.215, 0.16), SnowGlazeSky = float3(0.12, 0.08, 0.03);
// The plain snow's own soft glow toward the sun, as the terrain has always drawn it (smoothness 0.35's Blinn-Phong).
static const float SnowGlowPower = 22.63, SnowGlowStrength = 0.175;

float SnowPow5(float x) { float x2 = x * x; return x2 * x2 * x; }

float SnowHash2(int2 c)
{
    uint h = (uint)c.x * 0x8DA6B343u ^ (uint)c.y * 0xD8163841u;
    h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
    h = (h ^ (h >> 12)) * 0x297A2D39u;
    return (h >> 8) / 16777215.0;
}

float SnowBlotch(float2 p)
{
    float2 fl = floor(p);
    int2 i = (int2)fl + 8192;
    float2 f = p - fl;
    f = f * f * (3 - 2 * f);
    return lerp(lerp(SnowHash2(i), SnowHash2(i + int2(1, 0)), f.x), lerp(SnowHash2(i + int2(0, 1)), SnowHash2(i + int2(1, 1)), f.x), f.y);
}

// Value noise with its slope along x and y (yz).
float3 SnowBlotchSlope(float2 p)
{
    float2 fl = floor(p);
    int2 i = (int2)fl + 8192;
    float2 f = p - fl;
    float2 u = f * f * (3 - 2 * f), du = 6 * f * (1 - f);
    float a = SnowHash2(i), b = SnowHash2(i + int2(1, 0)), c = SnowHash2(i + int2(0, 1)), d = SnowHash2(i + int2(1, 1));
    return float3(lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y), du.x * lerp(b - a, d - c, u.y), du.y * (lerp(c, d, u.x) - lerp(a, b, u.x)));
}

// The glaze's faint ripples (a slope along x and z): a sheet of ice is never optically flat, so its glare breaks into
// streaks and shows over a wider range of views (the owner's "reflective texture"). Each size gone before it is a pixel.
float2 SnowGlazeRipple(float2 xz, float px)
{
    return SnowBlotchSlope(xz / 0.9).yz * (0.05 / 0.9 * (1 - saturate(px / 0.45)))
         + SnowBlotchSlope(xz / 0.23 + 5.1).yz * (0.012 / 0.23 * (1 - saturate(px / 0.115)));
}

// Where the shiny patches lie (0-1): irregular patches a few metres across covering `cover` of the ground, so ice draws as
// glassy patches with duller snow between them, not as one slab. From far away (px, metres a pixel) the patches give way
// to their own mean, so nothing shimmers.
float SnowPatches(float2 xz, float cover, float px)
{
    float fineShare = 0.35 * (1 - saturate(px / 1.2));
    float b = (0.65 * SnowBlotch(xz / 9.0) + fineShare * SnowBlotch(xz / 2.3 + 17.3) + (0.35 - fineShare) * 0.5);
    float mask = smoothstep(-0.13, 0.13, b - lerp(0.74, 0.26, cover));
    mask = lerp(mask, 1, saturate(cover * 10 - 9));   // full cover is the whole ground
    return lerp(mask, cover, saturate(px / 6));
}

// Task 4.08 B1b (R4): where a point takes its type's look from, a few metres off (within about 2.5 m): a type's border
// wanders as an irregular line instead of following the 8 m cells' edges. Smooth at every scale it has, so it never
// shimmers; the terrain uses it for the look only (the corduroy's edges stay on the trail's straight lines).
float2 SnowBorderWarp(float2 xz)
{
    float2 a = float2(SnowBlotch(xz / 13.0), SnowBlotch(xz / 13.0 + 41.3)) - 0.5;
    float2 b = float2(SnowBlotch(xz / 4.1 + 7.7), SnowBlotch(xz / 4.1 + 23.9)) - 0.5;
    return a * 3.6 + b * 1.6;
}

// The sky's colour in a direction, as Sky.shader draws its gradient (no sun, no clouds: a rough mirror shows neither).
float3 SnowSky(float3 direction)
{
    return lerp(_SnowSkyHorizon.rgb, _SnowSkyZenith.rgb, pow(max(direction.y, 0.03), 0.45));
}

// URP's own highlight (DirectBRDFSpecular's shape), a the roughness: its light sums to the same whatever its width.
float SnowGgx(float nh, float lh, float a)
{
    float a2 = a * a;
    float d = nh * nh * (a2 - 1) + 1.00001;
    return a2 / (d * d * max(0.1, lh * lh) * (a * 4 + 2));
}

// The shine of a glassy surface: the mirrored sky, stronger the lower the eye (mirrorShare is how much of the light under
// it the mirror takes), and the sun's glare, a broad lobe with a small bright core. gloss 0 a broad glow, 1 near a mirror.
// The mirrored sky has the sky's own colours (blue on a clear day, grey under cloud) at the brightness of the light the sky
// really gives the ground (skyLight), so under a closed deck ice is never brighter than the snow round it.
float3 SnowSheen(float3 n, float3 view, float3 sunDir, float3 sunLight, float sunDisc, float3 skyLight, float skyVis, float gloss, float px, out float mirrorShare)
{
    float a = lerp(0.50, 0.10, gloss);
    a = sqrt(a * a + saturate(px / 0.5) * 0.04);   // wider from far away: many facets a pixel, and no shimmer
    float nv = saturate(dot(n, view));
    mirrorShare = 0.04 + 0.96 * SnowPow5(1 - nv) * (1 - 0.6 * a);
    const float3 luma = float3(0.2126, 0.7152, 0.0722);
    float3 mean = 0.5 * (_SnowSkyZenith.rgb + _SnowSkyHorizon.rgb);
    float3 sky = lerp(SnowSky(reflect(-view, n)), mean, a) * (dot(skyLight, luma) / max(dot(mean, luma), 1e-3) * skyVis);
    float3 h = normalize(sunDir + view);
    float nh = saturate(dot(n, h)), lh = saturate(dot(sunDir, h));
    float low = 0.04 + 0.96 * SnowPow5(1 - lh);   // a low sun toward the eye: the glare grows
    float lobe = 0.8 * SnowGgx(nh, lh, a) + 0.2 * SnowGgx(nh, lh, max(0.06, 0.3 * a));
    // The glare is the sun's own disc mirrored: none under a closed deck, however much light still comes through it.
    return sky * mirrorShare + sunLight * (sunDisc * saturate(dot(n, sunDir)) * low * min(lobe, 24));
}

// One size of crystals: a world cell holds at most one, lying its own way; it lights when it mirrors the sun into the eye.
// A crystal draws at least a pixel wide (pix, metres a pixel facing the eye), so a small camera move never flickers it.
float SnowSparkleCells(float3 p, float3 n, float3 h, float cell, float pix, float density)
{
    float3 q = p / cell;
    float3 fl = floor(q);
    uint3 c = (uint3)(int3)fl;
    uint s = c.x * 73856093u ^ c.y * 19349663u ^ c.z * 83492791u;
    s = (s ^ (s >> 16)) * 0x7FEB352Du;
    s = (s ^ (s >> 15)) * 0x846CA68Bu;
    s ^= s >> 16;
    float3 r = float3(s & 1023u, (s >> 10) & 1023u, (s >> 20) & 1023u) / 1023.0;
    uint t = s * 0x9E3779B1u;
    t ^= t >> 15;
    float3 r2 = float3(t & 1023u, (t >> 10) & 1023u, (t >> 20) & 1023u) / 1023.0;
    float has = step(r2.z, density);
    float3 facet = normalize(n + (r - 0.5) * SnowFacetSpread);
    float g = saturate((dot(facet, h) - 0.985) * 66.7);   // within about 10 degrees of mirroring the sun
    g = saturate(g * g * 5);                            // most of those at full brightness, the edge of the cone fading
    // A smooth bump, not a hard disc: the light it puts on the pixels stays the same wherever their centres fall.
    float radius = clamp(1.2 * pix / cell, 0.12, 0.3);
    float spot = saturate(1 - length(q - fl - 0.5 - float3(r2.xy - 0.5, r.x - 0.5) * 0.4) / radius);
    spot = spot * spot * (3 - 2 * spot);
    return has * g * spot;
}

// The twinkle (0 to 8 times the sparkle's strength, to be lit by the sun's colour and shadow): crystals of the type's own size close up, and from further
// away cells a few pixels across in power-of-two steps, two sizes blended so no ring shows where one takes over. Coarse
// snow (corn) keeps bigger, fewer points at any distance. Only with SNOW_SPARKLE (WeatherView: direct sun, Medium and up).
float SnowSparkle(float3 p, float3 n, float3 h, float dist, float density, float grain)
{
#if defined(SNOW_SPARKLE)
    UNITY_BRANCH
    if (_SnowSparkle.x > 0 && density > 0.01 && dist < _SnowSparkle.y)
    {
        float near = 1 - smoothstep(_SnowSparkle.y * 0.4, _SnowSparkle.y, dist);
        float pix = dist * _SnowSparkle.z;
        float want = max(pix * (4 + 2 * saturate(grain / 0.03)), grain);
        float level = log2(max(want, 1e-4));
        float l0 = floor(level);
        float a = SnowSparkleCells(p, n, h, exp2(l0), pix, density);
        float b = SnowSparkleCells(p, n, h, exp2(l0 + 1), pix, density);
        return lerp(a, b, level - l0) * near * _SnowSparkle.x * 8;
    }
#endif
    return 0;
}

// Task 4.08 G1 (owner 2026-11-25: "make the groomed texture more noticeable from further away"): how corduroy takes the sun.
// Its ridges, 1 in apart and 8 mm deep (SK3), are two sloped faces. A face turned from the sun past its slope gets none;
// the face toward a low sun is shaded near the trough by the crest across the groove; the eye sees one face more than the
// other. So corduroy reads darker and bluer looking toward the sun or with the sun raking across it, and a little brighter
// with the sun behind the eye, as groomed runs do in photos (p4-08-findings, Round 4). Snow's own bounce keeps the grooves
// from going black. These scale the sun's light only; the shaded grooves keep the sky's (blue) light, a little of it lost.
// Presentation/CorduroyLight.cs is the C# twin (CorduroyLightTests holds the two equal).
static const float CordSpacingMetres = 0.032, CordDepthMetres = 0.010;   // 1¼ in (owner 2026-11-25: "a tad bigger"); same slope
static const float CordBounce = 0.45;          // of the light a shaded groove would get from the sun, bounced in by the faces
static const float CordSunMax = 1.15;          // sun-behind brightening at most (lit snow is near the top of the range)
static const float CordSkyLoss = 0.06;         // the sky the grooves don't see

// The faces' slope (rise over run): a V-groove with the sine's mean slope, 2 × depth / spacing.
float CordSlope(float depthScale) { return 2 * CordDepthMetres / CordSpacingMetres * depthScale; }

// The sun's light on corduroy as seen from `view`, over the same on smooth snow (n: the smooth surface; across: across the
// ridges; k: CordSlope). Bounce applied, at most CordSunMax.
float CorduroySun(float3 n, float3 across, float3 sunDir, float3 view, float k)
{
    float3 a = across - n * dot(across, n);
    a = a * rsqrt(max(dot(a, a), 1e-8));
    float sn = dot(sunDir, n), sa = dot(sunDir, a), vn = dot(view, n), va = dot(view, a);
    if (sn < 0.02 || vn < 0.02) return 1;
    float inv = rsqrt(1 + k * k);
    float sp = max(0, (sn + k * sa) * inv), sm = max(0, (sn - k * sa) * inv);   // the sun on each face (p faces +a)
    float vp = max(0, (vn + k * va) * inv), vm = max(0, (vn - k * va) * inv);   // the eye on each
    float ts = sn / max(abs(sa), 1e-4), tv = vn / max(abs(va), 1e-4);
    // Each face lit and seen from its crest down: the face toward the sun lit over 2t / (k + t) of it, the face away only
    // when the sun clears its slope; the same for the eye.
    float litToward = ts >= k ? 1 : 2 * ts / (k + ts), litAway = ts >= k ? 1 : 0;
    float seenToward = tv >= k ? 1 : 2 * tv / (k + tv), seenAway = tv >= k ? 1 : 0;
    float lp = sa >= 0 ? litToward : litAway, lm = sa >= 0 ? litAway : litToward;
    float wp = va >= 0 ? seenToward : seenAway, wm = va >= 0 ? seenAway : seenToward;
    float f = (min(lp, wp) * vp * sp + min(lm, wm) * vm * sm) / max(wp * vp + wm * vm, 1e-4) / sn;
    return min(f < 1 ? lerp(f, 1, CordBounce) : f, CordSunMax);
}

// Close up, a single ridge's shade (0 lit, 1 shaded) at its phase (cycles across, the sine's crest at a quarter): the
// shaded stretch ends where the sun grazes the face below the crest and runs back k / (k + t) of a ridge, CorduroySun's
// share. perCycle: cycles a pixel (the edges soften over it).
float CorduroyShade(float cycles, float perCycle, float3 n, float3 across, float3 sunDir, float k)
{
    float3 a = across - n * dot(across, n);
    a = a * rsqrt(max(dot(a, a), 1e-8));
    float sn = dot(sunDir, n), sa = dot(sunDir, a);
    if (sn < 0.02) return 0;
    float ts = sn / max(abs(sa), 1e-4);
    if (ts >= k) return 0;
    float m = 3.14159265 / 2 * k;                                    // the sine's own steepest slope
    float end = acos(saturate(ts / m)) / 6.2831853;                  // where the sun grazes, before the crest
    float phase = sa >= 0 ? frac(cycles) : frac(0.5 - cycles);       // a sun from -a sees the ridge mirrored
    float share = k / (k + ts), edge = 0.5 * max(perCycle, 0.02);
    float into = frac(end - phase);                                  // how far back from that point
    into = into > 0.5 * (1 + share) ? into - 1 : into;               // just past it, on the lit side
    // Edges softened over a pixel either side of the true line, so the mean shade stays the far light's as the ridges shrink.
    return smoothstep(-edge, edge, into) * (1 - smoothstep(share - edge, share + edge, into));
}

// The groove light under cloud (cover 0-1): the grooves shade only the direct sun, which a closing deck takes away as it
// does the sun disc (cover 0.5 to 0.9); the sky they miss stays (CordSkyLoss). C# twin: CorduroyLight.UnderCloud.
float CorduroyUnderCloud(float sun, float cover) { return lerp(1, sun, 1 - smoothstep(0.5, 0.9, cover)); }

struct SnowLitInput
{
    float3 albedo;       // the snow layer's albedo, its tint applied
    float3 normalSmooth; // the surface without its small relief: the shine's mirror
    float3 normalDetail; // with it: the sun's and the twinkle's
    float3 view;         // toward the eye
    float3 sunDir;       // toward the sun
    float3 sunLight;     // the sun's colour times every shadow on the point
    float sunCrease;     // the creases' share off the sun's plain light (1: none)
    float sunDisc;       // how clearly the sun's own disc shows through the sky (1 clear, 0 under a closed deck)
    float3 ambient;      // the sky's light on normalDetail, occlusion applied
    float3 skyLight;     // the sky's light from straight above, no occlusion: the mirrored sky's brightness
    float skyVis;        // how much sky the point sees (0-1), for the mirrored sky
    float3 positionWS;
    float dist;          // to the eye (m)
    float px;            // metres a pixel here
    float shineFade;     // 1; under 1 where the shell thins to painted snow, which has no shine
};

// A snow pixel, lit. shine and twinkle come out apart too (the probe's passes and the tests read them).
float3 SnowLit(SnowSurface s, SnowLitInput i, out float3 shine, out float3 twinkle)
{
    float3 detail = i.normalDetail;
    float ndl = saturate((dot(detail, i.sunDir) + s.soft) / (1 + s.soft));
    float3 h = normalize(i.sunDir + i.view);
    // The faint mottle that survives from far away: glassy and spring snow aren't one even sheet from play height.
    float mottle = 1 + s.mottle * 4 * (SnowBlotch(i.positionWS.xz / 23.0 + 3.1) - 0.5);
    float3 sunPart = i.albedo * i.sunLight * (ndl * i.sunCrease * mottle), skyPart = i.albedo * i.ambient * mottle;
    float glow = pow(saturate(dot(detail, h)), SnowGlowPower) * SnowGlowStrength;
    float m = 0, mirrorShare = 0;
    shine = 0;
    UNITY_BRANCH
    if (s.sheen * i.shineFade > 0.01)
    {
        m = s.sheen * i.shineFade * SnowPatches(i.positionWS.xz, s.patch, i.px);
        float2 ripple = SnowGlazeRipple(i.positionWS.xz, i.px);
        float3 mirror = normalize(i.normalSmooth - float3(ripple.x, 0, ripple.y));
        shine = m * SnowSheen(mirror, i.view, i.sunDir, i.sunLight, i.sunDisc, i.skyLight, i.skyVis, s.gloss, i.px, mirrorShare);
        // Under the glaze the snow scatters less light back: a little darker and cooler, the glassier the more. Without it a
        // sheet seen from above (where a mirror reflects 4 %) would be plain snow, and its glare would have nothing to show
        // against (sun-facing snow is already at the top of the picture's range).
        sunPart *= 1 - m * s.gloss * SnowGlazeSun;
        skyPart *= 1 - m * s.gloss * SnowGlazeSky;
    }
    float3 plain = sunPart + skyPart;
    twinkle = i.sunLight * (SnowSparkle(i.positionWS, detail, h, i.dist, s.density, s.grain) * s.sparkle * (1 - m));
    return plain * (1 - m * mirrorShare) + i.sunLight * (glow * (1 - m)) + shine + twinkle;
}

#endif
