// The haul rope (task 07, decision SK6): a thin round cable. Each vertex holds the rope's centre (position), the
// outward direction (normal) and the rope's radius (uv.x). Up close the tube is the rope's real 1⅝ in; far away it
// keeps about 0.8 px of width, so a lift line never breaks up into dots or vanishes, and fades a little toward the
// sky's colour as it thins, as a real cable does.
Shader "MountainPlanner/LiftCable"
{
    Properties
    {
        _Color ("Rope colour", Color) = (0.2, 0.2, 0.21, 1)
        _MinPixels ("Least width in pixels", Float) = 0.8
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _MinPixels;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float thin : TEXCOORD2;
                float fog : TEXCOORD3;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 centre = TransformObjectToWorld(v.positionOS.xyz);
                float3 outward = normalize(TransformObjectToWorldNormal(v.normalOS));
                float distance = length(centre - _WorldSpaceCameraPos);
                // Metres per pixel at this distance: 2 d tan(fov / 2) / screen height.
                float perPixel = 2 * distance / (UNITY_MATRIX_P[1][1] * _ScreenParams.y);
                float least = perPixel * _MinPixels * 0.5;
                float radius = max(v.uv.x, least);
                o.thin = saturate(v.uv.x / max(least, 1e-5));
                o.positionWS = centre + outward * radius;
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = outward;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float3 view = normalize(GetWorldSpaceViewDir(i.positionWS));
                float3 h = normalize(sun.direction + view);
                float ndl = saturate(dot(n, sun.direction)) * 0.8 + 0.2;
                float spec = pow(saturate(dot(n, h)), 24) * 0.35;
                float3 lit = _Color.rgb * (sun.color * ndl * sun.shadowAttenuation + SampleSH(n)) + spec * sun.color * sun.shadowAttenuation;
                // A rope thinner than a pixel covers only part of it: lean toward the sky behind as it thins.
                lit = lerp(SampleSH(float3(0, 1, 0)) * 0.9, lit, lerp(0.55, 1, i.thin));
                return half4(MixFog(lit, i.fog), 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _MinPixels;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            float4 Vert(Attributes v) : SV_POSITION
            {
                float3 centre = TransformObjectToWorld(v.positionOS.xyz);
                float3 outward = normalize(TransformObjectToWorldNormal(v.normalOS));
                float perPixel = 2 * length(centre - _WorldSpaceCameraPos) / (UNITY_MATRIX_P[1][1] * _ScreenParams.y);
                return TransformWorldToHClip(centre + outward * max(v.uv.x, perPixel * _MinPixels * 0.5));
            }
            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
