// The weather's air as one screen pass (task 4.06, WeatherVolume.cs): after the sky and before the transparents, the haze,
// the cloud deck, the snow curtains and the valley sea are worked out once per pixel and blended over the picture, so the
// surfaces' shaders carry none of it. Because it's in the air, not on the surfaces, it can be shadowed: each ray is stepped
// through the haze, asking at every step whether the sun reaches that air (the mountain's shadow ceiling, FarTerrainShadow;
// the clouds' shadow map, CloudShadowMap). Shadowed haze loses the sun's share of its light and lit haze glows toward the
// sun, which draws the shafts between clouds and the ridges' long shadows across a hazy valley.
//
// Pass 0 steps the rays at half resolution (the sun's reach changes slowly across the picture), pass 1 smooths the steps'
// fixed 4x4 pattern (no history, so nothing ghosts or flickers and a pinned look's shot is the same every run), pass 2 works
// the air out at full resolution with that reach and blends it over the picture: colour * a + rgb.
//
// Pass 4 runs first: it copies the camera's depth (sample 0 under MSAA) to a plain texture the other passes read, so pass 2
// can bind the real depth buffer for its stencil test. Lifts, cables, skier figures and the design's lines mark their
// pixels in the stencil (bit 8) and fog themselves, keeping a share of their own colour, so they still read in a white-out;
// the blend skips those samples (per sample under MSAA, so a thin cable keeps its edge).
//
// Pass 3 (High up) marches the cloud deck as a volume, at half resolution, smoothed by pass 1 too: the same field, cover,
// front and drift as the flat deck and the ground's shadows, given height. A cell's column rises with the field's value
// over the cover's threshold, so cells are domes on a flat base and a closed sky is a lumpy sheet; the detail channel carves
// the tops. Each step is lit by how much cloud lies between it and its column's top toward the sun, opened up where the cell
// beside it toward the sun stands lower (one more tap), and by the light inside a cloud (_WxLight: the haze's own brightness
// by day, the moon's and the sky's at night), so the inside of a cloud matches the air outside it (task 4.06, F1-F5). Cells
// lean and their carving changes with height, so their sides aren't upright walls (F7); long steps far off soften the cells'
// edges, and toward the horizon the deck dissolves into the horizon's haze, so it never ends in a line (F6).
Shader "Hidden/MountainPlanner/WeatherVolume"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #define WX_AIR 1
        #define WX_PASS 1
        #include "../../Weather.hlsl"

        // The camera's depth. Pass 4 reads the camera's own buffer (the game camera makes no depth texture), multisampled when
        // MSAA is on and the buffer binds that way: sample 0 is enough for fog. The other passes read pass 4's copy.
        #if defined(_WX_DEPTH_MSAA)
            Texture2DMS<float, 1> _WxVolumeDepthMS;
        #else
            TEXTURE2D_X_FLOAT(_WxVolumeDepth);
        #endif
        float4 _WxVolumeDepthSize;   // the depth buffer's width and height (pixels)
        float4 _WxVolumeSize;        // the low-resolution target's width and height, and 1 / them
        float4 _WxVolumeSteps;       // x: steps through the haze for the sun's reach, y: show the reach as grey (-wxdebug)
        TEXTURE2D(_WxVolumeTex);     // r: the share of the haze along the ray that the sun reaches, g: the lit air along it (km of valley-floor air)
        TEXTURE2D(_WxVolumeDeckTex); // the marched deck: rgb its light, a what it lets through
        float4 _WxDeckVol;           // x: the cloud's density (1/m), y: the most the deck hides (0-1), z: steps, w: the deck is marched (0/1)
        float4 _WxDeckShape;         // x: how tall the tallest cell is (a share of the deck's depth), y: the longest stretch marched (m)

        float RawDepthAt(int2 px)
        {
            px = clamp(px, int2(0, 0), int2(_WxVolumeDepthSize.xy) - 1);
            #if defined(_WX_DEPTH_MSAA)
                return _WxVolumeDepthMS.Load(px, 0).r;
            #else
                return LOAD_TEXTURE2D_X(_WxVolumeDepth, px).r;
            #endif
        }

        // The depth the low-resolution texel t stands for: the full-resolution pixel at its centre.
        float LowDepth(int2 t)
        {
            t = clamp(t, int2(0, 0), int2(_WxVolumeSize.xy) - 1);
            return RawDepthAt(int2((t + 0.5) * _WxVolumeSize.zw * _WxVolumeDepthSize.xy));
        }

        bool IsSky(float raw)
        {
            #if UNITY_REVERSED_Z
                return raw < 1e-7;
            #else
                return raw > 0.9999999;
            #endif
        }

        float EyeDepth(float raw) { return IsSky(raw) ? 1e6 : LinearEyeDepth(raw, _ZBufferParams); }

        // The point a pixel shows (the sky: as far as Sky.shader takes its air), and whether it's the sky.
        float3 PointAt(float2 uv, float raw, out bool sky)
        {
            sky = IsSky(raw);
            float mid = 0.5;
            #if !UNITY_REVERSED_Z
                raw = raw * 2 - 1;
                mid = 0;
            #endif
            float3 p = ComputeWorldSpacePosition(uv, sky ? mid : raw, UNITY_MATRIX_I_VP);
            if (sky) p = _WorldSpaceCameraPos + normalize(p - _WorldSpaceCameraPos) * 30000;
            return p;
        }

        float FragDepth(Varyings input) : SV_Target { return RawDepthAt(int2(input.positionCS.xy)); }

        // A fixed 4x4 pattern (0-1): each of 16 neighbours starts its steps at a different sixteenth.
        float Pattern(int2 t)
        {
            static const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
            return (bayer[(t.x & 3) + (t.y & 3) * 4] + 0.5) / 16;
        }

        // The share of the haze along the ray that the sun reaches, weighted by where the haze is (it thins upward).
        half4 FragMarch(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            int2 t = int2(uv * _WxVolumeSize.xy);
            bool sky;
            float3 p = PointAt(uv, LowDepth(t), sky);
            float3 cam = _WorldSpaceCameraPos;
            float d = distance(p, cam);
            float3 dir = (p - cam) / max(d, 1e-3);
            float from = _WxParams.z, to = min(d, _WxVolume.z);
            if (to <= from) return half4(1, 0, 0, 0);
            int steps = (int)_WxVolumeSteps.x;
            float start = Pattern(t);
            float all = 1e-5, lit = 0;
            UNITY_LOOP
            for (int i = 0; i < steps; i++)
            {
                float3 q = cam + dir * lerp(from, to, (i + start) / steps);
                float w = exp(-clamp((q.y - _WxHaze.y) * _WxHaze.z, -6, 4));
                all += w;
                lit += w * WxSunLit(q);
            }
            return half4(lit / all, lit / steps * (to - from) * 0.001, 0, 0);
        }

        // The deck along a pixel's ray: its light and what it lets through.
        half4 FragDeck(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            int2 t = int2(uv * _WxVolumeSize.xy);
            bool sky;
            float3 p = PointAt(uv, LowDepth(t), sky);
            float3 cam = _WorldSpaceCameraPos;
            float3 v = p - cam;
            if (sky) v *= 10;   // the sky's deck runs on toward the horizon (300 km), settling and then dissolving on the way
            float d = length(v);
            float3 dir = v / max(d, 1e-3);
            float thick = max(_WxDeck.y - _WxDeck.x, 1);
            float2 span = WxSpan(cam.y, v.y, _WxDeck.x, _WxDeck.x + thick * min(1, _WxDeckShape.x * 1.2));   // no steps above the tallest cell
            float from = max(span.x * d, _WxParams.z), to = span.y * d;
            if (to <= from) return half4(0, 0, 0, 1);
            to = min(to, from + _WxDeckShape.y);   // a longer stretch is far off or edge-on: its start is what shows
            float cover = WxCover((cam + dir * from).xz + _WxCloud.zw);
            float3 l = normalize(_SkySunDirection);
            float sunUp = saturate(l.y * 3 + 0.15);
            float phase = 0.5 + 0.3 * min(0.51 / pow(max(1.49 - 1.4 * dot(dir, l), 0.05), 1.5), 2.5);
            float2 sunWay = l.xz / max(length(l.xz), 1e-3);
            // Steps grow with distance (each the same share further than the last), so the near cells are stepped finely and
            // the far ones as coarsely as they look.
            int steps = (int)_WxDeckVol.z;
            float grow = pow(to / from, 1.0 / steps);
            float at = from * pow(grow, Pattern(t));
            float through = 1;
            float3 light = 0;
            UNITY_LOOP
            for (int i = 0; i < steps; i++)
            {
                float3 q = cam + dir * at;
                float ds = at * (grow - 1);
                float lod = WxLod(at, abs(dir.y));
                at *= grow;
                float h = saturate((q.y - _WxDeck.x) / thick);
                float2 c = q.xz + _WxCloud.zw + float2(0.35, 0.2) * (h * thick);   // the cells lean with height
                float n = SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, c * _WxCloudScale.x, lod).r;
                float edge = 1 - lerp(cover, _WxFrontOld.x, WxFrontAt(c, n));
                // The column's top, as a share of the deck's depth: by how far the field stands over the cover's threshold, so a
                // small cell is a low one (a sparse sky's cells are small puffs, not towers).
                float top = pow(saturate((n - edge) * 2), 0.77) * _WxDeckShape.x;
                UNITY_BRANCH
                if (top > h)
                {
                    float g = SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, WxDetailUv(c) + h * float2(0.31, -0.23), lod + 2).g;
                    top -= (0.5 - g) * 0.14 * smoothstep(0, 0.3, top);
                    // A cell's rim lifts off the deck's base (its underside rounds up to meet its top), so a small cell is a
                    // puff, not a dome cut flat.
                    float under = top * 0.55 * (1 - smoothstep(0, 0.6 * _WxDeckShape.x, top));
                    // A long step (far off) softens the edges, so a step half in a cell is half dense, never all or nothing.
                    float stepShare = ds / thick;
                    float dens = saturate((top - h) / (0.07 + 0.35 * stepShare)) * saturate((h - under) / (0.05 + 0.25 * stepShare) + 0.25 * step(under, 0.001));
                    // The side toward the sun: where the cell beside it that way stands lower at this height, the sun comes in
                    // from the side rather than down through the column.
                    float nSun = SAMPLE_TEXTURE2D_LOD(_WxCloudTex, sampler_WxCloudTex, (c + sunWay * 160) * _WxCloudScale.x, lod).r;
                    float open = saturate((h - pow(saturate((nSun - edge) * 2), 0.77) * _WxDeckShape.x) / 0.1);
                    float toSun = _WxDeckVol.x * 0.12 * max(top - h, 0) * thick / max(l.y, 0.2) * (1 - 0.7 * open);
                    float reach = exp(-toSun) * 0.8 + exp(-toSun * 0.2) * 0.2;
                    // Where the sun reaches, it lights the cloud and the haze's share gives way (half of it at a sunlit top), so
                    // the sunlit side stays below white with its form; deep inside, the haze's own light is all there is (F1).
                    float sunLit = reach * sunUp;
                    float3 lit = _WxSun.rgb * (sunLit * phase * 0.55) + _WxLight.rgb * (lerp(0.7, 1, saturate(h / max(_WxDeckShape.x, 0.1))) * (1 - 0.5 * sunLit));
                    float a = 1 - exp(-_WxDeckVol.x * dens * ds);
                    light += through * a * lit;
                    through *= 1 - a;
                    if (through < 0.02) break;
                }
            }
            // Far off the cells are smaller than a step: the deck settles to its average (the sky's sooner; the ground seen
            // through it keeps its holes further, F3). Toward the horizon it takes the horizon haze's colour and then thins
            // out by where the sky's rays stop, so it never ends in a line (F6).
            float farFrom = sky ? 12000 : 20000;
            float far = smoothstep(farFrom, farFrom * 2.5, from), mean = cover * 0.85;
            light = lerp(light, (_WxLight.rgb * (0.85 - 0.25 * sunUp) + _WxSun.rgb * (0.25 * sunUp)) * mean, far);
            through = lerp(through, 1 - mean, far);
            through = lerp(through, 1, smoothstep(120000, 290000, from));
            light = lerp(light, _WxFogColor.rgb * (1 - through), smoothstep(30000, 90000, from));
            return half4(light * _WxDeckVol.y, 1 - _WxDeckVol.y * (1 - through));
        }

        // The 4x4 neighbourhood the pattern repeats over, centred (5x5, the outer ring at half weight, so it still covers one
        // whole period each way), weighted so a ridge's reach doesn't bleed onto the sky behind it.
        half4 FragSmooth(Varyings input) : SV_Target
        {
            int2 t = int2(input.texcoord * _WxVolumeSize.xy);
            float dc = EyeDepth(LowDepth(t));
            float4 sum = 0;
            float weight = 0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                int2 s = clamp(t + int2(x, y), int2(0, 0), int2(_WxVolumeSize.xy) - 1);
                float ds = EyeDepth(LowDepth(s));
                float w = 1 / (1 + abs(ds - dc) / (0.03 * min(ds, dc) + 1) * 4);
                w *= w * (abs(x) == 2 ? 0.5 : 1) * (abs(y) == 2 ? 0.5 : 1);
                sum += LOAD_TEXTURE2D(_WxVolumeTex, s) * w;
                weight += w;
            }
            return sum / weight;
        }

        half4 FragBlend(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            bool sky;
            float raw = RawDepthAt(int2(uv * _WxVolumeDepthSize.xy));
            float3 p = PointAt(uv, raw, sky);
            bool marched = _WxDeckVol.w > 0.5;
            // The low-resolution results: the four texels round the pixel, each by how near its depth is to the pixel's own, so a
            // ridge in front of a cloud or a shaft keeps a clean edge (the rays' too, task 4.06: plain bilinear drew bands, F3).
            float dc = EyeDepth(raw);
            float2 at = uv * _WxVolumeSize.xy - 0.5;
            int2 t0 = int2(floor(at));
            float2 f = at - t0;
            float4 deck = 0, reachSum = 0;
            float weight = 0;
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                int2 s = clamp(t0 + int2(x, y), int2(0, 0), int2(_WxVolumeSize.xy) - 1);
                float ds = EyeDepth(LowDepth(s));
                float w = (x == 0 ? 1 - f.x : f.x) * (y == 0 ? 1 - f.y : f.y) + 1e-3;
                float near = 1 / (1 + abs(ds - dc) / (0.03 * min(ds, dc) + 1) * 4);
                w *= near * near;
                reachSum += LOAD_TEXTURE2D(_WxVolumeTex, s) * w;
                UNITY_BRANCH
                if (marched) deck += LOAD_TEXTURE2D(_WxVolumeDeckTex, s) * w;
                weight += w;
            }
            float2 reach = reachSum.rg / weight;
            deck /= weight;
            UNITY_BRANCH
            if (_WxVolumeSteps.y > 0.5) return half4((1 - reach.x) * 3, reach.y * 0.1, WxSunLit(p) * 0.5, 0);
            WxAir air = WxAirToShaded(p, sky, reach.x, !marched);
            // The sea's sunlit top lies in the mountain's shadow too (an inversion at sunrise: the ridges' shadows across it).
            UNITY_BRANCH
            if (air.sea > 0 && _WxVolume.y > 0) air.seaColour *= 1 - _WxVolume.y * (1 - WxSunLit(air.seaAt));
            float keepAir = 1 - air.amount, keep = (1 - air.sea) * keepAir;
            float3 light = air.seaColour * (air.sea * keepAir) + air.colour * air.amount;
            // The beams: lit air scatters the sun toward the eye, most looking toward it, so the shafts between the clouds and
            // past the ridges stand out against the shadowed air beside them.
            float toward = dot(normalize(p - _WorldSpaceCameraPos), normalize(_SkySunDirection));
            float forward = min(0.51 / pow(max(1.49 - 1.4 * toward, 0.05), 1.5), 2.2);   // Henyey-Greenstein, g 0.7, its peak capped
            light += _WxSun.rgb * ((1 - exp(-reach.y * 1000 * _WxVolume.w)) * (0.3 + forward) * keep);
            // The marched deck: from under it the low air is in front; from over it, behind. Across its base the one eases into
            // the other over 100 m, so going up into the cloud has no seam (task 4.06, F2).
            UNITY_BRANCH
            if (marched)
            {
                float over = saturate((_WorldSpaceCameraPos.y - _WxDeck.x) / 100 + 0.5);
                light = lerp(light + keep * deck.rgb, deck.rgb + deck.a * light, over);
                keep *= deck.a;
            }
            return half4(light, keep);
        }
        ENDHLSL

        Pass
        {
            Name "March"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMarch
            ENDHLSL
        }
        Pass
        {
            Name "Smooth"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragSmooth
            ENDHLSL
        }
        Pass
        {
            Name "Blend"
            Blend One SrcAlpha, Zero One
            Stencil { Ref 8 ReadMask 8 Comp NotEqual }   // not what fogs itself (lifts, cables, skiers, the design's lines)
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlend
            ENDHLSL
        }
        Pass
        {
            Name "Deck"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDeck
            ENDHLSL
        }
        Pass
        {
            Name "Depth"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDepth
            #pragma multi_compile_local_fragment _ _WX_DEPTH_MSAA
            ENDHLSL
        }
    }
}
