// Distance haze (0.5 §4): very light aerial perspective, so far ridges layer into depth. Nothing within
// _Haze.x metres is touched; it eases in to _Haze.z by _Haze.y metres. Every surface shader (terrain, trees,
// impostors, cliffs, diorama walls) applies the same function, so nothing hazes differently from its
// neighbour. SceneLighting sets both globals from the lighting preset (the colour follows its horizon);
// _Haze.w is the toggle (M in the viewer).
#ifndef MOUNTAIN_HAZE
#define MOUNTAIN_HAZE

float4 _Haze;          // x: start (m), y: full (m), z: strongest (0-1), w: on (0/1)
float3 _HazeColor;

half3 ApplyHaze(half3 colour, float3 positionWS)
{
    float d = distance(positionWS, _WorldSpaceCameraPos);
    float t = saturate((d - _Haze.x) / max(_Haze.y - _Haze.x, 1));
    return lerp(colour, _HazeColor, t * t * (3 - 2 * t) * _Haze.z * _Haze.w);
}

#endif
