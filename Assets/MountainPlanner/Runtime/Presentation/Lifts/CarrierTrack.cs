using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// The way a lift's carriers go round, with the speed and time at every point (LT41). On a fixed grip it's the rope
    /// loop at rope speed. On a detachable, the carriers leave the rope at each station and run round its rail: they slow
    /// on the tyre conveyors from line speed to the station's speed (boarding at the bottom, unloading at the top) at
    /// <see cref="Domain.Design.LiftType.RampAccel"/>, and speed back up before they rejoin the rope. Built once per lift;
    /// <see cref="DistanceAt"/> allocates nothing. Gondolas reuse it with their own station speeds and rails.
    /// </summary>
    public sealed class CarrierTrack
    {
        public Vector3[] Points { get; private set; }
        /// <summary>The distance walked to each point, metres.</summary>
        public float[] Walked { get; private set; }
        /// <summary>The time a carrier reaches each point, seconds from the first.</summary>
        public float[] Times { get; private set; }
        /// <summary>The speed at each point, m/s.</summary>
        public float[] Speeds { get; private set; }
        public float Length => Walked.Length > 0 ? Walked[Walked.Length - 1] : 0;
        /// <summary>How long one carrier takes round the whole track, seconds.</summary>
        public float LoopTime => Times.Length > 0 ? Times[Times.Length - 1] : 0;

        /// <summary>A fixed grip's track: the rope loop at one speed.</summary>
        public static CarrierTrack Uniform(Vector3[] loop, float speed)
        {
            var speeds = new float[loop.Length];
            for (int i = 0; i < speeds.Length; i++) speeds[i] = speed;
            return new CarrierTrack(loop, speeds);
        }

        /// <summary>
        /// A detachable's track: <paramref name="up"/> (the loaded rope, bottom to top), the top station's rail, the
        /// <paramref name="down"/> rope (top to bottom) and the bottom station's rail, each rail in travel order. On the
        /// rails the carriers slow from <paramref name="line"/> speed to <paramref name="alight"/> (top) and
        /// <paramref name="board"/> (bottom) and back, at <paramref name="accel"/>.
        /// </summary>
        public static CarrierTrack Detachable(IReadOnlyList<Vector3> up, IReadOnlyList<Vector3> topRail, IReadOnlyList<Vector3> down,
                                              IReadOnlyList<Vector3> bottomRail, float line, float alight, float board, float accel)
        {
            var points = new List<Vector3>(up.Count + down.Count + 256);
            var speeds = new List<float>(points.Capacity);
            void Rope(IReadOnlyList<Vector3> run)
            {
                foreach (var p in run) { points.Add(p); speeds.Add(line); }
            }
            void Rail(IReadOnlyList<Vector3> rail, float station)
            {
                // Resampled every half metre, so the ramps are smooth; the speed eases down from the rope's and back.
                var dense = Resample(rail, 0.5f);
                float length = 0;
                for (int i = 1; i < dense.Count; i++) length += Vector3.Distance(dense[i - 1], dense[i]);
                float walked = 0;
                for (int i = 0; i < dense.Count; i++)
                {
                    if (i > 0) walked += Vector3.Distance(dense[i - 1], dense[i]);
                    float fromEnd = Mathf.Min(walked, length - walked);
                    float ramp = Mathf.Sqrt(Mathf.Max(0, line * line - 2 * accel * fromEnd));
                    points.Add(dense[i]);
                    speeds.Add(Mathf.Clamp(ramp, Mathf.Min(station, line), line));
                }
            }
            Rope(up);
            Rail(topRail, alight);
            Rope(down);
            Rail(bottomRail, board);
            if (points.Count > 0) { points.Add(points[0]); speeds.Add(speeds[0]); }   // round to the start
            return new CarrierTrack(points.ToArray(), speeds.ToArray());
        }

        CarrierTrack(Vector3[] points, float[] speeds)
        {
            Points = points;
            Speeds = speeds;
            Walked = new float[points.Length];
            Times = new float[points.Length];
            for (int i = 1; i < points.Length; i++)
            {
                float d = Vector3.Distance(points[i - 1], points[i]);
                Walked[i] = Walked[i - 1] + d;
                float v = 0.5f * (speeds[i - 1] + speeds[i]);
                Times[i] = Times[i - 1] + (v > 1e-4f ? d / v : 0);
            }
        }

        /// <summary>
        /// Where a carrier is <paramref name="time"/> seconds round the track (0 to <see cref="LoopTime"/>): its distance
        /// along it, and the segment it's on (<paramref name="segment"/> is also a hint: the search starts there, so
        /// carriers taken in order cost a step each).
        /// </summary>
        public float DistanceAt(float time, ref int segment)
        {
            int n = Times.Length;
            if (n < 2) { segment = 1; return 0; }
            if (segment < 1 || segment >= n || Times[segment - 1] > time) segment = 1;
            while (segment < n - 1 && Times[segment] < time) segment++;
            float span = Times[segment] - Times[segment - 1];
            float f = span > 0 ? Mathf.Clamp01((time - Times[segment - 1]) / span) : 0;
            return Mathf.Lerp(Walked[segment - 1], Walked[segment], f);
        }

        /// <summary>The smallest gap between neighbouring carriers anywhere on the track, metres, with
        /// <paramref name="count"/> of them evenly spaced in time (tests: G4's station minimums).</summary>
        public float SmallestGap(int count)
        {
            if (count < 2 || LoopTime <= 0) return Length;
            float dt = LoopTime / count, smallest = float.MaxValue;
            int segA = 1, segB = 1;
            for (float t = 0; t < LoopTime; t += 0.05f)
            {
                float a = DistanceAt(t, ref segA);
                float tb = t + dt;
                float b = tb < LoopTime ? DistanceAt(tb, ref segB) : DistanceAt(tb - LoopTime, ref segB) + Length;
                smallest = Mathf.Min(smallest, b - a);
                if (tb >= LoopTime) segB = 1;
            }
            return smallest;
        }

        static List<Vector3> Resample(IReadOnlyList<Vector3> line, float step)
        {
            var dense = new List<Vector3>();
            if (line.Count == 0) return dense;
            dense.Add(line[0]);
            for (int i = 1; i < line.Count; i++)
            {
                float d = Vector3.Distance(line[i - 1], line[i]);
                int n = Mathf.Max(1, Mathf.CeilToInt(d / step));
                for (int k = 1; k <= n; k++) dense.Add(Vector3.Lerp(line[i - 1], line[i], (float)k / n));
            }
            return dense;
        }
    }
}
