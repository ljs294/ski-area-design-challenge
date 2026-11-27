using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Buildings
{
    /// <summary>
    /// Task 4.50 slice 3: the glare of every lit building lamp at night. Round 3 (owner's pick, night bloom): bloom makes the
    /// near glow and this marks only lamps too far off for it (<see cref="NearFadeMetres"/>). Round 2 (owner 2026-11-26: the first halo, a 1.2 m amber orb seen from every side, "doesn't look natural
    /// at all"): a glare is about the size of its lens, in the lamp's own colour, and shows only where the lens does: a
    /// shaded lamp seen from behind or above its aim has none (a cage sconce shines all round), and its own fixture and
    /// eave hide it (it is pulled only a few centimetres toward the eye). From afar it is a small bright point. One
    /// instanced draw a frame from a pooled buffer (as GroomerLayer), its brightness fixed on screen (the post exposure
    /// taken back out); each lamp's colour comes cached from <see cref="BuildingGlowState.LampColour"/>. It lights nothing:
    /// LampLightMap does.
    /// </summary>
    public sealed class LampHaloLayer : IDisposable
    {
        public const string ShaderResource = "MountainPlannerBuildings/LampHalo";
        public const int Cap = 2048;
        /// <summary>The glare's width in the world, and its clamp on screen (pixels across).</summary>
        public const float HaloMetres = 0.35f, MinPixels = 4f, MaxPixels = 14f;
        const int Stride = 3;   // float4s a glare: the lens and its level; its aim and 1 if it shines all round; its colour

        static readonly int HalosId = Shader.PropertyToID("_Halos"), MetresId = Shader.PropertyToID("_HaloMetres"),
                            MinId = Shader.PropertyToID("_MinPixels"), MaxId = Shader.PropertyToID("_MaxPixels"),
                            ScreenId = Shader.PropertyToID("_ScreenPixels"), ExposureId = Shader.PropertyToID("_ExposureEv"),
                            NearId = Shader.PropertyToID("_NearFade");

        readonly Material _material;
        readonly Vector4[] _halos = new Vector4[Cap * Stride];
        GraphicsBuffer _buffer;
        int _count;

        public int Count => _count;
        public float ExposureEv;
        /// <summary>Glare only from this far out, fading in over half as far again (0: near and far alike; round 3's
        /// bloom mode, where bloom makes the near glow and a dot only marks lamps too far off for it).</summary>
        public float NearFadeMetres;

        public LampHaloLayer(Shader shader)
        {
            _material = shader != null ? new Material(shader) { hideFlags = HideFlags.HideAndDontSave } : null;
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cap * Stride, 16);
        }

        public void Begin() => _count = 0;

        /// <summary>
        /// A glare at the lens <paramref name="at"/>, <paramref name="level"/> 0–1 (BuildingGlow.Level), pointing along
        /// <paramref name="aim"/> (<paramref name="allRound"/>: seen from every side), in the lamp's <paramref name="colour"/>.
        /// </summary>
        public void Add(Vector3 at, float level, Vector3 aim, bool allRound, Color colour)
        {
            if (_count >= Cap || level <= 0.004f) return;
            int k = _count++ * Stride;
            _halos[k] = new Vector4(at.x, at.y, at.z, Mathf.Clamp01(level));
            _halos[k + 1] = new Vector4(aim.x, aim.y, aim.z, allRound ? 1 : 0);
            _halos[k + 2] = new Vector4(colour.r, colour.g, colour.b, 0);
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
                    if (level > 0.004f) Add(s.LampAt(i), level, s.LampAim(i), lamps[i].ConeDegrees >= 359f, s.LampColour(i));
                }
            }
        }

        public void Draw(Camera camera)
        {
            if (camera == null || _material == null || _count == 0) return;
            _buffer.SetData(_halos, 0, 0, _count * Stride);
            _material.SetBuffer(HalosId, _buffer);
            _material.SetFloat(MetresId, HaloMetres);
            _material.SetFloat(MinId, MinPixels);
            _material.SetFloat(MaxId, MaxPixels);
            _material.SetFloat(ExposureId, ExposureEv);
            _material.SetFloat(NearId, NearFadeMetres);
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
