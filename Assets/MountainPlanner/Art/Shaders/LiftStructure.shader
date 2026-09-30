// The lift assets' shader (decisions LP2, LP5, LP6; tools/assets/lifts/README.md): physically based URP
// lighting over a shared palette, with the model's own data doing the rest.
//   UV2 (palette)  one swatch per face: RGB albedo, A packs metallic (A >= 0.5) and smoothness ((A mod 0.5) * 2)
//   UV0 (detail)   box-mapped metres + atlas tile (SampleDetail): RG tangent-space normal, B cavity, A brightness (0.5 = neutral)
//   UV1 (data)     x = snow capacity, y = livery mask (the drive hood takes _LiveryColor)
//   colour R       baked ambient occlusion
// Snow settles on up-facing surfaces (the tree formula), scaled by capacity and _SnowLoad. Glass is the same
// shader with _LIFT_GLASS: alpha blended, no shadows or depth, so the drive's interior shows through.
Shader "MountainPlanner/LiftStructure"
{
    Properties
    {
        [NoScaleOffset] _PaletteMap ("Palette (point sampled)", 2D) = "grey" {}
        [NoScaleOffset] _DetailMap ("Detail (RG normal, B cavity, A brightness)", 2D) = "linearGrey" {}
        _DetailStrength ("Detail strength", Range(0, 1)) = 1
        _DetailDensity ("Detail tiles per metre", Float) = 1
        _AOStrength ("Baked AO strength", Range(0, 1)) = 1
        _LiveryColor ("Livery colour", Color) = (0.72, 0.12, 0.09, 1)
        _SnowLoad ("Snow load", Range(0, 1)) = 1
        _SnowColor ("Snow colour", Color) = (0.93, 0.95, 0.98, 1)
        _SnowSmoothness ("Snow smoothness", Range(0, 1)) = 0.3
        _GlassAlpha ("Glass opacity", Range(0, 1)) = 0.6
        [Toggle(_LIFT_GLASS)] _Glass ("Glass", Float) = 0
        [HideInInspector] _SrcBlend ("Src blend", Float) = 1
        [HideInInspector] _DstBlend ("Dst blend", Float) = 0
        [HideInInspector] _ZWrite ("Z write", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float _DetailStrength;
            float _DetailDensity;
            float _AOStrength;
            float4 _LiveryColor;
            float _SnowLoad;
            float4 _SnowColor;
            float _SnowSmoothness;
            float _GlassAlpha;
            float _Glass;
        CBUFFER_END
        TEXTURE2D(_PaletteMap);   // sampled with the core library's sampler_PointClamp: flat swatches, no bleeding
        TEXTURE2D(_DetailMap); SAMPLER(sampler_DetailMap);

        // UV0 is box-mapped metres with the detail-atlas tile in the integer part of U (tile * 256 + metres; see
        // tools/assets/lifts/liftkit/textures.py). Sample inside that tile's 4 x 4 atlas cell, repeating every
        // metre, with the gradients of the unwrapped coordinates so mips don't jump at the wrap.
        half4 SampleDetail(float2 uv0)
        {
            float tile = floor(uv0.x / 256.0);
            float2 local = float2(uv0.x - tile * 256.0, uv0.y) * _DetailDensity;
            float2 cell = float2(tile - 4.0 * floor(tile / 4.0), floor(tile / 4.0));
            return SAMPLE_TEXTURE2D_GRAD(_DetailMap, sampler_DetailMap, (cell + frac(local)) * 0.25,
                                         ddx(local) * 0.25, ddy(local) * 0.25);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _LIFT_GLASS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv0 : TEXCOORD0;
                float2 data : TEXCOORD1;
                float2 palette : TEXCOORD2;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float2 data : TEXCOORD1;
                float2 palette : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float3 normalWS : TEXCOORD4;
                float4 tangentWS : TEXCOORD5;
                half ao : TEXCOORD6;
                half fogFactor : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = n.normalWS;
                o.tangentWS = float4(n.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv0 = v.uv0;
                o.data = v.data;
                o.palette = v.palette;
                o.ao = v.color.r;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                #if defined(LOD_FADE_CROSSFADE)
                    LODFadeCrossFade(i.positionCS);
                #endif
                half4 pal = SAMPLE_TEXTURE2D(_PaletteMap, sampler_PointClamp, i.palette);
                half metallic = pal.a >= 0.5h ? 1.0h : 0.0h;
                half smoothness = saturate((pal.a - 0.5h * metallic) * 2.0h);
                half4 detail = SampleDetail(i.uv0);
                half3 normalTS = normalize(half3((detail.rg * 2.0h - 1.0h) * _DetailStrength, 1.0h));
                half cavity = lerp(1.0h, saturate(detail.b * 2.0h), _DetailStrength);
                half3 albedo = pal.rgb * lerp(1.0h, detail.a * 2.0h, _DetailStrength);
                albedo = lerp(albedo, albedo * _LiveryColor.rgb, saturate(i.data.y));

                float3 geometricN = normalize(i.normalWS);
                half snow = saturate(i.data.x * _SnowLoad * smoothstep(0.3, 0.8, geometricN.y));
                albedo = lerp(albedo, _SnowColor.rgb, snow);
                metallic *= 1.0h - snow;
                smoothness = lerp(smoothness, _SnowSmoothness, snow);
                normalTS = normalize(lerp(normalTS, half3(0, 0, 1), snow));

                float sgn = i.tangentWS.w;
                float3 bitangent = sgn * cross(geometricN, i.tangentWS.xyz);
                half3x3 tangentToWorld = half3x3(i.tangentWS.xyz, bitangent, geometricN);

                InputData input = (InputData)0;
                input.positionWS = i.positionWS;
                input.positionCS = i.positionCS;
                input.normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));
                input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                input.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                input.fogCoord = i.fogFactor;
                input.bakedGI = SampleSH(input.normalWS);
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                input.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = metallic;
                surface.smoothness = smoothness;
                surface.normalTS = normalTS;
                surface.occlusion = lerp(1.0h, i.ao, _AOStrength) * cavity;
                surface.alpha = 1.0h;
                #if defined(_LIFT_GLASS)
                    surface.alpha = _GlassAlpha;
                #endif

                half4 color = UniversalFragmentPBR(input, surface);
                color.rgb = MixFog(color.rgb, input.fogCoord);
                #if defined(_LIFT_GLASS)
                    color.a = _GlassAlpha;
                #else
                    color.a = 1.0h;
                #endif
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirection = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirection = _LightDirection;
                #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirection));
                #if UNITY_REVERSED_Z
                    cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = cs;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                #if defined(LOD_FADE_CROSSFADE)
                    LODFadeCrossFade(i.positionCS);
                #endif
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
            Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                #if defined(LOD_FADE_CROSSFADE)
                    LODFadeCrossFade(i.positionCS);
                #endif
                return i.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            void Frag(Varyings i, out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(i);
                #if defined(LOD_FADE_CROSSFADE)
                    LODFadeCrossFade(i.positionCS);
                #endif
                outNormalWS = half4(NormalizeNormalPerPixel(i.normalWS), 0.0);
                #ifdef _WRITE_RENDERING_LAYERS
                    outRenderingLayers = EncodeMeshRenderingLayer();
                #endif
            }
            ENDHLSL
        }
    }
}
