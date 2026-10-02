// Distant terrain shadows (task 11): FarShadow.compute's lit map over the ring, where 1 is lit and 0 is in a
// ridge's shadow. The cascades end at the shadow distance, so the map fades in over their last stretch and
// takes over beyond; inside the cascades they already carry the terrain's shadow at full detail. Terrain,
// cliffs and trees use MainLightWithFarShadow instead of GetMainLight, so all of them darken together.
// FarTerrainShadow sets the globals; with _FarShadowParams.w = 0 (no mountain yet) nothing changes.
#ifndef MOUNTAIN_FAR_SHADOW
#define MOUNTAIN_FAR_SHADOW

TEXTURE2D(_FarShadowMap);
SAMPLER(sampler_FarShadowMap);
float4 _FarShadowRect;     // west, south, 1 / width, 1 / depth (world metres)
float4 _FarShadowParams;   // x: fade in from (m), y: full by (m), z: strength (0-1), w: on (0/1)

half FarShadow(float3 positionWS)
{
    UNITY_BRANCH
    if (_FarShadowParams.w < 0.5) return 1;
    float d = distance(positionWS, _WorldSpaceCameraPos);
    float fade = saturate((d - _FarShadowParams.x) / max(_FarShadowParams.y - _FarShadowParams.x, 1));
    UNITY_BRANCH
    if (fade <= 0) return 1;
    float2 uv = (positionWS.xz - _FarShadowRect.xy) * _FarShadowRect.zw;
    half lit = SAMPLE_TEXTURE2D_LOD(_FarShadowMap, sampler_FarShadowMap, uv, 0).r;
    return lerp(1, lit, fade * _FarShadowParams.z);
}

Light MainLightWithFarShadow(float3 positionWS)
{
    Light sun = GetMainLight(TransformWorldToShadowCoord(positionWS));
    sun.shadowAttenuation = min(sun.shadowAttenuation, FarShadow(positionWS));
    return sun;
}

#endif
