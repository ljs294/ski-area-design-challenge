// Task 4.13 PR C2 (-skierproof): the grey stand-in skier figures that measure what real 3D skiers would cost. Never
// shipped, not art. Each instance reads its feet and packed band + heading from _Near (SkierProof.cs) at _Base +
// instance, turns the mesh to its heading and stands it there. Lit by the sun with its shadows and the sky's ambient,
// the jacket (vertex colour red) tinted by ability like the dots; casts shadows and writes depth as a real model would.
Shader "MountainPlanner/SkierProof"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            StructuredBuffer<float4> _Near;
            int _Base;

            void PlaceFigure(uint instance, float3 positionOS, float3 normalOS, out float3 positionWS, out float3 normalWS, out uint band)
            {
                float4 d = _Near[_Base + instance];
                band = (uint)floor(d.w);
                float heading = frac(d.w) / 0.998 * 6.28318530718;
                float s, c;
                sincos(heading, s, c);
                // The mesh faces +z; turn it so +z points along (sin, cos), the way the skier last moved.
                positionWS = float3(positionOS.x * c + positionOS.z * s, positionOS.y, -positionOS.x * s + positionOS.z * c) + d.xyz;
                normalWS = float3(normalOS.x * c + normalOS.z * s, normalOS.y, -normalOS.x * s + normalOS.z * c);
            }

            half3 BandColour(uint band)
            {
                if (band == 0) return half3(0.16, 0.70, 0.27);   // beginner: green
                if (band == 1) return half3(0.13, 0.42, 0.93);   // intermediate: blue
                if (band == 2) return half3(0.06, 0.06, 0.07);   // advanced: black
                return half3(0.88, 0.13, 0.13);                  // expert: red
            }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; uint instance : SV_InstanceID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                nointerpolation half3 albedo : TEXCOORD2;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                uint band;
                PlaceFigure(v.instance, v.positionOS.xyz, v.normalOS, o.positionWS, o.normalWS, band);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.albedo = v.color.r > 0.5 ? BandColour(band) : half3(0.42, 0.43, 0.45);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half3 lit = i.albedo * (sun.color * saturate(dot(n, sun.direction)) * sun.shadowAttenuation + SampleSH(n));
                return half4(lit, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; uint instance : SV_InstanceID; };

            float4 Vert(Attributes v) : SV_POSITION
            {
                float3 positionWS, normalWS;
                uint band;
                PlaceFigure(v.instance, v.positionOS.xyz, v.normalOS, positionWS, normalWS, band);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return cs;
            }

            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; uint instance : SV_InstanceID; };

            float4 Vert(Attributes v) : SV_POSITION
            {
                float3 positionWS, normalWS;
                uint band;
                PlaceFigure(v.instance, v.positionOS.xyz, v.normalOS, positionWS, normalWS, band);
                return TransformWorldToHClip(positionWS);
            }

            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
