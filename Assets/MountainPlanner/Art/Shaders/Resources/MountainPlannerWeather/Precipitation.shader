// Task 4.06 (WX-50, B6, P4): what's falling, near the camera. One draw: three nested shells of flakes or streaks around the
// camera (10, 30 and 120 m), anchored to the world and wrapped, so walking or flying through them never drags them along.
// Flakes drift, tumble and swirl in the gusts, stretch along their motion in a wind (a blizzard streaks), and are lit by the
// sky and the sun (bright when backlit, dim at night); rain is thin dark-grey streaks along its fall. Near the ground each
// particle is snow or rain by its own height against the snow line; from up high they show what's falling where the camera
// looks, and the near shells fade out so nothing sits on the lens (the curtains carry the overview). Every particle's size is
// capped by the angle it covers, and fades by its shell's edge and the near plane. Premultiplied, no depth write.
Shader "MountainPlanner/Precipitation"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+20" }
        Pass
        {
            Name "Precipitation"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../Weather.hlsl"

            float4 _PrecipA;      // x: share drawing (0-1), y: snow line (world y), z: time (s, wrapped), w: alpha scale
            float4 _PrecipWind;   // xz: the wind (m/s, toward), y: unused, w: gusts (0-1)
            float4 _PrecipShells; // xyz: shell half-sizes (m, each dividing 120), w: particles per shell
            float4 _PrecipSize;   // xyz: flake radius per shell (m), w: the most angle a flake's radius covers (rad)
            float4 _PrecipFade;   // xyz: each shell's opacity (the near ones fade from the overview), w: near the ground (0-1)
            float4 _PrecipLight;  // rgb: the sky's light (linear), w: the snow share where the camera looks (0-1)
            float4 _PrecipSun;    // rgb: the sun's light (linear), w: rain (0-1, the rain arc: darker streaks)
            float4 _PrecipCam;    // xyz: the camera modulo 240 m (wrapped in double precision on the CPU)

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 colour : COLOR;
                float snow : TEXCOORD1;
            };

            float3 Hash3(uint n)
            {
                uint3 v = uint3(n * 1664525u + 1013904223u, n * 22695477u + 1u, n * 134775813u + 7u);
                v = (v ^ (v >> 16)) * 0x7FEB352Du;
                v = (v ^ (v >> 15)) * 0x846CA68Bu;
                v ^= v >> 16;
                return float3(v & 0xFFFFFFu) / 16777215.0;
            }

            Varyings Vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                Varyings o = (Varyings)0;
                uint per = (uint)_PrecipShells.w;
                uint shell = iid / max(per, 1u), local = iid % max(per, 1u);
                o.positionCS = float4(2, 2, 2, 1);   // off screen unless it draws
                if (shell > 2 || local >= (uint)(per * _PrecipA.x)) return o;
                float shellFade = shell == 0 ? _PrecipFade.x : shell == 1 ? _PrecipFade.y : _PrecipFade.z;
                if (shellFade <= 0.001) return o;

                float R = shell == 0 ? _PrecipShells.x : shell == 1 ? _PrecipShells.y : _PrecipShells.z;
                float size = shell == 0 ? _PrecipSize.x : shell == 1 ? _PrecipSize.y : _PrecipSize.z;
                float3 h = Hash3(iid * 3u + 11u), j = Hash3(iid * 7u + 5u);
                float3 cam = _WorldSpaceCameraPos;
                float near = _PrecipFade.w;
                float snowGuess = lerp(_PrecipLight.w, smoothstep(_PrecipA.y - 120, _PrecipA.y + 120, cam.y), near);
                float fall = lerp(7.5, 1.1 + j.x * 0.6, snowGuess);
                float t = _PrecipA.z;
                float3 vel = float3(_PrecipWind.x, -fall, _PrecipWind.z) * (0.85 + 0.3 * j.y);
                float3 p = h * 2 * R + vel * t;
                // Flakes tumble across their fall, and the gusts swirl neighbours together (two sines of position and time).
                float swirl = 0.35 * snowGuess + 1.6 * _PrecipWind.w * (0.4 + 0.6 * snowGuess);
                p.xz += swirl * float2(sin(p.y * 0.31 + p.x * 0.07 + t * (0.9 + j.x) + j.z * 6.283),
                                       cos(p.y * 0.27 - p.z * 0.06 + t * (0.8 + j.y) + j.x * 6.283));
                float3 off = (frac((p - _PrecipCam.xyz) / (2 * R) + 0.5) - 0.5) * 2 * R;
                float3 w = cam + off;
                float d = length(off);
                float snow = lerp(_PrecipLight.w, smoothstep(_PrecipA.y - 120, _PrecipA.y + 120, w.y), near) > 0.5 ? 1 : 0;

                // The quad: across the view, stretched along the motion across it (motion blur), its radius capped by angle.
                float3 toCam = (cam - w) / max(d, 1e-3);
                float3 across = vel - dot(vel, toCam) * toCam;
                float speed = length(across);
                float3 along = speed > 1e-3 ? across / speed : normalize(cross(toCam, abs(toCam.y) > 0.95 ? float3(1, 0, 0) : float3(0, 1, 0)));
                float3 side = normalize(cross(along, toCam) + float3(1e-5, 0, 0));
                float r = min(size, d * _PrecipSize.w);
                float length_ = snow > 0.5 ? r + speed * 0.035 * saturate((speed - 2) * 0.25) : r + speed * 0.045;
                float width = snow > 0.5 ? r : r * 0.22;
                float2 corner = float2((vid == 1 || vid == 2 || vid == 4) ? 1 : -1, (vid == 2 || vid == 4 || vid == 5) ? 1 : -1);
                w += side * (width * corner.x) + along * (length_ * corner.y);
                o.positionCS = TransformWorldToHClip(w);
                o.uv = corner;
                o.snow = snow;

                // Lit by the sky and the sun: snow glows when backlit (light through it toward the eye), rain is dark grey.
                float3 sun = normalize(_SkySunDirection);
                float phase = pow(saturate(dot(-toCam, sun)), 6);
                // Snow is always a step brighter than the fog it falls through, or a white-out swallows it.
                float3 c = snow > 0.5 ? 0.9 * (_PrecipLight.rgb * 1.6 + _PrecipSun.rgb * (0.3 + 1.6 * phase))
                                      : _PrecipLight.rgb * lerp(0.7, 0.5, _PrecipSun.w) + _PrecipSun.rgb * (0.04 + 0.4 * phase);
                UNITY_BRANCH
                if (snow > 0.5 && _WxParams.x > 0.5) c = max(c, _WxFogColor.rgb * 1.3);
                c = lerp(c, _WxFogColor.rgb, 0.25 * saturate(d / R));
                float fade = smoothstep(R, R * 0.55, d) * smoothstep(0.25, 1.2, d) * shellFade;
                float a = (snow > 0.5 ? 0.9 : 0.32) * fade * _PrecipA.w;
                o.colour = half4(c * a, a);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                // Snow: a soft round flake (an ellipse when it streaks). Rain: a thin streak, fading at its ends.
                float s = i.snow > 0.5 ? saturate(1 - length(i.uv)) : saturate(1 - abs(i.uv.x)) * saturate(1 - i.uv.y * i.uv.y);
                s *= s;
                return i.colour * s;
            }
            ENDHLSL
        }
    }
}
