// The Network view's diagram (task P3-10): lines, chevrons and node circles in one mesh, coloured per vertex, drawn over
// the ground and never cut by a ridge (as the lift map lines are), in the mesh's own order, so casings stay under lines.
Shader "MountainPlanner/NetworkLine"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-5" }
        Pass
        {
            Name "NetworkLine"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; half4 colour : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; half4 colour : COLOR; };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.colour = v.colour;
                return o;
            }
            half4 Frag(Varyings i) : SV_Target { return i.colour; }
            ENDHLSL
        }
    }
}
