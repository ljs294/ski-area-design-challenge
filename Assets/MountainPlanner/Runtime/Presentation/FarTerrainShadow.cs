using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Distant terrain shadows (task 11, owner 2026-10-01). The shadow cascades end at 150 m, so from any
    /// normal view ridges cast nothing at golden hour. When a mountain opens this gathers every tile's
    /// heightmap into one <see cref="MetresPerTexel"/> height map over the ring (GPU, once); whenever the
    /// light moves by more than <see cref="RebuildDegrees"/> it marches each texel toward the light
    /// (FarShadow.compute) into a lit map that FarShadow.hlsl fades in where the cascades end. Nothing runs
    /// while the light holds still, and nothing allocates per frame.
    /// </summary>
    public sealed class FarTerrainShadow : System.IDisposable
    {
        public const float MetresPerTexel = 8;
        /// <summary>The lit map is rebuilt when the light turns by more than this.</summary>
        public const float RebuildDegrees = 0.05f;
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
                            SkyParamsId = Shader.PropertyToID("_TerrainSkyParams");

        /// <summary>How strongly sky visibility dims the sky light (beauty pass, item 2).</summary>
        public const float SkyStrength = 1f;

        readonly ComputeShader _compute;
        readonly int _gather, _shadow;
        readonly RenderTexture _heights, _lit, _sky;
        public bool SkyOcclusion { get; private set; } = true;
        readonly Vector4 _mapRect;
        readonly int _width, _depth;
        Vector3 _built = Vector3.zero;
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
            compute.SetTexture(_shadow, LitId, _lit);

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
            Shader.SetGlobalTexture(MapId, _lit);
            Shader.SetGlobalVector(RectId, new Vector4(ring.xMin, ring.yMin, 1 / (_width * MetresPerTexel), 1 / (_depth * MetresPerTexel)));
            SetEnabled(true);
        }

        RenderTexture Create(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(_width, _depth, 0, format, RenderTextureReadWrite.Linear)
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

        Vector4 Params()
        {
            float distance = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp ? urp.shadowDistance : QualitySettings.shadowDistance;
            return new Vector4(distance * FadeFrom, distance, 1, Enabled && _lit != null ? 1 : 0);
        }

        /// <summary>The light moved (unit vector toward it, world space): rebuilds the lit map if it moved enough.</summary>
        public void SetLight(Vector3 toLight)
        {
            if (_lit == null) return;
            if (Builds > 0 && Vector3.Angle(toLight, _built) < RebuildDegrees) return;
            _built = toLight;
            var flat = new Vector2(toLight.x, toLight.z);
            float horizontal = flat.magnitude;
            flat = horizontal > 1e-5f ? flat / horizontal : Vector2.up;
            float tanElevation = toLight.y / Mathf.Max(horizontal, 1e-5f);
            _compute.SetVector(MapRectId, _mapRect);
            _compute.SetVector(LightId, new Vector4(flat.x, flat.y, tanElevation, 0));
            _compute.SetVector(ShadowParamsId, new Vector4(_width, _depth, Softness, Bias));
            _compute.Dispatch(_shadow, (_width + 7) / 8, (_depth + 7) / 8, 1);
            Builds++;
            Shader.SetGlobalVector(ParamsId, Params());   // the shadow distance may have changed (-shadows)
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(SkyParamsId, Vector4.zero);
            if (_sky != null) _sky.Release();
            if (_heights != null) _heights.Release();
            if (_lit != null) _lit.Release();
        }
    }
}
