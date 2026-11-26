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
float SnowLift8At(float3 positionWS)
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
float2 SnowLift8Slope(float3 positionWS)
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

// Task 4.08 PR A: the fine trail snow (SN-03 v3's 1, 2 or 4 m cells) over the 8 m lift. FineSnowAtlas keeps one slot per
// 64 m tile that holds trail snow, each with a three-cell gutter from its neighbours so the curve needs one slot:
// - _FineLiftAtlas (RG16, bilinear): R the fine lift (depth × the 8 m keep, mm ÷ 65535) where the cell has a block, else 0;
//   G 1 where it has a block, else 0. Through the curve G fades from 1 on the trail's blocks to 0 a cell or two past them.
// - _FineStateAtlas (RGBA8, by texel): type and finish bits, moguls, passes ÷ 4, age (FineStagingRing.Pack).
// - _FinePageTable (R float, by texel, one per 64 m tile of the 8 m grid): the slot + 1, 0 where no tile is kept.
// The lift is L8 × (1 − G) + R: the 8 m snow off the trails, the fine snow on them, blended over the trail's edge cells.
TEXTURE2D(_FineLiftAtlas); SAMPLER(sampler_FineLiftAtlas);
TEXTURE2D(_FineStateAtlas);
TEXTURE2D(_FinePageTable);
float4 _FineParams;   // x: on (0/1), y: fine cell (m), z: fine cells a tile's side (T), w: a slot's side with its gutters (T + 6)
float4 _FineGrid;     // x, y: tiles across and up the 8 m grid; z: slots a row of the atlas; w: 1 / the atlas's side (texels)

static const float FineLiftMetres = 65.535;   // R's 1.0, metres

// A tile's slot origin in the atlas (texels, where the tile's cell (0, 0) would be); false where the tile has no slot.
bool FineSlotOf(int2 tile, out float2 origin)
{
    origin = 0;
    if (any(tile < 0) || any(tile >= int2(_FineGrid.xy))) return false;
    float page = LOAD_TEXTURE2D(_FinePageTable, tile).r;
    if (page < 0.5) return false;
    float slot = page - 1;
    float row = floor(slot / _FineGrid.z);
    float gutter = (_FineParams.w - _FineParams.z) * 0.5;
    origin = float2(slot - row * _FineGrid.z, row) * _FineParams.w + gutter - tile * _FineParams.z;
    return true;
}

// The fine cell coordinates of a point (cell centres on whole numbers) and the slot whose cells and gutter hold the curve's
// 4 × 4 cells round it: the point's own tile's; in a tile with no slot, a neighbour's the curve reaches into (from the
// tile's last two cells or its first, which the three-cell gutter covers), so a trail's edge blends the same on both sides
// of a tile's border. False where none has one: every cell the curve reads is then bare of fine snow.
bool FineSlot(float2 xz, out float2 st, out float2 origin)
{
    st = (xz - _SnowDepthRect.xy) / _FineParams.y - 0.5;
    origin = 0;
    UNITY_BRANCH
    if (_FineParams.x <= 0) return false;
    float T = _FineParams.z;
    float2 i = floor(st);
    int2 home = int2(floor(i / T));
    if (FineSlotOf(home, origin)) return true;
    float2 local = i - home * T;
    int2 step = int2(local >= T - 2) - int2(local < 1);
    UNITY_BRANCH
    if (all(step == 0)) return false;
    if (step.x != 0 && FineSlotOf(home + int2(step.x, 0), origin)) return true;
    if (step.y != 0 && FineSlotOf(home + int2(0, step.y), origin)) return true;
    return step.x != 0 && step.y != 0 && FineSlotOf(home + step, origin);
}

float2 FineTap(float2 texel) { return SAMPLE_TEXTURE2D_LOD(_FineLiftAtlas, sampler_FineLiftAtlas, texel * _FineGrid.w, 0).rg; }

// R (metres) and G at a point through the curve, and their slopes per metre (x, z); all 0 off the kept tiles.
void FineLift(float3 positionWS, out float r, out float g, out float2 dr, out float2 dg, bool slopes)
{
    r = 0; g = 0; dr = 0; dg = 0;
    float2 st, origin;
    if (!FineSlot(positionWS.xz, st, origin)) return;
    float2 i = floor(st), f = st - i;
    float2 w0, w1, w2, w3;
    SnowSplineWeights(f, w0, w1, w2, w3);
    float2 g0 = w0 + w1, g1 = w2 + w3;
    float2 p0 = origin + i - 1 + w1 / g0 + 0.5, p1 = origin + i + 1 + w3 / g1 + 0.5;
    float2 v = g0.y * (g0.x * FineTap(float2(p0.x, p0.y)) + g1.x * FineTap(float2(p1.x, p0.y))) +
               g1.y * (g0.x * FineTap(float2(p0.x, p1.y)) + g1.x * FineTap(float2(p1.x, p1.y)));
    r = max(0, v.x) * FineLiftMetres; g = saturate(v.y);
    if (!slopes) return;
    float2 d0 = -(1 - f) * (1 - f) / 2, d1 = f * (3 * f - 4) / 2, d2 = (1 + 2 * f - 3 * f * f) / 2, d3 = f * f / 2;
    float2 h0 = d0 + d1, h1 = d2 + d3;
    float2 q0 = origin + i - 1 + d1 / min(h0, -1e-6) + 0.5, q1 = origin + i + 1 + d3 / max(h1, 1e-6) + 0.5;
    float2 dx = g0.y * (h0.x * FineTap(float2(q0.x, p0.y)) + h1.x * FineTap(float2(q1.x, p0.y))) +
                g1.y * (h0.x * FineTap(float2(q0.x, p1.y)) + h1.x * FineTap(float2(q1.x, p1.y)));
    float2 dz = h0.y * (g0.x * FineTap(float2(p0.x, q0.y)) + g1.x * FineTap(float2(p1.x, q0.y))) +
                h1.y * (g0.x * FineTap(float2(p0.x, q1.y)) + g1.x * FineTap(float2(p1.x, q1.y)));
    float perMetre = 1 / _FineParams.y;
    dr = float2(dx.x, dz.x) * FineLiftMetres * perMetre;
    dg = float2(dx.y, dz.y) * perMetre;
}

// The lift (metres) at a point: the 8 m curve off the trails, the fine snow on them (× the scale; 0 while it's off).
float SnowLiftAt(float3 positionWS)
{
    UNITY_BRANCH
    if (_SnowLiftParams.x <= 0) return 0;
    float l8 = SnowLift8At(positionWS);
    float r, g; float2 dr, dg;
    FineLift(positionWS, r, g, dr, dg, false);
    return max(0, l8 * (1 - g) + r * _SnowLiftParams.x);
}

// The lift's slope (metres per metre, x and z): the 8 m curve's exact derivative blended into the fine curve's.
float2 SnowLiftSlope(float3 positionWS)
{
    UNITY_BRANCH
    if (_SnowLiftParams.x <= 0) return 0;
    float2 s8 = SnowLift8Slope(positionWS);
    float r, g; float2 dr, dg;
    FineLift(positionWS, r, g, dr, dg, true);
    UNITY_BRANCH
    if (g <= 0 && all(dg == 0)) return s8;
    float l8 = SnowLift8At(positionWS);
    return s8 * (1 - g) - dg * l8 + dr * _SnowLiftParams.x;
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
static const float3 SnowTypeColours[12] =
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
    float3(0.549, 0.769, 0.941),   // 11 machine made      #8CC4F0  fresh (task 4.08, SN-13 v2)
};

float3 SnowTypeColour(int2 cell)
{
    cell = clamp(cell, int2(0, 0), int2(_SnowDepthSize.xy) - 1);
    int type = (int)round(LOAD_TEXTURE2D(_SnowSurfaceMap, cell).r * 255);
    return SnowTypeColours[clamp(type, 0, 11)];
}

// Task 4.25 PR B (GO-14): how much corduroy the snow under a point carries, from the simulation's surface type: machine
// groomed in full, a groomed surface skied to packed powder or hard pack fading, anything else (powder, ice, spring snow,
// bare) none. The four cells round the point blend bilinearly (4 loads; the corduroy's own ridges carry the detail). Before
// the snow map is on (no simulation yet) every trail keeps the full corduroy it had before.
float SnowCorduroyOf(int2 cell)
{
    cell = clamp(cell, int2(0, 0), int2(_SnowDepthSize.xy) - 1);
    int type = (int)round(LOAD_TEXTURE2D(_SnowSurfaceMap, cell).r * 255);
    return type == 3 ? 1.0 : type == 2 ? 0.45 : type == 4 ? 0.25 : 0.0;
}

// Task 4.08 PR A (GO-14 v2, SN-14 v4): on the trails the corduroy follows the groomed finish, not the type: a fine cell
// groomed since the last powder reset carries it in full when new, fading to a quarter as it is skied (to HardPasses, 800
// passes; the staged passes are ÷ 4), whatever its type. Off the fine tiles the 8 m type stands in, as before.
float FineCorduroyOf(int2 texel)
{
    float4 st = LOAD_TEXTURE2D(_FineStateAtlas, texel);
    int b0 = (int)round(st.r * 255);
    if ((b0 & 128) == 0 || (b0 & 64) == 0) return 0;
    return lerp(1.0, 0.25, saturate(st.b * 255 * 4 / 800));
}

float SnowCorduroyAt(float3 positionWS)
{
    UNITY_BRANCH
    if (_SnowDepthParams.w < 0.5) return 1;
    float2 fst, origin;
    UNITY_BRANCH
    if (FineSlot(positionWS.xz, fst, origin))
    {
        float2 fi = floor(fst), ff = fst - fi;
        int2 t = int2(origin + fi);
        float fa = lerp(FineCorduroyOf(t), FineCorduroyOf(t + int2(1, 0)), ff.x);
        float fb = lerp(FineCorduroyOf(t + int2(0, 1)), FineCorduroyOf(t + int2(1, 1)), ff.x);
        return lerp(fa, fb, ff.y);
    }
    float2 st = SnowUV(positionWS.xz) * _SnowDepthSize.xy - 0.5;
    int2 c = int2(floor(st));
    float2 f = st - floor(st);
    float a = lerp(SnowCorduroyOf(c), SnowCorduroyOf(c + int2(1, 0)), f.x);
    float b = lerp(SnowCorduroyOf(c + int2(0, 1)), SnowCorduroyOf(c + int2(1, 1)), f.x);
    return lerp(a, b, f.y);
}

// Task 4.08 PR B (SN-13 v3's look table): the snow's type shows in how it takes the light, not in its colour. Three separate
// things carry it (owner, 2026-11-24): shine, a glassy reflection of the sky and the sun (ice); sparkle, a twinkle across
// the snow (powder, corn); and the surface itself, how softly the sun wraps it. The tints stay within a few percent of plain
// snow and never above 1 (p4-08-findings R1, R15). The finish (groomed, skied, untouched) is drawn over the type. One row a
// type (SnowType):
// - SnowLookTint: rgb the tint over the snow layer's albedo, its family's colour; a how softly the sun wraps it (powder
//   pillowy, ice crisp).
// - SnowLookShine: x how much of the surface can shine; y how glassy the shine is (0 a broad glow, 1 near a mirror); z how
//   much of the ground the shiny patches cover; w the faint mottle that survives from far away.
// - SnowLookSparkle: x the twinkle's brightness; y the share of crystals that catch the sun; z the crystals' size (m);
//   w 1 for new snow, pristine while under SnowFreshHours.
// The surface itself is smooth for every type (owner, 2026-11-25: no textures; the types are told by colour and
// reflectivity). Colour tells four families apart, at about a third of round 1's strength: fresh (powder) the brightest
// white; packed and groomed a neutral white; hard and icy cooler, blue-grey; spring and wet a little darker and greyer.
// Shine and sparkle tell the types apart inside a family.
// - SnowLookTracks: how deep skis mark it: powder in full, ice nearly not at all.
// Presentation/SnowLooks.cs holds the same rows (SnowLooksTests checks they match). Row 0 is the snow before the
// simulation's types reach the screen.
static const float4 SnowLookTint[12] =
{
    float4(1.000, 1.000, 1.000, 0.15),   // 0 none (before the field is on)
    float4(1.000, 1.000, 1.000, 0.25),   // 1 powder (fresh): the whitest, pillowy
    float4(0.985, 0.988, 0.992, 0.12),   // 2 packed powder (packed and groomed): a neutral white
    float4(0.975, 0.980, 0.988, 0.10),   // 3 machine groomed (packed and groomed)
    float4(0.950, 0.961, 0.980, 0.10),   // 4 hard packed (hard and icy): cooler, blue-grey
    float4(0.980, 0.984, 0.992, 0.05),   // 5 icy (hard and icy): and it mirrors the sky
    float4(0.917, 0.902, 0.883, 0.15),   // 6 corn (spring and wet): a little darker, warm grey
    float4(0.962, 0.970, 0.984, 0.08),   // 7 frozen granular (hard and icy)
    float4(0.930, 0.921, 0.908, 0.12),   // 8 loose granular (spring and wet)
    float4(0.950, 0.948, 0.944, 0.10),   // 9 wet granular (spring and wet): the darkest, wet
    float4(0.883, 0.871, 0.860, 0.18),   // 10 wet powder (spring and wet): heavy
    float4(0.990, 0.995, 1.000, 0.10),   // 11 machine made (packed and groomed): bright, very even
};
static const float4 SnowLookShine[12] =
{
    float4(0.00, 0.30, 0.00, 0.00),   // 0
    float4(0.00, 0.20, 0.00, 0.00),   // 1 powder: no shine at all
    float4(0.04, 0.35, 0.30, 0.01),   // 2 packed powder: a trace
    float4(0.05, 0.35, 0.40, 0.01),   // 3 machine groomed: a trace
    float4(0.30, 0.50, 0.45, 0.02),   // 4 hard packed: a soft gloss at low angles
    float4(1.00, 0.85, 0.75, 0.03),   // 5 icy: glassy patches over most of it
    float4(0.00, 0.20, 0.00, 0.03),   // 6 corn: sparkles, never shines
    float4(0.55, 0.60, 0.55, 0.03),   // 7 frozen granular: patchy shine
    float4(0.00, 0.20, 0.00, 0.02),   // 8 loose granular
    float4(0.30, 0.70, 1.00, 0.04),   // 9 wet granular: a wet gloss all over
    float4(0.12, 0.55, 1.00, 0.02),   // 10 wet powder: a slight wet gloss
    float4(0.03, 0.35, 0.30, 0.00),   // 11 machine made
};
static const float4 SnowLookSparkle[12] =
{
    float4(0.60, 0.50, 0.004, 0),   // 0
    float4(1.00, 0.90, 0.003, 1),   // 1 powder: the most, fine and dense
    float4(0.45, 0.50, 0.004, 0),   // 2 packed powder: some
    float4(0.35, 0.40, 0.004, 0),   // 3 machine groomed: a little
    float4(0.15, 0.20, 0.004, 0),   // 4 hard packed: little
    float4(0.00, 0.00, 0.004, 0),   // 5 icy: none (it shines instead)
    float4(1.30, 0.45, 0.030, 0),   // 6 corn: fewer than powder's, coarse and bold
    float4(0.50, 0.30, 0.020, 0),   // 7 frozen granular: a few hard glints
    float4(0.90, 0.60, 0.012, 0),   // 8 loose granular: sugary
    float4(0.00, 0.00, 0.004, 0),   // 9 wet granular: none
    float4(0.00, 0.00, 0.004, 1),   // 10 wet powder: none
    float4(0.35, 0.60, 0.002, 0),   // 11 machine made: fine, some
};
static const float SnowLookTracks[12] = { 0.0, 1.0, 0.35, 0.30, 0.15, 0.03, 0.50, 0.05, 0.50, 0.50, 0.80, 0.80 };

// Older snow loses its sparkle and goes a little duller (age in face hours, up to four face days).
static const float SnowLookAgeHours = 96;
// New snow is pristine for its first half face day: softer, more sparkle.
static const float SnowFreshHours = 12;

struct SnowSurface
{
    float3 tint;
    float soft;                          // how softly the sun wraps it (0 crisp)
    float sheen, gloss, patch, mottle;   // the shine: its amount, how glassy, the patches' cover, the far mottle
    float sparkle, density, grain;       // the twinkle: brightness, share of crystals, their size (m; the heaviest type's)
    float grainWeight;
    float tracks;
    float corduroy;   // as SnowCorduroyAt
    float moguls;     // 0-1, the fine cells' (none off the trails)
    float passes;     // the fine cells' passes since the last grooming or powder reset, weighted by their share (owned)
    float owned;      // the share of the point the trail's own fine cells answer
};

void SnowLookAdd(inout SnowSurface s, int type, float age, float w)
{
    type = clamp(type, 0, 11);
    float dull = saturate(age / SnowLookAgeHours);
    float4 t = SnowLookTint[type], sh = SnowLookShine[type], sp = SnowLookSparkle[type];
    // New snow, for its first half face day: pillowy, the crystals at their brightest.
    float fresh = sp.w * (1 - saturate(age / SnowFreshHours));
    s.tint += w * t.rgb * (1 - 0.03 * dull);
    s.soft += w * (t.a + 0.10 * fresh);
    s.sheen += w * sh.x;
    s.gloss += w * sh.y;
    s.patch += w * sh.z;
    s.mottle += w * sh.w;
    s.sparkle += w * sp.x * (1 - 0.8 * dull) * (1 + 0.25 * fresh);
    s.density += w * sp.y;
    // A crystal's size doesn't blend (two sizes blended would squeeze the pattern across a border, R14): the heaviest type's.
    if (w > s.grainWeight) { s.grainWeight = w; s.grain = sp.z; }
    s.tracks += w * SnowLookTracks[type];
}

void SnowLook8Add(inout SnowSurface s, int2 cell, float w, float corduroy)
{
    cell = clamp(cell, int2(0, 0), int2(_SnowDepthSize.xy) - 1);
    float4 st = LOAD_TEXTURE2D(_SnowSurfaceMap, cell);
    int type = (int)round(st.r * 255);
    SnowLookAdd(s, type, st.b * 255, w);
    s.corduroy += corduroy * w * (type == 3 ? 1.0 : type == 2 ? 0.45 : type == 4 ? 0.25 : 0.0);
}

// The snow's surface at a point: the look of its type, its corduroy, moguls and passes. On a trail's fine tiles a cell with
// a block speaks for itself (its type, finish, moguls and passes) and a cell without one takes the 8 m snow's look; elsewhere
// the four 8 m cells round the point blend bilinearly. Each fine cell is read once, and inside a trail, where the fine cells
// answer everything, the 8 m snow isn't read at all (R16). The corduroy is SnowCorduroyAt's, unchanged.
// edgeMetres: how wide the 8 m cells' blend is round their borders (the terrain passes SnowTypeEdgeMetres and a wandering
// point, SnowBorderWarp, so a type's border is an irregular line a few metres wide, never an 8 m ramp or a square's
// edge, R4); the fine cells keep their own bilinear blend, a cell wide.
SnowSurface SnowSurfaceAt(float3 positionWS, float edgeMetres)
{
    SnowSurface s = (SnowSurface)0;
    UNITY_BRANCH
    if (_SnowDepthParams.w < 0.5)
    {
        SnowLookAdd(s, 0, 0, 1);
        s.corduroy = 1;
        return s;
    }
    float2 fst, origin;
    float owned = 0, fine = 0;
    UNITY_BRANCH
    if (FineSlot(positionWS.xz, fst, origin))
    {
        fine = 1;
        float2 fi = floor(fst), ff = fst - fi;
        int2 t = int2(origin + fi);
        float wq[4] = { (1 - ff.x) * (1 - ff.y), ff.x * (1 - ff.y), (1 - ff.x) * ff.y, ff.x * ff.y };
        int2 dq[4] = { int2(0, 0), int2(1, 0), int2(0, 1), int2(1, 1) };
        [unroll] for (int q = 0; q < 4; q++)
        {
            float4 fs = LOAD_TEXTURE2D(_FineStateAtlas, t + dq[q]);
            int b0 = (int)round(fs.r * 255);
            if ((b0 & 128) == 0) continue;
            float passes = fs.b * 255 * 4;
            if ((b0 & 64) != 0) s.corduroy += wq[q] * lerp(1.0, 0.25, saturate(passes / 800));   // FineCorduroyOf's
            owned += wq[q];
            SnowLookAdd(s, b0 & 15, fs.a * 255, wq[q]);
            s.moguls += wq[q] * fs.g;
            s.passes += wq[q] * passes;
        }
    }
    s.owned = owned;
    // The cells without a block: the 8 m snow's look in their share.
    UNITY_BRANCH
    if (owned < 0.999)
    {
        float rest = 1 - owned;
        float2 st = SnowUV(positionWS.xz) * _SnowDepthSize.xy - 0.5;
        int2 c = int2(floor(st));
        float2 f = st - floor(st);
        float cellMetres = 1 / (_SnowDepthRect.z * _SnowDepthSize.x);
        UNITY_FLATTEN
        if (edgeMetres < cellMetres)
        {
            f = saturate((f - 0.5) * (cellMetres / max(edgeMetres, 0.1)) + 0.5);
            f = f * f * (3 - 2 * f);
        }
        SnowLook8Add(s, c, rest * (1 - f.x) * (1 - f.y), 1 - fine);
        SnowLook8Add(s, c + int2(1, 0), rest * f.x * (1 - f.y), 1 - fine);
        SnowLook8Add(s, c + int2(0, 1), rest * (1 - f.x) * f.y, 1 - fine);
        SnowLook8Add(s, c + int2(1, 1), rest * f.x * f.y, 1 - fine);
    }
    return s;
}

// The plain blend (the 8 m cells' bilinear), as the probes read it.
SnowSurface SnowSurfaceAt(float3 positionWS) { return SnowSurfaceAt(positionWS, 1e4); }

// How wide a type's border is on the screen (m), round the wandering line SnowBorderWarp draws.
static const float SnowTypeEdgeMetres = 3;

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
