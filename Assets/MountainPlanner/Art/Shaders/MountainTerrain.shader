// The mountain's terrain shader (0.3 §4.4, 0.5 §2; style tile): all six ground layers in one pass
// from two splat maps (snow, forest floor, grass, rock / developed, ice), height-based blending so
// transitions look like snow drifting over rock rather than a cross-fade, triplanar rock so cliffs never
// stretch, frozen-lake shading, the cover-map overlay, and a clean cut at the edge of the downloaded
// data (the diorama edge, A1). The splat holds the snow and the bare ground cover under it (SplatTexels);
// the snow is laid over the land here, so the Snow and Cover map layers (task 12) switch with a float. The info
// layers and contours (task 12b, InfoLayers.hlsl) take their slope from the tile's own heightmap on the GPU.
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
            #ifdef UNITY_INSTANCING_ENABLED
            float4 _TerrainHeightmapRecipSize;   // set by Unity for instanced terrain: 1/width, 1/height, 1/(width-1), 1/(height-1)
            float4 _TerrainHeightmapScale;       // the heightmap's scale, y over the 16-bit range
            #endif
        CBUFFER_END
        // GPU-instanced terrain (task 15; TerrainTiles.DrawInstanced): Unity draws a tile's patches as instances of one
        // flat grid, and the vertex stage reads each vertex's height and normal from the terrain's own textures, as
        // URP's TerrainLit does (TerrainInstancing). Without this path instanced tiles drew flat and untextured (task 06).
        #ifdef UNITY_INSTANCING_ENABLED
        TEXTURE2D(_TerrainHeightmapTexture);
        #endif
        UNITY_INSTANCING_BUFFER_START(Terrain)
            UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)   // x, y: the patch's base; z: its sample spacing
        UNITY_INSTANCING_BUFFER_END(Terrain)

        void TerrainInstancing(inout float4 positionOS, inout float3 normalOS, inout float2 uv)
        {
        #ifdef UNITY_INSTANCING_ENABLED
            float4 patch = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);
            float2 sampleCoords = (positionOS.xy + patch.xy) * patch.z;
            float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));
            positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
            positionOS.y = height * _TerrainHeightmapScale.y;
            // The normal from the heights around the sample (central differences), so lighting doesn't depend on the
            // normal map Unity may or may not have built for the tile.
            int2 c = int2(sampleCoords);
            int2 last = int2(round(1 / _TerrainHeightmapRecipSize.zw));
            float hl = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(max(c.x - 1, 0), c.y, 0)));
            float hr = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(min(c.x + 1, last.x), c.y, 0)));
            float hd = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(c.x, max(c.y - 1, 0), 0)));
            float hu = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(c.x, min(c.y + 1, last.y), 0)));
            normalOS = normalize(float3((hl - hr) * _TerrainHeightmapScale.y / (2 * _TerrainHeightmapScale.x), 1,
                                        (hd - hu) * _TerrainHeightmapScale.y / (2 * _TerrainHeightmapScale.z)));
            uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
        #endif
        }
        float _ControlRes;           // per tile (property block): splat texels per edge
        float _TileSize;             // per tile: metres per splat uv
        TEXTURE2D(_Heightmap); SAMPLER(sampler_mp_linear_clamp);   // per tile: Unity's heightmap on the GPU (task 12b: slope and exposure at 1 m)
        float4 _HeightmapParams;     // per tile: x texels per edge, y metres per unit sample, z metres between samples
        float4 _RingBounds;          // xmin, zmin, xmax, zmax in world space: the data's edge
        float _Tile[8];              // metres per texture repeat, per terrain slot (task 12d: eight, with roads)
        float _Smooth[8];
        // The bare ground's variation within a layer (task 12c). The texture arrays hold four more looks after the
        // six splat layers (GroundTextures): alpine meadow, scree, bare dirt and meadow seen from a distance.
        float4 _AlpineBand;          // x, y: elevations (m) where valley grass gives way to alpine meadow; unset: none
        float4 _GrassTint;           // the season's colour on grass and meadow (rgb multiplier; unset reads as none), for the seasons task
        static const int MeadowLayer = 6, ScreeLayer = 7, DirtLayer = 8, MeadowFarLayer = 9;
        static const float MeadowTile = 4, ScreeTile = 6, DirtTile = 3, MeadowFarTile = 90;   // metres per repeat
        float4 _OverlayColor[8];
        // Task 10 seams (0.3 §4.6), set by SurfaceStates. The snow-depth map (metres) over the ring: snow covers
        // fully from _SnowDepthParams.x metres deep; .w = 0 (not set yet) is full cover. Iteration 1 is 12 in
        // everywhere, so the picture is the same as before the map existed.
        TEXTURE2D(_SnowDepthMap); SAMPLER(sampler_SnowDepthMap);
        float4 _SnowDepthRect;       // west, south, 1 / width, 1 / depth (world metres)
        float4 _SnowDepthParams;     // x: depth for full cover (m), w: on (0/1)
        // The water's surface state (WaterBodies): x = state + 1 (1 open water, 2 ice, 3 snow-covered ice;
        // 0 not set yet, drawn as snow-covered ice), y ice and z snow thickness (m).
        float4 _LakeState;

        // Snow depth in metres at a point (12 in until the field is set).
        float SnowDepthMetres(float3 positionWS)
        {
            if (_SnowDepthParams.w < 0.5) return 0.3048;
            float2 uv = (positionWS.xz - _SnowDepthRect.xy) * _SnowDepthRect.zw;
            return SAMPLE_TEXTURE2D_LOD(_SnowDepthMap, sampler_SnowDepthMap, uv, 0).r;
        }

        float SnowCover(float3 positionWS)
        {
            UNITY_BRANCH
            if (_SnowDepthParams.w < 0.5) return 1;
            float2 uv = (positionWS.xz - _SnowDepthRect.xy) * _SnowDepthRect.zw;
            return saturate(SAMPLE_TEXTURE2D_LOD(_SnowDepthMap, sampler_SnowDepthMap, uv, 0).r / _SnowDepthParams.x);
        }

        // The crown map (P2-09, ForestRenderer.BuildCrownMap): the share of the ground under tree crowns, 2 m over the
        // core and 6 m over the ring. -1 when there's no forest yet, which keeps the cover map's stand look.
        TEXTURE2D(_CrownMapCore); SAMPLER(sampler_CrownMapCore);
        TEXTURE2D(_CrownMapRing);
        float4 _CrownRectCore;       // west, south, 1 / width, 1 / depth (world metres)
        float4 _CrownRectRing;
        float4 _CrownParams;         // w: on (0/1)

        float CrownCover(float2 xz)
        {
            UNITY_BRANCH
            if (_CrownParams.w < 0.5) return -1;
            float2 core = (xz - _CrownRectCore.xy) * _CrownRectCore.zw;
            if (all(core > 0.001) && all(core < 0.999)) return SAMPLE_TEXTURE2D(_CrownMapCore, sampler_CrownMapCore, core).r;
            return SAMPLE_TEXTURE2D(_CrownMapRing, sampler_CrownMapCore, (xz - _CrownRectRing.xy) * _CrownRectRing.zw).r;
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
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Haze.hlsl"
            #include "FarShadow.hlsl"
            #include "InfoLayers.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
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
                UNITY_SETUP_INSTANCE_ID(v);
                TerrainInstancing(v.positionOS, v.normalOS, v.uv);
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

            // A photo layer at two scales (task 12c): its own repeat, and a copy 5.7 times larger and turned 40°, so
            // the repeat never shows from afar. Height and normal come from the near scale.
            void SampleDetail(int layer, float tile, float3 p, float3 n, out float4 albedo, out float3 normal)
            {
                float2 uv = p.xz / tile;
                float4 near = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, uv, layer);
                float2 turned = float2(uv.x * 0.766 - uv.y * 0.643, uv.x * 0.643 + uv.y * 0.766) / 5.7 + 0.37;
                float3 far = SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, turned, layer).rgb;
                albedo = float4(lerp(near.rgb, far, 0.4), near.a);
                float3 t = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Albedo, uv, layer).xyz * 2 - 1;
                normal = normalize(float3(n.x + t.x, n.y, n.z + t.y));
            }

            // Brightness drifting over a few hundred metres, so a slope of one cover type isn't one flat tone.
            float Macro(float3 p)
            {
                return 0.86 + 0.18 * ValueNoise(p.xz / 230) + 0.08 * ValueNoise(p.xz / 61 + 17);
            }

            // Grass (task 12c): winter-dormant valley grass giving way to sparser alpine meadow up high (its far
            // texture shows rock patches across 90 m), and bare dirt breaking through on steep grass.
            void SampleGrass(float3 p, float3 n, float slopePercent, out float4 albedo, out float3 normal)
            {
                SampleDetail(2, _Tile[2], p, n, albedo, normal);
                float alpine = _AlpineBand.y > _AlpineBand.x
                    ? smoothstep(_AlpineBand.x, _AlpineBand.y, p.y + (ValueNoise(p.xz / 150) - 0.5) * 80) : 0;
                [branch] if (alpine > 0.01)
                {
                    float4 meadow;
                    float3 meadowN;
                    SampleDetail(MeadowLayer, MeadowTile, p, n, meadow, meadowN);
                    meadow.rgb = lerp(meadow.rgb, SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xz / MeadowFarTile, MeadowFarLayer).rgb, 0.5);
                    albedo = lerp(albedo, meadow, alpine);
                    normal = normalize(lerp(normal, meadowN, alpine));
                }
                float bare = smoothstep(60, 95, slopePercent) * smoothstep(0.5, 0.75, ValueNoise(p.xz / 14)) * 0.75;
                [branch] if (bare > 0.01)
                {
                    float4 dirt;
                    float3 dirtN;
                    SampleDetail(DirtLayer, DirtTile, p, n, dirt, dirtN);
                    albedo = lerp(albedo, dirt, bare);
                    normal = normalize(lerp(normal, dirtN, bare));
                }
                albedo.rgb *= Macro(p) * (any(_GrassTint.rgb) ? _GrassTint.rgb : 1);
                // Depth up close (beauty pass, item 3): hollows between the blades darker, the relief a little stronger.
                albedo.rgb *= lerp(0.78, 1.12, albedo.a);
                normal = normalize(n + (normal - n) * 1.6);
            }

            // Rock (task 12c): granite faces on steep ground (triplanar), scree and talus where it lies back.
            void SampleRock(float3 p, float3 n, float slopePercent, out float4 albedo, out float3 normal)
            {
                SampleTriplanar(3, p, n, albedo, normal);
                float gentle = 1 - smoothstep(70, 110, slopePercent);
                [branch] if (gentle > 0.01)
                {
                    float4 scree;
                    float3 screeN;
                    SampleDetail(ScreeLayer, ScreeTile, p, n, scree, screeN);
                    albedo = lerp(albedo, scree, gentle);
                    normal = normalize(lerp(normal, screeN, gentle));
                }
                albedo.rgb *= Macro(p);
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

            // The ground's rise per metre east (x) and north (y), from the tile's heightmap by central differences two
            // samples either side (4 m across in the core, 8 m in the ring): the slope a skier feels, without the
            // 1 m lidar's ruts and ditches, the same at any distance whatever the mesh's LOD.
            float2 HeightGradient(float2 uv)
            {
                float res = max(_HeightmapParams.x, 2);
                float2 huv = (uv * (res - 1) + 0.5) / res;
                float t = 2 / res;
                float east = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv + float2(t, 0), 0).r;
                float west = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv - float2(t, 0), 0).r;
                float north = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv + float2(0, t), 0).r;
                float south = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv - float2(0, t), 0).r;
                return float2(east - west, north - south) * _HeightmapParams.y / max(4 * _HeightmapParams.z, 1e-3);
            }

            // Fine creases (beauty pass, item 2): how far the ground sits below its neighbours 3 samples away (3 m in
            // the core), so gullies, ditches and the feet of banks hold a little shade the 8 m sky map can't see.
            half Crease(float2 uv)
            {
                float res = max(_HeightmapParams.x, 2);
                float2 huv = (uv * (res - 1) + 0.5) / res;
                float t = 3 / res;
                float c = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv, 0).r;
                float around = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv + float2(t, 0), 0).r
                             + SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv - float2(t, 0), 0).r
                             + SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv + float2(0, t), 0).r
                             + SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv - float2(0, t), 0).r;
                float below = (around * 0.25 - c) * _HeightmapParams.y;   // metres below the neighbours' mean
                return 1 - 0.45 * saturate(below / 1.5);
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
                float composed = c0.g + c0.b + c0.a + c1.r + c1.g + c1.b + c1.a;
                float painted = saturate(composed * 50);
                float land = 1 - c1.g;
                float snowWeight = c0.r * lerp(1, SnowCover(i.positionWS), painted);
                // Under the crowns only (P2-09): SplatTexels thinned the snow over a whole stand (ForestShade, 45% at
                // full canopy); with the crown map the snow between trees comes back and the trees keep their wells.
                float crown = CrownCover(i.positionWS.xz);
                float underCrown = saturate(crown * 1.25);
                if (crown >= 0 && painted > 0.5)
                {
                    float standShade = 0.451 * smoothstep(0.35, 0.8, c0.g);
                    snowWeight = min(land, snowWeight / (1 - standShade)) * (1 - 0.451 * underCrown);
                }
                // The cover-map overlay leaves the snow out whatever the Snow layer says.
                float snowOn = lerp(1, _SnowOn > 0.5 && _Overlay < 0.5 ? 1 : 0, painted);
                snowWeight *= snowOn;
                float keep = land > 0.004 ? saturate(1 - snowWeight / land) : 0;
                float weights[8] = { snowWeight, c0.g * keep, c0.b * keep, c0.a * keep, c1.r * keep, c1.g, c1.b * keep, c1.a * keep };

                if (_Overlay > 0.5)
                {
                    float3 flat = 0;
                    [unroll] for (int k = 0; k < 8; k++) flat += weights[k] * _OverlayColor[k].rgb;
                    Light sun0 = GetMainLight();
                    float lambert = saturate(dot(n, sun0.direction)) * 0.6 + 0.5;
                    return half4(ApplyContours(flat * lambert, i.positionWS.y), 1);
                }

                // Info layers (task 12b): slope angle, exposure or snow depth over the ground, lit so the relief reads.
                UNITY_BRANCH
                if (_MP_InfoView > 0.5)
                {
                    float3 info;
                    if (_MP_InfoView > 3.5) info = SnowDepthColour(SnowDepthMetres(i.positionWS));
                    else
                    {
                        float2 rise = HeightGradient(i.uv);
                        float slopePercent = length(rise) * 100;
                        info = _MP_InfoView < 2.5 ? SlopeAngleColour(slopePercent, i.positionCS.xy) : ExposureColour(-rise, slopePercent);
                    }
                    Light sunI = GetMainLight();
                    float3 shaded = ApplyContours(ShadeInfo(info, n, sunI.direction), i.positionWS.y);
                    return half4(ApplyHaze(MixFog(shaded, i.fog), i.positionWS), 1);
                }

                // Height-based blend: a layer wins where its own relief rises above the others.
                float4 albedos[8];
                float3 normals[8];
                float smooths[8];
                float best = -10;
                float heights[8];
                // Forest-floor edges (beauty pass, item 3): the canopy cut each stand's floor out as a hard dark disc. Noise
                // at two scales breaks the outline, and what the floor gives up at its edge goes to the grass.
                float ragged = ValueNoise(i.positionWS.xz / 6) * 0.6 + ValueNoise(i.positionWS.xz / 1.7) * 0.4;
                // With the crown map (P2-09) the dark floor lies only under the crowns, and between the trees the stand
                // keeps grass, shaded and strewn with needles (needles: that grass's share of the stand).
                float floorWeight = crown >= 0 ? weights[1] * saturate(underCrown + (ragged - 0.5) * 0.3)
                                               : weights[1] * smoothstep(0.15, 0.75, weights[1] + (ragged - 0.5) * 0.7);
                float needles = crown >= 0 ? (weights[1] - floorWeight) / max(weights[2] + weights[1] - floorWeight, 1e-3) : 0;
                weights[2] += weights[1] - floorWeight;
                weights[1] = floorWeight;
                float footprint = length(fwidth(i.positionWS));   // metres per pixel, for the lake's cracks
                float groundPercent = length(n.xz) / max(n.y, 1e-3) * 100;   // the mesh's slope as a grade, for the ground's variation
                [unroll] for (int k = 0; k < 8; k++)
                {
                    albedos[k] = 0;
                    normals[k] = n;
                    smooths[k] = _Smooth[k];
                    heights[k] = -10;
                    [branch] if (weights[k] > 0.004)
                    {
                        if (k == 3) SampleRock(i.positionWS, n, groundPercent, albedos[k], normals[k]);
                        else if (k == 5) SampleLake(i.positionWS, n, cuv, res, weights[k], footprint, albedos[k], normals[k], smooths[k]);
                        else if (k == 2)
                        {
                            SampleGrass(i.positionWS, n, groundPercent, albedos[k], normals[k]);
                            albedos[k].rgb *= lerp(1, float3(0.8, 0.74, 0.62) * lerp(0.85, 1.05, ragged), needles);
                        }
                        else if (k == 1)
                        {
                            // Forest floor a touch lighter and mossier, so stands don't sit in black pools.
                            SampleDetail(1, _Tile[1], i.positionWS, n, albedos[k], normals[k]);
                            albedos[k].rgb *= Macro(i.positionWS) * float3(1.2, 1.28, 1.12);
                            UNITY_BRANCH
                            if (crown >= 0)
                            {
                                // With the crown map (P2-09) the floor is the stand's own ground in shade: needles over
                                // shaded grass, not a brown pad; the darkness under the crowns comes from the light below.
                                float4 grass;
                                float3 grassN;
                                SampleGrass(i.positionWS, n, groundPercent, grass, grassN);
                                albedos[k].rgb = lerp(grass.rgb * float3(0.86, 0.8, 0.68), albedos[k].rgb, 0.4);
                            }
                        }
                        else if (k == 4) { SampleTop(k, i.positionWS, n, albedos[k], normals[k]); albedos[k].rgb *= Macro(i.positionWS); }
                        else if (k == 6)
                        {
                            // Paved road (task 12d): the asphalt, finer and a little darker than parking and built land.
                            SampleDetail(4, _Tile[6], i.positionWS, n, albedos[k], normals[k]);
                            albedos[k].rgb *= 0.82;
                        }
                        else if (k == 7)
                        {
                            // Unpaved road and track (task 12d): gravel, scree stones worked into packed dirt.
                            float4 stones;
                            float3 stonesN;
                            SampleDetail(DirtLayer, _Tile[7], i.positionWS, n, albedos[k], normals[k]);
                            SampleDetail(ScreeLayer, _Tile[7] * 1.5, i.positionWS, n, stones, stonesN);
                            albedos[k] = lerp(albedos[k], stones, 0.5);
                            albedos[k].rgb *= Macro(i.positionWS) * float3(1.08, 1.04, 0.98);
                        }
                        else SampleTop(k, i.positionWS, n, albedos[k], normals[k]);
                        heights[k] = weights[k] + albedos[k].a * 0.6;
                        best = max(best, heights[k]);
                    }
                }
                float3 albedo = 0, normal = 0;
                float total = 0, smooth = 0;
                [unroll] for (int m = 0; m < 8; m++)
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
                // Ambient occlusion (beauty pass, item 2): the sky light dims in valleys (8 m sky map) and creases
                // (1 m heightmap); creases also take a little off the sun.
                half crease = Crease(i.uv);
                half skyLight = TerrainSkyVisibility(i.positionWS) * crease;
                // The crowns hide most of the sky from the ground below them (P2-09), and past the shadow cascades,
                // where trees cast no shadows of their own, some of the sun too.
                float under = saturate(crown);
                skyLight *= 1 - 0.45 * under;
                float farFade = saturate((distance(i.positionWS, _WorldSpaceCameraPos) - _FarShadowParams.x) / max(_FarShadowParams.y - _FarShadowParams.x, 1));
                sun.shadowAttenuation *= 1 - 0.5 * under * farFade;
                float3 lit = albedo * (sun.color * ndl * sun.shadowAttenuation * lerp(1, crease, 0.5) + SampleSH(normal) * skyLight)
                           + sun.color * spec * sun.shadowAttenuation;
                lit = ApplyContours(lit, i.positionWS.y);
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
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float2 uv = 0;
                TerrainInstancing(v.positionOS, v.normalOS, uv);
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
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 n = 0;
                float2 uv = 0;
                TerrainInstancing(v.positionOS, n, uv);
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
