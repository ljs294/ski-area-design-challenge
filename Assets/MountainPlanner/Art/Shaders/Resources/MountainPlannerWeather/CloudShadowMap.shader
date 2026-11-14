// Task 4.06: the clouds' shadow map (CloudShadowMap.cs): once a frame, the cloud over each texel of the deck's plane around the
// view, the field's large cells, their eroded edges, the region's cover and the front, all as the sky's deck has them. Surfaces
// read it with one tap (Weather.hlsl's CloudShadow). R: the cloud (0 clear, 1 cloud).
Shader "Hidden/MountainPlanner/CloudShadowMap"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../Weather.hlsl"

            float4 _WxShadowEye;   // xy: the camera, moved onto the deck's plane as the target is (grid xz); z: its height over the target (m)

            struct Attributes { float4 vertex : POSITION; };

            // Graphics.Blit's quad runs 0-1: it covers the map whatever the projection, and the texel's place comes from its
            // pixel (SV_POSITION and a texture read agree on every API), so the map is never flipped against its readers.
            float4 Vert(Attributes v) : SV_POSITION { return float4(v.vertex.xy * 2 - 1, 0.5, 1); }

            half4 Frag(float4 pixel : SV_POSITION) : SV_Target
            {
                float2 uv = pixel.xy / _WxShadowAt.w;
                float2 m = _WxShadowAt.xy + uv / _WxShadowAt.z, q = m + _WxCloud.zw;
                // The field at the map's own footprint: a texel is about 1.5 of the large tile's, so no shimmer.
                float lod = clamp(log2(max(1 / (_WxShadowAt.z * _WxShadowAt.w * _WxPixel.y), 1e-3)), 0, 4.5);
                float n = WxCloudField(q, lod, false);
                // The small tile's detail erodes the edges near the camera only, fading out between 2.5 and 4 km of the ground
                // under this texel: from further off it reads as noise, and the overview sees soft shapes.
                float near = 1 - smoothstep(2500, 4000, length(float3(m - _WxShadowEye.xy, _WxShadowEye.z)));
                UNITY_BRANCH
                if (near > 0) n += near * (SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, WxTurn(q) * _WxCloudScale.y + 0.37, lod + 2).g - 0.5) * 0.3;
                return WxCloudFrom(n, WxCover(q), WxFrontAt(q, n), lod);
            }
            ENDHLSL
        }
    }
}
