using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Distant terrain shadows (task 11, owner 2026-10-01). The shadow cascades end at 150 m, so from any
    /// normal view ridges cast nothing at golden hour. When a mountain opens this gathers every tile's
    /// heightmap into one <see cref="MetresPerTexel"/> height map over the ring (GPU, once), then marches each
    /// texel toward the light (FarShadow.compute) into a lit map that FarShadow.hlsl fades in where the cascades end.
    /// Task 4.06: two lit maps (and two shadow ceilings), one for where the light was and one a step ahead of it
    /// (<see cref="StepFor"/>), cross-faded as the light moves between them, so a fast clock slides the shadows instead of
    /// jumping them; a step is built only when the light passes the one ahead. A jump (a scrub, a preset) builds at once
    /// with no fade. Nothing runs while the light holds still, and nothing allocates per frame.
    /// </summary>
    public sealed class FarTerrainShadow : System.IDisposable
    {
        public const float MetresPerTexel = 8;
        /// <summary>The light must turn this far from the last build before the step ahead is built.</summary>
        public const float RebuildDegrees = 0.05f;
        /// <summary>Task 4.06: the step ahead is the larger of <see cref="StepDegrees"/> and <see cref="StepSeconds"/> of the
        /// light's travel, at most <see cref="MostStepDegrees"/>: about four builds a second however fast the clock runs.</summary>
        public const float StepDegrees = 0.25f, StepSeconds = 0.25f, MostStepDegrees = 3f;
        /// <summary>Task 4.06: the air's shadow ceiling fades in between these sun heights (tan elevation: 0.6° and 2°).</summary>
        public const float CeilingFrom = 0.0105f, CeilingFull = 0.035f;
        /// <summary>The penumbra, in tan(elevation) units: about 0.7° either side of the horizon line.</summary>
        public const float Softness = 0.012f;
        /// <summary>Metres the horizon must rise above a texel before it shades it (the coarse map's acne).</summary>
        public const float Bias = 1.5f;
        /// <summary>The map fades in from this share of the shadow distance, and is full at the shadow distance.</summary>
        public const float FadeFrom = 0.6f;

        static readonly int HeightsId = Shader.PropertyToID("_Heights"), HeightsInId = Shader.PropertyToID("_HeightsIn"),
                            TileHeightsId = Shader.PropertyToID("_TileHeights"), TileRectId = Shader.PropertyToID("_TileRect"),
                            TileParamsId = Shader.PropertyToID("_TileParams"), MapRectId = Shader.PropertyToID("_MapRect"),
                            RingRectId = Shader.PropertyToID("_RingRect"), RangeId = Shader.PropertyToID("_Range"),
                            LitId = Shader.PropertyToID("_Lit"), LightId = Shader.PropertyToID("_Light"),
                            ShadowParamsId = Shader.PropertyToID("_ShadowParams"),
                            MapId = Shader.PropertyToID("_FarShadowMap"), RectId = Shader.PropertyToID("_FarShadowRect"),
                            ParamsId = Shader.PropertyToID("_FarShadowParams"),
                            SkyId = Shader.PropertyToID("_Sky"), SkyMapId = Shader.PropertyToID("_TerrainSkyMap"),
                            SkyParamsId = Shader.PropertyToID("_TerrainSkyParams"),
                            CeilInId = Shader.PropertyToID("_CeilIn"), CeilOutId = Shader.PropertyToID("_CeilOut"),
                            CeilParamsId = Shader.PropertyToID("_CeilParams"), CeilScaleId = Shader.PropertyToID("_CeilScale"),
                            CeilingMapId = Shader.PropertyToID("_WxCeilingMap"), CeilingId = Shader.PropertyToID("_WxCeiling"),
                            CeilingRectId = Shader.PropertyToID("_WxCeilingRect"),
                            MapBId = Shader.PropertyToID("_FarShadowMapB"), BlendId = Shader.PropertyToID("_FarShadowBlend"),
                            CeilingMapBId = Shader.PropertyToID("_WxCeilingMapB");

        /// <summary>Task 4.06, the weather's rays: the shadow ceiling (the height under which the air is in the mountain's
        /// shadow) is kept at this many height texels per texel, and air goes from shadowed to lit over this many metres.</summary>
        public const int CeilingScale = 4;
        public const float CeilingSoftness = 60;
        /// <summary>How many times the ceiling has been built.</summary>
        public int CeilingBuilds { get; private set; }
        readonly int _ceilStart, _ceilStep, _ceilWidth, _ceilDepth;
        readonly RenderTexture _ceilScratch;
        RenderTexture _ceilA, _ceilB;

        /// <summary>How strongly sky visibility dims the sky light (beauty pass, item 2).</summary>
        public const float SkyStrength = 1f;

        readonly ComputeShader _compute;
        readonly int _gather, _shadow;
        readonly RenderTexture _heights, _sky;
        RenderTexture _lit, _litB;
        public bool SkyOcclusion { get; private set; } = true;
        readonly Vector4 _mapRect;
        readonly int _width, _depth;
        // The light the first map was built for, the one the second was (a step ahead), and whether the second is built.
        Vector3 _built = Vector3.zero, _ahead = Vector3.zero;
        bool _haveAhead;
        float _blend;
        // The light's speed (degrees per real second), eased, for the step ahead.
        Vector3 _last;
        float _lastAt = -1, _degreesPerSecond;
        public bool Enabled { get; private set; } = true;
        /// <summary>How many times the lit map has been built (for tests and the developer card).</summary>
        public int Builds { get; private set; }

        /// <summary>Gathers the tiles' heights over <paramref name="ring"/> (local x/z). The tiles must have their heights.</summary>
        public FarTerrainShadow(ComputeShader compute, IEnumerable<Terrain> tiles, Rect ring)
        {
            _compute = compute;
            _gather = compute.FindKernel("Gather");
            _shadow = compute.FindKernel("Shadow");
            _width = Mathf.CeilToInt(ring.width / MetresPerTexel);
            _depth = Mathf.CeilToInt(ring.height / MetresPerTexel);
            _mapRect = new Vector4(ring.xMin, ring.yMin, MetresPerTexel, 0);
            _heights = Create(RenderTextureFormat.RFloat, "Far shadow heights");
            _lit = Create(RenderTextureFormat.R16, "Far shadow lit");
            _litB = Create(RenderTextureFormat.R16, "Far shadow lit ahead");
            _ceilStart = compute.FindKernel("CeilingStart");
            _ceilStep = compute.FindKernel("CeilingStep");
            _ceilWidth = Mathf.Max(1, (_width + CeilingScale - 1) / CeilingScale);
            _ceilDepth = Mathf.Max(1, (_depth + CeilingScale - 1) / CeilingScale);
            _ceilA = Create(RenderTextureFormat.RFloat, "Shadow ceiling", _ceilWidth, _ceilDepth);
            _ceilB = Create(RenderTextureFormat.RFloat, "Shadow ceiling ahead", _ceilWidth, _ceilDepth);
            _ceilScratch = Create(RenderTextureFormat.RFloat, "Shadow ceiling scratch", _ceilWidth, _ceilDepth);
            Shader.SetGlobalVector(CeilingRectId, new Vector4(ring.xMin, ring.yMin, 1 / (_ceilWidth * MetresPerTexel * CeilingScale), 1 / (_ceilDepth * MetresPerTexel * CeilingScale)));
            Shader.SetGlobalVector(CeilingId, Vector4.zero);

            compute.SetVector(MapRectId, _mapRect);
            compute.SetVector(RingRectId, new Vector4(ring.xMin, ring.yMin, ring.xMax, ring.yMax));
            compute.SetTexture(_gather, HeightsId, _heights);
            foreach (var tile in tiles)
            {
                var data = tile.terrainData;
                var origin = tile.GetPosition();
                var size = data.size;
                // Texels whose centres fall in this tile (shared edges go to either side: the heights agree).
                int x0 = Mathf.Clamp(Mathf.FloorToInt((origin.x - ring.xMin) / MetresPerTexel), 0, _width);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((origin.z - ring.yMin) / MetresPerTexel), 0, _depth);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((origin.x + size.x - ring.xMin) / MetresPerTexel), 0, _width);
                int y1 = Mathf.Clamp(Mathf.CeilToInt((origin.z + size.z - ring.yMin) / MetresPerTexel), 0, _depth);
                if (x1 <= x0 || y1 <= y0) continue;
                compute.SetTexture(_gather, TileHeightsId, data.heightmapTexture);
                compute.SetVector(TileRectId, new Vector4(origin.x, origin.z, 1 / size.x, 1 / size.z));
                // Unity keeps heights at half range in the heightmap texture (32766 of 65535 is the top).
                compute.SetVector(TileParamsId, new Vector4(size.y * 65535f / 32766f, origin.y, data.heightmapResolution, 0));
                compute.SetInts(RangeId, x0, y0, x1, y1);
                compute.Dispatch(_gather, (x1 - x0 + 7) / 8, (y1 - y0 + 7) / 8, 1);
            }

            compute.SetTexture(_shadow, HeightsInId, _heights);

            // Sky visibility, once: valleys and the feet of slopes see less sky (beauty pass, item 2).
            _sky = Create(RenderTextureFormat.R16, "Terrain sky visibility");
            int skyView = compute.FindKernel("SkyView");
            compute.SetTexture(skyView, HeightsInId, _heights);
            compute.SetTexture(skyView, SkyId, _sky);
            compute.SetVector(MapRectId, _mapRect);
            compute.SetVector(ShadowParamsId, new Vector4(_width, _depth, Softness, Bias));
            compute.Dispatch(skyView, (_width + 7) / 8, (_depth + 7) / 8, 1);
            Shader.SetGlobalTexture(SkyMapId, _sky);
            SetSkyOcclusion(true);
            SetMaps();
            Shader.SetGlobalVector(RectId, new Vector4(ring.xMin, ring.yMin, 1 / (_width * MetresPerTexel), 1 / (_depth * MetresPerTexel)));
            SetEnabled(true);
        }

        RenderTexture Create(RenderTextureFormat format, string name) => Create(format, name, _width, _depth);

        RenderTexture Create(RenderTextureFormat format, string name, int width, int depth)
        {
            var rt = new RenderTexture(width, depth, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name, enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        /// <summary>Sky occlusion on or off (-noao for comparisons).</summary>
        public void SetSkyOcclusion(bool on)
        {
            SkyOcclusion = on;
            Shader.SetGlobalVector(SkyParamsId, new Vector4(SkyStrength, 0, 0, on && _sky != null ? 1 : 0));
        }

        /// <summary>Distant terrain shadows on or off (the developer panel; -nofarshadows for cost measurements).</summary>
        public void SetEnabled(bool on)
        {
            Enabled = on;
            Shader.SetGlobalVector(ParamsId, Params());
        }

        /// <summary>Task 4.06: how dark the far shadows fall (0–1), following the sun's shadow strength under cloud.</summary>
        public float Strength { get; private set; } = 1;

        public void SetStrength(float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (Mathf.Abs(strength - Strength) < 0.002f) return;
            Strength = strength;
            Shader.SetGlobalVector(ParamsId, Params());
        }

        Vector4 Params()
        {
            float distance = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp ? urp.shadowDistance : QualitySettings.shadowDistance;
            return new Vector4(distance * FadeFrom, distance, Strength, Enabled && _lit != null && Strength > 0.01f ? 1 : 0);
        }

        /// <summary>The share of the way from the first map's light to the one ahead that the shadows show (0–1).</summary>
        public float Blend => _blend;

        /// <summary>Whether a map a step ahead of the light is built (for tests).</summary>
        public bool HasAhead => _haveAhead;

        /// <summary>How far ahead the next map is built for a light turning this fast (degrees per real second).</summary>
        public static float StepFor(float degreesPerSecond) => Mathf.Clamp(degreesPerSecond * StepSeconds, StepDegrees, MostStepDegrees);

        /// <summary>The light moved (unit vector toward it, world space): fades between the two maps, building the one ahead
        /// when the light passes it.</summary>
        public void SetLight(Vector3 toLight) => SetLight(toLight, Time.unscaledTime);

        internal void SetLight(Vector3 toLight, float realTime)
        {
            if (_lit == null) return;
            toLight = toLight.normalized;
            Track(toLight, realTime);
            if (Builds == 0) { Anchor(toLight); return; }
            float step = StepFor(_degreesPerSecond);
            if (!_haveAhead)
            {
                float moved = Vector3.Angle(toLight, _built);
                if (moved < RebuildDegrees) return;
                if (moved > step) { Anchor(toLight); return; }   // a jump: no fade
                BuildAhead(toLight - _built, step);
                if (!_haveAhead) { Anchor(toLight); return; }
            }
            float w = Along(toLight, out float offDegrees);
            if (w > 1 && offDegrees < step)
            {
                // Past the step ahead: it becomes the first map, and the next step is built on along the same way.
                var way = _ahead - _built;
                (_lit, _litB) = (_litB, _lit);
                (_ceilA, _ceilB) = (_ceilB, _ceilA);
                _built = _ahead;
                BuildAhead(way, step);
                if (!_haveAhead) { Anchor(toLight); return; }
                w = Along(toLight, out offDegrees);
            }
            if (w < -0.01f || w > 1.01f || offDegrees > step) { Anchor(toLight); return; }   // turned back, or off the way
            SetBlend(Mathf.Clamp01(w));
        }

        void Track(Vector3 toLight, float realTime)
        {
            if (_lastAt >= 0)
            {
                float dt = realTime - _lastAt;
                if (dt > 1e-4f)
                {
                    float rate = Mathf.Min(Vector3.Angle(_last, toLight) / dt, 60f);
                    _degreesPerSecond = Mathf.Lerp(_degreesPerSecond, rate, 1 - Mathf.Exp(-dt / 0.5f));
                }
            }
            _last = toLight;
            _lastAt = realTime;
        }

        // Where the light sits on the way from the first map's light to the one ahead (0 at the first, 1 at the one ahead),
        // and how far off that way it is (degrees).
        float Along(Vector3 toLight, out float offDegrees)
        {
            var way = _ahead - _built;
            float w = Vector3.Dot(toLight - _built, way) / Mathf.Max(way.sqrMagnitude, 1e-12f);
            offDegrees = Vector3.Distance(toLight, _built + way * w) * Mathf.Rad2Deg;
            return w;
        }

        // Builds the first map where the light is now, with nothing ahead (a jump, or the first light).
        void Anchor(Vector3 toLight)
        {
            _built = toLight;
            _haveAhead = false;
            Build(toLight, _lit, _ceilA);
            SetBlend(0);
        }

        // Builds the map a step on from the first map's light, along the way it's moving.
        void BuildAhead(Vector3 way, float stepDegrees)
        {
            var axis = Vector3.Cross(_built, way);
            if (axis.sqrMagnitude < 1e-12f) { _haveAhead = false; return; }
            _ahead = (Quaternion.AngleAxis(stepDegrees, axis.normalized) * _built).normalized;
            _haveAhead = true;
            Build(_ahead, _litB, _ceilB);
        }

        void Build(Vector3 toLight, RenderTexture lit, RenderTexture ceiling)
        {
            var flat = new Vector2(toLight.x, toLight.z);
            float horizontal = flat.magnitude;
            flat = horizontal > 1e-5f ? flat / horizontal : Vector2.up;
            float tanElevation = toLight.y / Mathf.Max(horizontal, 1e-5f);
            _compute.SetVector(MapRectId, _mapRect);
            _compute.SetVector(LightId, new Vector4(flat.x, flat.y, tanElevation, 0));
            _compute.SetVector(ShadowParamsId, new Vector4(_width, _depth, Softness, Bias));
            _compute.SetTexture(_shadow, LitId, lit);
            _compute.Dispatch(_shadow, (_width + 7) / 8, (_depth + 7) / 8, 1);
            Builds++;
            BuildCeiling(Mathf.Max(tanElevation, CeilingFrom), ceiling);
            SetMaps();
            Shader.SetGlobalVector(ParamsId, Params());   // the shadow distance may have changed (-shadows)
        }

        void SetMaps()
        {
            Shader.SetGlobalTexture(MapId, _lit);
            Shader.SetGlobalTexture(MapBId, _haveAhead ? _litB : _lit);
            if (_ceilA != null)
            {
                Shader.SetGlobalTexture(CeilingMapId, _ceilA);
                Shader.SetGlobalTexture(CeilingMapBId, _haveAhead ? _ceilB : _ceilA);
            }
        }

        void SetBlend(float blend)
        {
            _blend = _haveAhead ? blend : 0;
            var light = _haveAhead ? Vector3.Lerp(_built, _ahead, _blend) : _built;
            float tanNow = light.y / Mathf.Max(new Vector2(light.x, light.z).magnitude, 1e-5f);
            Shader.SetGlobalVector(BlendId, new Vector4(_blend, 0, 0, 0));
            if (_ceilA != null && CeilingBuilds > 0)
                Shader.SetGlobalVector(CeilingId, new Vector4(CeilingStrength(tanNow), 1f / CeilingSoftness, _blend, 0));
        }

        /// <summary>The air's shadow ceiling's strength for a sun this high (tan elevation): 0 below 0.6°, full from 2°.</summary>
        public static float CeilingStrength(float tanElevation) => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(CeilingFrom, CeilingFull, tanElevation));

        // The shadow ceiling for the light just set (_Light and _ShadowParams are): doubling steps, a dozen small dispatches,
        // started on whichever texture makes the last step land in the target.
        void BuildCeiling(float tanElevation, RenderTexture target)
        {
            if (target == null) return;
            int gx = (_ceilWidth + 7) / 8, gy = (_ceilDepth + 7) / 8;
            int steps = 0;
            for (int step = 1; step < Mathf.Max(_ceilWidth, _ceilDepth); step *= 2) steps++;
            RenderTexture from = steps % 2 == 0 ? target : _ceilScratch, to = steps % 2 == 0 ? _ceilScratch : target;
            _compute.SetInt(CeilScaleId, CeilingScale);
            _compute.SetVector(CeilParamsId, new Vector4(_ceilWidth, _ceilDepth, 0, 0));
            _compute.SetTexture(_ceilStart, HeightsInId, _heights);
            _compute.SetTexture(_ceilStart, CeilOutId, from);
            _compute.Dispatch(_ceilStart, gx, gy, 1);
            float metres = MetresPerTexel * CeilingScale;
            for (int step = 1; step < Mathf.Max(_ceilWidth, _ceilDepth); step *= 2)
            {
                _compute.SetVector(CeilParamsId, new Vector4(_ceilWidth, _ceilDepth, step, step * metres * tanElevation));
                _compute.SetTexture(_ceilStep, CeilInId, from);
                _compute.SetTexture(_ceilStep, CeilOutId, to);
                _compute.Dispatch(_ceilStep, gx, gy, 1);
                (from, to) = (to, from);
            }
            CeilingBuilds++;
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(CeilingId, Vector4.zero);
            if (_ceilA != null) _ceilA.Release();
            if (_ceilB != null) _ceilB.Release();
            if (_ceilScratch != null) _ceilScratch.Release();
            Shader.SetGlobalVector(BlendId, Vector4.zero);
            Shader.SetGlobalVector(SkyParamsId, Vector4.zero);
            if (_sky != null) _sky.Release();
            if (_heights != null) _heights.Release();
            if (_lit != null) _lit.Release();
            if (_litB != null) _litB.Release();
        }
    }
}
