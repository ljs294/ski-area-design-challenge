// The forest's tree shader for mesh LODs (TR4): draws GPU-culled tree instances (ForestCull.compute).
// Foliage is lit as a crown, not card by card: import bakes crown-volume normals and ambient occlusion
// (TreeImport), so crowns have a lit side, a shaded side and dark interiors. Bark has a normal map, so the
// sun rakes across its ridges and scales. Snow sits on the upper side
// of branches that can hold it (UV1.x capacity, UV3.y which way the card faces). Wind sways trunks, bends
// branches and flutters needles from the weights in vertex colour (WindOffset). Impostors
// (TreeImpostor.shader) share the placement, tint and lighting in TreeCommon.hlsl, and stand still: at
// their distance the sway is under a pixel.
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
        _BackTint ("Underside tint (rgb on the needle brightness; a amount)", Color) = (1, 1, 1, 0)
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
            float4 _BackTint;
            float _BumpScale;
        CBUFFER_END
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

        // Wind (TR4), from the weights the Blender build bakes into vertex colour: R trunk sway ((height /
        // tree height)², 0 at the foot), G branch flex (0 at the branch's base, 1 at its tip), B the branch's
        // phase, A needle and leaf flutter. p is the vertex relative to the trunk's foot, in world metres.
        // Every pass calls this through Place(), so shadows and depth move with the trees. It fades out from
        // 250 to 450 m, where even strong sway is under a pixel at 1080p. Only LOD0-1 compile it (_WIND, set
        // by ForestRenderer, and off in calm): the code alone slows every vertex through occupancy, even
        // where it is skipped, which cost up to 0.95 ms in the benchmark's ring view when every LOD had it.
        float3 WindOffset(float3 treePosition, float3 p, half4 weights, uint index)
        {
        #if !defined(_WIND)
            return 0;
        #else
            float strength = _Wind.z * saturate((450 - distance(treePosition, _WorldSpaceCameraPos)) / 200);
            float3 offset = 0;
            UNITY_BRANCH
            if (strength > 0)   // calm, or too far to see
            {
                float2 dir = _Wind.xy;
                float clock = _Wind.w;
                float3 downwind = float3(dir.x, 0, dir.y);
                float treePhase = (TreeHash(index) >> 20) / 4095.0 * TWO_PI;
                // Gusts roll downwind across the forest: crests 60 m apart at about 7 m/s, varied across the wind.
                float along = dot(treePosition.xz, dir), across = dot(treePosition.xz, float2(-dir.y, dir.x));
                float gust = saturate(0.6 + 0.3 * sin(along * (TWO_PI / 60) - clock * (TWO_PI * 0.12))
                                          + 0.15 * sin(across * (TWO_PI / 37) + clock * (TWO_PI * 0.05)));
                // Trunk: leans downwind and sways about the lean (0.3 Hz), never back past upright. Height × the
                // weight grows like a bending pole: the top of a 25 m tree moves about 0.5 m in strong wind.
                float sway = (0.5 + 0.35 * sin(clock * (TWO_PI * 0.3) + treePhase)) * gust;
                offset = downwind * (0.022 * sway * weights.r * max(p.y, 0));
                // Branches bob and push downwind (1.2 Hz), each in its own phase.
                float bob = sin(clock * (TWO_PI * 1.2) + weights.b * TWO_PI + treePhase);
                offset += (float3(0, 0.12 * bob, 0) + downwind * (0.06 * (0.6 + 0.4 * bob))) * (weights.g * gust);
                // Needles and leaves flutter: small and fast (6 Hz), different from card to card.
                float flutter = sin(clock * (TWO_PI * 6) + dot(p, float3(3.1, 2.3, 4.7)));
                offset += float3(dir.x * 0.5, 1, dir.y * 0.5) * (0.02 * flutter * weights.a * (0.4 + 0.6 * gust));
                offset *= strength;
            }
            return offset;
        #endif
        }

        void Place(uint index, float3 positionOS, float3 normalOS, half4 wind, out float3 positionWS, out float3 normalWS, out float fade)
        {
            Tree t = _Trees[index];
            float2 sc = TreeRotation(t);
            fade = TreeFade(t.position);
            float w = t.widthScale * (1 + _TreeFade.w * fade);
            float3 scale = float3(w, t.heightScale, w);
            float3 p = RotateY(positionOS * scale, sc);
            positionWS = t.position + p + WindOffset(t.position, p, wind, index);
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
            #pragma multi_compile_local_vertex _ _WIND
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "TreeLighting.hlsl"
            #include "Haze.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 uv1 : TEXCOORD1;   // x: snow capacity
                float2 uv2 : TEXCOORD2;   // x: per-card random (snow clumps; season order on leaves)
                float2 uv3 : TEXCOORD3;   // x: ambient occlusion, y: which way the card faces (+1 up)
                half4 color : COLOR;      // wind weights (WindOffset)
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
                Place(index, v.positionOS.xyz, v.normalOS, v.color, positionWS, o.normalWS, fade);
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
                // Undersides (P2-09): a card's back face is the bottom of its spray, which on silver and noble firs is
                // white-banded; ForestRenderer sets the tint per species (none by default).
                if (_Foliage > 0.5 && !front) albedo.rgb = lerp(albedo.rgb, dot(albedo.rgb, half3(0.3, 0.59, 0.11)) * _BackTint.rgb, _BackTint.a);
                half3 colour = lerp(albedo.rgb * i.tint * _Brightness, _SnowColor.rgb, snow);
                half ao = lerp(i.shade.y, 1, snow * 0.3);
                half3 lit = TreeLight(colour, SnowNormal(n, snow), ao, positionWS, _Translucency * _Foliage * (1 - snow));
                return half4(ApplyHaze(lit, positionWS), coverage);
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
            #pragma multi_compile_local_vertex _ _WIND
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; half4 color : COLOR; uint instance : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS, normalWS;
                float fade;
                Place(TreeIndex(v.instance), v.positionOS.xyz, v.normalOS, v.color, positionWS, normalWS, fade);
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
            #pragma multi_compile_local_vertex _ _WIND
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; half4 color : COLOR; uint instance : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 positionWS, normalWS;
                float fade;
                Place(TreeIndex(v.instance), v.positionOS.xyz, v.normalOS, v.color, positionWS, normalWS, fade);
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
