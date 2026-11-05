using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// CL-10 (owner, 2026-11-02, finding F1: crowns outside the corridor reached the chairs; chosen after an A/B on Gilford):
    /// besides the corridor's ground (CL-03 v2), a tree whose crown reaches a lift's chair lane is cut. The lane follows
    /// ANSI B77.1 (2006 §3.1.1.4: vegetation cut back at least 5 ft from the rope and carriers, and no contact with a carrier
    /// swinging in wind): it runs bullwheel to bullwheel, as wide as the chairs either side of the line, swung
    /// <see cref="SwingDegrees"/> on their hangers, plus <see cref="MarginMetres"/>. Derived from the built lifts each time
    /// the design changes, by the view: nothing is saved, and lifts already built follow it.
    /// </summary>
    public sealed class LiftLanes
    {
        /// <summary>ANSI B77.1's 5 ft cut-back past the chairs.</summary>
        public const double MarginMetres = 1.524;
        /// <summary>The chairs' sideways swing in service (B77.1 2011: guards hold 8–15°).</summary>
        public const double SwingDegrees = 15;

        /// <summary>A type's lane, either side of the line: half the gauge, half a chair, its swing and the 5 ft margin
        /// (about 17½ ft for the quad: 35 ft across, inside the 70 ft corridor).</summary>
        public static double HalfWidth(LiftType type) =>
            type.GaugeMetres / 2 + type.ChairHalfWidthMetres + type.ChairDropMetres * Math.Sin(SwingDegrees * Math.PI / 180) + MarginMetres;

        readonly List<(double Bx, double By, double Dx, double Dy, double Length, double Half)> _lanes =
            new List<(double, double, double, double, double, double)>();

        public int Count => _lanes.Count;

        /// <summary>The chair lanes of every built lift in <paramref name="doc"/>, in the site's frame.</summary>
        public static LiftLanes Of(DesignDocument doc, EarthworkSite site)
        {
            if (doc == null) return null;
            var lanes = new LiftLanes();
            foreach (var lift in doc.Lifts)
            {
                if (lift.Stage != WorkStage.Built) continue;
                var type = LiftType.Of(lift);
                site.PointOf(lift.Bottom, out double bx, out double by);
                site.PointOf(lift.Top, out double tx, out double ty);
                double dx = tx - bx, dy = ty - by, length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= 0) continue;
                lanes._lanes.Add((bx, by, dx / length, dy / length, length, HalfWidth(type)));
            }
            return lanes;
        }

        /// <summary>Whether two sets hold the same lanes (null: none), so an edit that moves no lift re-checks no trees.</summary>
        public static bool Same(LiftLanes a, LiftLanes b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a._lanes.Count != b._lanes.Count) return false;
            for (int i = 0; i < a._lanes.Count; i++) if (!a._lanes[i].Equals(b._lanes[i])) return false;
            return true;
        }

        /// <summary>Whether any lane, grown by <paramref name="grow"/> metres, meets the box (west, south, east, north).</summary>
        public bool Near(double west, double south, double east, double north, double grow)
        {
            foreach (var (bx, by, dx, dy, length, half) in _lanes)
            {
                double r = half + grow, tx = bx + dx * length, ty = by + dy * length;
                if (Math.Max(bx, tx) + r < west || Math.Min(bx, tx) - r > east || Math.Max(by, ty) + r < south || Math.Min(by, ty) - r > north) continue;
                return true;
            }
            return false;
        }

        /// <summary>Whether a crown <paramref name="radius"/> metres across from a trunk at (x, y) reaches any lane.</summary>
        public bool Reaches(double x, double y, double radius)
        {
            foreach (var (bx, by, dx, dy, length, half) in _lanes)
            {
                double px = x - bx, py = y - by;
                double along = px * dx + py * dy;
                if (along < -radius || along > length + radius) continue;
                double across = Math.Abs(-px * dy + py * dx);
                if (across < half + radius) return true;
            }
            return false;
        }
    }
}
