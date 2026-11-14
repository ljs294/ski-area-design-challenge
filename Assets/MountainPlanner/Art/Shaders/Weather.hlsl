// The weather in the air (task 4.06, WX-50/53): haze that thickens toward the valleys, the cloud deck as a lit layer in the
// air, valley fog as a sea with a flat top, snow curtains hanging under the deck, and the clouds' shadows on the ground.
// Haze.hlsl's ApplyHaze calls WeatherFog and FarShadow.hlsl's MainLightWithFarShadow calls CloudShadow, so terrain, cliffs,
// trees, impostors and the diorama walls all fog and shade together; Sky.shader reads the same functions, so the sky meets the
// far ridges and the diorama's edge without a seam. WeatherView sets every global (Presentation/Weather); with _WxParams.x = 0
// nothing changes.
//
// The layers are sampled where the line of sight crosses them in the air (not at the ground point), so a cloud's hole or a
// shower hangs above the mountain and shifts against it as the camera moves. A tree (WX_COARSE_SHADOW, TreeLighting.hlsl)
// takes the cheaper path: one curtain tap, no snow-line fringe. The clouds' shadows come from a map drawn once a frame
// (CloudShadowMap.cs), so every surface reads them with one tap.
//
// The fog (WX_AIR) and the terrain's glint (WX_GLINT) are global keywords WeatherView sets only while they'd show. Measured,
// their code merely compiled into the terrain costs 0.2-0.35 ms on views near the ground even when its branch is skipped (the
// shader keeps registers for it and fewer pixels run at once), so a clear day, a tool or an info view runs without them.
#ifndef MOUNTAIN_WEATHER
#define MOUNTAIN_WEATHER

float4 _WxParams;      // x: on (0/1), y: fog scale (tools and info views thin it; 0: nothing in the air), z: clear radius (m), w: most (0-1)
float4 _WxHaze;        // x: density at the target's height (1/m), y: the target's height (world y), z: 1 / scale height (1/m), w: sun glow
float4 _WxDeck;        // x: base, y: top (world y), z: density (1/m; 0: no deck), w: raggedness of its base (m)
float4 _WxValley;      // x: top (world y; below -90000: none), y: density (1/m), z: most (0-1), w: raggedness of its top (m)
float4 _WxCurtain;     // x: density in a shower (1/m; 0: none), y: the share of the cloud that falls, zw: slant (m across per m fallen)
float4 _WxFogColor;    // rgb: the fog aloft (the graded horizon), w: 1 / the height over which it brightens from the low colour (1/m)
float4 _WxFogLow;      // rgb: the fog in the valleys (darker, cooler), w: the height it's that colour at (world y)
float4 _WxSun;         // rgb: the sun's light as graded (linear), w: day (0 night - 1 day)
float4 _WxLight;       // rgb: the sky's ambient as graded (linear), w: how dark the deck's base is against the valley fog (0-1)
float4 _WxCloud;       // x: cover (0-1), y: shadow strength (0-1), z, w: the clouds' drift (m)
float4 _WxCloudScale;  // x: 1 / large tile (m), y: 1 / small tile (m), z: the deck's base for the shadows' projection (world y), w: edge softness
float4 _WxFront;       // xy: the way the front travels (grid xz, unit), z: where it is along that (m), w: 1 / its width (m); w 0: no front
float4 _WxFrontOld;    // x: the cover ahead of the front
float4 _WxFrontScale;  // the share of the deck's density behind (x) and ahead of (y) the front, and the curtains' (z, w); all 1 with no front
float4 _WxSnowLine;    // x: the snow line (world y), y: strength (0: none), z: band height (m), w: wet darkening below (0-1)
float4 _WxPixel;       // x: one pixel's angle (rad), y: the large tile's texel (m), z: 1 / the huge tile (m), w: cover variation (0-1)
float4 _WxGust;        // xy: the way the gusts run (grid xz, unit), z: strength (0: none), w: how far they've run (m, wrapped at 2100)
float4 _WxGlint;       // x: the snow's sparkle (0: none), y: the distance it's gone by (m)
float4 _WxShadowAt;    // the clouds' shadow map: xy its origin on the deck's plane (grid xz), z 1 / its extent (m), w its size (texels)
float4 _WxSunRun;      // xy: how far the sun's ray runs across (grid xz) for each metre it climbs
float3 _SkySunDirection;
TEXTURE2D(_WxCloudTex);   // R: cumulus cells (Perlin-Worley, histogram-flattened), G: the detail that erodes their edges
SAMPLER(sampler_WxCloudTex);
TEXTURE2D(_WxShadowMap);  // R: the cloud over the deck's plane around the view (CloudShadowMap.cs)
SAMPLER(sampler_WxShadowMap);

// The mip that matches a pixel's footprint at a distance, seen at a grazing angle (|dir.y|): no shimmer far off, no streaks
// where the sky's plane runs to the horizon. Never past mip 4.5 (about 300 m texels): coarser, the cells' edges turn into the
// texel grid's rectangles; WxCloudFrom softens the far edges instead.
float WxLod(float dist, float graze)
{
    float footprint = dist * _WxPixel.x * rsqrt(max(graze, 0.04));
    return clamp(log2(max(footprint / _WxPixel.y, 1e-3)), 0, 4.5);
}

float2 WxTurn(float2 q) { return float2(q.x * 0.7986 - q.y * 0.6018, q.x * 0.6018 + q.y * 0.7986); }   // 37°, so the tiles never line up

// The cloud field at a point on the deck's plane (0-1): the large tile's cells, plus the small, turned tile's detail.
float WxCloudField(float2 q, float lod, bool detail)
{
    float n = SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, q * _WxCloudScale.x, lod).r;
    UNITY_BRANCH
    if (detail) n += (SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, WxTurn(q) * _WxCloudScale.y + 0.37, lod + 2).g - 0.5) * 0.3;
    return n;
}

// The cover over a region: the hour's, moved about by a huge turned tile so the 7 km tile never shows its repeat. One tap per
// pixel, at a high mip (it changes over tens of km).
float WxCover(float2 q)
{
    float cover = _WxCloud.x;
    UNITY_BRANCH
    if (_WxPixel.w > 0)
    {
        float v = SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, WxTurn(q.yx) * _WxPixel.z + 0.61, 5).r - 0.5;
        cover = saturate(cover + v * _WxPixel.w * cover * (1 - cover) * 4);
    }
    return cover;
}

// Where the front is at q: 0 behind it (the hour's weather), 1 ahead of it (the last). Its edge is warped by the field, so it
// arrives as a ragged line, not a ruler.
float WxFrontAt(float2 q, float n)
{
    UNITY_BRANCH
    if (_WxFront.w <= 0) return 0;
    return saturate((dot(q, _WxFront.xy) - _WxFront.z + (n - 0.5) * 3000) * _WxFront.w + 0.5);
}

// The cloud from a field value: 0 clear, 1 cloud, so a cover of c clouds a share c of the sky. Far off (a coarse mip) the edges
// soften, so the cells blur into the distance rather than step.
float WxCloudFrom(float n, float cover, float ahead, float lod)
{
    cover = lerp(cover, _WxFrontOld.x, ahead);
    float edge = 1 - cover, soft = _WxCloudScale.w + 0.035 * max(0, lod - 2);
    return smoothstep(edge - soft, edge + soft, n);
}

// Where along cam→p (0-1) the line of sight is between heights lo and hi.
float2 WxSpan(float a, float dy, float lo, float hi)
{
    UNITY_BRANCH
    if (abs(dy) < 0.01) return (a > lo && a < hi) ? float2(0, 1) : float2(1, 0);
    float t0 = (lo - a) / dy, t1 = (hi - a) / dy;
    return float2(saturate(min(t0, t1)), saturate(max(t0, t1)));
}

float WxKnee(float tau, float most) { return most * (1 - exp(-tau / max(most, 1e-3))); }

// What the air between the camera and p does: the general layers (haze, deck, curtains) as one amount and colour, and the
// valley sea under them as another, so a sea seen from above keeps its own flat bright top.
struct WxAir { float amount; float3 colour; float sea; float3 seaColour; };

WxAir WxAirTo(float3 p, bool sky)
{
    WxAir air = (WxAir)0;
    float3 cam = _WorldSpaceCameraPos;
    float3 v = p - cam;
    float d = length(v);
    float past = max(0, d - _WxParams.z);
    UNITY_BRANCH
    if (past <= 0) return air;
    float k = past / d * _WxParams.y;
    float3 dir = v / d;
    float graze = abs(dir.y);
    float dy = v.y;
    // The field's mip and the region's cover only for the layers that read the field: a clear day's haze needs neither.
    float lod = 0, cover = 0;
    UNITY_BRANCH
    if (_WxDeck.z > 0 || _WxCurtain.x > 0 || _WxValley.x > -90000)
    {
        lod = WxLod(d, graze);
        cover = WxCover(p.xz + _WxCloud.zw);
    }
    float3 sunL = _WxSun.rgb, amb = _WxLight.rgb;
    float sunUp = saturate(_SkySunDirection.y * 3 + 0.15);

    // Haze: exponential in height around the target's, so the valleys fill first, the near mountain keeps its contrast and the
    // far ridges step back in layers. Its colour darkens toward the valley floor and glows toward the sun.
    float tauH = 0;
    float3 colourH = 0;
    UNITY_BRANCH
    if (_WxHaze.x > 0)
    {
        float ya = clamp((cam.y - _WxHaze.y) * _WxHaze.z, -6, 30), yb = clamp((p.y - _WxHaze.y) * _WxHaze.z, -6, 30);
        float s = yb - ya;
        float f = abs(s) < 1e-3 ? exp(-ya) : (exp(-ya) - exp(-yb)) / s;
        tauH = _WxHaze.x * d * f * k;
        float lift = saturate((lerp(p.y, cam.y, 0.25) - _WxFogLow.w) * _WxFogColor.w);
        colourH = lerp(_WxFogLow.rgb, _WxFogColor.rgb, lift) + sunL * _WxHaze.w * pow(saturate(dot(dir, normalize(_SkySunDirection))), 8);
    }

    // The deck: the cloud field in the air between its base and top. Sampled where the line of sight enters and leaves it, so
    // its holes hang over the mountain; lit on top toward the sun (a third tap, only seen from above), dark at its base with a
    // bright rim where it thins toward the sun (seen from below).
    float tauD = 0;
    float3 colourD = 0;
    UNITY_BRANCH
    if (_WxDeck.z > 0 && !(sky && dir.y > 0 && cam.y < _WxDeck.x))
    {
        float2 t = WxSpan(cam.y, dy, _WxDeck.x, _WxDeck.y);
        UNITY_BRANCH
        if (t.y > t.x)
        {
            float3 pin = cam + v * t.x, pout = cam + v * t.y;
            float2 qin = pin.xz + _WxCloud.zw, qout = pout.xz + _WxCloud.zw;
            float nin = WxCloudField(qin, lod, false), nout = WxCloudField(qout, lod, false);
            float ahead = WxFrontAt(qin, nin);
            float cin = WxCloudFrom(nin, cover, ahead, lod), cout = WxCloudFrom(nout, cover, ahead, lod);
            // The base wavers with the field where it's crossed, so summits go into a ragged base, not a ruled line.
            float rag = (nin - 0.5) * _WxDeck.w;
            float2 tr = WxSpan(cam.y, dy, _WxDeck.x + rag, _WxDeck.y);
            float dens = 0.5 * (cin + cout) * lerp(0.25, 1, 0.5 * (nin + nout)) * lerp(_WxFrontScale.x, _WxFrontScale.y, ahead);
            tauD = _WxDeck.z * max(0, tr.y - tr.x) * d * dens * k;
            // Its colour by how deep in the deck the line of sight first meets it: the dark base from below (a bright rim where
            // it thins toward the sun), the lit top from above, and between them from inside it, so a camera in the cloud sees
            // no seam between looking up and looking down.
            float3 l = normalize(_SkySunDirection);
            float depth = saturate((lerp(pin.y, pout.y, 0.25) - _WxDeck.x) / max(_WxDeck.y - _WxDeck.x, 1));
            float rim = cin * (1 - cin) * 4 * pow(saturate(dot(dir, l)), 4) * sunUp * (1 - depth);
            colourD = _WxFogLow.rgb * _WxLight.w + sunL * (0.03 + 0.9 * rim);
            UNITY_BRANCH
            if (depth > 0.02)
            {
                float nsun = WxCloudField(qin + l.xz * 300, lod, false);
                float lit = saturate(0.55 + (nin - nsun) * 3) * sunUp;
                colourD = lerp(colourD, 0.8 * (amb + sunL * (0.15 + 0.5 * lit)), smoothstep(0.05, 0.95, depth));
            }
        }
    }

    // Snow curtains (B6): under the deck's densest cores the falling snow hangs in slanted grey sheets, so a storm reads as showers
    // sweeping the mountain. Three taps along the path below the deck (a tree takes one), each traced up the fall's slant.
    float tauC = 0;
    UNITY_BRANCH
    if (_WxCurtain.x > 0)
    {
        float2 t = WxSpan(cam.y, dy, -100000, _WxDeck.x);
        UNITY_BRANCH
        if (t.y > t.x)
        {
            float edge = 1 - _WxCurtain.y * cover;
            float shower = 0;
#if defined(WX_COARSE_SHADOW)
            const int taps = 1;
#else
            const int taps = 3;
#endif
            [unroll] for (int i = 0; i < taps; i++)
            {
                float3 m = cam + v * lerp(t.x, t.y, (i + 0.5) / taps);
                float2 qc = m.xz + _WxCurtain.zw * max(0, _WxDeck.x - m.y) + _WxCloud.zw;
                float nc = WxCloudField(qc, lod, false);
                shower += smoothstep(edge - 0.08, edge + 0.08, nc) * lerp(_WxFrontScale.z, _WxFrontScale.w, WxFrontAt(qc, nc));
            }
            tauC = _WxCurtain.x * (t.y - t.x) * d * (shower / taps) * k;
        }
    }

    float tau = tauH + tauD + tauC;
    UNITY_BRANCH
    if (tau > 1e-5)
    {
        air.amount = WxKnee(tau, _WxParams.w);
        // The curtains are the darkest thing in the air: a shower darkens the ground under it.
        air.colour = (tauH * colourH + tauD * colourD + tauC * _WxFogLow.rgb * 0.78) / tau;
    }

    // The valley sea: below its flat top (ragged by the field where it's crossed), nearly opaque, bright and sunlit from above.
    UNITY_BRANCH
    if (_WxValley.x > -90000)
    {
        float2 t = WxSpan(cam.y, dy, -100000, _WxValley.x);
        UNITY_BRANCH
        if (t.y > t.x)
        {
            float3 e = cam + v * t.x;
            float n = WxCloudField(e.xz * 0.5 + _WxCloud.zw * 0.2, lod, false);
            float2 tr = WxSpan(cam.y, dy, -100000, _WxValley.x + (n - 0.5) * _WxValley.w);
            float tauV = _WxValley.y * max(0, tr.y - tr.x) * d * lerp(0.7, 1, n) * _WxParams.y;
            air.sea = WxKnee(tauV, _WxValley.z);
            bool above = cam.y > _WxValley.x;
            air.seaColour = above ? 0.9 * (amb * 1.1 + sunL * (0.25 + 0.55 * n) * sunUp) : lerp(_WxFogLow.rgb, _WxFogColor.rgb, 0.4);
        }
    }
    return air;
}

// The snow line while it falls (F5): the ground below it wet and darker, a faint brighter fringe just above it (relative to
// the light, so it never glows at night), so the player sees where rain turns to snow. A weather veil only: the snow cover
// itself is 4.07's. The line wavers with the cloud field's detail, not a regular wave.
half3 WxSnowLineVeil(half3 colour, float3 p)
{
    UNITY_BRANCH
    if (_WxSnowLine.y <= 0) return colour;
    float waver = (SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, p.xz * 0.0011, 1).g - 0.5) * 2.5 * _WxSnowLine.z;
    float h = (p.y - _WxSnowLine.x + waver) / _WxSnowLine.z;
    half below = (half)(1 - smoothstep(-1, 0, h));
    colour *= 1 - below * (half)(_WxSnowLine.w * _WxSnowLine.y);
#if !defined(WX_COARSE_SHADOW)
    half fringe = (half)(smoothstep(-0.4, 0, h) * (1 - smoothstep(0, 1.5, h)));
    colour *= 1 + fringe * (half)(0.22 * _WxSnowLine.y);
#endif
    return colour;
}

half3 WxApplyAir(half3 colour, in WxAir air, half keep)
{
    colour = lerp(colour, (half3)air.seaColour, (half)min(air.sea, 1 - keep));
    return lerp(colour, (half3)air.colour, (half)min(air.amount, 1 - keep));
}

// How much the weather fogs p (0-1), for anything that blends rather than colours.
float WeatherFogAmount(float3 p)
{
#if !defined(WX_AIR)
    return 0;
#endif
    UNITY_BRANCH
    if (_WxParams.x < 0.5 || _WxParams.y <= 0) return 0;   // y 0: nothing in the air (a clear day), so no fog work
    WxAir air = WxAirTo(p, false);
    return 1 - (1 - air.amount) * (1 - air.sea);
}

half3 WeatherFog(half3 colour, float3 p)
{
#if !defined(WX_AIR)
    return colour;
#endif
    UNITY_BRANCH
    if (_WxParams.x < 0.5 || _WxParams.y <= 0) return colour;
    return WxApplyAir(WxSnowLineVeil(colour, p), WxAirTo(p, false), 0);
}

// The fog on something that must stay readable (lifts, cables, skier figures): it fogs like the ground but keeps at least
// keep of its own colour, so a lift's silhouette still shows in a white-out.
half3 WeatherFogKeep(half3 colour, float3 p, half keep)
{
#if !defined(WX_AIR)
    return colour;
#endif
    UNITY_BRANCH
    if (_WxParams.x < 0.5 || _WxParams.y <= 0) return colour;
    return WxApplyAir(colour, WxAirTo(p, false), keep);
}

// The ground's surface in the weather (the terrain calls it once): under a closed deck the light is flat, so the snow's fine
// relief fades from view; below the snow line while it rains the ground is wet and takes a soft sheen.
void WeatherSurface(inout float3 normal, inout float smooth, float3 geometric, float3 p)
{
#if !defined(WX_AIR)
    return;   // a closed deck and the rain both fill the air
#endif
    UNITY_BRANCH
    if (_WxParams.x < 0.5) return;
    UNITY_BRANCH
    if (_WxCloud.x > 0.7) normal = normalize(lerp(normal, geometric, smoothstep(0.7, 1, _WxCloud.x) * 0.55));
    UNITY_BRANCH
    if (_WxSnowLine.y > 0)
    {
        float below = 1 - smoothstep(-_WxSnowLine.z, 0, p.y - _WxSnowLine.x);
        smooth = lerp(smooth, max(smooth, 0.6), below * _WxSnowLine.y * 0.8);
    }
}

// A gale seen from the overview (F5): gust patches run downwind over the far forest, the crowns bending and paling in them, so
// the wind reads from 5 km (the near trees sway for real). The impostors multiply their light by this; 1 with no gale.
half WeatherGust(float3 p)
{
    UNITY_BRANCH
    if (_WxParams.x < 0.5 || _WxGust.z <= 0) return 1;
    float along = dot(p.xz, _WxGust.xy) - _WxGust.w;
    float across = dot(p.xz, float2(-_WxGust.y, _WxGust.x));
    float patch = SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, float2(along, across) * (1.0 / 2100), 0).g;
    float crest = 0.5 + 0.5 * sin(along * (6.2831853 / 300) + patch * 4);
    return 1 + (half)(_WxGust.z * 0.16 * crest * crest * smoothstep(0.35, 0.8, patch));
}

// The bluebird's payoff (F5, WX-53): fresh snow sparkles in the sun near the camera, crystals catching it one by one as the view
// moves. Cells a few pixels across at their distance (in power-of-two steps, so the pattern holds still as the camera moves),
// each with a facet of its own; it glints when that facet mirrors the sun into the eye. Added to the terrain's highlight (so
// shadow and the sun's colour still apply); only on snow-bright ground, gone by _WxGlint.y.
float WeatherGlint(float3 p, float3 normal, float3 h, float3 albedo)
{
#if !defined(WX_GLINT)
    return 0;
#endif
    UNITY_BRANCH
    if (_WxParams.x < 0.5 || _WxGlint.x <= 0) return 0;
    float dist = distance(p, _WorldSpaceCameraPos);
    float near = 1 - smoothstep(_WxGlint.y * 0.5, _WxGlint.y, dist);
    float snow = smoothstep(0.55, 0.75, dot(albedo, float3(0.2126, 0.7152, 0.0722)));
    UNITY_BRANCH
    if (near * snow <= 0) return 0;
    float cell = exp2(ceil(log2(max(dist * _WxPixel.x * 2.5, 0.02))));
    float3 q = p / cell;
    uint3 c = (uint3)(int3)floor(q);
    uint s = c.x * 73856093u ^ c.y * 19349663u ^ c.z * 83492791u;
    s = (s ^ (s >> 16)) * 0x7FEB352Du;
    s = (s ^ (s >> 15)) * 0x846CA68Bu;
    s ^= s >> 16;
    float3 r = float3(s & 1023u, (s >> 10) & 1023u, (s >> 20) & 1023u) / 1023.0;
    // Crystals lie every which way, so some mirror even a low sun into a low eye.
    float3 facet = normalize(normal + (r - 0.5) * 2.4);
    float g = pow(saturate(dot(facet, h)), 400);
    float spot = 1 - smoothstep(0.08, 0.3, length(frac(q) - 0.5 - (r.zxy - 0.5) * 0.4));
    return min(g * spot * snow * near * _WxGlint.x * 40, 8);
}

// The clouds' shadow on a surface (1: lit): the cloud where the sun's ray from p meets the deck's plane, read from the map
// WeatherView draws each frame (the field, its eroded edges, the cover and the front, worked out once per texel rather than per
// pixel). Beyond the map, the region's cover on average: the haze has the ground by then. Soft-edged, and they drift.
// No branch: the tap goes out early and its wait hides behind the lighting's own work (behind a branch it measured 0.15-0.3 ms
// near the ground). With the weather off _WxParams.x is 0, so whatever the map holds changes nothing.
half CloudShadow(float3 p)
{
    float2 uv = (p.xz + _WxSunRun.xy * max(0, _WxCloudScale.z - p.y) - _WxShadowAt.xy) * _WxShadowAt.z;
    float c = SAMPLE_TEXTURE2D_LOD(_WxShadowMap, sampler_WxShadowMap, uv, 0).r;
    float2 e = min(uv, 1 - uv);
    c = lerp(_WxCloud.x, c, saturate(min(e.x, e.y) * 20));
    return 1 - c * (_WxCloud.y * _WxParams.x);
}

#endif
