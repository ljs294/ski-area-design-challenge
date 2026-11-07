using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.World.Lifts;
using UnityEngine;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// The view's side of posing trains to the rope (task 07, findings #2 and #6): reads one rope side's sheave train from a
    /// placed model's nested pivots (the model contract's names: eq_, yoke_, link_, block_, beam_, rocker_ and sheave_, with
    /// the side's letter) into a <see cref="SheaveTree"/> in the line's vertical plane, and turns the pivots by the pose
    /// <see cref="RopeOverSheaves"/> works out. Models without the nested pivots aren't posable; the view then draws the
    /// simple rope as before.
    /// </summary>
    public static class TrainPosing
    {
        static readonly string[] NodePrefixes = { "eq_", "block_", "beam_", "yoke_hold_", "yoke_sup_", "yoke_", "link_", "rocker_" };

        public sealed class Train
        {
            public SheaveTree Tree;
            public Transform[] Nodes;
            public Transform[] Sheaves;
            /// <summary>For a combination, which sheaves are its hold-down row; otherwise null.</summary>
            public bool[] HoldRow;
            /// <summary>The sheaves' sideways offset from the line (metres, + to the right looking up the line).</summary>
            public float Lateral;
            /// <summary>From each sheave's axle to the rope's centreline as the model was built (<see cref="RopeRadius"/>), metres;
            /// NaN where the model doesn't say.</summary>
            public double Rho = double.NaN;
        }

        /// <summary>Whether a placed model has a posable train on that side (at least one rocker pivot).</summary>
        public static bool Posable(LiftRig rig, string side)
        {
            if (rig == null || rig.Pivots == null) return false;
            foreach (var p in rig.Pivots)
                if (Key(rig, p) is string key && key.StartsWith("rocker_" + side, StringComparison.Ordinal)) return true;   // rocker_r1…, rocker_rs1 / rocker_rh1
            return false;
        }

        /// <summary>
        /// A pivot's name after <c>_pivot_</c>, as the shown head has it (LD9, drive at either end): a hidden head's pivots
        /// (inactive) are none, and the shown head's prefix (top6_) comes off, so its train reads as the default head's would.
        /// Null for a pivot that isn't one or isn't shown.
        /// </summary>
        static string Key(LiftRig rig, Transform p)
        {
            if (p == null) return null;
            int at = p.name.IndexOf("_pivot_", StringComparison.Ordinal);
            if (at < 0) return null;
            for (var x = p; x != null && x != rig.transform; x = x.parent)
                if (!x.gameObject.activeSelf) return null;
            string key = p.name.Substring(at + 7);
            if (rig.ShownHead >= 0 && rig.Heads[rig.ShownHead].Prefix is string prefix && prefix.Length > 0
                && key.StartsWith(prefix, StringComparison.Ordinal)) key = key.Substring(prefix.Length);
            return key;
        }

        /// <summary>
        /// Reads a rig's train on <paramref name="side"/> ("r" or "l", the model's own right or left) in the plane through
        /// <paramref name="origin"/> along <paramref name="dir"/> (s along it, w up). Safe ranges default to ±35° until the
        /// models carry their own.
        /// </summary>
        public static Train Read(LiftRig rig, string side, Vector3 origin, Vector3 dir, Vector3 right, bool rigid = false)
        {
            var nodes = new List<Transform>();
            var sheaves = new List<Transform>();
            foreach (var p in rig.Pivots)
            {
                string key = Key(rig, p);
                if (key == null) continue;
                if (key.StartsWith("sheave_" + side, StringComparison.Ordinal)) { sheaves.Add(p); continue; }
                foreach (var prefix in NodePrefixes)
                    if (key.StartsWith(prefix + side, StringComparison.Ordinal) || key == prefix.TrimEnd('_') + "_" + side)
                    {
                        nodes.Add(p);
                        break;
                    }
            }
            if (nodes.Count == 0 || sheaves.Count == 0) return null;
            // Parents first: by depth below the rig.
            int Depth(Transform t) { int d = 0; for (var x = t; x != null && x != rig.transform; x = x.parent) d++; return d; }
            nodes.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            int ParentOf(Transform t)
            {
                for (var x = t.parent; x != null && x != rig.transform; x = x.parent)
                {
                    int i = nodes.IndexOf(x);
                    if (i >= 0) return i;
                }
                return -1;
            }
            float S(Vector3 p) => Vector3.Dot(p - origin, dir);
            // A fixed sheave outside the train (a terminal's guide or level sheave on the structure) isn't part of it.
            sheaves.RemoveAll(x => ParentOf(x) < 0);
            if (sheaves.Count < 2) return null;
            sheaves.Sort((a, b) => S(a.position).CompareTo(S(b.position)));
            // Each pivot's safe range from the model's clash sweep (degrees about its own axis), turned into this plane's
            // sense (positive tips +s up, about Cross(dir, up)); ±35° where a model carries none. A rigid train (the drive's
            // row until its rebuild is decided) doesn't turn at all.
            var plane = Vector3.Cross(dir, Vector3.up).normalized;
            var treeNodes = new List<SheaveTree.Node>();
            foreach (var n in nodes)
            {
                double lo = -35 * Math.PI / 180, hi = 35 * Math.PI / 180;
                int at = Array.IndexOf(rig.Pivots, n);
                if (at >= 0 && rig.PivotRange != null && at < rig.PivotRange.Length && rig.PivotAxes != null && at < rig.PivotAxes.Length)
                {
                    var axis = rig.transform.TransformDirection(rig.PivotAxes[at]);
                    float sense = Vector3.Dot(axis, plane) >= 0 ? 1 : -1;
                    var r = rig.PivotRange[at] * Mathf.Deg2Rad;
                    lo = sense > 0 ? r.x : -r.y;
                    hi = sense > 0 ? r.y : -r.x;
                }
                if (rigid) lo = hi = 0;
                treeNodes.Add(new SheaveTree.Node(ParentOf(n), S(n.position), n.position.y - origin.y, lo, hi));
            }
            var treeSheaves = new List<SheaveTree.Sheave>();
            bool[] hold = null;
            for (int k = 0; k < sheaves.Count; k++)
            {
                int parent = ParentOf(sheaves[k]);
                treeSheaves.Add(new SheaveTree.Sheave(parent, S(sheaves[k].position), sheaves[k].position.y - origin.y));
                for (var x = sheaves[k].parent; x != null && x != rig.transform; x = x.parent)
                    if (Key(rig, x) is string key && (key.StartsWith("yoke_hold_", StringComparison.Ordinal) || key.StartsWith("rocker_" + side + "h", StringComparison.Ordinal)))
                    {
                        hold ??= new bool[sheaves.Count];
                        hold[k] = true;
                    }
            }
            if (hold == null && Array.Exists(rig.Pivots, p => Key(rig, p) == "block_" + side)) hold = new bool[sheaves.Count];
            float lateral = 0;
            foreach (var s in sheaves) lateral += Vector3.Dot(s.position - origin, right);
            return new Train
            {
                Tree = new SheaveTree(treeNodes, treeSheaves), Nodes = nodes.ToArray(), Sheaves = sheaves.ToArray(), HoldRow = hold,
                Lateral = lateral / sheaves.Count, Rho = RopeRadius(rig, side, sheaves, hold),
            };
        }

        /// <summary>
        /// Owner finding 2026-11-06 (the T-bar's rope stood about 7 cm off its sheaves): how far the rope's centreline runs from
        /// the axles on this side, as the model was built. Each builder sizes its own sheaves (the T-bar's grooves are 300 mm,
        /// the Sessellift chairs' rope rides at their 432 mm rims, their terminals' entry rows at 370 mm), and writes where its
        /// rope rests: a tower head's <c>rope_right</c> / <c>rope_left</c>, a terminal's <c>rope_*_hold</c> (the level rope
        /// meeting its row) or else its <c>rope_*_bw</c> (level from the wheel). The radius is that point's gap to the row:
        /// to its arc where the row is one (see <see cref="Arc"/>), else straight down or up to the nearest axle.
        /// NaN when the model has no such socket or the distance isn't a sheave's (under 5 cm or over 60 cm).
        /// </summary>
        static double RopeRadius(LiftRig rig, string side, List<Transform> sheaves, bool[] hold)
        {
            string full = side == "r" ? "right" : side == "l" ? "left" : null;
            if (full == null) return double.NaN;
            var rest = rig.HeadSocket($"rope_{full}_hold") ?? rig.HeadSocket($"rope_{full}_bw") ?? rig.HeadSocket($"rope_{full}");
            if (rest == null) return double.NaN;
            // In the model's own frame: a head pitched to the line, or leaned (the T-bar's 8°), would otherwise skew the
            // vertical gap by each sheave's distance along the train.
            var frame = rig.transform;
            var r0 = frame.InverseTransformPoint(rest.position);
            var at = sheaves.ConvertAll(s => frame.InverseTransformPoint(s.position));
            double best = double.MaxValue;
            // A row on an arc (a breakover head, a terminal's bend): its axles sit on a circle and the rope rests on the
            // concentric one, so the radius is the gap between the two; straight up from the nearest axle would read a
            // few millimetres long. That holds only where the rest point lies along the row (not a wheel's socket short of
            // it). A combination's two rows aren't one circle; nor is a straight or kinked row (every axle must sit on the
            // arc within 3 mm).
            bool oneRow = hold == null || Array.TrueForAll(hold, h => h);
            var along = Plane(r0, at[0], at[at.Count - 1]).x;
            bool onRow = along > -0.01f && along < Plane(at[at.Count - 1], at[0], at[at.Count - 1]).x + 0.01f;
            if (oneRow && onRow && at.Count >= 3 && Arc(at[0], at[at.Count / 2], at[at.Count - 1], out var centre, out double radius)
                && at.TrueForAll(p => Math.Abs(Vector2.Distance(Plane(p, at[0], at[at.Count - 1]), centre) - radius) < 0.003))
                best = Math.Abs(Vector2.Distance(Plane(r0, at[0], at[at.Count - 1]), centre) - radius);
            else
                foreach (var p in at) best = Math.Min(best, Math.Abs(p.y - r0.y));
            return best > 0.05 && best < 0.6 ? best : double.NaN;
        }

        /// <summary>A point in the vertical plane through the row from <paramref name="a"/> to <paramref name="b"/> (model frame):
        /// along the row, level, and up.</summary>
        static Vector2 Plane(Vector3 p, Vector3 a, Vector3 b)
        {
            var h = new Vector3(b.x - a.x, 0, b.z - a.z).normalized;
            return new Vector2(Vector3.Dot(p - a, h), p.y - a.y);
        }

        /// <summary>The circle through a row's first, middle and last axles in the row's plane; false for a straight row
        /// (the middle axle within a millimetre of the chord).</summary>
        static bool Arc(Vector3 first, Vector3 mid, Vector3 last, out Vector2 centre, out double radius)
        {
            Vector2 a = Plane(first, first, last), b = Plane(mid, first, last), c = Plane(last, first, last);
            centre = default;
            radius = 0;
            double cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            double chord = Vector2.Distance(a, c);
            if (chord < 1e-3 || Math.Abs(cross) / chord < 1e-3) return false;
            double a2 = a.sqrMagnitude, b2 = b.sqrMagnitude, c2 = c.sqrMagnitude, d = 2 * cross;
            double x = (a2 * (b.y - c.y) + b2 * (c.y - a.y) + c2 * (a.y - b.y)) / d;
            double y = (a2 * (c.x - b.x) + b2 * (a.x - c.x) + c2 * (b.x - a.x)) / d;
            centre = new Vector2((float)x, (float)y);
            radius = Vector2.Distance(centre, a);
            return true;
        }

        /// <summary>
        /// A terminal row that can't turn (a model without pivots on that side, LT33): one fixed sheave whose rope passes
        /// through the model's <c>rope_{side}_out</c> socket, on top of it (<paramref name="above"/>) or under it. The line
        /// still poses round it, so one fixed row never switches posing off for every tower. Null without the socket.
        /// </summary>
        public static Train Fixed(LiftRig rig, string side, bool above, double rho, Vector3 origin, Vector3 dir, Vector3 right)
        {
            var socket = rig != null ? rig.HeadSocket($"rope_{(side == "r" ? "right" : "left")}_out") : null;
            if (socket == null) return null;
            var p = socket.position;
            double s = Vector3.Dot(p - origin, dir), w = p.y - origin.y;
            var nodes = new List<SheaveTree.Node> { new SheaveTree.Node(-1, s, w, 0, 0) };
            var sheaves = new List<SheaveTree.Sheave> { new SheaveTree.Sheave(0, s, above ? w - rho : w + rho) };
            return new Train
            {
                Tree = new SheaveTree(nodes, sheaves), Nodes = new[] { socket }, Sheaves = new[] { socket }, HoldRow = null,
                Lateral = Vector3.Dot(p - origin, right),
            };
        }

        /// <summary>Turns each node by its pose, parents first, about the axis across the line (positive tips +s up).</summary>
        public static void Apply(Train train, RopeOverSheaves.Pose pose, Vector3 dir)
        {
            var axis = Vector3.Cross(dir, Vector3.up).normalized;
            for (int i = 0; i < train.Nodes.Length; i++)
            {
                var t = train.Nodes[i];
                t.rotation = Quaternion.AngleAxis((float)(pose.Local[i] * Mathf.Rad2Deg), axis) * t.rotation;
            }
        }
    }
}
