// The trail being painted and the brush under the pointer (task 08), shared by every surface a run crosses: the ground
// (MountainTerrain.shader), its snow shell and the cliff shells (Cliff.shader). Task 4.07 PR A3 round 3 (finding A3-3): the
// cliffs drew none of it, so a run painted across a cliff band broke into pieces. Its values are globals (TrailPaintMap).
#ifndef MP_TRAIL_PAINT_INCLUDED
#define MP_TRAIL_PAINT_INCLUDED

// The trail being painted (task 08, TF-02; owner 2026-10-19: paint like a brush; 2026-10-20: make it elegant): the
// lattice's own 1 m cells (TrailPaintMap), r painted and g a patch apart, filtered so the coverage is smooth, then
// drawn as a light fill with a crisp line where the coverage crosses half (the paint's edge), a few pixels wide at
// any zoom. _PaintParams.x = 0 when nothing's painted, y the style: 0 A (light orange, darker edge), 1 B (white-blue
// highlight, blue edge), 2 C (the live difficulty's colour, _PaintTint). A patch apart is red in every style.
// _PaintCursor: the brush under the pointer (x, z, radius, w 1 painting, 2 rubbing out, 0 hidden).
TEXTURE2D(_PaintMap); SAMPLER(sampler_PaintMap);
float4 _PaintRect;           // west, south, 1 / width, 1 / depth (world metres, texel corners)
float4 _PaintParams;
float4 _PaintCursor;
float4 _PaintTint;

float3 ApplyPaint(float3 colour, float2 xz)
{
    UNITY_BRANCH
    if (_PaintParams.x > 0.5)
    {
        float2 uv = (xz - _PaintRect.xy) * _PaintRect.zw;
        if (all(uv > 0) && all(uv < 1))
        {
            float2 paint = SAMPLE_TEXTURE2D_LOD(_PaintMap, sampler_PaintMap, uv, 0).rg;
            float cover = saturate(paint.r + paint.g);
            float w = clamp(fwidth(cover), 1e-4, 0.25);
            float inside = smoothstep(0.5 - w, 0.5 + w, cover);
            float edge = saturate(1 - abs(cover - 0.5) / (1.5 * w));
            float apart = saturate(paint.g / max(cover, 1e-3));
            float3 fill, edgeInk;
            float alpha;
            // Snow is near white, so a fill needs colour and weight to read on it (linear space).
            if (_PaintParams.y < 0.5) { fill = float3(0.92, 0.50, 0.18); edgeInk = float3(0.66, 0.24, 0.04); alpha = 0.42; }
            else if (_PaintParams.y < 1.5) { fill = float3(0.42, 0.66, 1.00); edgeInk = float3(0.10, 0.38, 0.88); alpha = 0.40; }
            else { fill = _PaintTint.rgb; edgeInk = _PaintTint.rgb * 0.7; alpha = 0.45; }
            fill = lerp(fill, float3(0.86, 0.16, 0.10), apart);
            edgeInk = lerp(edgeInk, float3(0.70, 0.08, 0.05), apart);
            colour = lerp(colour, fill, alpha * inside);
            colour = lerp(colour, edgeInk, 0.9 * edge);
        }
    }
    UNITY_BRANCH
    if (_PaintCursor.w > 0.5)
    {
        // A soft disc in a crisp dark ring (red to rub out), about two pixels wide whatever the zoom.
        float d = distance(xz, _PaintCursor.xy), r = _PaintCursor.z;
        float px = max(fwidth(d), 1e-3);
        float ring = saturate(1.5 - abs(d - r) / px);
        float disc = 1 - smoothstep(r - px, r + px, d);
        float3 ink = _PaintCursor.w > 1.5 ? float3(0.80, 0.12, 0.08) : float3(0.12, 0.13, 0.16);
        colour = lerp(colour, ink, 0.10 * disc);
        colour = lerp(colour, ink, 0.80 * ring);
    }
    return colour;
}

#endif
