using UnityEngine;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06 (B8): the weather's grade over the time-of-day look, as deltas on a <see cref="LightingPreset"/> rather than a
    /// scaled sun, so a storm reads as a storm and not a greyer noon. Each weight is 0–1 and eased by <see cref="WeatherView"/>;
    /// all zero leaves the preset untouched (the benchmark's clear day). Every arc gets one look of its own the player can tell
    /// in half a second: a dark heavy storm, a darker colder big storm, a dirty warm-grey rain, warm sun patches when it clears,
    /// a deep blue bluebird with a warm sun, a hard cyan cold snap.
    /// </summary>
    public struct WeatherGrade
    {
        /// <summary>Overcast (from the cover) and storm (what's falling) follow the numbers; the rest are the arcs' own looks.</summary>
        public float Overcast, Storm, BigStorm, Rain, Building, Clearing, Bluebird, Thaw, ColdSnap;

        public bool IsClear => Overcast <= 0 && Storm <= 0 && BigStorm <= 0 && Rain <= 0 && Building <= 0 && Clearing <= 0 &&
                               Bluebird <= 0 && Thaw <= 0 && ColdSnap <= 0;

        public bool Near(in WeatherGrade o, float e = 0.002f) =>
            Mathf.Abs(Overcast - o.Overcast) < e && Mathf.Abs(Storm - o.Storm) < e && Mathf.Abs(BigStorm - o.BigStorm) < e &&
            Mathf.Abs(Rain - o.Rain) < e && Mathf.Abs(Building - o.Building) < e && Mathf.Abs(Clearing - o.Clearing) < e &&
            Mathf.Abs(Bluebird - o.Bluebird) < e && Mathf.Abs(Thaw - o.Thaw) < e && Mathf.Abs(ColdSnap - o.ColdSnap) < e;

        static readonly Color StormShadows = new Color(0.55f, 0.57f, 0.62f), RainGrey = new Color(0.66f, 0.63f, 0.58f),
                              RainShadows = new Color(0.58f, 0.55f, 0.52f), WarmSun = new Color(1f, 0.86f, 0.68f),
                              BluebirdSun = new Color(1f, 0.94f, 0.84f), BlueShadows = new Color(0.36f, 0.50f, 0.86f),
                              CyanShadows = new Color(0.30f, 0.66f, 0.86f), ColdHorizon = new Color(0.80f, 0.90f, 1f);

        /// <summary>
        /// The preset under this weather. <paramref name="night"/> is 0 by day and 1 at night, eased over dusk, so nothing
        /// jumps when the sun sets in a storm: by day a storm darkens the scene, at night the snow holds the light.
        /// </summary>
        public LightingPreset Apply(LightingPreset p, float night)
        {
            if (IsClear) return p;
            night = Mathf.Clamp01(night);
            float day = 1 - night;
            float o = Overcast, s = Storm, big = BigStorm, r = Rain;

            // Overcast: the sun goes behind the deck, shadows soften to nothing, the sky light evens out; the sky becomes a
            // ceiling darker than its horizon, and the fog (WeatherView) is never lighter than that horizon.
            p.SunIntensity *= 1 - 0.75f * o;
            p.ShadowStrength *= 1 - Mathf.Clamp01(o * 1.15f);
            var even = (p.AmbientSky + p.AmbientEquator + p.AmbientGround) / 3f;
            float ambient = 1 + 0.25f * o * (1 - s) - 0.2f * s * day;
            p.AmbientSky = Color.Lerp(p.AmbientSky, even, 0.6f * o) * ambient;
            p.AmbientEquator = Color.Lerp(p.AmbientEquator, even, 0.6f * o) * ambient;
            p.AmbientGround = Color.Lerp(p.AmbientGround, even, 0.6f * o) * ambient;
            var grey = Grey(p.Horizon) * Mathf.Lerp(1f, 0.55f, night);
            p.Zenith = Color.Lerp(p.Zenith, grey * (0.62f - 0.1f * s), 0.85f * o);
            p.Horizon = Color.Lerp(p.Horizon, grey * (0.95f - 0.12f * s), 0.8f * o);
            p.Below = Color.Lerp(p.Below, grey * 0.85f, 0.8f * o);
            p.Contrast -= 4 * o;
            p.Saturation -= 14 * o;
            p.Temperature -= 2 * o;
            p.Bloom *= 1 - o;
            p.Stars *= 1 - Mathf.Clamp01(o * 1.5f);
            p.SplitShadows = Color.Lerp(p.SplitShadows, StormShadows, 0.5f * o);

            // A storm: dark and heavy by day (contrast kept, so the forest and the ridges still read); at night the snow holds
            // the light. A big storm is darker and colder still.
            p.Exposure += Mathf.Lerp(-0.65f, 0.3f, night) * s - 0.3f * big * day;
            p.Saturation -= 6 * s + 8 * big;
            p.Contrast += 2 * s;
            p.Temperature -= 2 * big;
            p.Zenith *= 1 - 0.15f * big;
            p.Horizon *= 1 - 0.1f * big;
            if (night > 0)
            {
                var glow = new Color(0.10f, 0.11f, 0.14f) * (s * night);
                p.AmbientSky += glow; p.AmbientEquator += glow; p.AmbientGround += glow;
            }

            // Rain: the darkest, dirtiest look. Warm grey, flat, the colour drained out of the trees.
            p.Exposure -= 0.25f * r * day;
            p.Temperature += 5 * r;
            p.Saturation -= 10 * r;
            p.Horizon = Color.Lerp(p.Horizon, RainGrey * Luma(p.Horizon) / Luma(RainGrey) * 0.92f, 0.6f * r);
            p.Zenith = Color.Lerp(p.Zenith, RainGrey * Luma(p.Zenith) / Luma(RainGrey) * 0.85f, 0.6f * r);
            p.SplitShadows = Color.Lerp(p.SplitShadows, RainShadows, 0.6f * r);

            // Building: drama under a lowering sky, cooler shadows. Clearing: warm sun breaking through, the patches popping.
            float g = Building, c = Clearing;
            p.Contrast += 6 * g + 8 * c;
            p.SplitShadows = Color.Lerp(p.SplitShadows, BlueShadows, 0.35f * g);
            p.SunColor = Color.Lerp(p.SunColor, WarmSun, 0.45f * c * day);
            p.SunIntensity *= 1 + 0.25f * c;
            p.Saturation += 6 * c;
            // Bloom only where the hour already blooms (golden hour): at 0 URP skips its bloom pass, and the weather never turns it on.
            if (p.Bloom > 0) p.Bloom += 0.1f * c * day;

            // Bluebird: a deep saturated zenith over a clear blue horizon (the overview sees mostly the horizon and the backdrop
            // below it, so both go bluer), a warm sun against blue shadows, the snow held just under pure white so its form reads.
            float b = Bluebird;
            p.Zenith = Color.Lerp(p.Zenith, p.Zenith * new Color(0.6f, 0.74f, 1f), b);
            p.Horizon = Color.Lerp(p.Horizon, p.Horizon * new Color(0.84f, 0.92f, 1.03f), b);
            p.Below = Color.Lerp(p.Below, p.Below * new Color(0.82f, 0.9f, 1.02f), b);
            p.SunColor = Color.Lerp(p.SunColor, BluebirdSun, 0.6f * b * day);
            p.SunIntensity *= 1 + 0.08f * b;
            p.ShadowStrength = Mathf.Lerp(p.ShadowStrength, Mathf.Max(p.ShadowStrength, 0.95f), b);
            p.SplitShadows = Color.Lerp(p.SplitShadows, BlueShadows, 0.5f * b);
            p.Exposure -= 0.12f * b * day;
            p.Saturation += 12 * b;
            p.Contrast += 8 * b;
            if (p.Bloom > 0) p.Bloom += 0.15f * b * day;   // the glint is the bluebird's sparkle at noon (WeatherView)

            // Thaw: warm and flat. Cold snap: a hard blue sky, cyan shadows, crisp contrast.
            float t = Thaw, cs = ColdSnap;
            p.Temperature += 8 * t - 12 * cs;
            p.Saturation += -8 * t + 4 * cs;
            p.Horizon = Color.Lerp(p.Horizon, RainGrey * Luma(p.Horizon) / Luma(RainGrey), 0.35f * t);
            p.Zenith = Color.Lerp(p.Zenith, p.Zenith * new Color(0.7f, 0.82f, 1f), 0.8f * cs);
            p.Horizon = Color.Lerp(p.Horizon, ColdHorizon * Luma(p.Horizon) / Luma(ColdHorizon), 0.5f * cs);
            p.SplitShadows = Color.Lerp(p.SplitShadows, CyanShadows, 0.6f * cs);
            p.Tint += 3 * cs;
            p.Contrast += 10 * cs;
            return p;
        }

        static float Luma(Color c) => Mathf.Max(0.001f, 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b);
        static Color Grey(Color c) { float l = Luma(c); return new Color(l * 0.99f, l, l * 1.015f, 1); }
    }
}
