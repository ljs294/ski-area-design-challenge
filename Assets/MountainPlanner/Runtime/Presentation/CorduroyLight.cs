using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Task 4.08 G1 (owner 2026-11-25: "make the groomed texture more noticeable from further away"): how corduroy takes the
    /// sun, the C# twin of SnowLighting.hlsl's CorduroySun, CorduroyShade, CorduroyPassSun and CordStripCover (the terrain
    /// draws them; CorduroyLightTests holds the two equal and the light the right way round). The ridges, 1 in apart and
    /// 8 mm deep (SK3), are two sloped faces: a face turned from the sun past its slope gets none, the face toward a low sun is
    /// shaded near the trough by the crest across the groove, and the eye sees one face more than the other. So corduroy reads
    /// darker toward the sun and in a raking sun, a little brighter with the sun behind the eye (p4-08-findings, Round 4).
    /// Screen only: nothing here is hashed or saved.
    /// </summary>
    public static class CorduroyLight
    {
        public const float SpacingMetres = 0.0254f, DepthMetres = 0.008f;
        /// <summary>Of the light a shaded groove would get from the sun, bounced in by the faces.</summary>
        public const float Bounce = 0.45f;
        /// <summary>Sun-behind brightening at most (lit snow sits near the top of the picture's range).</summary>
        public const float SunMax = 1.15f;
        /// <summary>The sky the grooves don't see.</summary>
        public const float SkyLoss = 0.06f;
        /// <summary>Each pass's ridges this much deeper or shallower, and turned this far either way (a hash of the pass).</summary>
        public const float PassDepthSpread = 0.25f, PassTurnDegrees = 4;
        /// <summary>The rough strip with no ridges where two passes meet.</summary>
        public const float JoinMetres = 0.3f;

        /// <summary>The faces' slope (rise over run): a V-groove with the sine's mean slope, 2 × depth / spacing.</summary>
        public static float Slope(float depthScale) => 2 * DepthMetres / SpacingMetres * depthScale;

        /// <summary>The sun's light on corduroy as seen from <paramref name="view"/>, over the same on smooth snow (n: the smooth
        /// surface; across: across the ridges; k: <see cref="Slope"/>). Bounce applied, at most <see cref="SunMax"/>.</summary>
        public static float Sun(Vector3 n, Vector3 across, Vector3 sunDir, Vector3 view, float k)
        {
            Vector3 a = across - n * Vector3.Dot(across, n);
            a /= Mathf.Sqrt(Mathf.Max(Vector3.Dot(a, a), 1e-8f));
            float sn = Vector3.Dot(sunDir, n), sa = Vector3.Dot(sunDir, a), vn = Vector3.Dot(view, n), va = Vector3.Dot(view, a);
            if (sn < 0.02f || vn < 0.02f) return 1;
            float inv = 1 / Mathf.Sqrt(1 + k * k);
            float sp = Mathf.Max(0, (sn + k * sa) * inv), sm = Mathf.Max(0, (sn - k * sa) * inv);
            float vp = Mathf.Max(0, (vn + k * va) * inv), vm = Mathf.Max(0, (vn - k * va) * inv);
            float ts = sn / Mathf.Max(Mathf.Abs(sa), 1e-4f), tv = vn / Mathf.Max(Mathf.Abs(va), 1e-4f);
            float litToward = ts >= k ? 1 : 2 * ts / (k + ts), litAway = ts >= k ? 1 : 0;
            float seenToward = tv >= k ? 1 : 2 * tv / (k + tv), seenAway = tv >= k ? 1 : 0;
            float lp = sa >= 0 ? litToward : litAway, lm = sa >= 0 ? litAway : litToward;
            float wp = va >= 0 ? seenToward : seenAway, wm = va >= 0 ? seenAway : seenToward;
            float f = (Mathf.Min(lp, wp) * vp * sp + Mathf.Min(lm, wm) * vm * sm) / Mathf.Max(wp * vp + wm * vm, 1e-4f) / sn;
            return Mathf.Min(f < 1 ? Mathf.Lerp(f, 1, Bounce) : f, SunMax);
        }

        /// <summary>Close up, a single ridge's shade (0 lit, 1 shaded) at its phase (cycles across, the sine's crest at a
        /// quarter); perCycle: cycles a pixel.</summary>
        public static float Shade(float cycles, float perCycle, Vector3 n, Vector3 across, Vector3 sunDir, float k)
        {
            Vector3 a = across - n * Vector3.Dot(across, n);
            a /= Mathf.Sqrt(Mathf.Max(Vector3.Dot(a, a), 1e-8f));
            float sn = Vector3.Dot(sunDir, n), sa = Vector3.Dot(sunDir, a);
            if (sn < 0.02f) return 0;
            float ts = sn / Mathf.Max(Mathf.Abs(sa), 1e-4f);
            if (ts >= k) return 0;
            float m = Mathf.PI / 2 * k;
            float end = Mathf.Acos(Mathf.Clamp01(ts / m)) / (2 * Mathf.PI);
            float phase = sa >= 0 ? Frac(cycles) : Frac(0.5f - cycles);
            float share = k / (k + ts), edge = 0.5f * Mathf.Max(perCycle, 0.02f);
            float into = Frac(end - phase);
            into = into > 0.5f * (1 + share) ? into - 1 : into;
            return Smooth(-edge, edge, into) * (1 - Smooth(share - edge, share + edge, into));
        }

        /// <summary>One groomer pass's ridges (pass: its number across the trail), <paramref name="vary"/> of its own depth and
        /// turn; depth: the pass's depth scale.</summary>
        public static float PassSun(Vector3 n, Vector3 across, Vector3 sunDir, Vector3 view, float pass, float vary, out float depth)
        {
            int id = (int)pass;
            depth = 1 + PassDepthSpread * (2 * Hash(id, 7) - 1) * vary;
            float turn = PassTurnDegrees * Mathf.Deg2Rad * (2 * Hash(id, 11) - 1) * vary;
            Vector3 side = Vector3.Cross(n, across);
            return Sun(n, across * Mathf.Cos(turn) + side * Mathf.Sin(turn), sunDir, view, Slope(depth));
        }

        /// <summary>The groove light under cloud (<paramref name="cover"/> 0 to 1): the grooves shade only the direct sun, which a
        /// closing deck takes away as it does the sun disc (cover 0.5 to 0.9). The sky the grooves miss stays.</summary>
        public static float UnderCloud(float sun, float cover) => Mathf.Lerp(1, sun, 1 - Smooth(0.5f, 0.9f, cover));

        /// <summary>How much of a pixel <paramref name="p"/> metres wide a strip <paramref name="width"/> wide covers when its
        /// centre line is <paramref name="d"/> metres away (box filter).</summary>
        public static float StripCover(float d, float p, float width)
        {
            p = Mathf.Max(p, 1e-4f);
            return Mathf.Clamp01((Mathf.Min(d + p / 2, width / 2) - Mathf.Max(d - p / 2, -width / 2)) / p);
        }

        /// <summary>SnowLighting.hlsl's SnowHash2: 0 to 1 for a lattice cell.</summary>
        public static float Hash(int x, int y)
        {
            uint h = unchecked((uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u);
            h = unchecked((h ^ (h >> 15)) * 0x2C1B3C6Du);
            h = unchecked((h ^ (h >> 12)) * 0x297A2D39u);
            return (h >> 8) / 16777215f;
        }

        static float Frac(float x) => x - Mathf.Floor(x);

        static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }
    }
}
