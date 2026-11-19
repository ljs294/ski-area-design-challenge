using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>What a point on the map snaps to for the snowmaking tools (HY-02: connections are explicit).</summary>
    public enum SnowSnapKind : byte { None, Node, House, Pipe, Water }

    public readonly struct SnowSnapHit
    {
        public readonly SnowSnapKind Kind;
        /// <summary>The node (a house's own node for <see cref="SnowSnapKind.House"/>) or the pipe.</summary>
        public readonly SnowId Id;
        public readonly PointMm At;
        /// <summary>The water's source (<see cref="SnowSnapKind.Water"/>), and whether a pipe may fill it.</summary>
        public readonly ulong Source;
        public readonly bool Fillable;
        public readonly string Label;

        public SnowSnapHit(SnowSnapKind kind, SnowId id, PointMm at, string label, ulong source = 0, bool fillable = false)
        {
            Kind = kind; Id = id; At = at; Label = label ?? ""; Source = source; Fillable = fillable;
        }

        public bool Any => Kind != SnowSnapKind.None;

        /// <summary>A free end at a point (no connection).</summary>
        public static SnowSnapHit FreeAt(PointMm p) => new SnowSnapHit(SnowSnapKind.None, SnowId.None, p, "");
    }

    /// <summary>
    /// Task 4.15's snapping and words for the snowmaking tools: a pipe end lands on a node (not a pump), a pump house, a
    /// pipe (a new junction) or a source's water (an intake, or an outlet that fills it), nearest first within the tools'
    /// snap reach; and the labels and figures the cards share (H12, J3, P1, "164 psi").
    /// </summary>
    public static class SnowSnap
    {
        /// <summary>The nearest connection for a pipe end at <paramref name="at"/> within <paramref name="reachMm"/>.</summary>
        public static SnowSnapHit End(ToolContext ctx, SnowNetwork net, PointMm at, int reachMm)
        {
            long best = (long)reachMm * reachMm;
            SnowSnapHit hit = default;
            foreach (var n in net.Nodes)
            {
                if (n.Kind == SnowNodeKind.Pump) continue;   // pipes meet at junctions, never at a pump
                long d = n.At.DistanceSquared(at);
                if (n.Kind == SnowNodeKind.House)
                {
                    // Anywhere on its footprint connects to it.
                    var h = net.FindHouse(n.House);
                    if (h != null && InHouse(h, at)) d = 0;
                }
                if (d < best || d == best && hit.Kind == SnowSnapKind.None)
                {
                    best = d;
                    hit = n.Kind == SnowNodeKind.House
                        ? new SnowSnapHit(SnowSnapKind.House, n.Id, n.At, net.FindHouse(n.House)?.Name ?? "Pump house")
                        : new SnowSnapHit(SnowSnapKind.Node, n.Id, n.At, Label(net, n));
                }
            }
            if (hit.Any) return hit;
            foreach (var p in net.Pipes)
            {
                var on = SnowGeometry.Project(p.Route, at);
                long d = (long)(on.DistanceMm * on.DistanceMm);
                if (d >= best) continue;
                best = d;
                hit = new SnowSnapHit(SnowSnapKind.Pipe, p.Id, on.Point, Name(p));
            }
            if (hit.Any) return hit;
            var water = ctx.SourceNear?.Invoke(at, 0);
            if (water.HasValue && water.Value.DistanceMetres <= 0.01)
                return new SnowSnapHit(SnowSnapKind.Water, SnowId.None, at, water.Value.Name, water.Value.Id, water.Value.Fillable);
            return default;
        }

        /// <summary>A pipe under a point within the reach (for the inline pump and Fill hydrants), else none.</summary>
        public static SnowSnapHit Pipe(SnowNetwork net, PointMm at, int reachMm)
        {
            long best = (long)reachMm * reachMm;
            SnowSnapHit hit = default;
            foreach (var p in net.Pipes)
            {
                var on = SnowGeometry.Project(p.Route, at);
                long d = (long)(on.DistanceMm * on.DistanceMm);
                if (d >= best) continue;
                best = d;
                hit = new SnowSnapHit(SnowSnapKind.Pipe, p.Id, on.Point, Name(p));
            }
            return hit;
        }

        public static bool InHouse(PumpHouse h, PointMm p)
        {
            var c = h.Corners();
            bool inside = false;
            for (int i = 0, j = c.Length - 1; i < c.Length; j = i++)
                if ((c[i].Y > p.Y) != (c[j].Y > p.Y) && p.X < (double)(c[j].X - c[i].X) * (p.Y - c[i].Y) / (c[j].Y - c[i].Y) + c[i].X) inside = !inside;
            return inside;
        }

        /// <summary>A node's short name on the map and in the cards: H12, J3, P1, I1, O1, or its house's name.</summary>
        public static string Label(SnowNetwork net, SnowNode n)
        {
            switch (n.Kind)
            {
                case SnowNodeKind.Hydrant: return "H" + n.Label;
                case SnowNodeKind.Pump: return "P" + n.Label;
                case SnowNodeKind.Intake: return "Intake " + n.Label;
                case SnowNodeKind.Outlet: return "Outlet " + n.Label;
                case SnowNodeKind.House: return net.FindHouse(n.House)?.Name ?? "Pump house";
                default: return "J" + n.Label;
            }
        }

        /// <summary>Why a new hydrant or pump can't go at a chainage on a pipe (10 ft from its ends and its taps), or null.</summary>
        public static string TapProblem(SnowNetwork net, SnowPipe host, double chainageMm)
        {
            double len = SnowGeometry.Length(host.Route);
            if (chainageMm < SnowRules.TapGapMm || chainageMm > len - SnowRules.TapGapMm) return "Too close to the line's end: put it at least 10 ft along.";
            foreach (var t in net.TapsOn(host.Id))
                if (Math.Abs(SnowGeometry.Project(host.Route, t.At).ChainageMm - chainageMm) < SnowRules.TapGapMm)
                    return "A hydrant or pump is within 10 ft.";
            return null;
        }

        public static string Name(SnowPipe p) => string.IsNullOrEmpty(p.Name) ? "Water line" : p.Name;

        public static string Psi(double psi) => double.IsNaN(psi) ? "—" : Math.Round(psi).ToString("N0", CultureInfo.InvariantCulture) + " psi";
        public static string Inches(int d) => d + " in";
        public static string Speed(double ftS) => Presentation.DisplayUnits.Current == Domain.Measure.UnitSystem.Imperial ? ftS.ToString("0.0", CultureInfo.InvariantCulture) + " ft/s" : (ftS * 0.3048).ToString("0.0", CultureInfo.InvariantCulture) + " m/s";
        public static string Count(int n, string one, string many = null) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");

        /// <summary>Design mm to the preview's local x/z metres.</summary>
        public static Vector2 Xz(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);

        /// <summary>The house footprint as a closed preview line.</summary>
        public static int Footprint(PointMm centre, int bearingCentideg, Vector2[] into)
        {
            double yaw = bearingCentideg / 18_000.0 * Math.PI;
            double ux = Math.Sin(yaw), uy = Math.Cos(yaw);   // along the length
            double hl = SnowRules.HouseLengthMm / 2000.0, hw = SnowRules.HouseWidthMm / 2000.0;
            var c = Xz(centre);
            var u = new Vector2((float)ux, (float)uy);
            var s = new Vector2((float)uy, (float)-ux);
            into[0] = c + u * (float)hl + s * (float)hw;
            into[1] = c + u * (float)hl - s * (float)hw;
            into[2] = c - u * (float)hl - s * (float)hw;
            into[3] = c - u * (float)hl + s * (float)hw;
            return 4;
        }

        /// <summary>The pieces a network has that another doesn't (a command's new pipe, guns, nodes), in id order.</summary>
        public static List<SnowId> Added(SnowNetwork before, SnowNetwork after)
        {
            var ids = new List<SnowId>();
            foreach (var p in after.Pipes) if (before.FindPipe(p.Id) == null) ids.Add(p.Id);
            foreach (var n in after.Nodes) if (before.FindNode(n.Id) == null) ids.Add(n.Id);
            foreach (var h in after.Houses) if (before.FindHouse(h.Id) == null) ids.Add(h.Id);
            foreach (var g in after.Guns) if (before.FindGun(g.Id) == null) ids.Add(g.Id);
            return ids;
        }
    }
}
