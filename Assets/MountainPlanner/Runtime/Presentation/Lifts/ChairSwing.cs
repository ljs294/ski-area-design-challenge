using System;
using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// How a hanging chair swings (task P3-16, BB3), very lightweight and stateless: every angle is a function of the
    /// chair's time round its track and the clock, so a paused clock stops it and a scripted movie repeats exactly.
    /// <list type="bullet">
    /// <item>Along the line (about the hanger's hinge): each place the rope changes direction over a sheave train gives the
    /// chair a kick, as the grip's speed along the ground changes there; a detachable's tyre ramps lean it while they speed
    /// it up or slow it down. Each is a damped pendulum's answer, summed, so kicks overlap without jumps.</item>
    /// <item>Side to side, now and then: a slow gust envelope, from the forest's wind or a timer.</item>
    /// <item>A tiny steady pendulum, each chair in its own phase, so the line never looks frozen.</item>
    /// </list>
    /// Built once per lift from its <see cref="CarrierTrack"/>; <see cref="Pitch"/> and <see cref="Roll"/> allocate nothing.
    /// It works for any line a player draws: the kicks come from the rope's own shape and speeds.
    /// </summary>
    public sealed class ChairSwing
    {
        public enum SwayDriver { Wind, Timer, Off }

        /// <summary>The look's knobs; the owner picks the values in the demo build (BB3), then they become the defaults.</summary>
        public struct Look
        {
            public string Name;
            /// <summary>Scales the physical kick from a change of rope direction or a ramp (0 = none, 1 = the physics).</summary>
            public float Gain;
            /// <summary>The steady pendulum's amplitude, degrees.</summary>
            public float SteadyDegrees;
            /// <summary>The side sway's amplitude in a full gust, degrees.</summary>
            public float SwayDegrees;
            public SwayDriver Sway;
        }

        public static readonly Look[] Presets =
        {
            new Look { Name = "Off", Gain = 0, SteadyDegrees = 0, SwayDegrees = 0, Sway = SwayDriver.Off },
            new Look { Name = "Subtle", Gain = 2, SteadyDegrees = 0.3f, SwayDegrees = 0.8f, Sway = SwayDriver.Wind },
            new Look { Name = "Medium", Gain = 4, SteadyDegrees = 0.6f, SwayDegrees = 1.5f, Sway = SwayDriver.Wind },
        };

        /// <summary>What every lift draws with.</summary>
        public static Look Current = Presets[1];

        /// <summary>The largest swing either way, degrees: well inside G5's 11.5° swing clearance.</summary>
        public const float MaxDegrees = 6f;
        /// <summary>Seconds for a swing to die to about a third.</summary>
        public const float DampSeconds = 2.5f;
        /// <summary>Kicks older than this are spent (e^-4 of their size).</summary>
        const float Window = 4 * DampSeconds;
        /// <summary>Rope bends sharper than this (radians a metre) are sheave trains; a catenary's gentle curve isn't.</summary>
        const float BendCurvature = 0.01f;
        /// <summary>A sheave train's bends closer than this (metres) are one kick.</summary>
        const float TrainGap = 4f;
        const float Gravity = 9.81f;

        /// <summary>
        /// One kick: an impulse at <see cref="Start"/> (End == Start, Size the swing's amplitude, radians) or a lean held
        /// from Start to End (a ramp's acceleration, Size the leaning angle, radians). Times are round the track, seconds.
        /// </summary>
        public struct Kick
        {
            public float Start, End, Size;
            public bool Lean => End > Start;
        }

        public readonly float Omega;
        public readonly float LoopTime;
        readonly Kick[] _kicks;
        readonly float _longest;

        public int KickCount { get; }
        /// <summary>The kicks in time order, including copies one loop back so the first chairs see the last kicks.</summary>
        public IReadOnlyList<Kick> Kicks => _kicks;

        ChairSwing(Kick[] kicks, int count, float omega, float loopTime)
        {
            _kicks = kicks;
            KickCount = count;
            Omega = omega;
            LoopTime = loopTime;
            foreach (var k in kicks) _longest = Mathf.Max(_longest, k.End - k.Start);
        }

        /// <summary>
        /// The kicks along <paramref name="track"/> for a chair hanging <paramref name="drop"/> metres below its grip. A
        /// pendulum's length is taken as three quarters of the drop (to the chair's centre of mass).
        /// </summary>
        public static ChairSwing For(CarrierTrack track, float drop)
        {
            float omega = Mathf.Sqrt(Gravity / Mathf.Max(0.5f, 0.75f * drop));
            var kicks = new List<Kick>();
            var p = track.Points;
            var times = track.Times;
            var speeds = track.Speeds;
            int n = p.Length;

            // Where the rope changes direction over a sheave train, the grip's speed along the ground changes,
            // v (cos θ out − cos θ in), and the chair, hanging, answers with a swing of that over L ω (radians). A train's
            // sharp bends (closer than TrainGap) are one kick, at its middle.
            float length = Mathf.Max(0.5f, 0.75f * drop);
            int first = -1, last = -1;
            for (int i = 1; i + 1 < n; i++)
            {
                float a = Pitch(p[i - 1], p[i]), b = Pitch(p[i], p[i + 1]);
                float len = 0.5f * (Vector3.Distance(p[i - 1], p[i]) + Vector3.Distance(p[i], p[i + 1]));
                if (len <= 1e-4f || Mathf.Abs(b - a) / len <= BendCurvature) continue;
                if (first >= 0 && track.Walked[i] - track.Walked[last] > TrainGap) { AddTrain(first, last); first = -1; }
                if (first < 0) first = i;
                last = i;
            }
            if (first >= 0) AddTrain(first, last);

            void AddTrain(int i0, int i1)
            {
                float into = Pitch(p[i0 - 1], p[i0]), outOf = Pitch(p[i1], p[i1 + 1]);
                float du = speeds[(i0 + i1) / 2] * (Mathf.Cos(outOf) - Mathf.Cos(into));
                float at = 0.5f * (times[i0] + times[i1]);
                float size = du / (length * omega);
                if (Mathf.Abs(size) > 1e-5f) kicks.Add(new Kick { Start = at, End = at, Size = size });
            }

            // A detachable's tyre ramps: while the grip speeds up (or slows) at a, the chair leans back (or forward) by atan(a/g).
            for (int i = 1; i < n;)
            {
                float dt = times[i] - times[i - 1];
                float accel = dt > 1e-5f ? (speeds[i] - speeds[i - 1]) / dt : 0;
                if (Mathf.Abs(accel) < 0.05f) { i++; continue; }
                int j = i + 1;
                while (j < n)
                {
                    float dt2 = times[j] - times[j - 1];
                    float a2 = dt2 > 1e-5f ? (speeds[j] - speeds[j - 1]) / dt2 : 0;
                    if (Mathf.Abs(a2) < 0.05f || Mathf.Sign(a2) != Mathf.Sign(accel)) break;
                    j++;
                }
                float span = times[j - 1] - times[i - 1];
                float mean = span > 1e-5f ? (speeds[j - 1] - speeds[i - 1]) / span : 0;
                if (span > 1e-3f) kicks.Add(new Kick { Start = times[i - 1], End = times[j - 1], Size = Mathf.Atan(mean / Gravity) });
                i = j;
            }

            kicks.Sort((x, y) => x.Start.CompareTo(y.Start));
            int count = kicks.Count;
            float loop = track.LoopTime;
            // Copies one loop back, so a chair just past the start still feels the kicks just before the end.
            var all = new List<Kick>(count * 2);
            foreach (var k in kicks)
                if (k.End > loop - Window) all.Add(new Kick { Start = k.Start - loop, End = k.End - loop, Size = k.Size });
            all.AddRange(kicks);
            return new ChairSwing(all.ToArray(), count, omega, loop);
        }

        static float Pitch(Vector3 a, Vector3 b)
        {
            var d = b - a;
            return Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude);
        }

        /// <summary>
        /// The swing along the line, degrees (positive: the chair swings back, trailing its grip), for a chair
        /// <paramref name="t"/> seconds round its track; <paramref name="seconds"/> is the clock and <paramref name="index"/>
        /// the chair's number (its steady pendulum's phase).
        /// </summary>
        public float Pitch(float t, double seconds, int index, in Look look)
        {
            float rad = 0;
            if (look.Gain > 0 && _kicks.Length > 0)
            {
                // The last kick that has started, then back while any kick could still be swinging.
                int lo = 0, hi = _kicks.Length - 1, last = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_kicks[mid].Start <= t) { last = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                for (int i = last; i >= 0 && t - _kicks[i].Start < Window + _longest; i--)
                {
                    var k = _kicks[i];
                    rad += k.Lean ? k.Size * (Step(t - k.Start) - Step(t - k.End)) : k.Size * Impulse(t - k.Start);
                }
                rad *= look.Gain;
            }
            float deg = rad * Mathf.Rad2Deg;
            if (look.SteadyDegrees > 0) deg += look.SteadyDegrees * Mathf.Sin((float)(seconds % 3600.0) * Omega + index * 2.39996f);
            return Mathf.Clamp(deg, -MaxDegrees, MaxDegrees);
        }

        /// <summary>
        /// The sway side to side, degrees, now and then: a gust envelope that reaches each chair a little later along the
        /// line (<paramref name="along"/>, metres). With <see cref="SwayDriver.Wind"/> it follows the forest's wind strength
        /// (0–1) and how much it blows across the line (<paramref name="across"/>, 0–1); with Timer it ignores the wind.
        /// </summary>
        public float Roll(double seconds, float along, int index, float windStrength, float across, in Look look)
        {
            if (look.Sway == SwayDriver.Off || look.SwayDegrees <= 0) return 0;
            float scale = look.Sway == SwayDriver.Wind ? windStrength * across : 1;
            if (scale <= 1e-4f) return 0;
            float t = (float)(seconds % 3600.0) - along / 6f;
            return look.SwayDegrees * scale * Gust(t) * Mathf.Sin(t * Omega * 0.93f + index * 1.61803f);
        }

        /// <summary>0 most of the time, rising to 1 for a few seconds every half minute or so (three slow waves, sharpened).</summary>
        public static float Gust(float t)
        {
            float g = Mathf.Sin(t * 0.11f) + Mathf.Sin(t * 0.17f + 1.3f) + Mathf.Sin(t * 0.053f + 2.1f);
            float x = Mathf.Clamp01((g - 1.1f) / 1.2f);
            return x * x * (3 - 2 * x);
        }

        float Impulse(float age) => age <= 0 ? 0 : Mathf.Exp(-age / DampSeconds) * Mathf.Sin(Omega * age);

        float Step(float age) => age <= 0 ? 0 : 1 - Mathf.Exp(-age / DampSeconds) * Mathf.Cos(Omega * age);
    }
}
