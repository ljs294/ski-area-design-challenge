// Task 4.13: the skiers as dots. One draw, six vertices a dot, each read from a structured buffer by instance: a disc of
// a fixed size on screen (9 px at 100%, never more than 1.2 m across in the world, never under 6 px), coloured by ability
// band (green, blue, black, red for experts) with a 1 px dark rim (light on a black dot), depth-tested with a small pull towards the camera so
// snow never swallows a dot but a ridge still hides it. The selected skier is larger with a white ring; a hovered one
// has a ring.
Shader "MountainPlanner/SkierDot"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test (debug: Always draws on top)", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-4" }
        Pass
        {
            Name "SkierDot"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _Dots;
            float _DotPixels, _DotMetres, _MinDotPixels, _ColourMode;
            float _Dim;   // task 4.13 PR C: under the Traffic layer the dots step back (the selected and hovered ones don't)
            // SkierLayer.DotLiftMetres, PullMaxMetres, PullShare: the click target (SkierPlaceJob.DrawnCentre) uses the same.
            float _DotLift, _PullMax, _PullShare;
            // The screen's own pixels (set from C#): the render target can be larger or smaller (render scale), and a
            // dot's size is the player's pixels, not the target's (first capture: half size at High).
            float2 _ScreenPixels;
            // Task 4.22: nearer than this a guest is drawn as a figure (SkierFigures), so its dot isn't, unless it is the
            // selected or hovered one or the Traffic layer has dimmed the dots (0: no figures, every dot draws).
            float _FigureMetres;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                nointerpolation half4 colour : COLOR;
                nointerpolation float ring : TEXCOORD1;   // 0 none, 1 hovered, 2 selected
                nointerpolation float pixels : TEXCOORD2;
                nointerpolation float lightRim : TEXCOORD3;   // 1: a black dot, which needs a light rim to read
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            half3 BandColour(uint band)
            {
                if (band == 0) return half3(0.16, 0.70, 0.27);   // beginner: green
                if (band == 1) return half3(0.13, 0.42, 0.93);   // intermediate: blue
                if (band == 2) return half3(0.06, 0.06, 0.07);   // advanced: black
                return half3(0.88, 0.13, 0.13);                  // expert: red (owner, 2026-11-09)
            }

            half3 StateColour(uint cls)
            {
                if (cls == 0) return half3(0.92, 0.94, 0.98);    // skiing
                if (cls == 1) return half3(0.98, 0.70, 0.12);    // in line
                if (cls == 2) return half3(0.20, 0.80, 0.85);    // riding a tow
                if (cls == 3) return half3(0.60, 0.62, 0.66);    // standing about
                return half3(0.85, 0.20, 0.70);                  // in the line of a lift with no places (not built yet)
            }

            half3 SatisfactionColour(float s)
            {
                // Red at 0.5 and under, amber at 0.7, green at 0.9 and over (GU-18's verdict range).
                float t = saturate((s - 0.5) / 0.4);
                return t < 0.5 ? lerp(half3(0.85, 0.15, 0.12), half3(0.95, 0.70, 0.10), t * 2) : lerp(half3(0.95, 0.70, 0.10), half3(0.15, 0.72, 0.25), t * 2 - 1);
            }

            Varyings Vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings o;
                float4 d = _Dots[instanceID];
                float2 corner = Corners[vertexID % 6];
                o.corner = corner;
                if (d.w < 0)
                {
                    o.positionCS = float4(2, 2, 2, 1);   // hidden: outside the clip volume
                    o.colour = 0; o.ring = 0; o.pixels = 1; o.lightRim = 0;
                    return o;
                }
                uint packed = (uint)d.w;
                uint band = packed & 3, cls = (packed >> 2) & 15, flags = (packed >> 6) & 3;
                float sat = ((packed >> 8) & 255) / 255.0;
                if (_FigureMetres > 0 && _Dim <= 0 && flags == 0 && distance(d.xyz, GetCameraPositionWS()) < _FigureMetres)
                {
                    o.positionCS = float4(2, 2, 2, 1);
                    o.colour = 0; o.ring = 0; o.pixels = 1; o.lightRim = 0;
                    return o;
                }
                float3 world = d.xyz + float3(0, _DotLift, 0);   // a standing skier's middle
                float3 toCamera = GetCameraPositionWS() - world;
                float dist = length(toCamera);
                world += toCamera / max(dist, 1e-3) * min(_PullMax, dist * _PullShare);   // the pull towards the camera
                float4 clip = TransformWorldToHClip(world);
                // Pixels for 1.2 m at this distance, clamped between the floor and the dot size.
                float metresPx = _DotMetres * UNITY_MATRIX_P._m11 * _ScreenPixels.y * 0.5 / max(clip.w, 1e-3);
                float px = clamp(metresPx, _MinDotPixels, _DotPixels);
                if (flags & 1) px = max(px, _DotPixels) * 1.5;
                else if (flags & 2) px = max(px, _DotPixels) * 1.25;
                o.pixels = px;
                float2 ext = px / max(_ScreenPixels, 1);   // screen pixels to clip units (half the diameter × 2 / size)
                clip.xy += corner * ext * clip.w;
                o.positionCS = clip;
                half3 c = _ColourMode < 0.5 ? BandColour(band) : _ColourMode < 1.5 ? StateColour(cls) : SatisfactionColour(sat);
                o.colour = half4(c, (flags & 3) ? 1 : 1 - _Dim);
                o.lightRim = dot(c, half3(0.3, 0.59, 0.11)) < 0.25 ? 1 : 0;
                o.ring = (flags & 1) ? 2 : (flags & 2) ? 1 : 0;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float r = length(i.corner);              // 0 at the centre, 1 at the edge
                float px = max(i.pixels, 1);
                float edge = 1 - 1 / (px * 0.5);         // one pixel in from the edge
                float aa = 1 / (px * 0.5);
                float alpha = saturate((1 - r) / aa);
                if (alpha <= 0) discard;
                // Snow is the ground skiers are on, so the rim is dark; a black dot gets a light one. Rings are white.
                half3 rim = i.ring > 1.5 ? half3(1, 1, 1) : i.lightRim > 0.5 ? half3(0.92, 0.94, 0.96) : half3(0.08, 0.09, 0.11);
                float rimWidth = i.ring > 0.5 ? 2.5 / (px * 0.5) : 1 / (px * 0.5);
                float inRim = saturate((r - (1 - rimWidth)) / aa);
                half3 c = lerp(i.colour.rgb, rim, inRim);
                return half4(c, alpha * i.colour.a);
            }
            ENDHLSL
        }
    }
}
