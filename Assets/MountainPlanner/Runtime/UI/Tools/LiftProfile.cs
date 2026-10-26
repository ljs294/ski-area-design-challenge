using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// What the lift profile strip draws (LF-10, task 09; the approved mockup's liftStripInner): the ground along the line,
    /// the loaded rope span by span with its towers, the terminals, the chairs' height over the ground (TW-01's track) with
    /// the load and unload zones, and while towers go in by hand the green band and the tower in hand (TW-09). Made from a
    /// solved <see cref="LiftRope"/> when the line or its towers change; the tower in hand only moves its few fields.
    /// Distances are metres along the line from the bottom bullwheel; heights are elevations in metres.
    /// </summary>
    public sealed class LiftProfile
    {
        public double Length;
        public float[] S, Ground, Clear;            // the samples: along, ground, the chairs over the ground (NaN: none)
        public LiftRope Rope;
        public double BottomS, BottomZ, TopS, TopZ;  // where the rope leaves each terminal
        public double ZoneB, ZoneT;                  // the load zone's end and the unload zone's start (TW-01 v2's low zones)
        public double LowestS = double.NaN, Lowest = double.NaN;
        public List<(double From, double To)> Band = new List<(double, double)>();
        public double HandS = double.NaN;
        /// <summary>Towers by hand: where the line isn't strung yet (past the last placed tower), or NaN. The rope beyond is
        /// drawn dashed ("needs towers") and its chairs aren't judged.</summary>
        public double OpenFrom = double.NaN;
        public int HandMm;
        public bool HandOk;
        public double Rise, RiseLimit;
        public string Family;
        public int Towers;
        /// <summary>Bumped whenever the drawing changes (the strip redraws its labels only then).</summary>
        public int Version;

        const int MaxSamples = 400;

        /// <summary>
        /// The profile of <paramref name="rope"/> over <paramref name="ground"/> (in the rope's frame), the line from
        /// (<paramref name="bx"/>, <paramref name="by"/>) to (<paramref name="tx"/>, <paramref name="ty"/>), with its
        /// terminals at <paramref name="levels"/>.
        /// </summary>
        public static LiftProfile From(LiftRope rope, IHeightSource ground, double bx, double by, double tx, double ty, LiftLevels levels,
                                       double openFrom = double.NaN)
        {
            if (rope == null) throw new ArgumentNullException(nameof(rope));
            if (ground == null) throw new ArgumentNullException(nameof(ground));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            int n = Math.Max(2, Math.Min(MaxSamples, (int)Math.Ceiling(length / 1.524)) + 1);
            var p = new LiftProfile
            {
                Length = length, Rope = rope, S = new float[n], Ground = new float[n], Clear = new float[n],
                BottomS = rope.Type.Bottom.ExitAlong, BottomZ = levels.BottomMetres + rope.Type.Bottom.ExitRise,
                TopS = length - rope.Type.Top.ExitAlong, TopZ = levels.TopMetres + rope.Type.Top.ExitRise,
                Rise = rope.RiseMetres, RiseLimit = rope.Type.RiseLimitMetres, Family = rope.Type.Name, Towers = rope.Towers.Count,
                OpenFrom = openFrom,
            };
            LiftRope.Zones(rope.Type, length, out _, out double edgeB, out double edgeT, out _);
            p.ZoneB = edgeB;
            p.ZoneT = edgeT;
            double lowest = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double s = length * i / (n - 1);
                p.S[i] = (float)s;
                p.Ground[i] = (float)ground.At(bx + ux * s, by + uy * s);
                if (rope.ClearanceAt(s, out double clear, out _))
                {
                    p.Clear[i] = (float)clear;
                    if (clear < lowest && s > edgeB && s < edgeT && !(s > openFrom)) { lowest = clear; p.LowestS = s; }
                }
                else p.Clear[i] = float.NaN;
            }
            if (lowest < double.MaxValue) p.Lowest = lowest;
            return p;
        }
    }
}
