// The construction tools' preview lines and the placeholder drawing of committed work (task P3-06): flat, unlit, one
// colour. Each vertex is pulled toward the eye by a small share of its distance (at most 40 m), so a line draped on the
// float32 ground isn't swallowed by the terrain mesh, whose detail far away sits metres above or below the true
// surface; ridges in front still hide it. Tasks 08 and 09 draw the real trails and lifts. A line can fade out near the
// camera (_FadeNear, _FadeFar; the Trails map layer's run lines, owner TL26): dithered, so it stays opaque.
Shader "Hidden/MountainPlanner/DesignLine"
{
    Properties
    {
        _BaseColor("Colour", Color) = (1, 1, 1, 1)
        _Pull("Pull toward the eye (share of distance)", Float) = 0.006
        _FadeNear("Gone nearer than (m)", Float) = 0
        _FadeFar("Fully drawn from (m); 0 never fades", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+10" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "DesignLine"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On ZTest LEqual Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Pull;
                float _FadeNear;
                float _FadeFar;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float distance : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 world = TransformObjectToWorld(input.positionOS.xyz);
                float3 toEye = GetCameraPositionWS() - world;
                float distance = max(length(toEye), 1e-3);
                output.distance = distance;   // the line's own distance, before the pull
                world += toEye / distance * min(distance * _Pull, 40.0);
                output.positionCS = TransformWorldToHClip(world);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                if (_FadeFar > 0)
                {
                    float shown = saturate((input.distance - _FadeNear) / max(_FadeFar - _FadeNear, 1e-3));
                    // Interleaved gradient noise on the pixel: the share of pixels kept follows the fade.
                    float noise = frac(52.9829189 * frac(dot(input.positionCS.xy, float2(0.06711056, 0.00583715))));
                    clip(shown - noise - 1e-4);
                }
                return _BaseColor;
            }
            ENDHLSL
        }
    }
}
