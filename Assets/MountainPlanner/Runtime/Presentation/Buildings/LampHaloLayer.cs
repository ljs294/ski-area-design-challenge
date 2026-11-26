using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Buildings
{
    /// <summary>
    /// Task 4.50 slice 3: a small soft halo round every lit building lamp at night, so a lit hut reads from across the
    /// mountain without bloom (which costs a pass and is off at night). One instanced draw a frame from a pooled buffer
    /// (as GroomerLayer): a camera-facing quad per lamp, a few pixels across at any distance, its brightness fixed on
    /// screen (the post exposure taken back out). It lights nothing.
    /// </summary>
    public sealed class LampHaloLayer : IDisposable
    {
        public const string ShaderResource = "MountainPlannerBuildings/LampHalo";
        public const int Cap = 2048;
        /// <summary>The halo's radius in the world, and its clamp on screen (pixels).</summary>
        public const float HaloMetres = 1.2f, MinPixels = 12f, MaxPixels = 26f;   // 12 px: a lit hut reads from across the mountain (owner 2026-11-25)

        static readonly int HalosId = Shader.PropertyToID("_Halos"), MetresId = Shader.PropertyToID("_HaloMetres"),
                            MinId = Shader.PropertyToID("_MinPixels"), MaxId = Shader.PropertyToID("_MaxPixels"),
                            ScreenId = Shader.PropertyToID("_ScreenPixels"), ExposureId = Shader.PropertyToID("_ExposureEv");

        readonly Material _material;
        readonly Vector4[] _halos = new Vector4[Cap];
        GraphicsBuffer _buffer;
        int _count;

        public int Count => _count;
        public float ExposureEv;

        public LampHaloLayer(Shader shader)
        {
            _material = shader != null ? new Material(shader) { hideFlags = HideFlags.HideAndDontSave } : null;
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cap, 16);
        }

        public void Begin() => _count = 0;

        /// <summary>A halo at <paramref name="at"/>, <paramref name="level"/> 0–1 (BuildingGlow.Level).</summary>
        public void Add(Vector3 at, float level)
        {
            if (_count >= Cap || level <= 0.004f) return;
            _halos[_count++] = new Vector4(at.x, at.y, at.z, Mathf.Clamp01(level));
        }

        /// <summary>Every enabled building's lamps at this light (<see cref="BuildingGlowState.All"/>).</summary>
        public void Gather(float night, float sunElevation)
        {
            var all = BuildingGlowState.All;
            for (int b = 0; b < all.Count; b++)
            {
                var s = all[b];
                if (s == null || s.Lights == null || !s.isActiveAndEnabled) continue;
                var lamps = s.Lights.Lamps;
                for (int i = 0; i < lamps.Length; i++)
                {
                    float level = s.LampLevel(i, night, sunElevation) * night;   // a halo only reads in the dark
                    if (level > 0.004f) Add(s.LampAt(i), level);
                }
            }
        }

        public void Draw(Camera camera)
        {
            if (camera == null || _material == null || _count == 0) return;
            _buffer.SetData(_halos, 0, 0, _count);
            _material.SetBuffer(HalosId, _buffer);
            _material.SetFloat(MetresId, HaloMetres);
            _material.SetFloat(MinId, MinPixels);
            _material.SetFloat(MaxId, MaxPixels);
            _material.SetFloat(ExposureId, ExposureEv);
            _material.SetVector(ScreenId, new Vector4(camera.pixelWidth, camera.pixelHeight, 0, 0));
            var rp = new RenderParams(_material)
            {
                camera = camera,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 100_000f),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, _count);
        }

        public void Dispose()
        {
            _buffer?.Release(); _buffer = null;
            if (_material == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(_material);
            else UnityEngine.Object.DestroyImmediate(_material);   // edit mode (tests)
        }
    }
}
