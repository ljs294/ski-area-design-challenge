using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// A grade over the four time-of-day presets (beauty pass, item 1, owner 2026-10-02): the tonemapper and offsets
    /// to exposure, contrast, saturation, sun strength, sky depth, shadow fill and vignette. The presets keep their
    /// time-of-day character; a style shifts all of them alike. The owner chose **Bluebird** (2026-10-02), the
    /// default; the others stay for comparison captures (`-look name`).
    /// </summary>
    public struct LookStyle
    {
        public string Name;
        public TonemappingMode Tonemapper;
        public float ExposureAdd, ContrastAdd, SaturationAdd;
        /// <summary>Sun strength multiplier, and how much darker and bluer the zenith gets (0 none).</summary>
        public float SunScale, DeeperSky;
        /// <summary>Sky light multiplier (shadow fill), and a cool tint in the shadows (split toning strength added).</summary>
        public float AmbientScale, CoolShadows;
        public float Vignette;

        /// <summary>The owner's choice (2026-10-02).</summary>
        public const int Default = 1;

        public static readonly LookStyle[] All =
        {
            // What the game shipped with before the beauty pass.
            new LookStyle { Name = "Current", Tonemapper = TonemappingMode.Neutral, SunScale = 1, AmbientScale = 1 },
            // Crisp bluebird day: filmic contrast, a strong sun, deep blue sky, cool shadows. Exposure held back a
            // little so sunlit snow keeps its texture.
            new LookStyle
            {
                Name = "Bluebird", Tonemapper = TonemappingMode.ACES, ExposureAdd = 0.2f, ContrastAdd = 6, SaturationAdd = 10,
                SunScale = 1.15f, DeeperSky = 0.35f, AmbientScale = 0.9f, CoolShadows = 0.25f, Vignette = 0.16f,
            },
            // Soft and natural: gentle contrast, lifted shadows, a touch warmer.
            new LookStyle
            {
                Name = "Soft", Tonemapper = TonemappingMode.Neutral, ExposureAdd = 0.1f, ContrastAdd = -4, SaturationAdd = 4,
                SunScale = 1.05f, DeeperSky = 0.1f, AmbientScale = 1.2f, CoolShadows = 0.05f, Vignette = 0.1f,
            },
            // Postcard: vivid colour and punch.
            new LookStyle
            {
                Name = "Postcard", Tonemapper = TonemappingMode.ACES, ExposureAdd = 0.45f, ContrastAdd = 12, SaturationAdd = 22,
                SunScale = 1.25f, DeeperSky = 0.5f, AmbientScale = 0.85f, CoolShadows = 0.35f, Vignette = 0.22f,
            },
        };

        public static int IndexOf(string name)
        {
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Name, name, System.StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
