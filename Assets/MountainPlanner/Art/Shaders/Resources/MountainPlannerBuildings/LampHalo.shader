// Task 4.50 slice 3: a lit building lamp's glare at night (LampHaloLayer). One draw, six vertices a glare, read from a
// structured buffer by instance as the groomers' dots are: a camera-facing soft disc HaloMetres across in the world
// (about its lens), clamped between MinPixels and MaxPixels on screen, a hot point in the lamp's own colour, added over
// the scene and depth-tested. Round 2 (owner 2026-11-26): it shows only where the lens does. A shaded lamp fades out as
// the eye moves behind or above its aim; a cage sconce shines all round; and the pull toward the eye is a few
// centimetres, so the lamp's own fixture, hood or eave hides it as it would the bulb. Its brightness is fixed on screen:
// the post exposure is divided back out, as the groomers' headlights do.
Shader "MountainPlanner/LampHalo"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-4" }
        Pass
        {
            Name "LampHalo"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // three float4 a glare: the lens in the world and its level, 0-1; its aim and 1 if it shines all round; its colour
            StructuredBuffer<float4> _Halos;
            float _HaloMetres, _MinPixels, _MaxPixels, _ExposureEv;
            float _NearFade;   // glare only from this far out, fading in over half as far again (0: everywhere)
            float2 _ScreenPixels;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                nointerpolation float3 colour : TEXCOORD1;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            Varyings Vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings o;
                float4 h = _Halos[instanceID * 3], aim = _Halos[instanceID * 3 + 1];
                float2 corner = Corners[vertexID % 6];
                o.corner = corner;
                float3 world = h.xyz;
                float3 toCamera = GetCameraPositionWS() - world;
                float dist = length(toCamera);
                float3 toEye = toCamera / max(dist, 1e-3);
                // the lens shows from in front of its aim: gone by the time the eye is 9 degrees behind it
                float seen = aim.w > 0.5 ? 1.0 : smoothstep(-0.15, 0.25, dot(aim.xyz, toEye));
                float far = _NearFade > 0 ? smoothstep(_NearFade, _NearFade * 1.5, dist) : 1.0;
                o.colour = _Halos[instanceID * 3 + 2].rgb * (h.w * seen * far * exp2(-_ExposureEv));
                world += toEye * 0.04;   // clear of its own lens, no further: the fixture hides it as it hides the bulb
                float4 clip = TransformWorldToHClip(world);
                // abs: rendering into a texture flips the projection's y (_m11 < 0), which would pin every glare to its minimum
                float metresPx = _HaloMetres * abs(UNITY_MATRIX_P._m11) * _ScreenPixels.y * 0.5 / max(clip.w, 1e-3);
                float px = clamp(metresPx, _MinPixels, _MaxPixels);
                float2 ext = px / max(_ScreenPixels, 1);
                clip.xy += corner * ext * clip.w;
                o.positionCS = clip;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float r = length(i.corner);
                if (r >= 1) discard;
                float fall = 1 - r;
                float core = fall * fall * fall;      // the hot point: nearly white
                float glow = fall * fall * 0.35;      // a little of the lamp's colour round it
                half3 c = (core * lerp((half3)i.colour, dot(i.colour, half3(0.33, 0.34, 0.33)).xxx, 0.5) * 1.6 + glow * (half3)i.colour);
                return half4(c, 0);
            }
            ENDHLSL
        }
    }
}
