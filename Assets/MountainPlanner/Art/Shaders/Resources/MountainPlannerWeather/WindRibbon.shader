// Task 4.06 (WX-53, B9): snow carried on the wind from points (WindRibbons.cs): spindrift streaming off the exposed ridges in a
// gale, and snow guns' plumes. One draw: per emitter PUFFS soft puffs, each born at the emitter, thrown along its jet, carried
// downwind, swelling, sinking and wandering in the gusts, then thinning out; worked out here from the emitter buffer and the
// clock, so the CPU sets a few vectors per frame. Lit by the sky and the sun (bright when backlit), fogged like the ground the
// same way the weather fogs everything (Weather.hlsl), wisped by the cloud field's detail, soft where they meet the ground.
// Premultiplied, no depth write.
Shader "MountainPlanner/WindRibbon"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+10" }
        Pass
        {
            Name "WindRibbon"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ WX_AIR WX_KEEP   // task 4.06: the weather's fog while something's in the air (worked out per vertex; WX_KEEP while the screen pass draws the air)
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "../../Weather.hlsl"

            #define PUFFS 24u   // WindRibbons.Puffs

            StructuredBuffer<float4> _RibbonEmitters;   // per emitter: (position, strength), (jet, life), (size from, size to, sink, carry)
            float4 _RibbonA;      // x: emitters, y: clock (s, wrapped at 600), z: overall strength (0-1), w: gusts (0-1)
            float4 _RibbonWind;   // xz: the wind (m/s, toward), y: the camera has a depth texture (1: soft edges), w: the cloud field is there (1)
            float4 _RibbonSun;    // rgb: the sun's light (linear)
            float4 _RibbonSky;    // rgb: the sky's light (linear)

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 colour : COLOR;
                float4 extra : TEXCOORD1;   // x: eye depth (m), y: the puff's size (m), zw: where its wisps sit in the field
            };

            float RibbonHash(uint n)
            {
                n = (n ^ 61u) ^ (n >> 16);
                n *= 9u;
                n ^= n >> 4;
                n *= 0x27D4EB2Du;
                n ^= n >> 15;
                return (n & 0xFFFFFFu) / 16777215.0;
            }

            Varyings Vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                Varyings o = (Varyings)0;
                o.positionCS = float4(2, 2, 2, 1);   // off screen unless it draws
                uint e = iid / PUFFS, k = iid % PUFFS;
                if (e >= (uint)_RibbonA.x) return o;
                float4 e0 = _RibbonEmitters[e * 3u], e1 = _RibbonEmitters[e * 3u + 1u], e2 = _RibbonEmitters[e * 3u + 2u];
                float strength = e0.w * _RibbonA.z;
                if (strength <= 0.002) return o;

                float seed = RibbonHash(e * 7u + 3u), jitter = RibbonHash(iid * 13u + 1u), spin = RibbonHash(iid * 29u + 5u);
                float life = max(e1.w, 0.5);
                float age = frac(_RibbonA.y / life + (k + jitter * 0.6) / PUFFS + seed);
                // The jet slows as the puff leaves it (most of its reach early); the wind carries it on; it sinks, swells and
                // wanders, more in the gusts.
                float jet = 1 - (1 - age) * (1 - age);
                float t = age * life;
                float size = lerp(e2.x, e2.y, sqrt(age));
                float3 p = e0.xyz + e1.xyz * jet + float3(_RibbonWind.x, 0, _RibbonWind.z) * (t * e2.w) - float3(0, e2.z * age * age, 0);
                float wander = size * (0.35 + 0.5 * _RibbonA.w) * age;
                p += wander * float3(sin(t * 1.3 + jitter * 6.283), 0.5 * sin(t * 0.9 + spin * 6.283), cos(t * 1.1 + seed * 6.283));

                // A quad facing the camera, turned its own way and turning slowly.
                float3 cam = _WorldSpaceCameraPos;
                float3 toCam = normalize(cam - p);
                float3 side = normalize(cross(abs(toCam.y) > 0.98 ? float3(1, 0, 0) : float3(0, 1, 0), toCam));
                float3 up = cross(toCam, side);
                float2 corner = float2((vid == 1 || vid == 2 || vid == 4) ? 1 : -1, (vid == 2 || vid == 4 || vid == 5) ? 1 : -1);
                float angle = spin * 6.283 + age * (spin - 0.5) * 2;
                float2 turned = float2(corner.x * cos(angle) - corner.y * sin(angle), corner.x * sin(angle) + corner.y * cos(angle));
                float3 w = p + (side * turned.x + up * turned.y) * size;
                o.positionCS = TransformWorldToHClip(w);
                o.uv = corner;
                o.extra = float4(-TransformWorldToView(p).z, size, seed * 7.1 + k * 0.37, spin * 5.3);

                // Lit by the sky and the sun, brightest when backlit (light through the snow toward the eye); fogged like the
                // ground behind it, so a far ridge's plume sits in the same air.
                float3 sun = normalize(_SkySunDirection);
                float phase = pow(saturate(dot(-toCam, sun)), 4);
                // As bright as the sunlit snow it came off (a shade over, backlit), or it reads as grey smoke against the sky.
                float3 c = _RibbonSky.rgb * 1.3 + _RibbonSun.rgb * (0.75 + 1.4 * phase);
                float fog = WeatherFogAmount(p);
                c = lerp(c, _WxFogColor.rgb, fog);
                float a = strength * smoothstep(0, 0.04, age) * pow(1 - age, 1.6) * 0.55 * (1 - 0.6 * fog);
                o.colour = half4(c * a, a);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float r = length(i.uv);
                float s = saturate(1 - r);
                s = s * s * (3 - 2 * s);
                // Wisps: the cloud field's detail breaks the puff's edge, so the plume reads as blown snow, not balls.
                UNITY_BRANCH
                if (_RibbonWind.w > 0.5)
                {
                    float wisp = SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, i.uv * 0.18 + i.extra.zw, 1).g;
                    s *= saturate(lerp(0.35, 1.7, wisp) - r * 0.45);
                }
                // Soft where it meets the ground or anything in front of it (where the camera makes a depth texture).
                UNITY_BRANCH
                if (_RibbonWind.y > 0.5)
                {
                    float2 suv = i.positionCS.xy / _ScaledScreenParams.xy;
                    float scene = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                    s *= saturate((scene - i.extra.x) / max(i.extra.y * 0.5, 0.05));
                }
                return i.colour * s;
            }
            ENDHLSL
        }
    }
}
