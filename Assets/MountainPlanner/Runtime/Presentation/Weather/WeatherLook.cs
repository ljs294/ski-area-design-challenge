using System;
using MountainPlanner.Simulation;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06 (WX-50, WX-53): what the weather looks like, from the snapshot alone. Pure C#, so EditMode tests can step it.
    /// It reads the hour at zref, the base/mid/summit samples, the event bits and the cell under the camera's target, and gives
    /// the sky's cover, the cloud deck's base, the valley fog's top, the visibility (the true one, and the one the view uses,
    /// which never blinds the overview), the snow line, what's falling, the wind and an <see cref="Arc"/> that names the look.
    /// All the view's numbers live in <see cref="Tune"/>: no Pacing, snapshot or sidecar number changes for the look.
    /// </summary>
    public static class WeatherLook
    {
        /// <summary>The look's arc: one name per kind of weather the grades, the front and 4.17's banner agree on.</summary>
        public enum Arc : byte { Fair, Building, Storm, BigStorm, Clearing, Bluebird, Rain, Thaw, ColdSnap, Inversion }

        /// <summary>The view's constants, in one place.</summary>
        public static class Tune
        {
            public const float FeetToMetres = 0.3048f;
            /// <summary>Visibility on a clear day, under overcast with nothing falling, and the least anything falling gives (m).</summary>
            public const float ClearVisibility = 60000, OvercastVisibility = 22000, LeastVisibility = 120;
            /// <summary>With the rays on, the clearest the air is while the sun is low (m): a light haze for the ridges' shadows.</summary>
            public const float LowSunVisibility = 32000;
            /// <summary>Snow: the visibility at 1 mm/h of water and its exponent; rain the same (rain blinds far less).</summary>
            public const float SnowVisibilityAt1Mm = 1100, SnowExponent = 0.8f, RainVisibilityAt1Mm = 9000, RainExponent = 0.6f;
            /// <summary>Inside valley fog (m).</summary>
            public const float FogVisibility = 180;
            /// <summary>The overview's veil: from this camera distance up (m) the haze at the target's height is never more than
            /// <see cref="VeilMost"/> over the camera's distance (it thins upward, so the view down through it is lighter still).</summary>
            public const float VeilFrom = 300, VeilFull = 1500, VeilMost = 0.45f;
            /// <summary>A slab (the deck, the curtains) crossed straight through from the overview is at most this fogged where it's
            /// densest; its holes and thin parts much less, so it reads as a layer, never a lid.</summary>
            public const float SlabMost = 0.6f;
            /// <summary>Valley fog: its least depth over the valley floor, and the depth per °F of cold pool (m).</summary>
            public const float FogLeastDepth = 50, FogDepthPerDegree = 18, FogMostDepth = 420;
            /// <summary>The deck: the cover it starts at, and its thickness (m).</summary>
            public const float DeckFromCover = 0.35f, DeckThickness = 1400;
            /// <summary>The share of a face hour the look takes to reach the hour (all of it, evenly: task 4.06, so the sky never
            /// eases then holds), and the fastest real-time blend (per second).</summary>
            public const float EaseShare = 1f, MostBlendPerSecond = 0.35f;
            /// <summary>Task 4.06: a pan to other weather cells within the hour eases over about this many real seconds.</summary>
            public const float PanEaseSeconds = 1f;
            /// <summary>Snow share (‰) that counts as snow for the snow line.</summary>
            public const int SnowLineShare = 500;
            /// <summary>The cold pool (0.1 °F) that reads as an inversion when the event isn't set.</summary>
            public const int InversionPool = 40;
        }

        /// <summary>One look: every number the view needs, all lerpable.</summary>
        public struct State
        {
            /// <summary>Cloud cover 0–1 and daytime clearness 0–1 (1 at night: the night's own sky).</summary>
            public float Cover, Clearness;
            /// <summary>The cloud deck's base (m above sea level) and its density 0–1 (0: no deck).</summary>
            public float DeckBase, Deck;
            /// <summary>Valley fog's top (m above sea level) and its density 0–1 (0: none).</summary>
            public float FogTop, Fog;
            /// <summary>The visibility where the camera's target is (m), before the overview's veil.</summary>
            public float Visibility;
            /// <summary>The snow line (m above sea level): snow above, rain below.</summary>
            public float SnowLine;
            /// <summary>Water falling at the target (mm/h) and its snow share 0–1.</summary>
            public float Rate, Snow;
            /// <summary>The wind at the target (mph), its gust factor and where it comes from (degrees, true).</summary>
            public float Wind, Gust, Direction;
            public bool Night;

            public static State Lerp(in State a, in State b, float t)
            {
                t = Math.Clamp(t, 0f, 1f);
                return new State
                {
                    Cover = L(a.Cover, b.Cover, t), Clearness = L(a.Clearness, b.Clearness, t),
                    DeckBase = L(a.DeckBase, b.DeckBase, t), Deck = L(a.Deck, b.Deck, t),
                    FogTop = L(a.FogTop, b.FogTop, t), Fog = L(a.Fog, b.Fog, t),
                    // Visibility eases in log space, so a storm thickens evenly instead of all at the end.
                    Visibility = (float)Math.Exp(L((float)Math.Log(a.Visibility), (float)Math.Log(b.Visibility), t)),
                    SnowLine = L(a.SnowLine, b.SnowLine, t), Rate = L(a.Rate, b.Rate, t), Snow = L(a.Snow, b.Snow, t),
                    Wind = L(a.Wind, b.Wind, t), Gust = L(a.Gust, b.Gust, t),
                    Direction = LerpDegrees(a.Direction, b.Direction, t),
                    Night = t < 0.5f ? a.Night : b.Night,
                };
            }
        }

        /// <summary>The look's inputs: the snapshot's weather block and the cell under the target (pass <c>Known = false</c> for none).</summary>
        public static State From(in Domain.Weather.WeatherHour h, in WeatherSample bse, in WeatherSample mid, in WeatherSample top,
                                 in WeatherCell cell, int zrefFeet)
        {
            const float ft = Tune.FeetToMetres;
            var s = new State
            {
                Cover = h.Cloud / 1000f,
                Clearness = h.Night ? 1f : h.Clearness / 1000f,
                Night = h.Night,
                SnowLine = SnowLineFeet(bse, mid, top) * ft,
            };
            s.DeckBase = (zrefFeet + h.CloudBaseFeet) * ft;
            s.Deck = Smooth(Tune.DeckFromCover, 0.9f, s.Cover);

            // What's falling where the camera looks: the cell's own when it's known, else mid's.
            int liquid = cell.Known ? cell.Liquid : mid.Liquid, snow = cell.Known ? cell.SnowPermille : mid.SnowPermille;
            s.Rate = liquid * 0.254f;
            s.Snow = snow / 1000f;
            s.Wind = (cell.Known ? cell.Wind : mid.Wind) / 10f;
            s.Gust = h.GustFactor / 1000f;
            s.Direction = cell.Known ? cell.Direction : h.Direction;

            // Valley fog sits on the valley floor under the target, as deep as the pool is strong.
            float floor = cell.Known ? (cell.Feet - cell.HvfFeet) * ft : bse.Feet * ft;
            s.FogTop = floor + Math.Min(Tune.FogMostDepth, Tune.FogLeastDepth + h.Pool / 10f * Tune.FogDepthPerDegree);
            s.Fog = h.Fog ? 1f : 0f;

            s.Visibility = Visibility(s.Cover, s.Rate, s.Snow);
            return s;
        }

        /// <summary>
        /// A pinned look for shots and the benchmark (<c>-wxlook</c>; never the game's weather), over a mountain from
        /// <paramref name="baseM"/> to <paramref name="summitM"/> (m above sea level). Bluebird's wind gives the forest the
        /// breeze the benchmark's baselines were measured in.
        /// </summary>
        public static bool Preset(string name, float baseM, float summitM, out State s, out Arc arc)
        {
            float mid = (baseM + summitM) / 2, span = Math.Max(300, summitM - baseM);
            s = new State { Clearness = 1, DeckBase = summitM + 600, SnowLine = baseM - 300, Snow = 1, Gust = 1.3f, Direction = 250, FogTop = baseM - 1000 };
            switch (name?.ToLowerInvariant())
            {
                case "building": arc = Arc.Building; s.Cover = 0.72f; s.Clearness = 0.55f; s.DeckBase = summitM + 250; s.Wind = 16; break;
                case "storm": arc = Arc.Storm; s.Cover = 1; s.Clearness = 0.2f; s.DeckBase = mid + span * 0.2f; s.Rate = 2.5f; s.Wind = 24; break;
                case "bigstorm": arc = Arc.BigStorm; s.Cover = 1; s.Clearness = 0.12f; s.DeckBase = baseM + span * 0.35f; s.Rate = 6; s.Wind = 34; break;
                case "clearing": arc = Arc.Clearing; s.Cover = 0.58f; s.Clearness = 0.7f; s.DeckBase = summitM - span * 0.1f; s.Wind = 12;
                    s.Fog = 0.15f; s.FogTop = baseM + 90; break;
                case "bluebird": arc = Arc.Bluebird; s.Cover = 0.04f; s.Wind = (0.35f - 0.02f) / 0.0333f; break;
                case "rain": arc = Arc.Rain; s.Cover = 0.96f; s.Clearness = 0.25f; s.DeckBase = mid; s.Rate = 3; s.Snow = 0; s.SnowLine = summitM + 300; s.Wind = 14; break;
                case "thaw": arc = Arc.Thaw; s.Cover = 0.6f; s.Clearness = 0.6f; s.DeckBase = summitM + 400; s.SnowLine = summitM + 300; s.Wind = 8; break;
                case "coldsnap": arc = Arc.ColdSnap; s.Cover = 0.06f; s.Wind = 5; s.FogTop = baseM + 90; break;
                case "inversion": arc = Arc.Inversion; s.Cover = 0.1f; s.Wind = 2; s.Fog = 1; s.FogTop = baseM + 320; break;
                // The day after a storm with the wind still up: broken sky, spindrift off the ridges, gusts over the forest.
                case "gale": arc = Arc.Clearing; s.Cover = 0.3f; s.Clearness = 0.8f; s.DeckBase = summitM + 400; s.Wind = 38; s.Gust = 1.6f; break;
                default: arc = Arc.Fair; return false;
            }
            s.Deck = Smooth(Tune.DeckFromCover, 0.9f, s.Cover);
            s.Visibility = Visibility(s.Cover, s.Rate, s.Snow);
            return true;
        }

        /// <summary>The pinned looks, in the order the shots take them.</summary>
        public static readonly string[] PresetNames = { "building", "storm", "bigstorm", "clearing", "bluebird", "rain", "thaw", "coldsnap", "inversion", "gale" };

        /// <summary>The visibility in what's falling (m): snow blinds, rain hazes, overcast softens.</summary>
        public static float Visibility(float cover, float rateMmPerHour, float snowShare)
        {
            float dry = Tune.ClearVisibility + (Tune.OvercastVisibility - Tune.ClearVisibility) * Smooth(0.3f, 1f, cover);
            if (rateMmPerHour <= 0.01f) return dry;
            float r = Math.Max(rateMmPerHour, 0.05f);
            float snow = Tune.SnowVisibilityAt1Mm * (float)Math.Pow(1 / r, Tune.SnowExponent);
            float rain = Tune.RainVisibilityAt1Mm * (float)Math.Pow(1 / r, Tune.RainExponent);
            // Harmonic blend: a little snow in the rain blinds more than its share.
            float falling = 1f / (snowShare / snow + (1 - snowShare) / rain);
            return Math.Clamp(Math.Min(dry, falling), Tune.LeastVisibility, dry);
        }

        /// <summary>
        /// The view's visibility (m): the true one, raised so the camera's target is never more than <see cref="Tune.VeilMost"/>
        /// fogged once the camera is <see cref="Tune.VeilFull"/> away. Near the ground the storm is the storm (a white-out).
        /// </summary>
        public static float ViewVisibility(float trueVisibility, float cameraDistance)
        {
            float floor = 3.912f * cameraDistance / -(float)Math.Log(1 - Tune.VeilMost);
            return Math.Max(trueVisibility, L(trueVisibility, floor, Smooth(Tune.VeilFrom, Tune.VeilFull, cameraDistance)));
        }

        /// <summary>A slab's visibility for the view: crossed straight through from the overview it veils, never hides.</summary>
        public static float SlabViewVisibility(float trueVisibility, float thickness, float cameraDistance) =>
            SlabViewVisibility(trueVisibility, thickness, cameraDistance, Tune.SlabMost);

        /// <summary>The same, at most <paramref name="most"/> fogged where it's densest (a broken sky veils less than a closed one).</summary>
        public static float SlabViewVisibility(float trueVisibility, float thickness, float cameraDistance, float most)
        {
            float floor = 3.912f * thickness / -(float)Math.Log(1 - Math.Clamp(most, 0.01f, 0.99f));
            return Math.Max(trueVisibility, L(trueVisibility, floor, Smooth(Tune.VeilFrom, Tune.VeilFull, cameraDistance)));
        }

        /// <summary>The fog over a distance at a visibility: 1 − exp(−3.912 d ÷ v), the 2 % contrast definition.</summary>
        public static float FogAt(float distance, float visibility) => 1f - (float)Math.Exp(-3.912f * distance / Math.Max(visibility, 1f));

        /// <summary>
        /// The snow line (ft): from the summit down, the first height where the snow share drops through
        /// <see cref="Tune.SnowLineShare"/>, so a cold pool's snow at the base under rain at mid still reads as rain above it.
        /// Above the summit when it rains there; below the base when it snows there.
        /// </summary>
        public static float SnowLineFeet(in WeatherSample bse, in WeatherSample mid, in WeatherSample top)
        {
            const int k = Tune.SnowLineShare;
            if (top.SnowPermille < k) return top.Feet + Above(mid, top);
            if (mid.SnowPermille < k) return Cross(mid, top);
            if (bse.SnowPermille < k) return Cross(bse, mid);
            return bse.Feet - Above(bse, mid);
        }

        static float Cross(in WeatherSample lo, in WeatherSample hi)
        {
            float a = lo.SnowPermille, b = hi.SnowPermille;
            float t = b - a <= 0 ? 0.5f : (Tune.SnowLineShare - a) / (b - a);
            return L(lo.Feet, hi.Feet, Math.Clamp(t, 0f, 1f));
        }

        // How far past the ends the line sits: a step's worth, so the veil stays off the mountain.
        static float Above(in WeatherSample a, in WeatherSample b) => Math.Max(500f, Math.Abs(b.Feet - a.Feet) * 0.5f);

        /// <summary>The arc: the events first (the forecast's words), then the hour's state and what's falling.</summary>
        public static Arc ArcOf(in Domain.Weather.WeatherHour h, in WeatherSample top, int events)
        {
            bool Has(WeatherEventKind e) => (events & (1 << (int)e)) != 0;
            bool falling = h.Liquid > 0;
            if (h.State == 2 || falling)
            {
                if (falling && top.SnowPermille < Tune.SnowLineShare) return Arc.Rain;
                if (!falling) return Arc.Storm;
                return Has(WeatherEventKind.BigStorm) || Has(WeatherEventKind.StormOfTheSeason) ? Arc.BigStorm : Arc.Storm;
            }
            if (h.State == 1) return Arc.Building;
            if (h.State == 3) return Arc.Clearing;
            if (Has(WeatherEventKind.Bluebird) || Has(WeatherEventKind.PowderDay)) return Arc.Bluebird;
            if (Has(WeatherEventKind.Thaw)) return Arc.Thaw;
            if (Has(WeatherEventKind.ColdSnap)) return Arc.ColdSnap;
            if (Has(WeatherEventKind.Inversion) || (h.Fog && h.Pool >= Tune.InversionPool)) return Arc.Inversion;
            return Arc.Fair;
        }

        /// <summary>
        /// The look between hours: it eases from the look it showed when the hour arrived to the hour's own evenly over
        /// <see cref="Tune.EaseShare"/> of the face hour, and never faster than <see cref="Tune.MostBlendPerSecond"/> of real
        /// time, so a night hour at 8× (1.9 s) still blends instead of strobing. Within an hour a new target (the camera panned
        /// to another cell) eases over <see cref="Tune.PanEaseSeconds"/>. A skip snaps.
        /// </summary>
        public sealed class Easer
        {
            State _from, _to, _shown;
            long _hour = long.MinValue;
            float _blend = 1;

            public State Shown => _shown;
            public State Target => _to;
            public float Blend => _blend;
            public bool HasLook => _hour != long.MinValue;

            /// <summary>Steps the look: <paramref name="hourKey"/> names the face hour, <paramref name="faceMsIntoHour"/> how far in it is.</summary>
            public void Step(in State target, long hourKey, long faceMsIntoHour, float realSeconds, bool snap)
            {
                if (_hour == long.MinValue || snap || hourKey < _hour || hourKey > _hour + 1)
                {
                    _from = _to = _shown = target; _hour = hourKey; _blend = 1;
                    return;
                }
                if (hourKey != _hour) { _from = _shown; _hour = hourKey; _blend = 0; _to = target; }
                else
                {
                    bool night = target.Night;
                    _to = State.Lerp(_to, target, 1 - (float)Math.Exp(-Math.Max(0, realSeconds) / Tune.PanEaseSeconds));
                    _to.Night = night;
                }
                float wanted = Math.Clamp(faceMsIntoHour / (3_600_000f * Tune.EaseShare), 0f, 1f);
                _blend = Math.Min(wanted, _blend + Math.Max(0, realSeconds) * Tune.MostBlendPerSecond);
                _blend = Math.Max(_blend, 0);
                _shown = State.Lerp(_from, _to, _blend);
            }
        }

        static float L(float a, float b, float t) => a + (b - a) * t;

        static float Smooth(float e0, float e1, float x)
        {
            float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
            return t * t * (3 - 2 * t);
        }

        static float LerpDegrees(float a, float b, float t)
        {
            float d = ((b - a) % 360 + 540) % 360 - 180;
            return ((a + d * t) % 360 + 360) % 360;
        }
    }
}
