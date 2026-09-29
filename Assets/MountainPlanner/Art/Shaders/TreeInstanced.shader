// The forest's tree shader for mesh LODs (TR4): draws GPU-culled tree instances (ForestCull.compute).
// Foliage is lit as a crown, not card by card: import bakes crown-volume normals and ambient occlusion
// (TreeImport), so crowns have a lit side, a shaded side and dark interiors. Bark has a normal map, so the
// sun rakes across its ridges and scales. Snow sits on the upper side
// of branches that can hold it (UV1.x capacity, UV3.y which way the card faces). Impostors
// (TreeImpostor.shader) share the placement, tint and lighting in TreeCommon.hlsl.
Shader "MountainPlanner/TreeInstanced"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _SnowLoad ("Snow load", Range(0, 1)) = 1
        _SnowColor ("Snow colour", Color) = (0.93, 0.95, 0.98, 1)
        _Foliage ("Foliage (crown normals, glow)", Float) = 0
        _Translucency ("Back-light glow", Range(0, 1)) = 0.6
        _Brightness ("LOD brightness correction", Float) = 1
        _SnowScale ("LOD snow correction", Float) = 1
        [Normal] _BumpMap ("Bark normal map", 2D) = "bump" {}
        _BumpScale ("Bark relief (0 = off)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "TreeCommon.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float _Cutoff;
            float _SnowLoad;
            float4 _SnowColor;
            float _Foliage;
            float _Translucency;
            float _Brightness;
            float _SnowScale;
            float _BumpScale;
        CBUFFER_END
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

        void Place(uint index, float3 positionOS, float3 normalOS, out float3 positionWS, out float3 normalWS, out float fade)
        {
            Tree t = _Trees[index];
            float2 sc = TreeRotation(t);
            fade = TreeFade(t.position);
            float w = t.widthScale * (1 + _TreeFade.w * fade);
            float3 scale = float3(w, t.heightScale, w);
            positionWS = t.position + RotateY(positionOS * scale, sc);
            normalWS = normalize(RotateY(normalOS / scale, sc));
        }

        half Coverage(half alpha)
        {
            // Sharpened coverage: crisp up close, a soft one-pixel edge in the distance.
            return _Cutoff > 0 ? saturate((alpha - _Cutoff) / max(fwidth(alpha), 0.0001) + 0.5) : 1;
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
            #include "TreeLighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 uv1 : TEXCOORD1;   // x: snow capacity
                float2 uv2 : TEXCOORD2;   // x: per-card random (snow clumps; season order on leaves)
                float2 uv3 : TEXCOORD3;   // x: ambient occlusion, y: which way the card faces (+1 up)
                uint instance : SV_InstanceID;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionRWS : TEXCOORD2;       // relative to the camera: precise derivatives anywhere on the map
                float3 shade : TEXCOORD3;             // snow capacity × load, ao, face up
                nointerpolation half3 tint : TEXCOORD4;
                float seed : TEXCOORD5;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                uint index = TreeIndex(v.instance);
                float fade;
                float3 positionWS;
                Place(index, v.positionOS.xyz, v.normalOS, positionWS, o.normalWS, fade);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.positionRWS = positionWS - _WorldSpaceCameraPos;
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.shade = float3(v.uv1.x * _SnowLoad * _SnowScale * lerp(1, _TreeFade.z, fade), v.uv3.x, v.uv3.y);
                o.tint = _Foliage > 0.5 ? TreeTint(index) : half3(1, 1, 1);
                o.seed = v.uv2.x;
                return o;
            }

            half4 Frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half coverage = Coverage(albedo.a);
                clip(coverage - 0.01);
                float3 n = normalize(i.normalWS);
                // Bark is a closed surface; foliage carries crown normals that face outward from both sides.
                if (_Foliage < 0.5 && !front) n = -n;
                // Bark relief. The tree meshes carry no tangents, so the frame comes from screen-space
                // derivatives of position and uv (taken outside the branch, which only bark takes).
                float3 dp1 = ddx(i.positionRWS), dp2 = ddy(i.positionRWS);
                float2 duv1 = ddx(i.uv), duv2 = ddy(i.uv);
                UNITY_BRANCH
                if (_BumpScale > 0)
                {
                    half3 tn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_BumpMap, sampler_BumpMap, i.uv, duv1, duv2), _BumpScale);
                    float3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
                    float3 t = dp2perp * duv1.x + dp1perp * duv2.x;
                    float3 b = dp2perp * duv1.y + dp1perp * duv2.y;
                    float invmax = rsqrt(max(max(dot(t, t), dot(b, b)), 1e-20));
                    n = normalize(t * invmax * tn.x + b * invmax * tn.y + n * tn.z);
                }
                float3 positionWS = i.positionRWS + _WorldSpaceCameraPos;
                float up = front ? i.shade.z : -i.shade.z;
                float snow = saturate(i.shade.x * saturate(up * 1.6 + 0.1) * (_Foliage > 0.5 ? SnowPattern(i.uv, i.seed) : 1));
                half3 colour = lerp(albedo.rgb * i.tint * _Brightness, _SnowColor.rgb, snow);
                half ao = lerp(i.shade.y, 1, snow * 0.3);
                half3 lit = TreeLight(colour, SnowNormal(n, snow), ao, positionWS, _Translucency * _Foliage * (1 - snow));
                return half4(lit, coverage);
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
                float fade;
                Place(TreeIndex(v.instance), v.positionOS.xyz, v.normalOS, positionWS, normalWS, fade);
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
                float fade;
                Place(TreeIndex(v.instance), v.positionOS.xyz, v.normalOS, positionWS, normalWS, fade);
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
