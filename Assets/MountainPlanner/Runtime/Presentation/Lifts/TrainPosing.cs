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
        }

        /// <summary>Whether a placed model has a posable train on that side (at least one rocker pivot).</summary>
        public static bool Posable(LiftRig rig, string side) => rig != null && rig.Pivot("rocker_" + side + "1") != null;

        /// <summary>
        /// Reads a rig's train on <paramref name="side"/> ("r" or "l", the model's own right or left) in the plane through
        /// <paramref name="origin"/> along <paramref name="dir"/> (s along it, w up). Safe ranges default to ±35° until the
        /// models carry their own.
        /// </summary>
        public static Train Read(LiftRig rig, string side, Vector3 origin, Vector3 dir, Vector3 right)
        {
            var nodes = new List<Transform>();
            var sheaves = new List<Transform>();
            foreach (var p in rig.Pivots)
            {
                if (p == null) continue;
                int at = p.name.IndexOf("_pivot_", StringComparison.Ordinal);
                if (at < 0) continue;
                string key = p.name.Substring(at + 7);
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
            sheaves.Sort((a, b) => S(a.position).CompareTo(S(b.position)));
            double range = 35 * Math.PI / 180;
            var treeNodes = new List<SheaveTree.Node>();
            foreach (var n in nodes) treeNodes.Add(new SheaveTree.Node(ParentOf(n), S(n.position), n.position.y - origin.y, -range, range));
            var treeSheaves = new List<SheaveTree.Sheave>();
            bool[] hold = null;
            for (int k = 0; k < sheaves.Count; k++)
            {
                int parent = ParentOf(sheaves[k]);
                if (parent < 0) return null;   // a sheave outside the train's tree: not posable
                treeSheaves.Add(new SheaveTree.Sheave(parent, S(sheaves[k].position), sheaves[k].position.y - origin.y));
                for (var x = sheaves[k].parent; x != null && x != rig.transform; x = x.parent)
                    if (x.name.Contains("_pivot_yoke_hold_"))
                    {
                        hold ??= new bool[sheaves.Count];
                        hold[k] = true;
                    }
            }
            if (hold == null && rig.Pivot("block_" + side) != null) hold = new bool[sheaves.Count];
            float lateral = 0;
            foreach (var s in sheaves) lateral += Vector3.Dot(s.position - origin, right);
            return new Train
            {
                Tree = new SheaveTree(treeNodes, treeSheaves), Nodes = nodes.ToArray(), Sheaves = sheaves.ToArray(), HoldRow = hold,
                Lateral = lateral / sheaves.Count,
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
