// Tree lighting, shared by TreeInstanced.shader and TreeImpostor.shader. Include after URP Lighting.hlsl.
#ifndef MOUNTAIN_TREE_LIGHTING
#define MOUNTAIN_TREE_LIGHTING
// Foliage: wrapped diffuse (needles pass light, so crowns never go black), ambient from the sky, both
// darkened by the crown's own occlusion, plus a glow when the sun is behind the tree.
half3 TreeLight(half3 albedo, float3 n, half ao, float3 positionWS, half translucency)
{
    Light sun = GetMainLight(TransformWorldToShadowCoord(positionWS));
    float3 v = normalize(_WorldSpaceCameraPos - positionWS);
    half ndl = dot(n, sun.direction);
    half wrap = saturate((ndl + 0.3) / 1.3);
    half3 direct = sun.color * wrap * sun.shadowAttenuation * lerp(1.0, ao, 0.6);
    half3 ambient = SampleSH(n) * ao;
    half back = pow(saturate(dot(-v, sun.direction)), 5) * translucency;
    half3 glow = sun.color * back * sun.shadowAttenuation * ao * 0.9;
    return albedo * (direct + ambient) + albedo * glow;
}
#endif
