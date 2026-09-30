// The sky (0.5 §4, style tile): a gradient from the horizon to the zenith, a glow around the sun, the sun
// or moon disc, and stars at night. Below the horizon it fades to a soft backdrop colour, so the diorama
// sits in a clean space rather than on a grey-brown ground. SceneLighting sets every colour from the
// lighting preset, and _SkySunDirection (toward the sun) globally.
Shader "MountainPlanner/Sky"
{
    Properties
    {
        _Zenith ("Zenith", Color) = (0.37, 0.61, 0.84, 1)
        _Horizon ("Horizon", Color) = (0.86, 0.92, 0.97, 1)
        _Below ("Below the horizon", Color) = (0.76, 0.82, 0.86, 1)
        _SunColor ("Sun or moon", Color) = (1, 0.97, 0.92, 1)
        _DiscCos ("Cosine of the disc's radius", Float) = 0.99992
        _Stars ("Stars", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Zenith, _Horizon, _Below, _SunColor;
                float _DiscCos, _Stars;
            CBUFFER_END
            float3 _SkySunDirection;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.direction = v.positionOS.xyz;
                return o;
            }

            float Hash(int2 c)
            {
                uint h = asuint(c.x) * 0x8DA6B343u ^ asuint(c.y) * 0xD8163841u;
                h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
                h = (h ^ (h >> 12)) * 0x297A2D39u;
                return (h >> 8) / 16777215.0;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.direction);
                float3 sun = normalize(_SkySunDirection);
                float up = d.y;

                float3 colour = lerp(_Horizon.rgb, _Zenith.rgb, pow(saturate(up), 0.45));
                // The horizon glows toward the sun (strongest when the sun is low).
                float toward = saturate(dot(normalize(float3(d.x, 0, d.z)), normalize(float3(sun.x, 0.0001, sun.z))));
                float low = 1 - saturate(sun.y * 2.5);
                colour += _SunColor.rgb * pow(toward, 6) * (1 - saturate(abs(up) * 5)) * 0.35 * low;
                // A soft halo, then the disc itself (HDR, so golden-hour bloom picks it up).
                float c = dot(d, sun);
                colour += _SunColor.rgb * pow(saturate(c), 180) * 0.4;
                float edge = (1 - _DiscCos) * 0.25;
                colour = lerp(colour, _SunColor.rgb * 6, smoothstep(_DiscCos - edge, _DiscCos + edge, c));

                // Stars: one chance per cell of a longitude-latitude grid, a sharp point near the cell centre.
                UNITY_BRANCH
                if (_Stars > 0 && up > 0)
                {
                    float2 uv = float2(atan2(d.x, d.z) * 190, asin(up) * 190);
                    int2 cell = (int2)floor(uv);
                    float h = Hash(cell);
                    if (h > 0.985)
                    {
                        float2 centre = cell + 0.5 + (float2(Hash(cell + int2(7, 0)), Hash(cell + int2(0, 7))) - 0.5) * 0.6;
                        float r = length(uv - centre);
                        float bright = (h - 0.985) / 0.015;
                        colour += (0.4 + 1.6 * bright * bright) * smoothstep(0.35, 0.1, r) * saturate(up * 4) * _Stars;
                    }
                }

                // Below the horizon: a soft backdrop the diorama floats in.
                colour = lerp(colour, _Below.rgb, smoothstep(0, -0.12, up));
                return half4(colour, 1);
            }
            ENDHLSL
        }
    }
}
