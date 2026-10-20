// A built lift on the map (task 09, LT10, LT23): the trail map's red line, its end dots and tower ticks, draped over
// the ground; see-through so it can fade out as the camera comes close and the 3D lift takes over.
Shader "MountainPlanner/LiftMapLine"
{
    Properties
    {
        _Color ("Colour (alpha: fade)", Color) = (0.78, 0.13, 0.1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" }
        Pass
        {
            Name "MapLine"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always   // a map stroke: over the ground, never cut by a ridge between its ends
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; };
            float4 Vert(Attributes v) : SV_POSITION { return TransformObjectToHClip(v.positionOS.xyz); }
            half4 Frag() : SV_Target { return _Color; }
            ENDHLSL
        }
    }
}
