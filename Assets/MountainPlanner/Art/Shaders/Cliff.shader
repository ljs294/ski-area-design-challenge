// Cliff shells (style tile): the volume a heightmap can't hold, drawn with the terrain's own rock and snow
// textures so shell and terrain read as one surface. Triplanar granite (no UVs, no stretching), faint
// colour banding along the strata, and snow that settles on ledge tops and other up-facing rock.
Shader "MountainPlanner/Cliff"
{
    Properties
    {
        _Albedo ("Albedo + height (array)", 2DArray) = "" {}
        _Normals ("Normals (array)", 2DArray) = "" {}
        _RockTile ("Rock metres per repeat", Float) = 9
        _SnowTile ("Snow metres per repeat", Float) = 12
        _SnowLoad ("Snow on ledges", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D_ARRAY(_Albedo); SAMPLER(sampler_Albedo);
        TEXTURE2D_ARRAY(_Normals);
        CBUFFER_START(UnityPerMaterial)
            float _RockTile;
            float _SnowTile;
            float _SnowLoad;
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
            #include "Haze.hlsl"
            #include "FarShadow.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float weight : TEXCOORD2; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.weight = v.uv.x;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 p = i.positionWS;
                float3 w = pow(abs(n), 4);
                w /= w.x + w.y + w.z;
                float s = 1 / _RockTile;
                float4 ax = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.zy * s, RockLayer);
                float4 ay = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xz * s, RockLayer);
                float4 az = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xy * s, RockLayer);
                float3 tx = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.zy * s, RockLayer).xyz * 2 - 1;
                float3 ty = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.xz * s, RockLayer).xyz * 2 - 1;
                float3 tz = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.xy * s, RockLayer).xyz * 2 - 1;
                float4 rock = ax * w.x + ay * w.y + az * w.z;
                float3 rn = normalize(n + float3(0, tx.y, tx.x) * sign(n.x) * w.x + float3(ty.x, 0, ty.y) * w.y + float3(tz.x, tz.y, 0) * sign(n.z) * w.z);

                // Beds of slightly different stone, a few metres thick (the shell's ledges follow the same idea).
                float band = sin(p.y * 1.14 + sin(p.x * 0.013 + p.z * 0.011) * 3.0);
                rock.rgb *= 0.92 + 0.1 * band;

                // Snow settles where the rock faces up (ledge tops), more where the rock's own relief is low.
                float4 snowAlbedo = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xz / _SnowTile, SnowLayer);
                float snow = smoothstep(0.45, 0.75, rn.y + (0.5 - rock.a) * 0.35);
                // Where the shell fades into the slope, it becomes the snowfield it merges with.
                snow = max(snow, 1 - smoothstep(0.15, 0.55, i.weight)) * _SnowLoad;
                float3 albedo = lerp(rock.rgb, snowAlbedo.rgb, snow);
                float3 normal = normalize(lerp(rn, n, snow * 0.7));

                Light sun = MainLightWithFarShadow(p);
                float3 view = normalize(GetWorldSpaceViewDir(p));
                float ndl = saturate(dot(normal, sun.direction));
                float smooth = lerp(0.18, 0.35, snow);
                float3 h = normalize(sun.direction + view);
                float spec = pow(saturate(dot(normal, h)), exp2(10 * smooth + 1)) * smooth * 0.5;
                float3 lit = albedo * (sun.color * ndl * sun.shadowAttenuation + SampleSH(normal)) + sun.color * spec * sun.shadowAttenuation;
                return half4(ApplyHaze(lit, p), 1);
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
            float4 Vert(Attributes v) : SV_POSITION
            {
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(p, n, _LightDirection));
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
            struct Attributes { float4 positionOS : POSITION; };
            float4 Vert(Attributes v) : SV_POSITION { return TransformWorldToHClip(TransformObjectToWorld(v.positionOS.xyz)); }
            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
