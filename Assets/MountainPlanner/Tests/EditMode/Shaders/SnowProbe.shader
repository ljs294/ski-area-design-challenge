// Task 4.07 PR A2: the snow as the terrain draws it, through the real Snow.hlsl, onto a flat window of the map for
// SnowPictureTests (EditMode): pass 0 writes the depth (m) and the cover, pass 1 the Snow conditions colour.
Shader "Hidden/MountainPlanner/Tests/SnowProbe"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "../../../Art/Shaders/Snow.hlsl"

        float4 _ProbeRect;   // the window: west, south, width, depth (world metres)

        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        // Graphics.Blit's quad spans 0..1: straight to clip space (the picture's orientation doesn't matter to the test).
        Varyings Vert(Attributes a)
        {
            Varyings v;
            v.positionCS = float4(a.uv * 2 - 1, 0.5, 1);
            v.uv = a.uv;
            return v;
        }

        float3 World(float2 uv)
        {
            float2 xz = _ProbeRect.xy + uv * _ProbeRect.zw;
            return float3(xz.x, 0, xz.y);
        }
        ENDHLSL

        Pass
        {
            Name "Depth"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target
            {
                float3 p = World(v.uv);
                return float4(SnowDepthAt(p), SnowCoverAt(p), 0, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Conditions"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target { return float4(SnowConditionsColour(World(v.uv)), 1); }
            ENDHLSL
        }
    }
}
