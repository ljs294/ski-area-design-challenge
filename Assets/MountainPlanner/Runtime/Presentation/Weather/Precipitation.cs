using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06 (B6, P4): what's falling near the camera, as one <see cref="Graphics.RenderPrimitives"/> call
    /// (Precipitation.shader): three nested shells of flakes or streaks, lit by the sky and the sun. How many draw follows the
    /// rate; a skip freezes them (their clock stops); a tool or info view, or Weather effects Minimal, draws none. From up high
    /// the near shells fade out and the far ones thin, so nothing sits on the lens; the curtains carry the overview. 0 B per frame.
    /// </summary>
    public sealed class Precipitation
    {
        public const string ShaderResource = "MountainPlannerWeather/Precipitation";
        /// <summary>The shells' half-sizes (m; each doubled divides <see cref="CamWrap"/>) and each one's flake radius (m).</summary>
        public static readonly Vector3 Shells = new Vector3(10, 30, 120), FlakeSize = new Vector3(0.035f, 0.09f, 0.22f);
        /// <summary>The most angle a flake's radius covers (rad, about 11 px at 1080p), so far flakes are specks, not blobs.</summary>
        public const float MostAngle = 0.012f;
        /// <summary>Particles per shell by tier (High, Medium, Low), and the rate (mm/h of water) at which all of them draw.</summary>
        public const int PerShellHigh = 6000, PerShellMedium = 3000, PerShellLow = 1500;
        /// <summary>The clock's wrap (s; short, so positions stay precise) and the camera's wrap (m).</summary>
        public const float FullRate = 4f, ClockWrap = 600f, CamWrap = 240f;

        static readonly int AId = Shader.PropertyToID("_PrecipA"), WindId = Shader.PropertyToID("_PrecipWind"),
                            ShellsId = Shader.PropertyToID("_PrecipShells"), SizeId = Shader.PropertyToID("_PrecipSize"),
                            FadeId = Shader.PropertyToID("_PrecipFade"), LightId = Shader.PropertyToID("_PrecipLight"),
                            SunId = Shader.PropertyToID("_PrecipSun"), CamId = Shader.PropertyToID("_PrecipCam");

        Material _material;
        float _clock, _share;
        bool _missing;

        /// <summary>Particles per shell (the tier's).</summary>
        public int PerShell { get; set; } = PerShellHigh;
        /// <summary>The share drawing now (0–1), eased so a burst thickens instead of popping.</summary>
        public float Share => _share;

        /// <summary>
        /// Draws one frame. <paramref name="frozen"/>: a skip is running (the flakes hang still). <paramref name="windToward"/>:
        /// grid degrees the wind blows toward. <paramref name="sun"/>, <paramref name="sky"/>: the graded light (linear).
        /// <paramref name="cameraDistance"/>: how far the camera is from its target (the overview fades the near shells).
        /// </summary>
        public void Draw(Camera camera, in WeatherLook.State look, float windToward, bool frozen, bool off, float realSeconds,
                         Color sun, Color sky, float cameraDistance, float rain)
        {
            float want = off ? 0 : Mathf.Clamp01(look.Rate / FullRate);
            if (want > 0 && want < 0.04f) want = 0.04f;   // a flurry still shows
            _share = Mathf.MoveTowards(_share, want, 0.5f * Mathf.Max(0, realSeconds));
            if (_share <= 0.001f || camera == null) return;
            if (_material == null)
            {
                if (_missing) return;
                var shader = Resources.Load<Shader>(ShaderResource);
                if (shader == null) { _missing = true; Debug.LogWarning("[Weather] the precipitation shader is missing: " + ShaderResource); return; }
                _material = new Material(shader) { name = "Precipitation", hideFlags = HideFlags.DontSave };
            }
            if (!frozen) _clock = (_clock + Mathf.Max(0, realSeconds)) % ClockWrap;
            float a = windToward * Mathf.Deg2Rad, speed = look.Wind * 0.447f * 0.6f;   // mph to m/s, the near-ground share
            float nearGround = 1 - Smooth(300, 1500, cameraDistance);
            var c = camera.transform.position;
            _material.SetVector(AId, new Vector4(_share, look.SnowLine, _clock, 1));
            _material.SetVector(WindId, new Vector4(Mathf.Sin(a) * speed, 0, Mathf.Cos(a) * speed, Mathf.Clamp01(look.Gust - 1) + Smooth(15, 35, look.Wind) * 0.5f));
            _material.SetVector(ShellsId, new Vector4(Shells.x, Shells.y, Shells.z, PerShell));
            _material.SetVector(SizeId, new Vector4(FlakeSize.x, FlakeSize.y, FlakeSize.z, MostAngle));
            _material.SetVector(FadeId, new Vector4(1 - Smooth(250, 900, cameraDistance), 1 - 0.65f * Smooth(400, 2000, cameraDistance),
                                                    1 - 0.4f * Smooth(600, 3000, cameraDistance), nearGround));
            _material.SetVector(LightId, new Vector4(sky.r, sky.g, sky.b, look.Snow));
            _material.SetVector(SunId, new Vector4(sun.r, sun.g, sun.b, rain));
            _material.SetVector(CamId, new Vector4(Wrap(c.x), Wrap(c.y), Wrap(c.z), 0));
            var rp = new RenderParams(_material)
            {
                camera = camera,
                worldBounds = new Bounds(c, Vector3.one * (Shells.z * 2.5f)),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, PerShell * 3);
        }

        static float Wrap(float x) => (float)(((double)x % CamWrap + CamWrap) % CamWrap);

        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        public void Dispose()
        {
            if (_material != null) Object.Destroy(_material);
            _material = null;
        }
    }
}
