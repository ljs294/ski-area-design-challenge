// Task 4.25 (GO-13, owner 2026-11-20): the groomers as dots. One draw, six vertices a dot, read from a structured buffer by
// instance as the skiers' dots are: an amber disc a little bigger than a skier's (14 px at 100%, never under 8 px, never
// more than 4 m across in the world) with a 1 px dark rim, depth-tested with a small pull towards the camera. At night a
// working groomer carries a soft warm glow round it (its headlights); a hovered one has a white ring.
Shader "MountainPlanner/GroomerDot"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-3" }
        Pass
        {
            Name "GroomerDot"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // xyz: where the groomer stands; w: < 0 hidden, else bit 0 hovered, bit 1 headlights, bits 2+ the glow (0-255)
            StructuredBuffer<float4> _Dots;
            float _DotPixels, _DotMetres, _MinDotPixels, _DotLift, _PullMax, _PullShare, _GlowScale;
            // the scene's post exposure (EV, SceneLighting): the dot is divided by it, so the night's +1.1 EV keeps it amber
            float _ExposureEv;
            float2 _ScreenPixels;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                nointerpolation float pixels : TEXCOORD1;   // the dot's diameter
                nointerpolation float scale : TEXCOORD2;    // the quad's size over the dot's (the glow's reach)
                nointerpolation float ring : TEXCOORD3;
                nointerpolation float glow : TEXCOORD4;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            Varyings Vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings o;
                float4 d = _Dots[instanceID];
                float2 corner = Corners[vertexID % 6];
                o.corner = corner;
                if (d.w < 0)
                {
                    o.positionCS = float4(2, 2, 2, 1);
                    o.pixels = 1; o.scale = 1; o.ring = 0; o.glow = 0;
                    return o;
                }
                uint packed = (uint)d.w;
                float glow = ((packed >> 2) & 255) / 255.0;
                float3 world = d.xyz + float3(0, _DotLift, 0);
                float3 toCamera = GetCameraPositionWS() - world;
                float dist = length(toCamera);
                world += toCamera / max(dist, 1e-3) * min(_PullMax, dist * _PullShare);
                float4 clip = TransformWorldToHClip(world);
                float metresPx = _DotMetres * UNITY_MATRIX_P._m11 * _ScreenPixels.y * 0.5 / max(clip.w, 1e-3);
                float px = clamp(metresPx, _MinDotPixels, _DotPixels);
                if (packed & 1) px *= 1.25;
                float scale = glow > 0 ? _GlowScale : 1;
                o.pixels = px; o.scale = scale; o.ring = (packed & 1) ? 1 : 0; o.glow = glow;
                float2 ext = px * scale / max(_ScreenPixels, 1);
                clip.xy += corner * ext * clip.w;
                o.positionCS = clip;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float r = length(i.corner) * i.scale;   // 1 at the dot's edge
                float px = max(i.pixels, 1);
                float aa = 1 / (px * 0.5);
                float disc = saturate((1 - r) / aa);
                // the headlights: a warm halo past the dot, fading out by the quad's edge
                float fall = saturate(1 - (r - 1) / max(i.scale - 1, 1e-3));
                float halo = i.glow * 0.85 * fall * fall;
                if (disc <= 0 && halo <= 0.002) discard;
                half3 amber = half3(0.95, 0.52, 0.04);
                half3 rim = i.ring > 0.5 ? half3(1, 1, 1) : half3(0.08, 0.07, 0.05);
                float rimWidth = i.ring > 0.5 ? 2.5 / (px * 0.5) : 1 / (px * 0.5);
                float inRim = saturate((r - (1 - rimWidth)) / aa);
                half3 dotColour = lerp(amber, rim, inRim);
                half3 light = half3(1.0, 0.92, 0.70);
                half3 c = (disc > 0 ? dotColour : light) * exp2(-_ExposureEv);
                return half4(c, max(disc, halo));
            }
            ENDHLSL
        }
    }
}
