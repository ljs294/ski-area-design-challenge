// Task 4.50 slice 3: a lit building lamp's halo at night (LampHaloLayer). One draw, six vertices a halo, read from a
// structured buffer by instance as the groomers' dots are: a camera-facing soft disc HaloMetres across in the world,
// clamped between MinPixels and MaxPixels on screen, a hot warm-white core fading to amber, added over the scene and
// depth-tested with a small pull towards the camera (so its own fixture doesn't hide it). Its brightness is fixed on
// screen: the post exposure is divided back out, as the groomers' headlights do.
Shader "MountainPlanner/LampHalo"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-4" }
        Pass
        {
            Name "LampHalo"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // xyz: the lens in the world; w: its level, 0-1
            StructuredBuffer<float4> _Halos;
            float _HaloMetres, _MinPixels, _MaxPixels, _ExposureEv;
            float2 _ScreenPixels;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                nointerpolation float level : TEXCOORD1;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            Varyings Vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings o;
                float4 h = _Halos[instanceID];
                float2 corner = Corners[vertexID % 6];
                o.corner = corner;
                o.level = h.w;
                float3 world = h.xyz;
                float3 toCamera = GetCameraPositionWS() - world;
                float dist = length(toCamera);
                world += toCamera / max(dist, 1e-3) * min(1.0, dist * 0.04);   // out from under its own eave or hood
                float4 clip = TransformWorldToHClip(world);
                // abs: rendering into a texture flips the projection's y (_m11 < 0), which would pin every halo to its minimum
                float metresPx = _HaloMetres * abs(UNITY_MATRIX_P._m11) * _ScreenPixels.y * 0.5 / max(clip.w, 1e-3);
                float px = clamp(metresPx, _MinPixels, _MaxPixels);
                float2 ext = px / max(_ScreenPixels, 1);
                clip.xy += corner * ext * clip.w;
                o.positionCS = clip;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float r = length(i.corner);
                if (r >= 1) discard;
                float fall = 1 - r;
                float core = fall * fall * fall * fall;   // a hot point
                float halo = fall * fall * 0.6;            // the soft warm glow round it
                // warm amber round a warm-white point (owner 2026-11-25: warmer, so a lit hut reads against moonlit snow), linear
                half3 warm = half3(1.0, 0.55, 0.24);
                half3 c = (core * half3(1, 0.86, 0.66) + halo * warm) * i.level * exp2(-_ExposureEv);
                return half4(c, 0);
            }
            ENDHLSL
        }
    }
}
