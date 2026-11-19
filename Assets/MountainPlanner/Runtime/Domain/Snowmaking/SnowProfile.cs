using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>
    /// Task 4.15: a pipe as one solve sees it, for the map's pressure layer, the pipe card and the water line's readouts.
    /// - **Pressure along it.** The solve knows the head at the pipe's graph nodes (its ends, its taps, a high point). Between
    ///   two of them the head falls with the friction (linearly in length, Hazen–Williams on one flow), and the pressure at a
    ///   point is that head over the ground there: psi = (H − z) ÷ 2.31. So a climb shows as the pressure falling even where
    ///   no hydrant stands.
    /// - **Its stretches.** Each stretch between two graph nodes: its flow (signed along the route), velocity and friction.
    /// </summary>
    public static class SnowProfile
    {
        /// <summary>One graph stretch of a pipe, in route order.</summary>
        public readonly struct Stretch
        {
            public readonly double FromMm, ToMm;
            /// <summary>Flow along the route (negative: against it), gpm.</summary>
            public readonly double Gpm;
            public readonly double LengthFt, VelocityFtS, FrictionPsi;
            /// <summary>Pressure at grade at either end (NaN where unknown).</summary>
            public readonly double FromPsi, ToPsi;
            public readonly int Link;

            public Stretch(double fromMm, double toMm, double gpm, double lengthFt, double velocity, double friction, double fromPsi, double toPsi, int link)
            {
                FromMm = fromMm; ToMm = toMm; Gpm = gpm; LengthFt = lengthFt; VelocityFtS = velocity; FrictionPsi = friction;
                FromPsi = fromPsi; ToPsi = toPsi; Link = link;
            }
        }

        /// <summary>The pipe's stretches in route order (empty when the solve has none: unsolved, or a pipe it doesn't have).</summary>
        public static List<Stretch> Stretches(SnowSolution sol, SnowPipe pipe)
        {
            var list = new List<Stretch>();
            if (sol == null || !sol.Solved || pipe == null) return list;
            var g = sol.Graph;
            for (int k = 0; k < g.LinkCount; k++)
            {
                if (g.LinkKind[k] != HLinkKind.Pipe || g.LinkDesign[k] != pipe.Id) continue;
                double ca = SnowGeometry.Project(pipe.Route, g.At[g.A[k]]).ChainageMm, cb = SnowGeometry.Project(pipe.Route, g.At[g.B[k]]).ChainageMm;
                double q = sol.LinkGpm[k];
                double pa = sol.Known[g.A[k]] ? sol.GradePsi[g.A[k]] : double.NaN, pb = sol.Known[g.B[k]] ? sol.GradePsi[g.B[k]] : double.NaN;
                if (cb < ca) { (ca, cb) = (cb, ca); (pa, pb) = (pb, pa); q = -q; }
                double friction = HydraulicGraph.FrictionFt(g.LengthFt[k], q, g.DiameterIn[k]) / HydraulicGraph.FtPerPsi;
                list.Add(new Stretch(ca, cb, q, g.LengthFt[k], HydraulicGraph.VelocityFtS(q, g.DiameterIn[k]), friction, pa, pb, k));
            }
            list.Sort((a, b) => a.FromMm.CompareTo(b.FromMm));
            return list;
        }

        /// <summary>
        /// Pressure at grade (psi) at each of <paramref name="chainagesMm"/> along <paramref name="pipe"/>, written to
        /// <paramref name="psi"/>; NaN where the solve has none.
        /// </summary>
        public static void PressureAlong(SnowSolution sol, SnowPipe pipe, IGroundHeights ground, IReadOnlyList<double> chainagesMm, float[] psi)
        {
            for (int i = 0; i < chainagesMm.Count; i++) psi[i] = float.NaN;
            if (sol == null || !sol.Solved || pipe == null) return;
            var g = sol.Graph;
            // The heads (ft) at the pipe's graph nodes, by chainage.
            var heads = new List<(double C, double H)>();
            for (int k = 0; k < g.LinkCount; k++)
            {
                if (g.LinkKind[k] != HLinkKind.Pipe || g.LinkDesign[k] != pipe.Id) continue;
                Add(g.A[k]);
                Add(g.B[k]);
            }
            void Add(int n)
            {
                if (!sol.Known[n]) return;
                double c = SnowGeometry.Project(pipe.Route, g.At[n]).ChainageMm;
                foreach (var h in heads) if (Math.Abs(h.C - c) < 1) return;
                heads.Add((c, g.GradeFt[n] + sol.GradePsi[n] * HydraulicGraph.FtPerPsi));
            }
            if (heads.Count == 0) return;
            heads.Sort((a, b) => a.C.CompareTo(b.C));
            for (int i = 0; i < chainagesMm.Count; i++)
            {
                double c = chainagesMm[i];
                if (!ground.TryHeightMm(SnowGeometry.At(pipe.Route, c), out int mm)) continue;
                double head;
                if (heads.Count == 1 || c <= heads[0].C) head = heads[0].H;
                else if (c >= heads[heads.Count - 1].C) head = heads[heads.Count - 1].H;
                else
                {
                    int j = 1;
                    while (heads[j].C < c) j++;
                    var a = heads[j - 1];
                    var b = heads[j];
                    head = a.H + (b.H - a.H) * (c - a.C) / Math.Max(1e-9, b.C - a.C);
                }
                psi[i] = (float)((head - mm * HydraulicGraph.FtPerMm) / HydraulicGraph.FtPerPsi);
            }
        }
    }
}
