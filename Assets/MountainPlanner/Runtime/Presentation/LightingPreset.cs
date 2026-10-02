using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// One time-of-day look (0.5 §4, style tile): the sun's (or moon's) colour, the sky, the sky light that
    /// fills the shadows, and the colour grading. The light's direction is not part of the look: it comes from
    /// the clock (<see cref="SceneLighting"/>, task 11), and the look follows the sun's elevation
    /// (<see cref="Look"/>). Colours are sRGB, as the art direction's palette gives them.
    /// </summary>
    public struct LightingPreset
    {
        public string Name;
        public Color SunColor;
        public float SunIntensity, ShadowStrength;
        /// <summary>The sky's zenith and horizon, the backdrop below the horizon, and the size (degrees) of the sun or moon disc.</summary>
        public Color Zenith, Horizon, Below;
        public float DiscDegrees, Stars;
        /// <summary>Sky light (trilight ambient): from above, the horizon and the snow below.</summary>
        public Color AmbientSky, AmbientEquator, AmbientGround;
        /// <summary>Grading (URP bakes these into its colour LUT): exposure (EV), white balance, contrast, saturation, split toning.</summary>
        public float Exposure, Temperature, Tint, Contrast, Saturation;
        public Color SplitShadows, SplitHighlights;
        /// <summary>Bloom on sunlit snow (golden hour only, 0.5 §4).</summary>
        public float Bloom;

        static Color C(string hex) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.magenta;

        /// <summary>The four presets, in the order the developer panel lists them (their times: <see cref="SceneLighting.PresetSecond"/>).</summary>
        public static readonly LightingPreset[] All =
        {
            new LightingPreset
            {
                Name = "Dawn", SunColor = C("#FFB08A"), SunIntensity = 1.05f, ShadowStrength = 0.85f,
                Zenith = C("#5A6FA0"), Horizon = C("#F0C4B8"), Below = C("#B8AFC0"), DiscDegrees = 1.6f, Stars = 0,
                AmbientSky = C("#4F638F"), AmbientEquator = C("#7C7E9E"), AmbientGround = C("#7A84A4"),
                Exposure = 0.3f, Temperature = -5, Tint = 3, Contrast = 10, Saturation = 0,
                SplitShadows = C("#6A7AB8"), SplitHighlights = C("#F2B8A6"), Bloom = 0,
            },
            new LightingPreset
            {
                Name = "Noon", SunColor = C("#FFF6EA"), SunIntensity = 1.35f, ShadowStrength = 0.9f,
                Zenith = C("#4E8FD4"), Horizon = C("#DCEBF7"), Below = C("#B7C6D3"), DiscDegrees = 1.4f, Stars = 0,
                AmbientSky = C("#5F80AE"), AmbientEquator = C("#8497AE"), AmbientGround = C("#7C8796"),
                Exposure = 0.1f, Temperature = -3, Tint = 0, Contrast = 14, Saturation = 6,
                SplitShadows = C("#8098C0"), SplitHighlights = C("#F4F2EC"), Bloom = 0,
            },
            new LightingPreset
            {
                Name = "Golden hour", SunColor = C("#F2B880"), SunIntensity = 1.3f, ShadowStrength = 0.85f,
                Zenith = C("#5A83C0"), Horizon = C("#F6D2A8"), Below = C("#CDB9A6"), DiscDegrees = 1.6f, Stars = 0,
                AmbientSky = C("#5872A6"), AmbientEquator = C("#8A8FAE"), AmbientGround = C("#8A8CA2"),
                Exposure = 0.15f, Temperature = 10, Tint = 2, Contrast = 12, Saturation = 8,
                SplitShadows = C("#5E70B0"), SplitHighlights = C("#F6C48E"), Bloom = 0.35f,
            },
            new LightingPreset
            {
                // Moonlight: the snow stays readable, blue rather than black (0.5 §4).
                Name = "Night", SunColor = C("#9DB6FF"), SunIntensity = 0.32f, ShadowStrength = 0.7f,
                Zenith = C("#05091C"), Horizon = C("#1B2A4C"), Below = C("#141C30"), DiscDegrees = 1.1f, Stars = 1,
                AmbientSky = C("#1C2A52"), AmbientEquator = C("#1A2440"), AmbientGround = C("#26314E"),
                Exposure = 1.1f, Temperature = -12, Tint = 0, Contrast = 4, Saturation = -10,
                SplitShadows = C("#3A4C8C"), SplitHighlights = C("#B8C8F0"), Bloom = 0,
            },
        };

        public static int IndexOf(string name)
        {
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Name.Replace(" hour", ""), name, System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(All[i].Name, name, System.StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public const int Dawn = 0, Noon = 1, GoldenHour = 2, Night = 3;

        /// <summary>
        /// The look for a sun elevation (degrees): night at or below <see cref="NightBelow"/>, the morning's
        /// dawn or the evening's golden hour at <see cref="DawnAt"/> or <see cref="GoldenAt"/>, and noon from
        /// <see cref="NoonFrom"/> up, blended smoothly between. <paramref name="nearest"/> is the preset it is
        /// closest to, for labels.
        /// </summary>
        public static LightingPreset Look(float sunElevation, bool morning, out int nearest)
        {
            int warm = morning ? Dawn : GoldenHour;
            float warmAt = morning ? DawnAt : GoldenAt;
            int a, b;
            float t;
            if (sunElevation <= NightBelow) { a = b = Night; t = 0; }
            else if (sunElevation < warmAt) { a = Night; b = warm; t = (sunElevation - NightBelow) / (warmAt - NightBelow); }
            else if (sunElevation < NoonFrom) { a = warm; b = Noon; t = (sunElevation - warmAt) / (NoonFrom - warmAt); }
            else { a = b = Noon; t = 0; }
            t = t * t * (3 - 2 * t);
            nearest = t < 0.5f ? a : b;
            return Lerp(All[a], All[b], t);
        }

        /// <summary>Sun elevations (degrees) where each look is at full strength.</summary>
        public const float NightBelow = -8, DawnAt = 3, GoldenAt = 6, NoonFrom = 22;

        /// <summary>Blends two presets' looks.</summary>
        public static LightingPreset Lerp(in LightingPreset a, in LightingPreset b, float t)
        {
            return new LightingPreset
            {
                Name = t < 0.5f ? a.Name : b.Name,
                SunColor = Color.Lerp(a.SunColor, b.SunColor, t),
                SunIntensity = Mathf.Lerp(a.SunIntensity, b.SunIntensity, t),
                ShadowStrength = Mathf.Lerp(a.ShadowStrength, b.ShadowStrength, t),
                Zenith = Color.Lerp(a.Zenith, b.Zenith, t),
                Horizon = Color.Lerp(a.Horizon, b.Horizon, t),
                Below = Color.Lerp(a.Below, b.Below, t),
                DiscDegrees = Mathf.Lerp(a.DiscDegrees, b.DiscDegrees, t),
                Stars = Mathf.Lerp(a.Stars, b.Stars, t),
                AmbientSky = Color.Lerp(a.AmbientSky, b.AmbientSky, t),
                AmbientEquator = Color.Lerp(a.AmbientEquator, b.AmbientEquator, t),
                AmbientGround = Color.Lerp(a.AmbientGround, b.AmbientGround, t),
                Exposure = Mathf.Lerp(a.Exposure, b.Exposure, t),
                Temperature = Mathf.Lerp(a.Temperature, b.Temperature, t),
                Tint = Mathf.Lerp(a.Tint, b.Tint, t),
                Contrast = Mathf.Lerp(a.Contrast, b.Contrast, t),
                Saturation = Mathf.Lerp(a.Saturation, b.Saturation, t),
                SplitShadows = Color.Lerp(a.SplitShadows, b.SplitShadows, t),
                SplitHighlights = Color.Lerp(a.SplitHighlights, b.SplitHighlights, t),
                Bloom = Mathf.Lerp(a.Bloom, b.Bloom, t),
            };
        }
    }
}
