// The mountain's terrain shader (0.3 §4.4, 0.5 §2; style tile): all six ground layers in one pass
// from two splat maps (snow, forest floor, grass, rock / developed, ice), height-based blending so
// transitions look like snow drifting over rock rather than a cross-fade, triplanar rock so cliffs never
// stretch, frozen-lake shading, the cover-map overlay, and a clean cut at the edge of the downloaded
// data (the diorama edge, A1). The splat holds the snow and the bare ground cover under it (SplatTexels);
// the snow is laid over the land here, so the Snow and Cover map layers (task 12) switch with a float.
Shader "MountainPlanner/Terrain"
{
    Properties
    {
        _Albedo ("Albedo + height (array)", 2DArray) = "" {}
        _Normals ("Normals (array)", 2DArray) = "" {}
        _Overlay ("Cover-map overlay", Float) = 0
        _HeightBlend ("Height-blend sharpness", Range(0.01, 1)) = 0.2
        _SnowOn ("Snow layer: snow on the ground (lakes: snow on ice, or bare ice)", Float) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Geometry-100" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "TerrainCompatible" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_Control0); SAMPLER(sampler_Control0);
        TEXTURE2D(_Control1);
        TEXTURE2D_ARRAY(_Albedo); SAMPLER(sampler_Albedo);
        TEXTURE2D_ARRAY(_Normals);

        CBUFFER_START(UnityPerMaterial)
            float _Overlay;
            float _HeightBlend;
            float _SnowOn;
        CBUFFER_END
        float _ControlRes;           // per tile (property block): splat texels per edge
        float _TileSize;             // per tile: metres per splat uv
        float4 _RingBounds;          // xmin, zmin, xmax, zmax in world space: the data's edge
        float _Tile[6];              // metres per texture repeat, per layer
        float _Smooth[6];
        float4 _OverlayColor[6];
        // Task 10 seams (0.3 §4.6), set by SurfaceStates. The snow-depth map (metres) over the ring: snow covers
        // fully from _SnowDepthParams.x metres deep; .w = 0 (not set yet) is full cover. Iteration 1 is 12 in
        // everywhere, so the picture is the same as before the map existed.
        TEXTURE2D(_SnowDepthMap); SAMPLER(sampler_SnowDepthMap);
        float4 _SnowDepthRect;       // west, south, 1 / width, 1 / depth (world metres)
        float4 _SnowDepthParams;     // x: depth for full cover (m), w: on (0/1)
        // The water's surface state (WaterBodies): x = state + 1 (1 open water, 2 ice, 3 snow-covered ice;
        // 0 not set yet, drawn as snow-covered ice), y ice and z snow thickness (m).
        float4 _LakeState;

        float SnowCover(float3 positionWS)
        {
            UNITY_BRANCH
            if (_SnowDepthParams.w < 0.5) return 1;
            float2 uv = (positionWS.xz - _SnowDepthRect.xy) * _SnowDepthRect.zw;
            return saturate(SAMPLE_TEXTURE2D_LOD(_SnowDepthMap, sampler_SnowDepthMap, uv, 0).r / _SnowDepthParams.x);
        }

        // The terrain stops at the edge of the downloaded data; the diorama walls take over below it.
        void ClipToRing(float3 positionWS)
        {
            clip(min(min(positionWS.x - _RingBounds.x, _RingBounds.z - positionWS.x),
                     min(positionWS.z - _RingBounds.y, _RingBounds.w - positionWS.z)));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Haze.hlsl"
            #include "FarShadow.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fog : TEXCOORD3;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            // Top-down sample of one layer: albedo (height in alpha) and a world-space normal (whiteout blend).
            void SampleTop(int layer, float3 p, float3 n, out float4 albedo, out float3 normal)
            {
                float2 uv = p.xz / _Tile[layer];
                albedo = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, uv, layer);
                float3 t = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, uv, layer).xyz * 2 - 1;
                normal = normalize(float3(n.x + t.x, n.y, n.z + t.y));
            }

            // Triplanar sample (rock): each face projects from its own axis, so cliffs never stretch. The
            // strata in the rock texture run horizontally on the side projections.
            void SampleTriplanar(int layer, float3 p, float3 n, out float4 albedo, out float3 normal)
            {
                float3 w = pow(abs(n), 4);
                w /= w.x + w.y + w.z;
                float s = 1 / _Tile[layer];
                float4 ax = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.zy * s, layer);
                float4 ay = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xz * s, layer);
                float4 az = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xy * s, layer);
                float3 tx = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.zy * s, layer).xyz * 2 - 1;
                float3 ty = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.xz * s, layer).xyz * 2 - 1;
                float3 tz = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, p.xy * s, layer).xyz * 2 - 1;
                albedo = ax * w.x + ay * w.y + az * w.z;
                float3 nx = float3(0, tx.y, tx.x) * sign(n.x);
                float3 ny = float3(ty.x, 0, ty.y);
                float3 nz = float3(tz.x, tz.y, 0) * sign(n.z);
                normal = normalize(n + nx * w.x + ny * w.y + nz * w.z);
            }

            // An integer hash of a lattice cell: exact, so neighbouring cells always agree on a shared corner. (A
            // float hash of world-sized coordinates rounded differently cell to cell, and noise patches broke
            // along straight lattice lines on the ring's lakes.)
            float Hash12(int2 c)
            {
                uint h = asuint(c.x) * 0x8DA6B343u ^ asuint(c.y) * 0xD8163841u;
                h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
                h = (h ^ (h >> 12)) * 0x297A2D39u;
                return (h >> 8) / 16777215.0;
            }

            float ValueNoise(float2 p)
            {
                int2 i = (int2)floor(p);
                float2 f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash12(i), Hash12(i + int2(1, 0)), f.x), lerp(Hash12(i + int2(0, 1)), Hash12(i + int2(1, 1)), f.x), f.y);
            }

            // The water weight at a world offset (metres) from this pixel's splat uv.
            float WaterAt(float2 cuv, float2 offsetMetres, float res)
            {
                return SAMPLE_TEXTURE2D_LOD(_Control1, sampler_Control0, cuv + offsetMetres / max(_TileSize, 1) * ((res - 1) / res), 0).g;
            }

            // Frozen lakes (T8, 0.5 §2): flat snow-covered ice, smoother and a touch brighter than land snow,
            // with a thin rim of exposed blue-grey ice at the waterline and pressure cracks running along the
            // shore. Streams stay snow-filled channels. With the snow off,
            // lakes are bare dark ice. The shore is found from the water weight on rings 4 m and 12 m out:
            // a lake's interior reads 1 on both, its waterline about 0.5; a stream a few metres wide reads
            // low on the outer ring. Only water pixels pay for this (the layer is skipped elsewhere).
            void SampleLake(float3 p, float3 n, float2 cuv, float res, float water, float footprint, out float4 albedo, out float3 normal, out float smooth)
            {
                float near = 0, far = 0;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float2 d;
                    sincos(k * (TWO_PI / 8) + 0.39, d.y, d.x);
                    near += WaterAt(cuv, d * 4, res);
                    far += WaterAt(cuv, d * 12, res);
                }
                near /= 8;
                far /= 8;
                float lake = smoothstep(0.3, 0.5, far);                  // 0 a stream, 1 a lake or a wide river
                float inside = saturate((near - 0.5) * 2);               // 0 at the waterline, 1 from about 4 m out
                float shore = saturate(near + far - 1);                  // 0 at the waterline, 1 about 12 m out

                float4 snow, ice;
                float3 snowN, iceN;
                SampleTop(0, p, n, snow, snowN);
                SampleTop(5, p, n, ice, iceN);

                // Pressure cracks: two broken lines running along the shore, antialiased and faded with distance
                // (footprint: metres per pixel, taken outside the branch; the shore field changes about 1/12 a metre).
                float wobble = (ValueNoise(p.xz / 6) - 0.5) * 0.12;
                float width = max(0.025, footprint / 12 * 1.5);
                float crack = max(1 - smoothstep(0, width, abs(shore - 0.3 + wobble)),
                                  1 - smoothstep(0, width, abs(shore - 0.62 - wobble)));
                crack *= smoothstep(0.35, 0.6, ValueNoise(p.xz / 14 + 7)) * saturate(0.025 / width) * lake;

                // Clear ice over dark water (0.5 §2: dark blue-green): the ice texture's clouds and cracks only
                // as a faint grain, and a broad variation so its 20 m repeat never shows.
                float grain = dot(ice.rgb, float3(0.3, 0.5, 0.2)) - 0.72;
                float3 clear = max(float3(0.11, 0.18, 0.19) * (0.8 + 0.4 * ValueNoise(p.xz / 25)) + grain * 0.1, 0.02);
                const float3 rimIce = float3(0.52, 0.69, 0.79);                  // #BFD9E6, the art direction's ice rim

                float state = _LakeState.x > 0.5 ? _LakeState.x - 1 : 2;   // WaterSurfaceState; unset reads as snow-covered ice
                if (state > 1.5 && _SnowOn > 0.5)
                {
                    // (Wind-scoured patches of bare ice were tried and read as grey smudges, so the snow is unbroken.)
                    float rim = (1 - smoothstep(0, 0.35, inside)) * lake;       // about a metre of bare ice at the waterline
                    float3 packed = snow.rgb * float3(1.02, 1.03, 1.05);          // wind-packed snow: brighter, a touch cool
                    float3 colour = lerp(packed, rimIce, rim * 0.7);
                    float bare = rim * 0.7;
                    colour = lerp(colour, rimIce * 0.8, crack * (1 - bare) * 0.6);
                    // Streams: a snow-filled channel, a little cooler toward the middle.
                    colour = lerp(colour, snow.rgb * float3(0.93, 0.96, 1.0), (1 - lake) * saturate(water * 1.5 - 0.3));
                    albedo = float4(colour, 0.12 + 0.18 * (1 - bare));
                    normal = normalize(lerp(n, snowN, 0.35 * (1 - bare)));      // packed flat: less relief than land snow
                    smooth = lerp(0.5, 0.8, bare);
                }
                else if (state < 0.5)
                {
                    // Open water (a thaw, for the future weather engine): darker than clear ice, glassy, with a
                    // faint ripple, and the sky's horizon reflected at grazing angles. No ice rim or cracks.
                    float3 v = normalize(_WorldSpaceCameraPos - p);
                    float ripple = ValueNoise(p.xz / 3) - 0.5;
                    float3 rippled = normalize(n + float3(ripple, 0, ValueNoise(p.xz / 3 + 11) - 0.5) * 0.06);
                    float fresnel = 0.02 + 0.98 * pow(1 - saturate(dot(rippled, v)), 5);
                    float3 deep = float3(0.035, 0.07, 0.075) * (0.85 + 0.3 * ValueNoise(p.xz / 40));
                    albedo = float4(lerp(deep, _HazeColor * 0.8, fresnel * lake + fresnel * 0.4 * (1 - lake)), 0);
                    normal = rippled;
                    smooth = 0.95;
                }
                else
                {
                    // Bare ice: clear and dark, whiter at the shore and along the cracks.
                    float3 colour = lerp(clear, rimIce * 0.75, saturate((1 - inside) * 0.7 + crack * 0.4));
                    albedo = float4(colour, 0.2);
                    normal = normalize(lerp(n, iceN, 0.2));
                    smooth = 0.6;
                }
            }

            half4 Frag(Varyings i) : SV_Target
            {
                ClipToRing(i.positionWS);
                float3 n = normalize(i.normalWS);

                // Splat weights, sampled texel-centre to texel-centre as Unity lays them out.
                float res = max(_ControlRes, 2);
                float2 cuv = (i.uv * (res - 1) + 0.5) / res;
                float4 c0 = SAMPLE_TEXTURE2D(_Control0, sampler_Control0, cuv);
                float4 c1 = SAMPLE_TEXTURE2D(_Control1, sampler_Control0, cuv);
                // c0.r is the snow's weight; the other five channels are the bare ground cover (summing to 1), or 0
                // on a tile whose cover isn't painted yet, which stays all snow. The snow lies over the land share,
                // and the land layers keep what it leaves (as SplatTexels composed it before task 12). Thin snow
                // (the depth map) lets the ground show through.
                float composed = c0.g + c0.b + c0.a + c1.r + c1.g;
                float painted = saturate(composed * 50);
                float land = 1 - c1.g;
                float snowWeight = c0.r * lerp(1, SnowCover(i.positionWS), painted);
                // The cover-map overlay leaves the snow out whatever the Snow layer says.
                float snowOn = lerp(1, _SnowOn > 0.5 && _Overlay < 0.5 ? 1 : 0, painted);
                snowWeight *= snowOn;
                float keep = land > 0.004 ? saturate(1 - snowWeight / land) : 0;
                float weights[6] = { snowWeight, c0.g * keep, c0.b * keep, c0.a * keep, c1.r * keep, c1.g };

                if (_Overlay > 0.5)
                {
                    float3 flat = 0;
                    [unroll] for (int k = 0; k < 6; k++) flat += weights[k] * _OverlayColor[k].rgb;
                    Light sun0 = GetMainLight();
                    float lambert = saturate(dot(n, sun0.direction)) * 0.6 + 0.5;
                    return half4(flat * lambert, 1);
                }

                // Height-based blend: a layer wins where its own relief rises above the others.
                float4 albedos[6];
                float3 normals[6];
                float smooths[6];
                float best = -10;
                float heights[6];
                float footprint = length(fwidth(i.positionWS));   // metres per pixel, for the lake's cracks
                [unroll] for (int k = 0; k < 6; k++)
                {
                    albedos[k] = 0;
                    normals[k] = n;
                    smooths[k] = _Smooth[k];
                    heights[k] = -10;
                    [branch] if (weights[k] > 0.004)
                    {
                        if (k == 3) SampleTriplanar(k, i.positionWS, n, albedos[k], normals[k]);
                        else if (k == 5) SampleLake(i.positionWS, n, cuv, res, weights[k], footprint, albedos[k], normals[k], smooths[k]);
                        else SampleTop(k, i.positionWS, n, albedos[k], normals[k]);
                        heights[k] = weights[k] + albedos[k].a * 0.6;
                        best = max(best, heights[k]);
                    }
                }
                float3 albedo = 0, normal = 0;
                float total = 0, smooth = 0;
                [unroll] for (int m = 0; m < 6; m++)
                {
                    float w = max(heights[m] - best + _HeightBlend, 0);
                    albedo += albedos[m].rgb * w;
                    normal += normals[m] * w;
                    smooth += smooths[m] * w;
                    total += w;
                }
                albedo /= total;
                normal = normalize(normal);
                smooth /= total;

                Light sun = MainLightWithFarShadow(i.positionWS);
                float3 view = normalize(GetWorldSpaceViewDir(i.positionWS));
                float ndl = saturate(dot(normal, sun.direction));
                float3 h = normalize(sun.direction + view);
                float spec = pow(saturate(dot(normal, h)), exp2(10 * smooth + 1)) * smooth * 0.5;
                float3 lit = albedo * (sun.color * ndl * sun.shadowAttenuation + SampleSH(normal))
                           + sun.color * spec * sun.shadowAttenuation;
                lit = MixFog(lit, i.fog);
                return half4(ApplyHaze(lit, i.positionWS), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(o.positionWS, n, _LightDirection));
                #if UNITY_REVERSED_Z
                    cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = cs;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                ClipToRing(i.positionWS);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                ClipToRing(i.positionWS);
                return 0;
            }
            ENDHLSL
        }
    }
}
