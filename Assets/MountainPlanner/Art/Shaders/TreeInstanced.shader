// The forest's tree shader (TR4, first version): draws GPU-culled tree instances (ForestCull.compute),
// alpha-tested cards lit from both sides, the sun's shadows, and snow on up-facing branches from the
// model's snow mask (UV1.x) times the snow load. Wind and seasons come later on the same inputs.
Shader "MountainPlanner/TreeInstanced"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _SnowLoad ("Snow load", Range(0, 1)) = 1
        _SnowColor ("Snow colour", Color) = (0.93, 0.95, 0.98, 1)
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _SnowFlat ("Snow without a mask (cards)", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Tree
        {
            float3 position;
            float rotation;
            float heightScale;
            float widthScale;
            uint prototype;
            uint pad;
        };
        StructuredBuffer<Tree> _Trees;
        StructuredBuffer<uint> _Visible;

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float _Cutoff;
            float _SnowLoad;
            float4 _SnowColor;
            float4 _Tint;
            float _SnowFlat;
        CBUFFER_END
        uint _VisibleOffset;
        float _LodWidth;
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

        void Place(uint instance, float3 positionOS, float3 normalOS, out float3 positionWS, out float3 normalWS)
        {
            Tree t = _Trees[_Visible[_VisibleOffset + instance]];
            float s, c;
            sincos(t.rotation, s, c);
            float w = t.widthScale * _LodWidth;
            float3 p = positionOS * float3(w, t.heightScale, w);
            positionWS = t.position + float3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
            float3 n = normalOS / float3(w, t.heightScale, w);
            normalWS = normalize(float3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            // Alpha to coverage (with MSAA): needle and leaf edges become partial coverage, so distant
            // foliage blends softly instead of snapping on and off (shimmer) as the camera moves.
            AlphaToMask On
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; uint instance : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 positionWS : TEXCOORD2; float snow : TEXCOORD3; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS, normalWS;
                Place(v.instance, v.positionOS.xyz, v.normalOS, positionWS, normalWS);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.positionWS = positionWS;
                o.normalWS = normalWS;
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.snow = v.uv1.x * _SnowLoad;
                return o;
            }

            half4 Frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _Tint;
                // Sharpened coverage: crisp up close, a soft one-pixel edge in the distance.
                half coverage = _Cutoff > 0 ? saturate((albedo.a - _Cutoff) / max(fwidth(albedo.a), 0.0001) + 0.5) : 1;
                clip(coverage - 0.01);
                float3 n = normalize(front ? i.normalWS : -i.normalWS);
                // Snow sits on the up-facing side of branches that can hold it.
                float snow = saturate(i.snow * saturate(n.y * 1.6 + 0.1) + _SnowFlat * saturate(n.y + 0.2));
                albedo.rgb = lerp(albedo.rgb, _SnowColor.rgb, snow);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                // Wrapped diffuse: needles and leaves pass light, so crowns never go black on the shaded side.
                half wrap = saturate((dot(n, sun.direction) + 0.5) / 1.5);
                half3 light = sun.color * wrap * sun.shadowAttenuation + SampleSH(n);
                return half4(albedo.rgb * light, coverage);
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

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; uint instance : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS, normalWS;
                Place(v.instance, v.positionOS.xyz, v.normalOS, positionWS, normalWS);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = cs;
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a - _Cutoff);
                return 0;
            }
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
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; uint instance : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS, normalWS;
                Place(v.instance, v.positionOS.xyz, v.normalOS, positionWS, normalWS);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
