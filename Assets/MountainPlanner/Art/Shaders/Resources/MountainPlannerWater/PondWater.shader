// Task 4.14: a pond's or reservoir's water (PondWater.cs), a flat opaque surface at its level over the graded bowl. The
// ground climbing out of it through the depth buffer draws the shoreline. It takes 4.09's lake looks by the body's state
// (_WaterBodyStates, the table's order: _Body is this one's index): open water dark blue-green with the sky in it at a
// glancing view, bare ice cool and clear, snow on ice packed and bright. Opaque (queue Geometry, under AlphaTest+39).
// MountainTerrain.shader is untouched.
Shader "MountainPlanner/PondWater"
{
    Properties
    {
        _Body ("Body index in the table", Float) = 0
        _SnowOn ("Snow layer", Float) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Geometry+10" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ WX_AIR WX_VEIL
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "../../Haze.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Body;
                float _SnowOn;
            CBUFFER_END
            TEXTURE2D(_WaterBodyStates);
            float4 _WaterBodyParams;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float Hash21(int2 c)
            {
                uint h = asuint(c.x) * 0x8DA6B343u ^ asuint(c.y) * 0xD8163841u;
                h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
                h = (h ^ (h >> 12)) * 0x297A2D39u;
                return (h >> 8) / 16777215.0;
            }

            float ValueNoise2(float2 x)
            {
                int2 i = (int2)floor(x);
                float2 f = frac(x);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash21(i), Hash21(i + int2(1, 0)), f.x), lerp(Hash21(i + int2(0, 1)), Hash21(i + int2(1, 1)), f.x), f.y);
            }

            // 0 open water, 1 bare ice, 2 snow on ice (unset or no table: open water, a new pond's look).
            float State()
            {
                if (_WaterBodyParams.x < 0.5) return 0;
                uint body = (uint)max(_Body, 0);
                if (body >= (uint)_WaterBodyParams.y) return 0;
                float s = LOAD_TEXTURE2D(_WaterBodyStates, uint2(body, 0)).x;
                return s > 0.5 ? s - 1 : 0;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 p = i.positionWS;
                float3 view = normalize(GetCameraPositionWS() - p);
                float state = State();
                if (_SnowOn < 0.5 && state > 1.5) state = 1;
                float3 n = float3(0, 1, 0);
                // A faint ripple on open water, so it isn't a mirror-flat plate.
                if (state < 0.5)
                {
                    float2 g = float2(ValueNoise2(p.xz / 3 + _Time.y * 0.05) - ValueNoise2(p.xz / 3 + 13.7), ValueNoise2(p.xz / 3 + 41.1 - _Time.y * 0.04) - 0.5);
                    n = normalize(float3(g.x * 0.08, 1, g.y * 0.08));
                }
                Light sun = GetMainLight(TransformWorldToShadowCoord(p));
                float ndl = saturate(dot(n, sun.direction));
                float3 ambient = SampleSH(n);
                float3 colour;
                if (state < 0.5)
                {
                    // Dark blue-green (0.5 §2), the sky in it toward the horizon, a sun glint.
                    float3 deep = float3(0.035, 0.07, 0.075) * (0.85 + 0.3 * ValueNoise2(p.xz / 25));
                    float fresnel = pow(1 - saturate(dot(n, view)), 5) * 0.9 + 0.03;
                    float3 sky = SampleSH(reflect(-view, n));
                    float3 h = normalize(sun.direction + view);
                    float glint = pow(saturate(dot(n, h)), 400) * 4;
                    colour = deep * (sun.color * sun.shadowAttenuation * ndl * 0.5 + ambient) + sky * fresnel + sun.color * glint * sun.shadowAttenuation;
                }
                else if (state < 1.5)
                {
                    float3 clear = float3(0.11, 0.18, 0.19) * (0.8 + 0.4 * ValueNoise2(p.xz / 25));
                    colour = clear * (sun.color * sun.shadowAttenuation * ndl + ambient) + SampleSH(reflect(-view, n)) * 0.08;
                }
                else
                {
                    float3 packed = float3(0.86, 0.89, 0.93) * (0.96 + 0.06 * ValueNoise2(p.xz / 6));
                    colour = packed * (sun.color * sun.shadowAttenuation * ndl + ambient);
                }
                return half4(ApplyHaze(colour, p), 1);
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; };
            float4 Vert(Attributes v) : SV_POSITION { return TransformObjectToHClip(v.positionOS.xyz); }
            half Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
