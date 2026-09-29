// The diorama base (A1, 0.5 §2; DioramaBase.cs): side walls where the downloaded data ends, cut through
// stylized rock strata, and the thin plinth under them. Strata are horizontal bands of varying thickness
// in the rock palette, dipping a few degrees and gently warped, drawn over the terrain's own rock texture
// so walls and cliffs match. The top carries a snow cap and a dark band of soil and roots.
Shader "MountainPlanner/DioramaWall"
{
    Properties
    {
        _Albedo ("Albedo + height (array)", 2DArray) = "" {}
        _RockTile ("Rock metres per repeat", Float) = 14
        _SnowOn ("Snow cap", Float) = 1
        _Plinth ("Plinth (flat base colour)", Float) = 0
        _PlinthColor ("Plinth colour", Color) = (0.11, 0.1, 0.095, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D_ARRAY(_Albedo); SAMPLER(sampler_Albedo);
        CBUFFER_START(UnityPerMaterial)
            float _RockTile;
            float _SnowOn;
            float _Plinth;
            float4 _PlinthColor;
        CBUFFER_END
        static const int RockLayer = 3;
        static const int SnowLayer = 0;
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;   // x: metres around the perimeter, y: metres below the top of the wall
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                return o;
            }

            // Exact integer hashes (a float hash of world-sized values rounds differently from cell to cell).
            float Hash11(int c)
            {
                uint h = asuint(c) * 0x8DA6B343u;
                h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
                h = (h ^ (h >> 12)) * 0x297A2D39u;
                return (h >> 8) / 16777215.0;
            }

            float Noise1(float x)
            {
                int i = (int)floor(x);
                float f = frac(x);
                return lerp(Hash11(i), Hash11(i + 1), f * f * (3 - 2 * f));
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 p = i.positionWS;
                Light sun = GetMainLight(TransformWorldToShadowCoord(p));
                float3 ambient = SampleSH(n);
                float ndl = saturate(dot(n, sun.direction));

                if (_Plinth > 0.5)
                {
                    // A model's base board: flat and dark, its top ledge a shade lighter.
                    float3 board = _PlinthColor.rgb * (n.y > 0.5 ? 1.35 : 1);
                    return half4(board * (sun.color * ndl * 0.8 + ambient), 1);
                }

                // Strata: beds about 20-90 m thick, dipping about 3 degrees and warped along the wall, so they read
                // as geology rather than stripes; each bed takes a tone from a model-railway rock palette around the
                // art direction's rock grey (#6E6A66). Fine laminae show up close and fade out before they could shimmer.
                float along = i.uv.x;
                float layerY = p.y + dot(p.xz, float2(0.035, 0.03)) + (Noise1(along / 400) - 0.5) * 30 + (Noise1(along / 90 + 17) - 0.5) * 6;
                float scaled = layerY / 42 + Noise1(layerY / 60) * 0.9;
                int band = (int)floor(scaled);
                float pick = Hash11(band);
                float3 tone = pick < 0.26 ? float3(0.43, 0.415, 0.4)    // granite grey
                            : pick < 0.44 ? float3(0.56, 0.47, 0.36)    // tan sandstone
                            : pick < 0.58 ? float3(0.45, 0.33, 0.26)    // rust shale
                            : pick < 0.74 ? float3(0.26, 0.26, 0.28)    // dark slate
                            : pick < 0.88 ? float3(0.36, 0.35, 0.34)    // mid grey
                                          : float3(0.63, 0.59, 0.51);   // cream limestone
                float within = frac(scaled);
                float laminae = 0.06 * sin(layerY * (TWO_PI / 3.1)) * saturate(1 - fwidth(layerY) / 1.2);
                float bedding = 1 - 0.18 * smoothstep(0.92, 1, within) + 0.08 * smoothstep(0.08, 0, within) + laminae;   // dark foot, pale top
                float2 ruv = float2(along, p.y) / _RockTile;
                float3 rock = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, ruv, RockLayer).rgb;
                float rockLum = dot(rock, float3(0.3, 0.59, 0.11));
                float3 colour = tone * bedding * (0.7 + 0.6 * rockLum / 0.42);

                // The top: snow, then a dark band of soil and roots, fading into the rock.
                float depth = i.uv.y;
                float3 soil = float3(0.2, 0.16, 0.12) * (0.85 + 0.3 * Noise1(along / 3));
                float soilEdge = 5 + 3 * Noise1(along / 25 + 3);
                colour = lerp(soil, colour, smoothstep(soilEdge - 2, soilEdge + 2, depth));
                float capEdge = 1.4 + 0.5 * Noise1(along / 6 + 9);
                if (_SnowOn > 0.5)
                {
                    float3 snow = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, float2(along, p.y) / 12, SnowLayer).rgb;
                    colour = lerp(snow, colour, smoothstep(capEdge - 0.15, capEdge + 0.15, depth));
                }
                // A little darker toward the base: a model's walls catch less light low down.
                colour *= lerp(1, 0.75, saturate(depth / 1500));

                float3 lit = colour * (sun.color * ndl * sun.shadowAttenuation + ambient);
                return half4(lit, 1);
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
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }
            half4 Frag(Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
