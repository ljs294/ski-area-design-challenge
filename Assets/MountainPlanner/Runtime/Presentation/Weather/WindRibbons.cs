using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06 (WX-53, B9): snow carried on the wind from points, as soft puffs streaming downwind: spindrift off the exposed
    /// ridges in a gale (<see cref="Spindrift"/>) and snow guns' plumes (Lift Lab's gun field now; 4.16 hands its guns to
    /// <see cref="SetEmitters"/>). Each emitter throws its puffs along its own jet, the wind carries them off, and they swell,
    /// sink and thin as they go; lit by the sky and the sun (bright when backlit), fogged like the ground, soft where they meet
    /// it. One <see cref="Graphics.RenderPrimitives"/> draw (WindRibbon.shader): the puffs are worked out in the vertex shader
    /// from a small buffer set once per set of emitters, so nothing is allocated per frame.
    /// </summary>
    public sealed class WindRibbons
    {
        public const string ShaderResource = "MountainPlannerWeather/WindRibbon";
        /// <summary>Puffs per emitter (the shader's PUFFS), and the clock's wrap (s): every life divides it, so the wrap never jumps.</summary>
        public const int Puffs = 24;
        public const float ClockWrap = 600f;

        /// <summary>
        /// One emitter: where it is and how strong (0–1); its jet (m: where the jet alone carries a puff by the end of its life);
        /// its life (s, snapped to divide <see cref="ClockWrap"/>); a puff's size at birth and at death (m); how far it sinks over
        /// its life (m); how much the wind carries it (0–1).
        /// </summary>
        public struct Emitter
        {
            public Vector3 Position, Jet;
            public float Strength, Life, SizeFrom, SizeTo, Sink, Carry;
        }

        static readonly int EmittersId = Shader.PropertyToID("_RibbonEmitters"), AId = Shader.PropertyToID("_RibbonA"),
                            WindId = Shader.PropertyToID("_RibbonWind"), SunId = Shader.PropertyToID("_RibbonSun"),
                            SkyId = Shader.PropertyToID("_RibbonSky"), CloudTexId = Shader.PropertyToID("_WxCloudTex");

        Material _material;
        GraphicsBuffer _buffer;
        Camera _depthFor;
        bool _depth;
        Bounds _bounds;
        int _count;
        float _clock, _shown;
        bool _missing, _haveClouds;

        /// <summary>How many emitters are set.</summary>
        public int Count => _count;
        /// <summary>The overall strength drawing now (0–1), eased so a gale's spindrift builds instead of popping.</summary>
        public float Shown => _shown;

        /// <summary>Shows <paramref name="strength"/> at once, with no build-up (a scene that opens with its guns running).</summary>
        public void Snap(float strength) => _shown = Mathf.Clamp01(strength);

        /// <summary>Sets the emitters (allocates the buffer once per set, never per frame). A count of 0 draws nothing.</summary>
        public void SetEmitters(Emitter[] emitters, int count)
        {
            _count = 0;
            _buffer?.Release();
            _buffer = null;
            if (!SystemInfo.supportsComputeShaders) return;   // no ribbons (task 4.24: a -nographics player has no compute shaders; it still runs the simulation)
            if (emitters == null || count <= 0) return;
            count = Mathf.Min(count, emitters.Length);
            var data = new Vector4[count * 3];
            var b = new Bounds(emitters[0].Position, Vector3.zero);
            for (int i = 0; i < count; i++)
            {
                var e = emitters[i];
                float life = ClockWrap / Mathf.Max(1, Mathf.Round(ClockWrap / Mathf.Max(0.5f, e.Life)));
                data[i * 3] = new Vector4(e.Position.x, e.Position.y, e.Position.z, Mathf.Clamp01(e.Strength));
                data[i * 3 + 1] = new Vector4(e.Jet.x, e.Jet.y, e.Jet.z, life);
                data[i * 3 + 2] = new Vector4(e.SizeFrom, e.SizeTo, e.Sink, Mathf.Clamp01(e.Carry));
                b.Encapsulate(e.Position);
                b.Encapsulate(e.Position + e.Jet);
            }
            // The wind can carry a puff a long way over its life: room for 25 m/s on every side.
            float reach = 0;
            for (int i = 0; i < count; i++) reach = Mathf.Max(reach, emitters[i].Life * 25f + emitters[i].SizeTo);
            b.Expand(reach * 2);
            _bounds = b;
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Length, 16);
            _buffer.SetData(data);
            _count = count;
        }

        /// <summary>
        /// Draws one frame. <paramref name="wind"/>: the wind (m/s, toward, on the grid's x and z); <paramref name="gust"/> 0–1;
        /// <paramref name="strength"/>: the overall strength wanted (0–1, eased here); <paramref name="frozen"/>: a skip is running
        /// (the puffs hang still); <paramref name="sun"/>, <paramref name="sky"/>: the light (linear).
        /// </summary>
        public void Draw(Camera camera, Vector2 wind, float gust, float strength, float realSeconds, bool frozen, Color sun, Color sky)
        {
            _shown = Mathf.MoveTowards(_shown, Mathf.Clamp01(strength), 0.4f * Mathf.Max(0, realSeconds));
            if (_shown <= 0.002f || _count == 0 || camera == null) return;
            if (_material == null)
            {
                if (_missing) return;
                var shader = Resources.Load<Shader>(ShaderResource);
                if (shader == null) { _missing = true; Debug.LogWarning("[Weather] the wind ribbon shader is missing: " + ShaderResource); return; }
                _material = new Material(shader) { name = "Wind ribbons", hideFlags = HideFlags.DontSave };
            }
            if (!_haveClouds)
            {
                // The puffs' wisps come from the cloud field's detail; outside the game (Lift Lab) nothing else has set it.
                var clouds = CloudField.TryMake();
                if (clouds != null) { _material.SetTexture(CloudTexId, clouds); _haveClouds = true; }
            }
            if (!frozen) _clock = (_clock + Mathf.Max(0, realSeconds)) % ClockWrap;
            // Soft where the puffs meet the ground only when the camera makes a depth texture (the game's doesn't: a depth copy
            // would cost more than the plumes); without it they're drawn whole.
            if (_depthFor != camera)
            {
                _depthFor = camera;
                var data = camera.GetUniversalAdditionalCameraData();
                var option = data != null ? data.requiresDepthOption : CameraOverrideOption.UsePipelineSettings;
                _depth = option == CameraOverrideOption.On ||
                         (option == CameraOverrideOption.UsePipelineSettings && UniversalRenderPipeline.asset != null && UniversalRenderPipeline.asset.supportsCameraDepthTexture);
            }
            _material.SetBuffer(EmittersId, _buffer);
            _material.SetVector(AId, new Vector4(_count, _clock, _shown, Mathf.Clamp01(gust)));
            _material.SetVector(WindId, new Vector4(wind.x, _depth ? 1 : 0, wind.y, _haveClouds ? 1 : 0));
            _material.SetVector(SunId, new Vector4(sun.r, sun.g, sun.b, 0));
            _material.SetVector(SkyId, new Vector4(sky.r, sky.g, sky.b, 0));
            var rp = new RenderParams(_material)
            {
                camera = camera,
                worldBounds = _bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, _count * Puffs);
        }

        public void Dispose()
        {
            _buffer?.Release();
            _buffer = null;
            _count = 0;
            if (_material != null) Object.Destroy(_material);
            _material = null;
        }
    }
}
