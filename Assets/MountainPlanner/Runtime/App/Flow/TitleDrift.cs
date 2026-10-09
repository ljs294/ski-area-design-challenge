using System;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// The title's slow camera drift (task P2-03; owner: "postcard path"): the camera eases through a few chosen views
    /// of the mountain and loops, slowing near each view but never stopping. Over the Jackson Hole demo the views look
    /// up at the face from the valley, so the summit ridge hides the surround ring's edge; any other mountain behind the
    /// title gets a gentle sweep round its centre instead.
    ///
    /// It's a pure function of time over precomputed shots: <see cref="Evaluate"/> allocates nothing.
    /// </summary>
    public sealed class TitleDrift
    {
        /// <summary>One view: the orbit target (local metres, x east, z north, y the ground there), distance and angles in degrees.</summary>
        public struct Shot
        {
            public float X, Y, Z, Distance, Yaw, Pitch;

            public Shot(float x, float z, float distance, float yaw, float pitch)
            {
                X = x;
                Y = 0;
                Z = z;
                Distance = distance;
                Yaw = yaw;
                Pitch = pitch;
            }
        }

        /// <summary>Seconds from one view to the next: four views loop in two minutes.</summary>
        public const float SecondsPerShot = 30f;

        /// <summary>How much the drift slows at each view: at 0.85 it still moves at 15% of its mean speed there.</summary>
        const float Linger = 0.85f;

        /// <summary>The demo's site centre (manifest Site.Latitude/Longitude), and how near another site must be to count as it.</summary>
        const double DemoLatitude = 43.593002, DemoLongitude = -110.848007, SameSiteDegrees = 0.01;

        /// <summary>
        /// Jackson Hole 5 km (D1), in the demo's local frame: the face from the valley; Corbet's and the tram dock under
        /// Rendezvous; Rendezvous Bowl towards Cody; the Headwall and Casper from the north-east.
        /// </summary>
        static readonly Shot[] JacksonHole =
        {
            new Shot(-900, 300, 4200, 290, 14),
            new Shot(-1550, 600, 1900, 262, 16),
            new Shot(-1800, -500, 2600, 300, 13),
            new Shot(-300, 1300, 3000, 245, 15),
        };

        readonly Shot[] _shots;

        public int Shots => _shots.Length;
        public float LoopSeconds => _shots.Length * SecondsPerShot;
        public Shot this[int i] => _shots[i];

        TitleDrift(Shot[] shots)
        {
            _shots = (Shot[])shots.Clone();
            // Unwrap the headings so each move turns the short way round (the loop's last move included).
            for (int i = 1; i < _shots.Length; i++) _shots[i].Yaw = _shots[i - 1].Yaw + Mathf.DeltaAngle(_shots[i - 1].Yaw, _shots[i].Yaw);
        }

        /// <summary>
        /// The drift for a site: the demo's postcard path when the site is the demo's, else a sweep sized to the area.
        /// <paramref name="groundAt"/> gives each view's target height once, here (NaN off the terrain: the centre's is used).
        /// </summary>
        public static TitleDrift For(double latitude, double longitude, float sizeMetres, float homeYaw, Func<float, float, float> groundAt)
        {
            bool demo = Math.Abs(latitude - DemoLatitude) < SameSiteDegrees && Math.Abs(longitude - DemoLongitude) < SameSiteDegrees;
            var shots = demo ? JacksonHole : Sweep(sizeMetres, homeYaw);
            var drift = new TitleDrift(shots);
            float centre = groundAt?.Invoke(0, 0) ?? 0;
            if (float.IsNaN(centre)) centre = 0;
            for (int i = 0; i < drift._shots.Length; i++)
            {
                float y = groundAt?.Invoke(drift._shots[i].X, drift._shots[i].Z) ?? centre;
                drift._shots[i].Y = float.IsNaN(y) ? centre : y;
            }
            return drift;
        }

        /// <summary>Any other mountain: round its centre from the Home view's side, ±30°, easing in and out a little.</summary>
        static Shot[] Sweep(float size, float homeYaw) => new[]
        {
            new Shot(0, 0, size * 1.1f, homeYaw - 30, 24),
            new Shot(size * 0.05f, size * 0.05f, size * 0.95f, homeYaw - 10, 20),
            new Shot(0, 0, size * 1.05f, homeYaw + 30, 22),
            new Shot(-size * 0.05f, -size * 0.05f, size * 0.95f, homeYaw + 10, 26),
        };

        /// <summary>The camera at <paramref name="seconds"/> into the drift (it loops). Allocates nothing.</summary>
        public Shot Evaluate(float seconds)
        {
            int n = _shots.Length;
            float loop = n * SecondsPerShot;
            float t = seconds % loop;
            if (t < 0) t += loop;
            int i = (int)(t / SecondsPerShot);
            if (i >= n) i = n - 1;
            float x = t / SecondsPerShot - i;
            // Eased along the move: slow near each view (but never still), fastest between.
            float u = x - Linger * Mathf.Sin(2 * Mathf.PI * x) / (2 * Mathf.PI);
            // Each move reaches the next view and turns the same way the shots are unwrapped; the loop's last move
            // carries its heading on by whole turns.
            Shot p0 = At(i - 1), p1 = At(i), p2 = At(i + 1), p3 = At(i + 2);
            return new Shot
            {
                X = CatmullRom(p0.X, p1.X, p2.X, p3.X, u),
                Y = CatmullRom(p0.Y, p1.Y, p2.Y, p3.Y, u),
                Z = CatmullRom(p0.Z, p1.Z, p2.Z, p3.Z, u),
                Distance = CatmullRom(p0.Distance, p1.Distance, p2.Distance, p3.Distance, u),
                Yaw = Mathf.Repeat(CatmullRom(p0.Yaw, p1.Yaw, p2.Yaw, p3.Yaw, u), 360f),
                Pitch = CatmullRom(p0.Pitch, p1.Pitch, p2.Pitch, p3.Pitch, u),
            };
        }

        /// <summary>Shot <paramref name="i"/> round the loop, its heading continued by whole turns past either end.</summary>
        Shot At(int i)
        {
            int n = _shots.Length;
            int wraps = i < 0 ? -1 : i / n;
            var s = _shots[((i % n) + n) % n];
            if (wraps != 0)
            {
                // The loop's own turn: from the last view back to the first, the short way.
                float turn = _shots[n - 1].Yaw + Mathf.DeltaAngle(_shots[n - 1].Yaw, _shots[0].Yaw) - _shots[0].Yaw;
                s.Yaw += wraps * turn;
            }
            return s;
        }

        static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (3 * p1 - p0 - 3 * p2 + p3) * t3);
        }

        /// <summary>Puts the camera on the drift (orbit mode, no smoothing).</summary>
        public void Apply(Presentation.ViewCamera camera, float seconds)
        {
            var s = Evaluate(seconds);
            camera.Frame(new Vector3(s.X, s.Y, s.Z), s.Distance);
            camera.SetAngles(s.Yaw, s.Pitch);
        }
    }
}
