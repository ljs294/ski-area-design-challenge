// Task 4.50 slice 3 part 2 (owner 2026-11-25): the building lamps' light on whatever is near them at night, the walls,
// decks, lift steel, ground and snow alike (LampLightMap). LampLight(position, normal) is the light falling on a surface
// there, in the scene's light units: the caller multiplies its diffuse colour by it and adds that in, as it does the moon.
// A pixel reads its 32 m tile in the table (_LampCells): almost everywhere no lamp reaches, one read and out. Otherwise
// at most 16 lamps, each a real fixture's light (round 2, owner 2026-11-26: the first, a hard cone, read as a stage
// spotlight painted on the doors):
// - its spread: brightest along its aim and falling away smoothly (cos^n, a tenth at its cone's edge), with no edge;
// - shielded: nothing above the lens's own height, so no glow up a wall or over a door hood;
// - the inverse square from a metre out (no hot spot beside the lens), fading to nothing at its range;
// - the wall-side clip: the light stays on the lamp's side of its wall, and the wall itself takes only a dim wash
//   (LampWallShare), so the pool on the ground carries the light, as it does under a real lamp;
// - the surface's facing. No shadows.
// The whole function is skipped by day (_LampParams.x = 0).
#ifndef MOUNTAINPLANNER_LAMP_LIGHT
#define MOUNTAINPLANNER_LAMP_LIGHT

// four float4 a lamp: lens xyz, range; colour × candela, level class (1 dusk, 2 in use); aim, its spread's power n
// (−1 all round); the wall's outward facing (zero when free-standing), how far behind the lens the wall is
StructuredBuffer<float4> _Lamps;
StructuredBuffer<int2> _LampCells;   // per tile: first index, count
StructuredBuffer<int> _LampIndex;
float4 _LampGrid;                    // the table's first tile column (x) and row (z), its width and depth in tiles
float4 _LampParams;                  // x on, y gain, z the dusk lamps' level, w the in-use lamps' level

#define LAMP_TILE_METRES 32.0
#define LampWallShare 0.35           // the wall a lamp is on takes this much of its light

float3 LampLight(float3 positionWS, float3 normalWS)
{
    float3 sum = 0;
    int2 cell = int2(floor(positionWS.xz / LAMP_TILE_METRES)) - int2(_LampGrid.xy);
    int2 size = int2(_LampGrid.zw);
    // one return (FXC warns of an uninitialised result past early ones): off by day, or outside the table, count 0
    bool inside = _LampParams.x > 0.5 && all(cell >= 0) && all(cell < size);
    int2 list = int2(0, 0);
    UNITY_BRANCH
    if (inside) list = _LampCells[cell.y * size.x + cell.x];
    int count = min(list.y, 16);
    UNITY_LOOP
    for (int k = 0; k < count; k++)
    {
        int at = _LampIndex[list.x + k] * 4;
        float4 p = _Lamps[at];
        float3 toLamp = p.xyz - positionWS;
        float d2 = dot(toLamp, toLamp);
        float r2 = p.w * p.w;
        UNITY_BRANCH
        if (d2 >= r2) continue;
        float4 c = _Lamps[at + 1], a = _Lamps[at + 2], f = _Lamps[at + 3];
        float3 l = toLamp * rsqrt(max(d2, 1e-6));
        float window = saturate(1.0 - d2 * d2 / (r2 * r2));
        window *= window;
        float spread = a.w < 0.0 ? 1.0 : pow(saturate(dot(-l, a.xyz)), a.w);
        float shield = saturate((p.y - positionWS.y) / 0.15);
        float side = 1.0;
        if (dot(f.xyz, f.xyz) > 0.5)
        {
            float beyond = dot(positionWS - p.xyz, f.xyz) + f.w;   // metres out from the wall's plane
            side = saturate((beyond + 0.08) / 0.06);
            side *= lerp(1.0, LampWallShare, saturate(dot(normalWS, f.xyz)) * (1.0 - saturate(beyond / 0.5)));
        }
        float ndl = saturate(dot(normalWS, l));
        float level = c.w < 1.5 ? _LampParams.z : _LampParams.w;
        sum += c.rgb * (level * window * spread * shield * side * ndl / (d2 + 1.0));
    }
    return sum * _LampParams.y;
}

#endif
