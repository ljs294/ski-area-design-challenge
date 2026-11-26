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
        _ShellOffset ("Depth offset (the snow shell: -1, so it wins over the ground under thin snow)", Float) = 0
        _ShellDebug ("Developer (-shelldebug): 1 paints this material flat, cyan on the shell variant, magenta on the plain one", Float) = 0
        _ShellForce ("Developer (-shelldebug): metres this material is raised by, whatever the variant", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Geometry-100" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "TerrainCompatible" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // The cover maps read through a fixed sampler of their own, not _Control0's: a variant that uses only _Control1 (the
        // snow shell, task 4.07 PR A3) drops _Control0 and its sampler with it, and then failed to compile in the built game.
        TEXTURE2D(_Control0); SAMPLER(sampler_mp_control_linear_clamp);
        TEXTURE2D(_Control1);
        TEXTURE2D_ARRAY(_Albedo); SAMPLER(sampler_Albedo);
        TEXTURE2D_ARRAY(_Normals);

        CBUFFER_START(UnityPerMaterial)
            float _Overlay;
            float _HeightBlend;
            float _SnowOn;
            float _ShellDebug;   // -shelldebug (task 4.07 PR A3 repair): plain numbers, not a variant, so they tell which variant ran
            float _ShellForce;
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
        float4 _HeightmapParams;     // per tile: x texels per edge, y metres per unit sample, z metres between samples, w the tile's base elevation (BR13)
        float4 _RingBounds;          // xmin, zmin, xmax, zmax in world space: the data's edge
        float _Tile[8];              // metres per texture repeat, per terrain slot (task 12d: eight, with roads)
        float _Smooth[8];
        // The bare ground's variation within a layer (task 12c). The texture arrays hold four more looks after the
        // six splat layers (GroundTextures): alpine meadow, scree, bare dirt and meadow seen from a distance.
        float4 _AlpineBand;          // x, y: elevations (m) where valley grass gives way to alpine meadow; unset: none
        float4 _GrassTint;           // the season's colour on grass and meadow (rgb multiplier; unset reads as none), for the seasons task
        // Task 4.28: the grass follows the land and the date. The ground's moisture (GroundMoisture, a byte per 8 m cell on the
        // snow map's grid, 128 ordinary ground) and the grass's colour through the year (GrassSeason, a strip from January to
        // December), both read through the fixed sampler so every variant keeps them.
        TEXTURE2D(_GroundMoistureMap);
        float4 _GroundMoistureParams;   // x: the map is bound; y: draw the map itself (a check's view)
        TEXTURE2D(_GrassSeasonStrip);
        float4 _GrassSeason;            // x: the day of the year (northern calendar); y: the lowest ground, m; z: days later per metre up; w: on
        static const int MeadowLayer = 6, ScreeLayer = 7, DirtLayer = 8, MeadowFarLayer = 9;
        static const float MeadowTile = 4, ScreeTile = 6, DirtTile = 3, MeadowFarTile = 90;   // metres per repeat
        float4 _OverlayColor[8];
        // Task 10 seams (0.3 §4.6), set by SurfaceStates. The snow-depth map (metres) over the ring: snow covers
        // fully from _SnowDepthParams.x metres deep; .w = 0 (not set yet) is full cover. Task 4.07 PR A2: read through
        // Snow.hlsl's curved filter (SN-29 v2), with the live snow's surface for the conditions layer.
        #include "Snow.hlsl"
        // Each water body's surface state (task 4.09 PR B, SurfaceStates): the body map is body index + 1 per 8 m cell on
        // the snow map's grid (R8, linear, dilated one cell; 0 no body), and the states texture has one texel per body:
        // x = state + 1 (1 open water, 2 ice, 3 snow-covered ice; 0 not set yet, drawn as snow-covered ice), y ice and
        // z snow thickness (m). _WaterBodyParams: x map bound (0/1), y body count, z width, w depth (cells).
        TEXTURE2D(_WaterBodyMap);
        TEXTURE2D(_WaterBodyStates);
        float4 _WaterBodyParams;
        // Task 4.29 PR B: each trail's grass (SN-35), set by SurfaceStates. The owners map is 1 + the layout trail per 8 m cell
        // on the snow map's grid (R16, 0 none); the table holds each trail's grass, % of brush (R8, 256 a row; 255 not known
        // yet, drawn mown). _TrailGrassParams: x bound (0/1; 0 under -plaingrass), y trails, z width, w depth (cells).
        TEXTURE2D(_TrailGrassMap);
        TEXTURE2D(_TrailGrassStates);
        float4 _TrailGrassParams;

        // Snow depth in metres at a point (12 in until the field is set), and how fully it covers the ground (Snow.hlsl).
        float SnowDepthMetres(float3 positionWS) { return SnowDepthAt(positionWS); }
        float SnowCover(float3 positionWS) { return SnowCoverAt(positionWS); }

        // Built trails (task 07, TrailSurfaceMap; task 4.01, TL24: wherever the trails are): 1 m numbers per lattice point
        // in 65 × 65 pages, each a 64 m lattice tile and its east and south neighbours' first column and row (so the four
        // points round any spot sit on one page), found through a page table over the pages' box. Per point: the footprint,
        // the height change (edited − natural, m), 1 on a graded trail, and the edited ground's slope across the trail
        // (0 on the bench, 1 on a 1:1 face), as halves; then the offset across the trail at full float precision (finding
        // #11: half floats and hardware weights can't place 1 in corduroy ridges 100 ft from the centreline). Filtered here
        // by hand. _TrailParams.x = 0 when there are no built trails.
        struct TrailPoint { uint footChange; uint gradedAcross; float offset; };
        StructuredBuffer<TrailPoint> _TrailPoints;
        StructuredBuffer<int> _TrailPages;
        float4 _TrailRect;           // local x and z of lattice point (0, 0), then the page table's first tile column and row
        float4 _TrailParams;         // x on, y and z the page table's width and depth in tiles
        float _CordLightOff;         // Task 4.08 G1, dev only (-lookshots cord=off): 1 leaves out the corduroy's groove light

        // The north-west of the four points round xz and the weights towards the others; false where no trail reaches.
        bool TrailCell(float2 xz, out int at, out float2 f)
        {
            at = 0;
            f = 0;
            UNITY_BRANCH
            if (_TrailParams.x < 0.5) return false;
            float2 p = float2(xz.x - _TrailRect.x, _TrailRect.y - xz.y);   // lattice columns east, rows south
            float2 tile = floor(p / 64);
            int2 cell = int2(tile) - int2(_TrailRect.zw);
            int2 size = int2(_TrailParams.yz);
            if (any(cell < 0) || any(cell >= size)) return false;
            int slot = _TrailPages[cell.y * size.x + cell.x];
            if (slot < 0) return false;
            float2 q = p - tile * 64;
            int2 i = clamp(int2(q), 0, 63);
            f = q - i;
            at = slot * 4225 + i.y * 65 + i.x;
            return true;
        }

        float4 TrailUnpack(TrailPoint v)
        {
            return float4(f16tof32(v.footChange), f16tof32(v.footChange >> 16), f16tof32(v.gradedAcross), f16tof32(v.gradedAcross >> 16));
        }

        // r the footprint, g the height change, b graded, a the slope across.
        float4 TrailAt(float2 xz)
        {
            int at;
            float2 f;
            if (!TrailCell(xz, at, f)) return 0;
            float4 a = TrailUnpack(_TrailPoints[at]), b = TrailUnpack(_TrailPoints[at + 1]);
            float4 c = TrailUnpack(_TrailPoints[at + 65]), d = TrailUnpack(_TrailPoints[at + 66]);
            return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
        }

        // The same, and the offset across the trail (m) from the same four points, at full float precision (finding #11).
        float4 TrailAt(float2 xz, out float offset)
        {
            int at;
            float2 f;
            offset = 0;
            if (!TrailCell(xz, at, f)) return 0;
            TrailPoint pa = _TrailPoints[at], pb = _TrailPoints[at + 1], pc = _TrailPoints[at + 65], pd = _TrailPoints[at + 66];
            offset = lerp(lerp(pa.offset, pb.offset, f.x), lerp(pc.offset, pd.offset, f.x), f.y);
            return lerp(lerp(TrailUnpack(pa), TrailUnpack(pb), f.x), lerp(TrailUnpack(pc), TrailUnpack(pd), f.x), f.y);
        }

        // The trail being painted and the brush (task 08): TrailPaint.hlsl, shared with the snow shell and the cliffs.
        #include "TrailPaint.hlsl"

        // The offset across the trail (m) at full float precision (task 07, finding #11), from the trail pages.
        float TrailOffsetAt(float2 xz)
        {
            int at;
            float2 f;
            if (!TrailCell(xz, at, f)) return 0;
            float a = _TrailPoints[at].offset, b = _TrailPoints[at + 1].offset;
            float c = _TrailPoints[at + 65].offset, d = _TrailPoints[at + 66].offset;
            return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
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
            Offset [_ShellOffset], [_ShellOffset]   // task 4.07 PR A3 round 3: the snow shell a step nearer than the ground
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ WX_AIR WX_VEIL   // task 4.06: the weather's fog while something's in the air; only its snow-line veil while the screen pass draws the air (WeatherView)
            #pragma multi_compile_fragment _ SNOW_SPARKLE   // task 4.08 PR B: the snow's twinkle in direct sun (WeatherView; was 4.06's WX_GLINT)
            #pragma multi_compile_local _ _SNOW_SHELL   // task 4.07 PR A3 round 3: the snow shell over the ground (TerrainTiles)
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
            #include "SnowLighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fog : TEXCOORD3;
                float earthY : TEXCOORD4;      // task 4.07 PR A3: the earth's height (the shell's is above it; the contours follow the earth)
                float2 liftSlope : TEXCOORD5;  // the snow's slope on the shell, 0 on the ground (graded trails' smoothed normal adds it)
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                TerrainInstancing(v.positionOS, v.normalOS, v.uv);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.earthY = o.positionWS.y;
                // Task 4.07 PR A3 round 3: the ground never moves; the snow shell (a second terrain on the same data) stands on
                // the snow's depth, and the snow's slope tilts its normal (a pile shades as a pile).
                #if defined(_SNOW_SHELL)
                o.positionWS.y += SnowLiftAt(o.positionWS);
                o.liftSlope = SnowLiftSlope(o.positionWS);
                #else
                o.liftSlope = 0;
                #endif
                o.positionWS.y += _ShellForce;   // -shelldebug only (0 in play)
                o.positionCS = TransformWorldToHClip(o.positionWS);
                float3 ground = TransformObjectToWorldNormal(v.normalOS);
                float up = max(ground.y, 1e-3);
                o.normalWS = normalize(float3(ground.x / up - o.liftSlope.x, 1, ground.z / up - o.liftSlope.y));
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

            // Task 4.28: the ground's moisture at a point, −1 the driest ridge, 0 ordinary ground (and until the map is bound),
            // +1 a stream bank.
            float GroundWetness(float3 p)
            {
                if (_GroundMoistureParams.x < 0.5) return 0;
                float m = SAMPLE_TEXTURE2D_LOD(_GroundMoistureMap, sampler_mp_linear_clamp, SnowUV(p.xz), 0).r;
                return clamp((m * 255 - 128) / 127, -1, 1);
            }

            // Task 4.28: the grass's colour for the date (an rgb multiplier on the summer grass; 1 until the strip is set). The
            // season runs later up high (spring arrives late, autumn early) and dry ground cures sooner after midsummer, while
            // wet ground stays green longer.
            float3 GrassSeasonTint(float3 p, float wet)
            {
                if (_GrassSeason.w < 0.5) return 1;
                float late = clamp((p.y - _GrassSeason.y) * _GrassSeason.z, 0, 45);
                float day = _GrassSeason.x > 196 ? _GrassSeason.x + late - wet * (wet > 0 ? 10 : 18) : _GrassSeason.x - late;
                return SAMPLE_TEXTURE2D_LOD(_GrassSeasonStrip, sampler_mp_linear_clamp, float2(clamp(day, 0.5, 364.5) / 365, 0.5), 0).rgb * 2;
            }

            // Grass (task 12c): winter-dormant valley grass giving way to sparser alpine meadow up high (its far
            // texture shows rock patches across 90 m), and bare dirt breaking through on steep grass. Task 4.28: lush in the
            // draws and beside water, sparse and straw on the ridges and the steep sun-facing slopes, in ragged patches (two
            // scales of noise tilt the moisture, so no edge follows the map's 8 m cells).
            void SampleGrass(float3 p, float3 n, float slopePercent, float fineBelow, out float4 albedo, out float3 normal)
            {
                SampleDetail(2, _Tile[2], p, n, albedo, normal);
                float wet = GroundWetness(p);
                float patches = ValueNoise(p.xz / 17 + 31) * 0.55 + ValueNoise(p.xz / 63 + 7) * 0.45;
                // The 8 m map, the patches, and the fine lie of the land from the 1 m heights: a ditch or the foot of a bank is
                // wetter than the map can say, a small crest drier.
                bool varied = _GroundMoistureParams.x > 0.5;
                float damp = varied ? wet * 1.2 + (patches - 0.5) * 1.3 + clamp(fineBelow / 0.5, -1, 1) * 0.5 : 0;
                float lush = smoothstep(0.05, 0.65, damp), dry = smoothstep(-0.05, -0.65, damp);
                float alpine = _AlpineBand.y > _AlpineBand.x
                    ? smoothstep(_AlpineBand.x, _AlpineBand.y, p.y + (ValueNoise(p.xz / 150) - 0.5) * 80) : 0;
                float sparse = max(alpine, dry * 0.7);   // dry ground wears the sparse meadow's look without its far rock patches
                [branch] if (sparse > 0.01)
                {
                    float4 meadow;
                    float3 meadowN;
                    SampleDetail(MeadowLayer, MeadowTile, p, n, meadow, meadowN);
                    [branch] if (alpine > 0.01)
                        meadow.rgb = lerp(meadow.rgb, SAMPLE_TEXTURE2D_ARRAY(_Albedo, sampler_Albedo, p.xz / MeadowFarTile, MeadowFarLayer).rgb, 0.5 * alpine / sparse);
                    albedo = lerp(albedo, meadow, sparse);
                    normal = normalize(lerp(normal, meadowN, sparse));
                }
                float bare = smoothstep(60, 95, slopePercent - dry * 20) * smoothstep(0.5, 0.75, ValueNoise(p.xz / 14)) * 0.75;
                [branch] if (bare > 0.01)
                {
                    float4 dirt;
                    float3 dirtN;
                    SampleDetail(DirtLayer, DirtTile, p, n, dirt, dirtN);
                    albedo = lerp(albedo, dirt, bare);
                    normal = normalize(lerp(normal, dirtN, bare));
                }
                albedo.rgb *= Macro(p) * (any(_GrassTint.rgb) ? _GrassTint.rgb : 1);
                // Task 4.28: wet grass a deeper, cooler green; dry grass paler and yellower; a slow drift of hue over the
                // slope; then the season.
                albedo.rgb *= lerp(1, float3(0.62, 0.90, 0.56), lush) * lerp(1, float3(1.24, 1.10, 0.78), dry);
                if (varied)
                {
                    albedo.rgb *= lerp(float3(1.09, 0.98, 0.86), float3(0.91, 1.02, 1.03), ValueNoise(p.xz / 140 + 53));
                    // Clumps a few metres across, as a meadow has: tussocks a little darker and greener, thin turf lighter.
                    float clump = ValueNoise(p.xz / 4.3 + 11) * 0.6 + ValueNoise(p.xz / 1.9 + 3) * 0.4;
                    albedo.rgb *= lerp(float3(0.86, 0.90, 0.84), float3(1.10, 1.08, 1.02), clump);
                }
                albedo.rgb *= GrassSeasonTint(p, damp);
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
                return SAMPLE_TEXTURE2D_LOD(_Control1, sampler_mp_control_linear_clamp, cuv + offsetMetres / max(_TileSize, 1) * ((res - 1) / res), 0).g;
            }

            // A body's state as 0 open water, 1 bare ice, 2 snow-covered ice (unset reads as snow-covered ice).
            float LakeBodyState(uint body)
            {
                float s = LOAD_TEXTURE2D(_WaterBodyStates, uint2(min(body, (uint)max(_WaterBodyParams.y, 1) - 1), 0)).x;
                return s > 0.5 ? s - 1 : 2;
            }

            // Task 4.29 PR B: the trail's grass here, 0 freshly mown to 1 brush: the four 8 m cells round the point, each its
            // trail's height, blended bilinear (a cell with no trail, or a trail not known yet, doesn't vote), so two trails
            // meet in a soft band. Nothing bound, or nothing voting: 0, the mown look every trail had before.
            float TrailGrassAt(float3 p)
            {
                if (_TrailGrassParams.x < 0.5) return 0;
                float2 size = _TrailGrassParams.zw;
                float2 c = (p.xz - _SnowDepthRect.xy) * _SnowDepthRect.zw * size - 0.5;
                float2 f = frac(c);
                int2 c0 = (int2)floor(c);
                float sum = 0, weight = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int2 o = int2(k & 1, k >> 1);
                    int2 q = clamp(c0 + o, int2(0, 0), (int2)size - 1);
                    uint id = (uint)round(LOAD_TEXTURE2D(_TrailGrassMap, q).r * 65535);
                    if (id == 0) continue;
                    id -= 1;
                    float pct = LOAD_TEXTURE2D(_TrailGrassStates, uint2(id & 255, id >> 8)).r * 255;
                    if (pct > 100.5) continue;
                    float w = (o.x ? f.x : 1 - f.x) * (o.y ? f.y : 1 - f.y);
                    sum += pct * w;
                    weight += w;
                }
                return weight > 1e-4 ? saturate(sum / weight / 100) : 0;
            }

            // Task 4.09 PR B: how much of each look (x open water, y bare ice, z snow on ice) this pixel takes, from the
            // bodies of the four 8 m cells around it (loaded, no sampler), bilinear and then sharpened, so two bodies in
            // different states meet in a band a few metres wide. A cell with no body doesn't vote; no map bound (or no
            // body near) is body 0, the streams and iteration 1's one shared state.
            float3 LakeStateWeights(float3 p)
            {
                float3 votes = 0;
                UNITY_BRANCH
                if (_WaterBodyParams.x > 0.5)
                {
                    float2 size = _WaterBodyParams.zw;
                    float2 c = (p.xz - _SnowDepthRect.xy) * _SnowDepthRect.zw * size - 0.5;
                    float2 f = frac(c);
                    int2 c0 = (int2)floor(c);
                    [unroll] for (int k = 0; k < 4; k++)
                    {
                        int2 o = int2(k & 1, k >> 1);
                        int2 q = clamp(c0 + o, int2(0, 0), (int2)size - 1);
                        uint id = (uint)round(LOAD_TEXTURE2D(_WaterBodyMap, q).r * 255);
                        if (id == 0) continue;
                        float s = LakeBodyState(id - 1);
                        float w = (o.x ? f.x : 1 - f.x) * (o.y ? f.y : 1 - f.y);
                        votes += float3(s < 0.5, s >= 0.5 && s < 1.5, s >= 1.5) * w;
                    }
                }
                float total = votes.x + votes.y + votes.z;
                if (total < 1e-4)
                {
                    float s = LakeBodyState(0);
                    return float3(s < 0.5, s >= 0.5 && s < 1.5, s >= 1.5);
                }
                votes /= total;
                float3 sharp = smoothstep(0.35, 0.65, votes);
                float sum = sharp.x + sharp.y + sharp.z;
                if (sum < 1e-3) sharp = float3(votes.x >= votes.y && votes.x >= votes.z, votes.y > votes.x && votes.y >= votes.z, votes.z > votes.x && votes.z > votes.y);
                return sharp / max(sharp.x + sharp.y + sharp.z, 1e-4);
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

                // Each look runs only where this pixel takes some of it (task 4.09 PR B: per body, blended at a seam).
                float3 looks = LakeStateWeights(p);
                if (_SnowOn < 0.5) { looks.y += looks.z; looks.z = 0; }    // with the snow off, snow on ice is bare ice
                albedo = 0; normal = 0; smooth = 0;
                UNITY_BRANCH
                if (looks.z > 0)
                {
                    // (Wind-scoured patches of bare ice were tried and read as grey smudges, so the snow is unbroken.)
                    float rim = (1 - smoothstep(0, 0.35, inside)) * lake;       // about a metre of bare ice at the waterline
                    float3 packed = snow.rgb * float3(1.02, 1.03, 1.05);          // wind-packed snow: brighter, a touch cool
                    float3 colour = lerp(packed, rimIce, rim * 0.7);
                    float bare = rim * 0.7;
                    colour = lerp(colour, rimIce * 0.8, crack * (1 - bare) * 0.6);
                    // Streams: a snow-filled channel, a little cooler toward the middle.
                    colour = lerp(colour, snow.rgb * float3(0.93, 0.96, 1.0), (1 - lake) * saturate(water * 1.5 - 0.3));
                    albedo += float4(colour, 0.12 + 0.18 * (1 - bare)) * looks.z;
                    normal += normalize(lerp(n, snowN, 0.35 * (1 - bare))) * looks.z;   // packed flat: less relief than land snow
                    smooth += lerp(0.5, 0.8, bare) * looks.z;
                }
                UNITY_BRANCH
                if (looks.x > 0)
                {
                    // Open water (a thaw, for the future weather engine): darker than clear ice, glassy, with a
                    // faint ripple, and the sky's horizon reflected at grazing angles. No ice rim or cracks.
                    float3 v = normalize(_WorldSpaceCameraPos - p);
                    float ripple = ValueNoise(p.xz / 3) - 0.5;
                    float3 rippled = normalize(n + float3(ripple, 0, ValueNoise(p.xz / 3 + 11) - 0.5) * 0.06);
                    float fresnel = 0.02 + 0.98 * pow(1 - saturate(dot(rippled, v)), 5);
                    float3 deep = float3(0.035, 0.07, 0.075) * (0.85 + 0.3 * ValueNoise(p.xz / 40));
                    albedo += float4(lerp(deep, _HazeColor * 0.8, fresnel * lake + fresnel * 0.4 * (1 - lake)), 0) * looks.x;
                    normal += rippled * looks.x;
                    smooth += 0.95 * looks.x;
                }
                UNITY_BRANCH
                if (looks.y > 0)
                {
                    // Bare ice: clear and dark, whiter at the shore and along the cracks.
                    float3 colour = lerp(clear, rimIce * 0.75, saturate((1 - inside) * 0.7 + crack * 0.4));
                    albedo += float4(colour, 0.2) * looks.y;
                    normal += normalize(lerp(n, iceN, 0.2)) * looks.y;
                    smooth += 0.6 * looks.y;
                }
                normal = normalize(normal);
            }

            // The ground's rise per metre east (x) and north (y), from the tile's heightmap by central differences two
            // samples either side (4 m across in the core, 8 m in the ring): the slope a skier feels, without the
            // 1 m lidar's ruts and ditches, the same at any distance whatever the mesh's LOD.
            // The slope over two samples either side, divided by the span actually sampled: at a tile's edge the clamped
            // sampler can't reach past it, and dividing by the full 4 samples there halved the slope, a light or dark
            // stripe along every tile edge (the review, 2026-10-20).
            float2 HeightGradient(float2 uv)
            {
                float res = max(_HeightmapParams.x, 2);
                float2 huv = (uv * (res - 1) + 0.5) / res;
                float t = 2 / res, lo = 0.5 / res, hi = 1 - 0.5 / res;
                float2 e1 = float2(min(huv.x + t, hi), huv.y), w1 = float2(max(huv.x - t, lo), huv.y);
                float2 n1 = float2(huv.x, min(huv.y + t, hi)), s1 = float2(huv.x, max(huv.y - t, lo));
                float east = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, e1, 0).r;
                float west = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, w1, 0).r;
                float north = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, n1, 0).r;
                float south = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, s1, 0).r;
                float2 span = max(float2(e1.x - w1.x, n1.y - s1.y) * res, 1e-3) * max(_HeightmapParams.z, 1e-3);
                return float2(east - west, north - south) * _HeightmapParams.y / span;
            }

            // Fine creases (beauty pass, item 2): how far the ground sits below its neighbours 3 samples away (3 m in
            // the core), so gullies, ditches and the feet of banks hold a little shade the 8 m sky map can't see.
            // Metres the ground sits below (+) or above (−) the mean of its neighbours 3 samples away.
            float CreaseBelow(float2 uv)
            {
                float res = max(_HeightmapParams.x, 2);
                float2 huv = (uv * (res - 1) + 0.5) / res;
                float t = 3 / res;
                float c = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv, 0).r;
                float around = SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv + float2(t, 0), 0).r
                             + SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv - float2(t, 0), 0).r
                             + SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv + float2(0, t), 0).r
                             + SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv - float2(0, t), 0).r;
                return (around * 0.25 - c) * _HeightmapParams.y;
            }

            // BR13 (task 15): the elevation the close-up contours follow, the 1 m heightmap's own (the slope's sample),
            // so the mesh's coarser levels of detail never push a 10 ft line sideways. The mesh's where no heightmap is bound.
            float ContourElevation(float2 uv, float meshY)
            {
                UNITY_BRANCH
                if (_MP_Contours < 0.5 || _HeightmapParams.x < 1.5) return meshY;
                float res = _HeightmapParams.x;
                float2 huv = (uv * (res - 1) + 0.5) / res;
                return _HeightmapParams.w + SAMPLE_TEXTURE2D_LOD(_Heightmap, sampler_mp_linear_clamp, huv, 0).r * _HeightmapParams.y;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                ClipToRing(i.positionWS);
                #if defined(_SNOW_SHELL)
                // The shell is only where the snow is deeper than ShellMinMetres (thinner snow stays painted on the ground,
                // so the shell's edge meets white ground), and never over the cover-map overlay.
                if (_ShellDebug > 0.5) return half4(0, 1, 1, 1);   // -shelldebug: the shell variant ran here
                float shellLift = SnowLiftAt(i.positionWS);
                clip(shellLift - ShellMinMetres);
                clip(0.5 - _Overlay);
                #else
                if (_ShellDebug > 0.5) return half4(1, 0, 1, 1);   // -shelldebug: the plain variant ran on a debug material
                #endif
                float3 n = normalize(i.normalWS);

                // Splat weights, sampled texel-centre to texel-centre as Unity lays them out.
                float res = max(_ControlRes, 2);
                float2 cuv = (i.uv * (res - 1) + 0.5) / res;
                float4 c0 = SAMPLE_TEXTURE2D(_Control0, sampler_mp_control_linear_clamp, cuv);
                float4 c1 = SAMPLE_TEXTURE2D(_Control1, sampler_mp_control_linear_clamp, cuv);
                // A built trail (task 07): graded faces are fresh earth, not the rock the cover gives anything this steep;
                // the bench and gentle faces are groomed in winter (full snow) and mown in summer.
                float trailU;
                float4 trail = TrailAt(i.positionWS.xz, trailU);
                // Task 4.08 PR B: the offset across the trail comes with the same four points, and its screen derivatives (the
                // across-trail direction, no extra reads) are taken here, outside every branch (p4-08-findings R17).
                float2 gradU;
                float trailUPerPixel;
                {
                    float2 dx = ddx(i.positionWS.xz), dy = ddy(i.positionWS.xz);
                    float du_x = ddx(trailU), du_y = ddy(trailU);
                    float det = dx.x * dy.y - dx.y * dy.x;
                    gradU = abs(det) > 1e-8 ? float2(du_x * dy.y - du_y * dx.y, du_y * dx.x - du_x * dy.x) / det : float2(1, 0);
                    trailUPerPixel = abs(du_x) + abs(du_y);
                }
                float onTrail = saturate(trail.r);
                float earth = onTrail * smoothstep(0.08, 0.4, abs(trail.g));
                float earthFace = earth * smoothstep(0.35, 0.7, trail.a);
                float groomed = onTrail * (1 - smoothstep(0.55, 0.85, trail.a));
                // The faces' crests and toes (finding #12): the 1 m heightmap stair-steps a crease running across its grid,
                // so on earthwork the shading takes the slope over 4 m instead, which follows the crease, not the steps.
                // The whole trail too (owner's report 2026-10-19, TL22: graded ground looked steppy): the heightmap holds
                // heights in steps of about 4 cm (15 bits of the tile's height range), which rough lidar hides and a smooth
                // bench shows as bands along the contours.
                // Graded trails only (the review, 2026-10-20: on a natural trail the smoothing drew the paint's outline over
                // untouched lidar); channel b says how far a graded trail covers the point.
                float smoothShade = max(earth, saturate(trail.b));
                [branch] if (smoothShade > 0.01)
                {
                    float2 g = HeightGradient(i.uv);
                    float3 wide = normalize(float3(-g.x - i.liftSlope.x, 1, -g.y - i.liftSlope.y));
                    n = normalize(lerp(n, wide, 0.85 * smoothShade));
                }
                {
                    float moved = c0.a * earth;
                    c0.a -= moved;
                    c0.b += moved;
                }
                // c0.r is the snow's weight; the other five channels are the bare ground cover (summing to 1), or 0
                // on a tile whose cover isn't painted yet, which stays all snow. The snow lies over the land share,
                // and the land layers keep what it leaves (as SplatTexels composed it before task 12). Thin snow
                // (the depth map) lets the ground show through.
                float composed = c0.g + c0.b + c0.a + c1.r + c1.g + c1.b + c1.a;
                float painted = saturate(composed * 50);
                float land = 1 - c1.g;
                float cover = SnowCover(i.positionWS);
                float snowWeight = c0.r * lerp(1, cover, painted);
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
                // Task 4.07 PR A2: a groomed trail is white only where there is snow (bare October ground stays bare).
                snowWeight = max(snowWeight, land * snowOn * groomed * cover);
                #if defined(_SNOW_SHELL)
                snowWeight = land;   // the shell is snow wherever it stands (a lake's ice is the ground's; the shell isn't there)
                #endif
                float keep = land > 0.004 ? saturate(1 - snowWeight / land) : 0;
                float weights[8] = { snowWeight, c0.g * keep, c0.b * keep, c0.a * keep, c1.r * keep, c1.g, c1.b * keep, c1.a * keep };

                if (_Overlay > 0.5)
                {
                    float3 flat = 0;
                    [unroll] for (int k = 0; k < 8; k++) flat += weights[k] * _OverlayColor[k].rgb;
                    Light sun0 = GetMainLight();
                    float lambert = saturate(dot(n, sun0.direction)) * 0.6 + 0.5;
                    return half4(ApplyContours(ApplyPaint(flat, i.positionWS.xz) * lambert, i.earthY, ContourElevation(i.uv, i.earthY)), 1);
                }

                // Info layers (task 12b): slope angle, exposure or snow depth over the ground, lit so the relief reads.
                UNITY_BRANCH
                if (_MP_InfoView > 0.5)
                {
                    float3 info;
                    if (_MP_InfoView > 8.5) info = SnowConditionsColour(i.positionWS);   // task 4.07 PR A2: Snow conditions
                    else if (_MP_InfoView > 4.5) info = NetworkReliefColour;   // task P3-10: the Network view
                    else if (_MP_InfoView > 3.5) info = SnowDepthColour(SnowDepthMetres(i.positionWS));
                    else
                    {
                        float2 rise = HeightGradient(i.uv);
                        float slopePercent = length(rise) * 100;
                        info = _MP_InfoView < 2.5 ? SlopeAngleColour(slopePercent, i.positionCS.xy) : ExposureColour(-rise, slopePercent);
                    }
                    Light sunI = GetMainLight();
                    float3 shaded = ShadeInfo(ApplyPaint(info, i.positionWS.xz), n, sunI.direction);
                    // The earth's lines (owner, 2026-11-17: the real ground's contours show through the snow): the shell draws
                    // them at the earth's height under it, so from above they match the bare earth exactly.
                    shaded = ApplyContours(shaded, i.earthY, ContourElevation(i.uv, i.earthY));
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
                float fineBelow = CreaseBelow(i.uv);   // the fine creases: the light's (below) and the grass's (task 4.28)
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
                            SampleGrass(i.positionWS, n, groundPercent, fineBelow, albedos[k], normals[k]);
                            albedos[k].rgb *= lerp(1, float3(0.8, 0.74, 0.62) * lerp(0.85, 1.05, ragged), needles);
                        }
                        else if (k == 1)
                        {
                            // Forest floor a touch lighter and mossier, so stands don't sit in black pools.
                            SampleDetail(1, _Tile[1], i.positionWS, n, albedos[k], normals[k]);
                            albedos[k].rgb *= Macro(i.positionWS) * float3(1.2, 1.28, 1.12);

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
                // Task 4.08 PR B (SN-13 v3): the snow's surface by its type (SnowSurfaceAt: the fine trail cells on the trails,
                // the 8 m snow elsewhere). Here only its tint, a few percent at most; how it takes the light (shine, sparkle,
                // softness) is SnowLighting.hlsl's, with the rest of the light below.
                SnowSurface snowLook = (SnowSurface)0;
                [branch] if (weights[0] > 0.004)
                {
                    // Task 4.08 B1b (R4): the look's types meet along a wandering line (SnowBorderWarp), a few metres wide.
                    float2 wander = SnowBorderWarp(i.positionWS.xz);
                    snowLook = SnowSurfaceAt(i.positionWS + float3(wander.x, 0, wander.y), SnowTypeEdgeMetres);
                    albedos[0].rgb *= snowLook.tint;
                }
                // Built trails (task 07). Groomed snow: the snow's own grain mostly smoothed away, then dense corduroy
                // (owner, SK20 v2): ridges 1¼ in (32 mm) apart along the trail (lines of constant offset).
                // Task 4.25 PR B: the corduroy follows the simulation's grooming (SnowCorduroyAt): full where it says machine
                // groomed, fading as skiers wear it to packed powder and hard pack, none on powder.
                // Task 4.08 G1 (owner 2026-11-25, SK3 v2, SK20 v2): it reads from far away, as groomed runs do in photos.
                // The ridges take the sun as two sloped faces with their own shade (SnowLighting.hlsl): darker toward the sun
                // and in a raking sun, a little brighter with the sun behind the eye. Close up one ridge at a time while a
                // ridge spans 2 pixels or more, then their mean. Every groomer pass is the same, with no line where two meet
                // (owner 2026-11-25, O-G1-1 and O-G1-2: no lines, no tone difference between passes).
                // Only the light changes (cordSun on the sun's, cordSky on the sky's, below), never the snow's colour.
                // The derivatives come first, under the trail geometry's own test (smooth across a 2x2 quad), and only
                // then the simulation's weight, which can change from pixel to pixel inside a trail.
                // Task 4.08 PR B: the offset and its screen derivatives are taken at the top, outside every branch (R17).
                float cordSun = 1, cordSky = 1;
                [branch] if (onTrail > 0.01 && weights[0] > 0.004)
                {
                    float u = trailU;
                    float3 acrossWS = normalize(float3(gradU.x, 0, gradU.y) + float3(1e-5, 0, 0));
                    float uPerPixel = trailUPerPixel;
                    float cord = groomed * SnowCorduroyAt(i.positionWS);   // at the point itself: grooming's edges stay straight
                    if (cord > 0.01)
                    {
                    normals[0] = normalize(lerp(normals[0], n, 0.6 * cord));
                    float3 cordLight = GetMainLight().direction;
                    float3 cordEye = normalize(GetWorldSpaceViewDir(i.positionWS));
                    float ridges = cord, k = CordSlope(1);
                    float far = CorduroySun(n, acrossWS, cordLight, cordEye, k);
                    // Close up, single ridges: a sine across the trail with its own shade (the ridges run with the trail's
                    // lines), handing over to their mean as a ridge shrinks under 2 pixels.
                    float cycles = u / CordSpacingMetres, perCycle = uPerPixel / CordSpacingMetres;
                    float fine = 1 - smoothstep(0.25, 0.5, perCycle);
                    normals[0] = normalize(normals[0] - acrossWS * (cos(cycles * 6.2831853) * 1.5707963 * k * fine * ridges));
                    float near = 1 - CorduroyShade(cycles, perCycle, n, acrossWS, cordLight, k) * (1 - CordBounce);
                    cordSun = lerp(1, lerp(far, near, fine), ridges);
                    cordSky = 1 - CordSkyLoss * ridges;
                    }
                }
                // Summer: the bench is seeded and mown, a fresher, more even green; the faces fresh earth, stonier in cut.
                // Task 4.29 PR B, by the trail's grass (SN-35): mown (to about 15 %) the even, fresher stripe; left to grow it
                // fades into the meadow beside it, and from about 75 % (long to brush) it is that meadow, the same grass as the
                // ground that was never a trail (owner 2026-11-23: an unmown trail turns back into the field around it).
                [branch] if (onTrail > 0.01 && weights[2] > 0.004)
                {
                    float tall = TrailGrassAt(i.positionWS);
                    float mownShare = 1 - smoothstep(0.12, 0.75, tall);
                    float3 mown = albedos[2].rgb * float3(1.02, 1.12, 0.92);
                    float lum = dot(mown, float3(0.3, 0.59, 0.11));
                    mown = lerp(mown, lum * float3(0.86, 1.08, 0.72) * 1.05, 0.35);
                    albedos[2].rgb = lerp(albedos[2].rgb, mown, onTrail * (1 - earthFace) * mownShare);
                    [branch] if (earthFace > 0.01)
                    {
                        float4 dirt, stones;
                        float3 dirtN, stonesN;
                        SampleDetail(DirtLayer, DirtTile, i.positionWS, n, dirt, dirtN);
                        SampleDetail(ScreeLayer, ScreeTile, i.positionWS, n, stones, stonesN);
                        float stony = trail.g < 0 ? 0.45 : 0.15;
                        dirt = lerp(dirt, stones, stony);
                        dirtN = normalize(lerp(dirtN, stonesN, stony));
                        dirt.rgb *= Macro(i.positionWS);
                        albedos[2] = lerp(albedos[2], dirt, earthFace);
                        normals[2] = normalize(lerp(normals[2], dirtN, earthFace));
                    }
                }
                // With the crown map (P2-09) the floor is the stand's own ground in shade: needles over the grass beside
                // them (already sampled, needle-tinted), not a brown pad; the darkness under crowns comes from the light.
                if (crown >= 0 && weights[1] > 0.004)
                    albedos[1].rgb = lerp(weights[2] > 0.004 ? albedos[2].rgb : albedos[1].rgb * float3(0.75, 0.9, 0.7), albedos[1].rgb, 0.4);
                float3 albedo = 0, normal = 0;
                float total = 0, smooth = 0, snowShare = 0;
                [unroll] for (int m = 0; m < 8; m++)
                {
                    float w = max(heights[m] - best + _HeightBlend, 0);
                    if (m == 0) snowShare = w;
                    albedo += albedos[m].rgb * w;
                    normal += normals[m] * w;
                    smooth += smooths[m] * w;
                    total += w;
                }
                albedo /= total;
                snowShare /= total;
                // Task 4.28, a check's view (-moisturemap): the moisture map itself, straw for dry through green to blue for wet.
                if (_GroundMoistureParams.y > 0.5)
                {
                    float wetView = GroundWetness(i.positionWS);
                    albedo = wetView < 0 ? lerp(float3(0.30, 0.50, 0.22), float3(0.85, 0.72, 0.35), -wetView)
                                         : lerp(float3(0.30, 0.50, 0.22), float3(0.10, 0.25, 0.75), wetView);
                }
                albedo = ApplyPaint(albedo, i.positionWS.xz);
                normal = normalize(normal);
                smooth /= total;
                WeatherSurface(normal, smooth, n, i.positionWS);   // task 4.06: flat light under a closed deck, wet ground in the rain

                Light sun = MainLightWithFarShadow(i.positionWS);
                // the grooves shade only the direct sun: under a closing deck it goes, as the sun disc does (snowIn.sunDisc)
                cordSun = CorduroyUnderCloud(cordSun, _WxCloud.x * saturate(_WxParams.x));
                if (_CordLightOff > 0.5) { cordSun = 1; cordSky = 1; }   // dev only: the A/B pictures
                sun.shadowAttenuation *= lerp(1, cordSun, snowShare);   // Task 4.08 G1: the corduroy's own shade (above)
                float3 view = normalize(GetWorldSpaceViewDir(i.positionWS));
                float ndl = saturate(dot(normal, sun.direction));
                float3 h = normalize(sun.direction + view);
                float spec = pow(saturate(dot(normal, h)), exp2(10 * smooth + 1)) * smooth * 0.5;
                // Ambient occlusion (beauty pass, item 2): the sky light dims in valleys (8 m sky map) and creases
                // (1 m heightmap); creases also take a little off the sun.
                half crease = 1 - 0.45 * saturate(fineBelow / 1.5);
                half skyLight = TerrainSkyVisibility(i.positionWS) * crease;
                skyLight *= lerp(1, cordSky, snowShare);   // Task 4.08 G1: the sky the corduroy's grooves don't see
                // The crowns hide most of the sky from the ground below them (P2-09), and past the shadow cascades,
                // where trees cast no shadows of their own, some of the sun too.
                float under = saturate(crown);
                skyLight *= 1 - 0.45 * under;
                float farFade = saturate((distance(i.positionWS, _WorldSpaceCameraPos) - _FarShadowParams.x) / max(_FarShadowParams.y - _FarShadowParams.x, 1));
                sun.shadowAttenuation *= 1 - 0.5 * under * farFade;
                float3 lit = albedo * (sun.color * ndl * sun.shadowAttenuation * lerp(1, crease, 0.5) + SampleSH(normal) * skyLight)
                           + sun.color * spec * sun.shadowAttenuation;
                // Task 4.08 PR B (SN-13 v3): the snow's own light. On the shell (snow 5 cm deep and more) the whole of it:
                // softness, shine and sparkle by type (SnowLighting.hlsl). Painted snow thinner than that keeps the plain light
                // and takes the sparkle alone. Last, so its sums don't keep the layers' registers.
                [branch] if (snowShare > 0.01)
                {
                    float eyeDist = distance(i.positionWS, _WorldSpaceCameraPos);
                    #if defined(_SNOW_SHELL)
                    SnowLitInput snowIn;
                    snowIn.albedo = albedo;
                    snowIn.normalDetail = normal;
                    snowIn.normalSmooth = n;
                    float shineFade = smoothstep(ShellMinMetres, ShellMinMetres + 0.10, shellLift);
                    // The mirror is the ground's wide shape, not the mesh's facets (R12: a tight highlight on lidar facets tore).
                    [branch] if (snowLook.sheen * shineFade > 0.01 && smoothShade <= 0.01)
                    {
                        float2 gWide = HeightGradient(i.uv);
                        snowIn.normalSmooth = normalize(lerp(n, normalize(float3(-gWide.x - i.liftSlope.x, 1, -gWide.y - i.liftSlope.y)), 0.85));
                    }
                    snowIn.view = view;
                    snowIn.sunDir = sun.direction;
                    snowIn.sunLight = sun.color * sun.shadowAttenuation;
                    snowIn.sunCrease = lerp(1, crease, 0.5);
                    snowIn.sunDisc = 1 - smoothstep(0.5, 0.9, _WxCloud.x) * saturate(_WxParams.x);
                    snowIn.ambient = SampleSH(normal) * skyLight;
                    snowIn.skyLight = SampleSH(float3(0, 1, 0));
                    snowIn.skyVis = skyLight;
                    snowIn.positionWS = i.positionWS;
                    snowIn.dist = eyeDist;
                    snowIn.px = footprint;
                    snowIn.shineFade = shineFade;
                    float3 snowShine, snowTwinkle;
                    lit = lerp(lit, SnowLit(snowLook, snowIn, snowShine, snowTwinkle), snowShare);
                    #else
                    lit += sun.color * (SnowSparkle(i.positionWS, normal, h, eyeDist, snowLook.density, snowLook.grain) * snowLook.sparkle * snowShare * sun.shadowAttenuation);
                    #endif
                }
                // The earth's lines, on the ground and on the snow shell alike (owner, 2026-11-17: the real ground's contours show
                // through the snow). earthY is the earth's height under the pixel, so a line is where the bare earth's is.
                lit = ApplyContours(lit, i.earthY, ContourElevation(i.uv, i.earthY));
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
            #pragma multi_compile_local _ _SNOW_SHELL
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
                #if defined(_SNOW_SHELL)
                o.positionWS.y += SnowLiftAt(o.positionWS);   // task 4.07 PR A3: the shell's lift, as the picture
                #endif
                o.positionWS.y += _ShellForce;   // -shelldebug only (0 in play)
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
                #if defined(_SNOW_SHELL)
                if (_ShellDebug < 0.5) clip(SnowLiftAt(i.positionWS) - ShellMinMetres);
                #endif
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
            Offset [_ShellOffset], [_ShellOffset]
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_local _ _SNOW_SHELL
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
                #if defined(_SNOW_SHELL)
                o.positionWS.y += SnowLiftAt(o.positionWS);   // task 4.07 PR A3: the shell's lift, as the picture
                #endif
                o.positionWS.y += _ShellForce;   // -shelldebug only (0 in play)
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                ClipToRing(i.positionWS);
                #if defined(_SNOW_SHELL)
                if (_ShellDebug < 0.5) clip(SnowLiftAt(i.positionWS) - ShellMinMetres);
                #endif
                return 0;
            }
            ENDHLSL
        }
    }
}
