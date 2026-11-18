// Task 4.07 PR A2 (SN-29 v2, owner 2026-11-13: the snow map is fluid and smooth, never jagged): the 8 m snow field as every
// shader reads it, through one curved filter. SurfaceStates sets the globals: the depth map (metres, the worker's tiles),
// the surface map (four bytes a cell: type, quality ÷ 4, age in face hours, density ÷ 4) and their placement.
// - Depth: a cubic B-spline over the 4 × 4 cells round the point, in four bilinear reads. It is smooth to the second
//   derivative everywhere, so a melt edge or a drift reads as a curve, never as 8 m squares or a crease along their
//   borders; it never overshoots (no negative snow).
// - Conditions: the same weights over the 4 × 4 cells' type colours (16 texel loads, only while that layer shows), so two
//   types blend across their border instead of meeting at a square's edge.
// MapLayers' C# (SnowCursor) evaluates the same curve for the cursor's readout.
#ifndef MP_SNOW_INCLUDED
#define MP_SNOW_INCLUDED

TEXTURE2D(_SnowDepthMap); SAMPLER(sampler_SnowDepthMap);
TEXTURE2D(_SnowSurfaceMap);
float4 _SnowDepthRect;       // west, south, 1 / width, 1 / depth (world metres)
float4 _SnowDepthParams;     // x: depth for full cover (m), w: on (0/1)
float4 _SnowDepthSize;       // the maps in cells: width, depth, 1 / width, 1 / depth

float2 SnowUV(float2 xz) { return (xz - _SnowDepthRect.xy) * _SnowDepthRect.zw; }

// The cubic B-spline's four weights for a fraction f past the second of four cells.
void SnowSplineWeights(float2 f, out float2 w0, out float2 w1, out float2 w2, out float2 w3)
{
    float2 f2 = f * f, f3 = f2 * f;
    w0 = (1 - 3 * f + 3 * f2 - f3) / 6;
    w1 = (4 - 6 * f2 + 3 * f3) / 6;
    w2 = (1 + 3 * f + 3 * f2 - 3 * f3) / 6;
    w3 = f3 / 6;
}

// Depth (metres) through the curved filter: four bilinear reads, each placed so the hardware's weights make two of the
// spline's (the usual fast B-spline).
float SnowDepthSmooth(float2 uv)
{
    float2 st = uv * _SnowDepthSize.xy - 0.5;
    float2 i = floor(st);
    float2 w0, w1, w2, w3;
    SnowSplineWeights(st - i, w0, w1, w2, w3);
    float2 g0 = w0 + w1, g1 = w2 + w3;
    float2 p0 = (i - 1 + w1 / g0 + 0.5) * _SnowDepthSize.zw;
    float2 p1 = (i + 1 + w3 / g1 + 0.5) * _SnowDepthSize.zw;
    return g0.y * (g0.x * SAMPLE_TEXTURE2D_LOD(_SnowDepthMap, sampler_SnowDepthMap, float2(p0.x, p0.y), 0).r +
                   g1.x * SAMPLE_TEXTURE2D_LOD(_SnowDepthMap, sampler_SnowDepthMap, float2(p1.x, p0.y), 0).r) +
           g1.y * (g0.x * SAMPLE_TEXTURE2D_LOD(_SnowDepthMap, sampler_SnowDepthMap, float2(p0.x, p1.y), 0).r +
                   g1.x * SAMPLE_TEXTURE2D_LOD(_SnowDepthMap, sampler_SnowDepthMap, float2(p1.x, p1.y), 0).r);
}

// Task 4.07 PR A3: the snow stands on the ground. SurfaceStates keeps a lift map beside the depth (the depth × a keep that is
// 0 on lakes and ponds and at the ring's edge), switched on once the live snow's tiles have all come in. Round 3 (owner,
// 2026-11-17): the ground never moves; the snow shell, a second terrain over the same data (MountainTerrain's _SNOW_SHELL),
// lifts its vertices by this curve in every pass, so its picture, shadows and depth agree, and the C# twin
// (SnowCursor.LiftAt) gives the cursor, the skiers and the camera the same height. The Snow map layer shows or hides the
// shell at once.
TEXTURE2D(_SnowLiftMap); SAMPLER(sampler_SnowLiftMap);
float4 _SnowLiftParams;      // x: 1 once the live snow's first pass is in (the shell stands on it), 0 before

// The shell draws only where the snow is deeper than this (2 in); thinner snow is painted on the ground.
static const float ShellMinMetres = 0.05;

float SnowLiftTap(float2 uv) { return SAMPLE_TEXTURE2D_LOD(_SnowLiftMap, sampler_SnowLiftMap, uv, 0).r; }

// The lift (metres) at a point: the curved filter over the lift map × the scale, 0 while it's off.
float SnowLiftAt(float3 positionWS)
{
    UNITY_BRANCH
    if (_SnowLiftParams.x <= 0) return 0;
    float2 st = SnowUV(positionWS.xz) * _SnowDepthSize.xy - 0.5;
    float2 i = floor(st);
    float2 w0, w1, w2, w3;
    SnowSplineWeights(st - i, w0, w1, w2, w3);
    float2 g0 = w0 + w1, g1 = w2 + w3;
    float2 p0 = (i - 1 + w1 / g0 + 0.5) * _SnowDepthSize.zw;
    float2 p1 = (i + 1 + w3 / g1 + 0.5) * _SnowDepthSize.zw;
    return _SnowLiftParams.x * max(0, g0.y * (g0.x * SnowLiftTap(float2(p0.x, p0.y)) + g1.x * SnowLiftTap(float2(p1.x, p0.y))) +
                                      g1.y * (g0.x * SnowLiftTap(float2(p0.x, p1.y)) + g1.x * SnowLiftTap(float2(p1.x, p1.y))));
}

// The lift's slope (metres per metre, along x and z): the curve's exact derivative. Its weights pair with one sign each
// (the first two ≤ 0, the last two ≥ 0), so it takes the same bilinear trick: four reads a direction.
float2 SnowLiftSlope(float3 positionWS)
{
    UNITY_BRANCH
    if (_SnowLiftParams.x <= 0) return 0;
    float2 st = SnowUV(positionWS.xz) * _SnowDepthSize.xy - 0.5;
    float2 i = floor(st), f = st - i;
    float2 w0, w1, w2, w3;
    SnowSplineWeights(f, w0, w1, w2, w3);
    float2 d0 = -(1 - f) * (1 - f) / 2, d1 = f * (3 * f - 4) / 2, d2 = (1 + 2 * f - 3 * f * f) / 2, d3 = f * f / 2;
    float2 g0 = w0 + w1, g1 = w2 + w3, h0 = d0 + d1, h1 = d2 + d3;
    float2 p0 = (i - 1 + w1 / g0 + 0.5) * _SnowDepthSize.zw, p1 = (i + 1 + w3 / g1 + 0.5) * _SnowDepthSize.zw;
    float2 q0 = (i - 1 + d1 / min(h0, -1e-6) + 0.5) * _SnowDepthSize.zw, q1 = (i + 1 + d3 / max(h1, 1e-6) + 0.5) * _SnowDepthSize.zw;
    float dx = g0.y * (h0.x * SnowLiftTap(float2(q0.x, p0.y)) + h1.x * SnowLiftTap(float2(q1.x, p0.y))) +
               g1.y * (h0.x * SnowLiftTap(float2(q0.x, p1.y)) + h1.x * SnowLiftTap(float2(q1.x, p1.y)));
    float dz = h0.y * (g0.x * SnowLiftTap(float2(p0.x, q0.y)) + g1.x * SnowLiftTap(float2(p1.x, q0.y))) +
               h1.y * (g0.x * SnowLiftTap(float2(p0.x, q1.y)) + g1.x * SnowLiftTap(float2(p1.x, q1.y)));
    // Per cell to per metre: a cell is 1 / (_SnowDepthRect.zw × _SnowDepthSize.xy) metres.
    return _SnowLiftParams.x * float2(dx, dz) * _SnowDepthRect.zw * _SnowDepthSize.xy;
}

// Snow depth in metres at a point (12 in until the field is set).
float SnowDepthAt(float3 positionWS)
{
    if (_SnowDepthParams.w < 0.5) return 0.3048;
    return max(0, SnowDepthSmooth(SnowUV(positionWS.xz)));
}

// How fully the snow covers the ground at a point (0 bare to 1 from _SnowDepthParams.x deep).
float SnowCoverAt(float3 positionWS)
{
    UNITY_BRANCH
    if (_SnowDepthParams.w < 0.5) return 1;
    return saturate(SnowDepthSmooth(SnowUV(positionWS.xz)) / _SnowDepthParams.x);
}

// The Snow conditions layer (info view 9): four families by colour, lighter and darker steps within each (checked for
// colour-blind readers as families: fresh blue, groomed and packed teal, hard and icy violet, spring amber; worst CVD ΔE
// 8.4, normal 18.9 over all pairs). Bare ground is the Snow depth layer's bare brown. Indexed by SimSnow's SnowType; the
// HUD's legend (InfoLegend) shows the same colours.
static const float3 SnowTypeColours[11] =
{
    float3(0.545, 0.455, 0.345),   // 0 bare               #8B7458
    float3(0.353, 0.635, 0.925),   // 1 powder             #5AA2EC  fresh
    float3(0.169, 0.710, 0.522),   // 2 packed powder      #2BB585  groomed and packed
    float3(0.086, 0.541, 0.369),   // 3 machine groomed    #168A5E  groomed and packed
    float3(0.718, 0.478, 0.788),   // 4 hard packed        #B77AC9  hard and icy
    float3(0.431, 0.184, 0.533),   // 5 icy                #6E2F88  hard and icy
    float3(0.961, 0.753, 0.290),   // 6 corn               #F5C04A  spring
    float3(0.604, 0.310, 0.690),   // 7 frozen granular    #9A4FB0  hard and icy
    float3(0.929, 0.631, 0.000),   // 8 loose granular     #EDA100  spring
    float3(0.769, 0.498, 0.000),   // 9 wet granular       #C47F00  spring
    float3(0.173, 0.455, 0.776),   // 10 wet powder        #2C74C6  fresh
};

float3 SnowTypeColour(int2 cell)
{
    cell = clamp(cell, int2(0, 0), int2(_SnowDepthSize.xy) - 1);
    int type = (int)round(LOAD_TEXTURE2D(_SnowSurfaceMap, cell).r * 255);
    return SnowTypeColours[clamp(type, 0, 10)];
}

// The conditions colour at a point, the types' colours blended by the same curve as the depth, then faded to bare by the
// snow's cover, as the ground draws it (owner's demo 111, 2026-11-15: a few mm left by a thaw read as wet granular on
// ground that looked bare). The readout says "Bare" below half cover (InfoLegend.SnowBareBelowMetres).
float3 SnowConditionsColour(float3 positionWS)
{
    float2 st = SnowUV(positionWS.xz) * _SnowDepthSize.xy - 0.5;
    float2 i = floor(st);
    float2 w[4];
    SnowSplineWeights(st - i, w[0], w[1], w[2], w[3]);
    int2 base = int2(i) - 1;
    float3 colour = 0;
    [unroll] for (int y = 0; y < 4; y++)
    {
        float3 row = 0;
        [unroll] for (int x = 0; x < 4; x++) row += w[x].x * SnowTypeColour(base + int2(x, y));
        colour += w[y].y * row;
    }
    return lerp(SnowTypeColours[0], colour, SnowCoverAt(positionWS));
}

#endif
