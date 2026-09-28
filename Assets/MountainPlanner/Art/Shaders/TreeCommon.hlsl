// Shared by the forest's mesh LODs (TreeInstanced.shader) and its impostors (TreeImpostor.shader), so every
// LOD is placed, tinted and lit the same way and trees don't change brightness when they switch LOD.
#ifndef MOUNTAIN_TREE_COMMON
#define MOUNTAIN_TREE_COMMON

struct Tree
{
    float3 position;   // trunk base, world space
    float rotation;    // radians about +Y
    float heightScale;
    float widthScale;
    uint prototype;
    uint pad;
};
StructuredBuffer<Tree> _Trees;
StructuredBuffer<uint> _Visible;
uint _VisibleOffset;

// Distance fades (continuous, so nothing pops at a LOD switch): far trees carry less snow, so forests keep
// their dark green against the snowfield, and grow slightly wider, closing the gaps a real canopy doesn't show.
float4 _TreeFade;      // x: fade start (m), y: fade end (m), z: snow kept at the end, w: extra width at the end

uint TreeIndex(uint instance) { return _Visible[_VisibleOffset + instance]; }

float2 TreeRotation(Tree t)
{
    float s, c;
    sincos(t.rotation, s, c);
    return float2(s, c);
}

float3 RotateY(float3 v, float2 sc) { return float3(sc.y * v.x + sc.x * v.z, v.y, -sc.x * v.x + sc.y * v.z); }
float3 UnrotateY(float3 v, float2 sc) { return float3(sc.y * v.x - sc.x * v.z, v.y, sc.x * v.x + sc.y * v.z); }

float TreeFade(float3 treePosition)
{
    float d = distance(treePosition, _WorldSpaceCameraPos);
    return smoothstep(_TreeFade.x, _TreeFade.y, d);
}

// A few percent of brightness and warmth per tree, from its index: stands stop looking cloned.
half3 TreeTint(uint index)
{
    uint h = index * 747796405u + 2891336453u;
    h = ((h >> ((h >> 28u) + 4u)) ^ h) * 277803737u;
    h = (h >> 22u) ^ h;
    half a = (h & 1023u) / 1023.0, b = ((h >> 10) & 1023u) / 1023.0;
    half3 warm = half3(1.05, 1.0, 0.9), cool = half3(0.94, 1.0, 1.07);
    return lerp(0.86, 1.1, a) * lerp(warm, cool, b);
}

// Octahedral normal encoding (impostor data atlases).
float2 OctEncode(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 p = n.y >= 0 ? n.xz : (1 - abs(n.zx)) * (n.xz >= 0 ? 1 : -1);
    return p * 0.5 + 0.5;
}

float3 OctDecode(float2 e)
{
    float2 f = e * 2 - 1;
    float3 n = float3(f.x, 1 - abs(f.x) - abs(f.y), f.y);
    float t = saturate(-n.y);
    n.xz += n.xz >= 0 ? -t : t;
    return normalize(n);
}

// Hemi-octahedral mapping of the upper hemisphere to the unit square (impostor frames).
float2 HemiOctEncode(float3 d)
{
    d /= abs(d.x) + abs(d.y) + abs(d.z);
    return float2(d.x + d.z, d.x - d.z) * 0.5 + 0.5;
}

float3 HemiOctDecode(float2 uv)
{
    float2 e = uv * 2 - 1;
    float x = (e.x + e.y) * 0.5, z = (e.x - e.y) * 0.5;
    return normalize(float3(x, 1 - abs(x) - abs(z), z));
}

// Snow on foliage (tree realism review: every up-facing card turned white, so snowy crowns read as white
// feathers). Snow lies along the middle of a spray and toward the branch, in clumps, and the needle tips
// and edges stay green. uv: the card's (branch along +u, spray across v); seed: per card (UV2.x).
float Hash21(float2 p)
{
    p = frac(p * float2(233.34, 851.73));
    p += dot(p, p + 23.45);
    return frac(p.x * p.y);
}

float ValueNoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x), lerp(Hash21(i + float2(0, 1)), Hash21(i + 1), f.x), f.y);
}

half SnowPattern(float2 uv, float seed)
{
    float core = 1 - abs(uv.y - 0.5) * 3;                          // 1 on the branch line, 0 inside the frond's edge
    float along = 1.1 - 0.55 * uv.x;                               // wider toward the branch than the tip
    float clumps = ValueNoise(uv * float2(9, 7) + seed * 41.0);    // ragged, clumpy edges
    return smoothstep(0.3, 0.5, core * along + (clumps - 0.5) * 0.45);
}

// Snow faces the sky: lit surfaces lean toward up and see more of it, so snow on branches reads bright.
float3 SnowNormal(float3 n, half snow) { return normalize(lerp(n, float3(0, 1, 0), snow * 0.6)); }

// The image basis used to bake and draw impostors: "up" is world up projected onto the view plane.
void ViewBasis(float3 d, out float3 right, out float3 up)
{
    float3 ref = abs(d.y) > 0.999 ? float3(0, 0, 1) : float3(0, 1, 0);
    right = normalize(cross(ref, d));
    up = cross(d, right);
}


#endif
