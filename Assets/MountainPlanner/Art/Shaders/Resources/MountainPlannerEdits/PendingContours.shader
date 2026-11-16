// The pending edit's changed contours (CF-08, task P3-05): flat yellow lines a little above the ground, drawn over it.
Shader "MountainPlanner/PendingContours"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.85, 0.1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "AlphaTest+45" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Lines"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest LEqual
            Offset -2, -2
            Stencil { Ref 8 WriteMask 8 Comp Always Pass Replace }   // task 4.06: the weather's screen pass doesn't fog the pending contours (WeatherVolume)
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target { return _Color; }
            ENDHLSL
        }
    }
}
