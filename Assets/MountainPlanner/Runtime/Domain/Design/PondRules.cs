using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 4.14's pond and dam constants (PD-02, PD-05…07; MapLibre's, kept) and the checks a command makes without the
    /// ground (the shape, the count, the overlaps). What needs the ground (the berm's height, the flood) is the earthwork's
    /// (<see cref="PondEarthwork"/>, <see cref="DamEarthwork"/>), shown by the tools before Build.
    /// </summary>
    public static class PondRules
    {
        /// <summary>PD-02: 2 ft (0.6 m) of freeboard between full pool and the crest.</summary>
        public const int FreeboardMm = 600;
        /// <summary>PD-02, PD-06: the crest (deck) is 11.5 ft (3.5 m) wide.</summary>
        public const int CrestMm = 3500;
        /// <summary>PD-02: the water face runs 3:1, the outer (downstream) face 2:1, a cut 1:1.</summary>
        public const int WetRun = 3, DryRun = 2, CutRun = 1;
        /// <summary>PD-02: the berm and the cut reach at most 820 ft (250 m) from the ring.</summary>
        public const int ReachMm = 250_000;
        /// <summary>PD-02: a pond is dug 0–50 ft (15 m) under full pool.</summary>
        public const int ExcavationMaxMm = 15_000;
        /// <summary>The excavation a new pond starts with: 10 ft.</summary>
        public const int ExcavationDefaultMm = 3_048;
        /// <summary>PD-02: a berm stands at most 65 ft (20 m) ("dam a stream instead").</summary>
        public const int BermMaxMm = 20_000;
        /// <summary>PD-02: at least 1,100 ft² (100 m²) of water.</summary>
        public const int MinAreaSquareMetres = 100;
        /// <summary>PD-05: the far bank snaps within 250 ft (75 m) and at least 16 ft (5 m) from the first.</summary>
        public const int SnapMm = 75_000, DamMinLengthMm = 5_000;
        /// <summary>A dam's line is at most 3,300 ft (1 km) long.</summary>
        public const int DamMaxLengthMm = 1_000_000;
        /// <summary>PD-05: the far bank is on the full-pool contour, within 3 in.</summary>
        public const int ContourToleranceMm = 75;
        /// <summary>PD-06: a dam stands at most 130 ft (40 m).</summary>
        public const int DamMaxMm = 40_000;
        /// <summary>PD-06: how far a dam's flood may run from the dam before it is refused as reaching the edge, 1,640 ft (500 m).</summary>
        public const int DamReachMm = 500_000;
        /// <summary>PD-06's leak check: flooded ground this close downstream of the dam has come round an end, 100 ft (30 m).</summary>
        public const int LeakMm = 30_000;
        /// <summary>PD-07: the spillway notch is 20 ft (6.1 m) wide, down to full pool.</summary>
        public const int SpillwayMm = 6_100;
        /// <summary>A pond's ring holds at most this many points.</summary>
        public const int MaxRingPoints = 256;
        /// <summary>WaterBodyTable's places for ponds and dams together (Pacing.MaxPonds).</summary>
        public const int MaxWater = 32;
        /// <summary>WS-04: a spring of at most 5,000 gpm.</summary>
        public const int SpringMaxGpm = 5_000;
        /// <summary>WS-03: a stream width override, 1–1,000 ft, whole centimetres.</summary>
        public const int WidthMaxCm = 30_480;
        /// <summary>LK-02: a lake's depth override, 1–300 ft, tenths of a foot.</summary>
        public const int LakeDepthMaxTenthsFt = 3_000;
        /// <summary>PD-11 v1: water stored per volume of earth moved under 1:10 is refused, under 1:1 warned.</summary>
        public const double WorthRefuse = 0.1, WorthWarn = 1.0;
        /// <summary>PD-12: a dam's first bank is within 250 ft (76.2 m) of a stream.</summary>
        public const int DamStartReachMm = 76_200;
        /// <summary>PD-13: full pool stands at least 5 ft (1.524 m) over the lowest ground on the dam's line.</summary>
        public const int DamMinPoolOverBedMm = 1_524;

        /// <summary>
        /// PD-11 v1: water stored against earth moved, both in cubic metres. Null when it is worth building; otherwise the
        /// refusal (<paramref name="refused"/>) or the amber warning.
        /// </summary>
        public static string WorthProblem(string what, double water, double earth, out bool refused)
        {
            refused = false;
            if (earth <= 1) return null;
            double ratio = water / earth;
            if (ratio >= WorthWarn) return null;
            refused = ratio < WorthRefuse;
            if (water <= 0) return $"This {what} moves earth and holds no water.";
            double times = earth / water;
            string n = times >= 10 ? Math.Round(times).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                                   : times.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return refused
                ? $"This {what} moves {n}× more earth than it holds water: find a deeper, narrower spot."
                : $"This {what} moves {n}× more earth than it holds water.";
        }

        /// <summary>Full pool stays within ±10 km of the datum, like a stroke's targets.</summary>
        const int ElevationLimitMm = 10_000_000;

        /// <summary>PD-03: the suggested pool, the mean ground inside rounded up to 0.5 m, plus 1 m.</summary>
        public static int SuggestedPoolMm(double meanGroundMetres) => checked((int)(Math.Ceiling(meanGroundMetres / 0.5) * 500) + 1000);

        /// <summary>PD-02: the ring alone, without the ground: points, simple, big enough.</summary>
        public static string RingProblem(IReadOnlyList<PointMm> ring)
        {
            if (ring == null || ring.Count < 3) return "Draw a shoreline of at least three points.";
            if (ring.Count > MaxRingPoints) return $"A shoreline holds at most {MaxRingPoints} points.";
            foreach (var p in ring) if (DesignRules.PointProblem(p) is string p0) return p0;
            var r = RingDistance.Of(ring);
            if (!r.Simple()) return "The shoreline crosses itself.";
            if (r.Area() < MinAreaSquareMetres) return "Too small: draw at least 1,100 ft² of water.";
            return null;
        }

        public static string PondProblem(Pond p)
        {
            if (p.Id.IsNone) return "A pond has no ID.";
            if (DesignRules.NameProblem(p.Name) is string n) return n;
            if (RingProblem(p.Ring) is string r) return r;
            if (p.FullPoolMm < -ElevationLimitMm || p.FullPoolMm > ElevationLimitMm) return "A pond's full pool is out of range.";
            if (p.ExcavationMm < 0 || p.ExcavationMm > ExcavationMaxMm) return "A pond is dug 0–50 ft deep.";
            if (p.SpringGpm < 0 || p.SpringGpm > SpringMaxGpm) return "A spring gives 0–5,000 gpm.";
            if (p.Fill != PondFill.Natural && p.Fill != PondFill.Pumped) return "A pond's fill isn't one this build knows.";
            return null;
        }

        public static string DamProblem(Dam d)
        {
            if (d.Id.IsNone) return "A dam has no ID.";
            if (DesignRules.NameProblem(d.Name) is string n) return n;
            if (DesignRules.PointProblem(d.BankA) is string a) return a;
            if (DesignRules.PointProblem(d.BankB) is string b) return b;
            long len2 = d.BankA.DistanceSquared(d.BankB);
            if (len2 < (long)DamMinLengthMm * DamMinLengthMm) return "A dam's banks are at least 16 ft apart.";
            if (len2 > (long)DamMaxLengthMm * DamMaxLengthMm) return "A dam is at most 3,300 ft long.";
            if (d.FullPoolMm < -ElevationLimitMm || d.FullPoolMm > ElevationLimitMm) return "A dam's full pool is out of range.";
            if (d.Spill != SpillBank.First && d.Spill != SpillBank.Far) return "A dam's spillway bank isn't one this build knows.";
            return null;
        }

        public static string LakeProblem(LakeRecord s) =>
            s.Lake == 0 ? "A lake record names no lake." :
            s.DepthTenthsFt < 0 || s.DepthTenthsFt > LakeDepthMaxTenthsFt ? "A lake's depth is 0.1–300 ft." :
            (s.Name ?? "").Length > 0 && DesignRules.NameProblem(s.Name) is string n ? n : null;

        /// <summary>LK-10, WS-03: a stream record's way, name and width.</summary>
        public static string StreamProblem(StreamRecord s) =>
            s.Way == 0 ? "A stream record names no stream." :
            s.WidthCm < 0 || s.WidthCm > WidthMaxCm ? "A stream is 0–1,000 ft wide." :
            (s.Name ?? "").Length > 0 && DesignRules.NameProblem(s.Name) is string n ? n : null;

        /// <summary>
        /// Ponds and dams own their footprints (task 4.14): a pond's ring may not overlap another pond's, and a dam's line
        /// may not cross or sit in a pond. Checked on the shapes alone; the berms are the earthwork's.
        /// </summary>
        public static string OverlapProblem(DesignDocument doc, Pond pond, Dam dam)
        {
            var mine = pond != null ? RingDistance.Of(pond.Ring) : null;
            foreach (var other in doc.Ponds)
            {
                if (pond != null && other.Id == pond.Id) continue;
                var r = RingDistance.Of(other.Ring);
                if (mine != null && mine.Overlaps(r)) return $"This shoreline overlaps {other.Name}.";
                if (dam != null && (r.Crosses(dam.BankA.XMetres, dam.BankA.YMetres, dam.BankB.XMetres, dam.BankB.YMetres) || r.Inside(dam.BankA.XMetres, dam.BankA.YMetres)))
                    return $"This dam runs into {other.Name}.";
            }
            foreach (var other in doc.Dams)
            {
                if (dam != null && other.Id == dam.Id) continue;
                if (mine != null && (mine.Crosses(other.BankA.XMetres, other.BankA.YMetres, other.BankB.XMetres, other.BankB.YMetres) || mine.Inside(other.BankA.XMetres, other.BankA.YMetres)))
                    return $"This shoreline runs into {other.Name}.";
                if (dam != null && RingDistance.SegmentsMeet(dam.BankA.XMetres, dam.BankA.YMetres, dam.BankB.XMetres, dam.BankB.YMetres,
                                                             other.BankA.XMetres, other.BankA.YMetres, other.BankB.XMetres, other.BankB.YMetres))
                    return $"This dam crosses {other.Name}.";
            }
            return null;
        }
    }
}
