// Task 4.08 PR B (SnowLitTests): snow of one type, lit by the real SnowLighting.hlsl, on a flat window under a given sun,
// sky and eye. Pass 0 is the whole light, pass 1 the shine alone, pass 2 the twinkle alone (linear, no tone mapping).
Shader "Hidden/MountainPlanner/Tests/SnowLitProbe"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #define SNOW_SPARKLE 1
        #include "../../../Art/Shaders/Snow.hlsl"
        #include "../../../Art/Shaders/SnowLighting.hlsl"

        float4 _ProbeRect;       // the window on the snow: west, south, width, depth (m)
        float4 _ProbeLook;       // x the type (SnowType), y its age (face hours), z the eye's distance (m), w metres a pixel
        float4 _ProbeNormal;     // the snow's normal
        float4 _ProbeView;       // toward the eye
        float4 _ProbeSun;        // toward the sun
        float4 _ProbeSunLight;   // the sun's colour times its shadow
        float4 _ProbeAmbient;    // the sky's light on the snow
        float4 _ProbeAlbedo;     // rgb the snow layer's albedo before the tint, a how much sky the point sees

        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings Vert(Attributes a)
        {
            Varyings v;
            v.positionCS = float4(a.uv * 2 - 1, 0.5, 1);
            v.uv = a.uv;
            return v;
        }

        float3 Lit(float2 uv, out float3 shine, out float3 twinkle)
        {
            SnowSurface s = (SnowSurface)0;
            SnowLookAdd(s, (int)round(_ProbeLook.x), _ProbeLook.y, 1);
            SnowLitInput i;
            i.albedo = _ProbeAlbedo.rgb * s.tint;
            i.normalSmooth = normalize(_ProbeNormal.xyz);
            i.normalDetail = i.normalSmooth;
            i.view = normalize(_ProbeView.xyz);
            i.sunDir = normalize(_ProbeSun.xyz);
            i.sunLight = _ProbeSunLight.rgb;
            i.sunCrease = 1;
            i.sunDisc = 1;
            i.ambient = _ProbeAmbient.rgb;
            i.skyLight = _ProbeAmbient.rgb;
            i.skyVis = _ProbeAlbedo.a;
            float2 xz = _ProbeRect.xy + uv * _ProbeRect.zw;
            // A gentle slope, so the snow crosses the sparkle's world cells as real ground does (a level plane at 0 would lie on their faces).
            i.positionWS = float3(xz.x, 3.37 + 0.213 * xz.x + 0.127 * xz.y, xz.y);
            i.dist = _ProbeLook.z;
            i.px = _ProbeLook.w;
            i.shineFade = 1;
            return SnowLit(s, i, shine, twinkle);
        }
        ENDHLSL

        Pass
        {
            Name "Lit"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target { float3 shine, twinkle; return float4(Lit(v.uv, shine, twinkle), 1); }
            ENDHLSL
        }

        Pass
        {
            Name "Shine"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target { float3 shine, twinkle; Lit(v.uv, shine, twinkle); return float4(shine, 1); }
            ENDHLSL
        }

        Pass
        {
            Name "Twinkle"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings v) : SV_Target { float3 shine, twinkle; Lit(v.uv, shine, twinkle); return float4(twinkle, 1); }
            ENDHLSL
        }
    }
}
