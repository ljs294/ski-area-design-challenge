using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.UI
{
    /// <summary>A name's box on screen (panel pixels): its centre, half its size and its slant.</summary>
    public readonly struct LabelBox
    {
        public readonly Vector2 Centre, Half;
        public readonly float Cos, Sin;

        public LabelBox(Vector2 centre, Vector2 half, float degrees)
        {
            Centre = centre;
            Half = half;
            float r = degrees * Mathf.Deg2Rad;
            Cos = Mathf.Cos(r);
            Sin = Mathf.Sin(r);
        }

        /// <summary>Whether two boxes overlap (separating axes: each box's two sides).</summary>
        public static bool Overlap(in LabelBox a, in LabelBox b)
        {
            var d = b.Centre - a.Centre;
            return !Apart(d, a, b, a.Cos, a.Sin) && !Apart(d, a, b, -a.Sin, a.Cos)
                && !Apart(d, a, b, b.Cos, b.Sin) && !Apart(d, a, b, -b.Sin, b.Cos);
        }

        static bool Apart(Vector2 d, in LabelBox a, in LabelBox b, float x, float y) =>
            Mathf.Abs(d.x * x + d.y * y) > a.Reach(x, y) + b.Reach(x, y);

        // Half the box's extent along the axis (x, y).
        float Reach(float x, float y) => Half.x * Mathf.Abs(Cos * x + Sin * y) + Half.y * Mathf.Abs(-Sin * x + Cos * y);
    }

    /// <summary>
    /// Names on the map that never overlap (NT7, owner 2026-10-26): every label set (lifts, Areas, runs, landmarks) is
    /// placed in one pass, highest priority first. Each name takes the first of its spots whose box is free, or hides; a
    /// set that doesn't yield (the lifts: "lift labels win") always shows and only blocks the rest. Within a set the
    /// caller's order decides (runs longest first, then by ID), so the same view always shows the same names, and a still
    /// camera places them just as it did the frame before. Allocates nothing per frame.
    /// </summary>
    public sealed class LabelPlacement
    {
        /// <summary>The sets' ranks: lower places first.</summary>
        public const int Lifts = 0, Areas = 1, Trails = 2, Landmarks = 3;

        readonly List<LandmarkLabelOverlay> _sets = new List<LandmarkLabelOverlay>();
        readonly List<LabelBox> _taken = new List<LabelBox>(256);

        internal void Add(LandmarkLabelOverlay set)
        {
            int at = _sets.Count;
            while (at > 0 && _sets[at - 1].Rank > set.Rank) at--;   // stable: equal ranks keep the order they came in
            _sets.Insert(at, set);
        }

        /// <summary>Call once a frame, after every set's Update and after the camera has moved.</summary>
        public void Place()
        {
            _taken.Clear();
            for (int i = 0; i < _sets.Count; i++) _sets[i].Place(_taken);
        }

        /// <summary>How many names were placed last frame (tests).</summary>
        public int Placed => _taken.Count;

        /// <summary>Whether <paramref name="box"/> overlaps none of <paramref name="taken"/>.</summary>
        public static bool Free(List<LabelBox> taken, in LabelBox box)
        {
            for (int i = 0; i < taken.Count; i++)
                if (LabelBox.Overlap(taken[i], box)) return false;
            return true;
        }
    }
}
