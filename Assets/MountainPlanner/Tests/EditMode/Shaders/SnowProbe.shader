// Task 4.07 PR A2: the snow as the terrain draws it, through the real Snow.hlsl, onto a flat window of the map for
// SnowPictureTests (EditMode): pass 0 writes the depth (m) and the cover, pass 1 the Snow conditions colour; A3's lift
// passes follow.
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

        // Task 4.07 PR A3 (SnowLiftTests): pass 2 the ground's lift (m) and its slope along x and z; pass 3 where each pixel is.
        Pass
        {
            Name "Lift"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target
            {
                float3 p = World(v.uv);
                float2 slope = SnowLiftSlope(p);
                return float4(SnowLiftAt(p), slope.x, slope.y, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Where"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target { float3 p = World(v.uv); return float4(p.x, p.z, 0, 1); }
            ENDHLSL
        }

        // Task 4.08 PR B (SnowLooksTests): pass 4 the look SnowSurfaceAt gives (tint r and b, the shine's gloss, track depth); pass 5
        // the finish (corduroy, moguls, passes / 1020, the fine cells' share).
        Pass
        {
            Name "Look"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target { SnowSurface s = SnowSurfaceAt(World(v.uv)); return float4(s.tint.r, s.tint.b, s.gloss, s.tracks); }
            ENDHLSL
        }

        Pass
        {
            Name "Finish"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target { SnowSurface s = SnowSurfaceAt(World(v.uv)); return float4(s.corduroy, s.moguls, s.passes / 1020, s.owned); }
            ENDHLSL
        }
    }
}
