// The mountain's terrain shader (0.3 §4.4, 0.5 §2; style tile): all six ground layers in one pass
// from two splat maps (snow, forest floor, grass, rock / developed, ice), height-based blending so
// transitions look like snow drifting over rock rather than a cross-fade, triplanar rock so cliffs never
// stretch, frozen-lake shading, the cover-map overlay, and a clean cut at the edge of the downloaded
// data (the diorama edge, A1).
Shader "MountainPlanner/Terrain"
{
    Properties
    {
        _Albedo ("Albedo + height (array)", 2DArray) = "" {}
        _Normals ("Normals (array)", 2DArray) = "" {}
        _Overlay ("Cover-map overlay", Float) = 0
        _HeightBlend ("Height-blend sharpness", Range(0.01, 1)) = 0.2
    }
    SubShader
    {
        Tags { "Queue" = "Geometry-100" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "TerrainCompatible" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_Control0); SAMPLER(sampler_Control0);
        TEXTURE2D(_Control1);
        TEXTURE2D_ARRAY(_Albedo); SAMPLER(sampler_Albedo);
        TEXTURE2D_ARRAY(_Normals);

        CBUFFER_START(UnityPerMaterial)
            float _Overlay;
            float _HeightBlend;
        CBUFFER_END
        float _ControlRes;           // per tile (property block): splat texels per edge
        float4 _RingBounds;          // xmin, zmin, xmax, zmax in world space: the data's edge
        float _Tile[6];              // metres per texture repeat, per layer
        float _Smooth[6];
        float4 _OverlayColor[6];

        // The terrain stops at the edge of the downloaded data; the diorama walls take over below it.
        void ClipToRing(float3 positionWS)
        {
            clip(min(min(positionWS.x - _RingBounds.x, _RingBounds.z - positionWS.x),
                     min(positionWS.z - _RingBounds.y, _RingBounds.w - positionWS.z)));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fog : TEXCOORD3;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            // Top-down sample of one layer: albedo (height in alpha) and a world-space normal (whiteout blend).
            void SampleTop(int layer, float3 p, float3 n, out float4 albedo, out float3 normal)
            {
                float2 uv = p.xz / _Tile[layer];
                albedo = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, uv, layer);
                float3 t = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, uv, layer).xyz * 2 - 1;
                normal = normalize(float3(n.x + t.x, n.y, n.z + t.y));
            }

            // Triplanar sample (rock): each face projects from its own axis, so cliffs never stretch. The
            // strata in the rock texture run horizontally on the side projections.
            void SampleTriplanar(int layer, float3 p, float3 n, out float4 albedo, out float3 normal)
            {
                float3 w = pow(abs(n), 4);
                w /= w.x + w.y + w.z;
                float s = 1 / _Tile[layer];
                float4 ax = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.zy * s, layer);
                float4 ay = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xz * s, layer);
                float4 az = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xy * s, layer);
                float3 tx = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.zy * s, layer).xyz * 2 - 1;
                float3 ty = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.xz * s, layer).xyz * 2 - 1;
                float3 tz = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.xy * s, layer).xyz * 2 - 1;
                albedo = ax * w.x + ay * w.y + az * w.z;
                float3 nx = float3(0, tx.y, tx.x) * sign(n.x);
                float3 ny = float3(ty.x, 0, ty.y);
                float3 nz = float3(tz.x, tz.y, 0) * sign(n.z);
                normal = normalize(n + nx * w.x + ny * w.y + nz * w.z);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                ClipToRing(i.positionWS);
                float3 n = normalize(i.normalWS);

                // Splat weights, sampled texel-centre to texel-centre as Unity lays them out.
                float res = max(_ControlRes, 2);
                float2 cuv = (i.uv * (res - 1) + 0.5) / res;
                float4 c0 = SAMPLE_TEXTURE2D(_Control0, sampler_Control0, cuv);
                float4 c1 = SAMPLE_TEXTURE2D(_Control1, sampler_Control0, cuv);
                float weights[6] = { c0.r, c0.g, c0.b, c0.a, c1.r, c1.g };

                if (_Overlay > 0.5)
                {
                    float3 flat = 0;
                    [unroll] for (int k = 0; k < 6; k++) flat += weights[k] * _OverlayColor[k].rgb;
                    Light sun0 = GetMainLight();
                    float lambert = saturate(dot(n, sun0.direction)) * 0.6 + 0.5;
                    return half4(flat * lambert, 1);
                }

                // Height-based blend: a layer wins where its own relief rises above the others.
                float4 albedos[6];
                float3 normals[6];
                float best = -10;
                float heights[6];
                [unroll] for (int k = 0; k < 6; k++)
                {
                    albedos[k] = 0;
                    normals[k] = n;
                    heights[k] = -10;
                    [branch] if (weights[k] > 0.004)
                    {
                        if (k == 3) SampleTriplanar(k, i.positionWS, n, albedos[k], normals[k]);
                        else SampleTop(k, i.positionWS, n, albedos[k], normals[k]);
                        heights[k] = weights[k] + albedos[k].a * 0.6;
                        best = max(best, heights[k]);
                    }
                }
                float3 albedo = 0, normal = 0;
                float total = 0, smooth = 0;
                [unroll] for (int k = 0; k < 6; k++)
                {
                    float w = max(heights[k] - best + _HeightBlend, 0);
                    albedo += albedos[k].rgb * w;
                    normal += normals[k] * w;
                    smooth += _Smooth[k] * w;
                    total += w;
                }
                albedo /= total;
                normal = normalize(normal);
                smooth /= total;

                // Frozen lakes (T8): where snow lies on ice, the snow is smoother and faintly blue, and the
                // shoreline shows a thin rim of exposed ice.
                float ice = weights[5];
                float rim = saturate(1 - abs(ice - 0.5) * 3) * saturate(ice * 4);
                albedo = lerp(albedo, albedo * float3(0.94, 0.97, 1.0), saturate(ice * 2) * weights[0]);
                albedo = lerp(albedo, float3(0.6, 0.72, 0.8), rim * 0.55);
                smooth = lerp(smooth, 0.7, saturate(ice * 2));

                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float3 view = normalize(GetWorldSpaceViewDir(i.positionWS));
                float ndl = saturate(dot(normal, sun.direction));
                float3 h = normalize(sun.direction + view);
                float spec = pow(saturate(dot(normal, h)), exp2(10 * smooth + 1)) * smooth * 0.5;
                float3 lit = albedo * (sun.color * ndl * sun.shadowAttenuation + SampleSH(normal))
                           + sun.color * spec * sun.shadowAttenuation;
                lit = MixFog(lit, i.fog);
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
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(o.positionWS, n, _LightDirection));
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
                ClipToRing(i.positionWS);
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
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                ClipToRing(i.positionWS);
                return 0;
            }
            ENDHLSL
        }
    }
}
