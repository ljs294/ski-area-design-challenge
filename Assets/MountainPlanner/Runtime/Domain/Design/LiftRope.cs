using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A fixed-grip type's numbers (LC-01, LC-02 as tuned at task 07, decision SK8), in SI units.</summary>
    public sealed class LiftType
    {
        public string CatalogId { get; }
        public int Seats { get; }
        public double SpeedMetresPerSecond { get; }
        public double ChairSpacingMetres { get; }
        public double RopeKgPerMetre { get; }
        public double RatedTensionNewtons { get; }
        public double CounterweightNewtons { get; }
        public double ChairKg { get; }
        public double RiderKg { get; }
        /// <summary>From the rope down to the chair's lowest point.</summary>
        public double ChairDropMetres { get; }
        /// <summary>Between the two ropes, bullwheel pitch diameter.</summary>
        public double GaugeMetres { get; }
        public double RopeDiameterMetres { get; }
        /// <summary>The line sheaves' radius to their flange tops (17 in, 432 mm across; the models).</summary>
        public const double SheaveRadiusMetres = 0.216;
        /// <summary>
        /// TW-16: the bottom (return) terminal's built-in hold-down row turns the rope up from level in the loading area;
        /// it leaves at no more than this (radians; 15.9° on the Sessellift's station arc).
        /// </summary>
        public double BottomExitMax { get; } = 15.9 * Math.PI / 180;
        /// <summary>TW-16: the top (drive) terminal's built-in 4-sheave support row brings the rope level to the bullwheel;
        /// it can take at most 4 × 4.5° = 18° (LC-02) of arriving rope angle.</summary>
        public double TopEntryMax { get; } = 18 * Math.PI / 180;

        public LiftType(string catalogId, int seats, double speed, double spacing, double ropeKgPerMetre, double rated,
                        double counterweight, double chairKg, double riderKg, double chairDrop, double gauge, double ropeDiameter)
        {
            CatalogId = catalogId;
            Seats = seats;
            SpeedMetresPerSecond = speed;
            ChairSpacingMetres = spacing;
            RopeKgPerMetre = ropeKgPerMetre;
            RatedTensionNewtons = rated;
            CounterweightNewtons = counterweight;
            ChairKg = chairKg;
            RiderKg = riderKg;
            ChairDropMetres = chairDrop;
            GaugeMetres = gauge;
            RopeDiameterMetres = ropeDiameter;
        }

        public const double Gravity = 9.80665;

        /// <summary>RP-03: the uphill side, full, in newtons per metre.</summary>
        public double LoadedNewtonsPerMetre => (RopeKgPerMetre + (ChairKg + Seats * RiderKg) / ChairSpacingMetres) * Gravity;
        public double EmptyNewtonsPerMetre => (RopeKgPerMetre + ChairKg / ChairSpacingMetres) * Gravity;
        /// <summary>LC-03, people per hour.</summary>
        public double Capacity => Seats * SpeedMetresPerSecond * 3600 / ChairSpacingMetres;

        /// <summary>
        /// LC-02 v2 for both quads (rope 1¾ in, owner, task 07, SK17): 450 fpm, 45 ft spacing, a 1¾ in (44.5 mm) 6x19
        /// fibre-core rope, 5.15 lb/ft (7.66 kg/m), breaking strength 248,000 lbf (1,104 kN, the 1⅝ in table value scaled by
        /// diameter²), rated at a fifth, 49,600 lbf (220.8 kN); counterweight 18,000 lbf per side; 350 lb chairs, 170 lb
        /// riders; the chair hangs 8.7 ft (2.66 m, measured on the model) below the rope; the Sessellift's gauge is 13.5 ft
        /// (4.12 m), the Monta's 16.06 ft (4.895 m). RP-05's rise is now (49,600 − 18,000) ÷ 28.0 lb/ft, about 1,130 ft.
        /// </summary>
        public static LiftType Of(string catalogId)
        {
            double gauge = catalogId == LiftCatalogIds.MontaFg4 ? 4.895 : 4.12;
            return new LiftType(catalogId, 4, 2.286, 13.716, 7.66, 220_800, 80_070, 158.8, 77.1, 2.66, gauge, 0.04445);
        }

        /// <summary>
        /// The terminals' footprints (CL-09, LF-12), measured on the Sessellift's models (task 07): metres behind the
        /// bullwheel (away from the line), in front of it and either side. The Monta takes the same until its own are
        /// measured.
        /// </summary>
        public double BottomBehind { get; } = 2.3;
        public double BottomAhead { get; } = 8.1;
        public double BottomHalfWidth { get; } = 3.0;
        public double TopBehind { get; } = 2.5;
        public double TopAhead { get; } = 7.2;
        public double TopHalfWidth { get; } = 3.3;

        /// <summary>
        /// Where the rope leaves each terminal's row (the models' rope_*_out sockets): metres along the line from the
        /// bullwheel, and above the terminal's origin (its load or unload level). The view measures the real sockets and
        /// may pass its own; these are the Sessellift's.
        /// </summary>
        public double BottomExitAlong { get; set; } = 5.889;
        public double BottomExitRise { get; set; } = 3.543;
        public double TopExitBack { get; set; } = 3.1;
        public double TopExitRise { get; set; } = 3.039;
    }

    /// <summary>
    /// The style tile's rope (task 07, decision SK6): each span a catenary between the rope's support points, with RP-03's
    /// tension (the counterweight at the bottom, plus the load per metre times the height gained), for the loaded uphill
    /// side and the empty downhill side; the head each tower needs from the rope's bend (a simple form of TW-04); TW-14's
    /// lean; TW-01's clearance. Task 09 replaces the support points with RP-02's sheave trains and tangent solve, and the
    /// head choice with TW-04's per-sheave forces.
    ///
    /// The profile frame: s is the horizontal distance from the bottom bullwheel along the line, y the elevation (both
    /// metres). Terminal rope exits are given by the caller (the models' sockets).
    /// </summary>
    public sealed class LiftRope
    {
        public enum Head : byte { S4, S6, B8, D8, C8, TooSharp }

        public readonly struct TowerSolution
        {
            /// <summary>Where the tower's base stands, along the line.</summary>
            public readonly double BaseS;
            public readonly double BaseY;
            /// <summary>Where the rope passes the head (the lean moves it along the line).</summary>
            public readonly double RopeS;
            public readonly double RopeY;
            /// <summary>The mast's lean, radians, positive leaning down the line (toward the bottom terminal).</summary>
            public readonly double Lean;
            /// <summary>The rope's bend over the head, radians, for the loaded and the empty side: positive turns up (pulls up).</summary>
            public readonly double BendLoaded, BendEmpty;
            public readonly Head Head;

            public TowerSolution(double baseS, double baseY, double ropeS, double ropeY, double lean, double bendLoaded, double bendEmpty, Head head)
            {
                BaseS = baseS;
                BaseY = baseY;
                RopeS = ropeS;
                RopeY = ropeY;
                Lean = lean;
                BendLoaded = bendLoaded;
                BendEmpty = bendEmpty;
                Head = head;
            }
        }

        /// <summary>One catenary span, y = a·cosh((s − s0)/a) + c, from S0 to S1.</summary>
        public readonly struct Span
        {
            public readonly double From, To, A, Centre, C;
            public Span(double from, double to, double a, double centre, double c) { From = from; To = to; A = a; Centre = centre; C = c; }
            public double Y(double s) => A * Math.Cosh((s - Centre) / A) + C;
            public double Slope(double s) => Math.Sinh((s - Centre) / A);
        }

        public LiftType Type { get; }
        public double LengthMetres { get; }
        public IReadOnlyList<TowerSolution> Towers => _towers;
        public IReadOnlyList<Span> Loaded => _loaded;
        public IReadOnlyList<Span> Empty => _empty;
        /// <summary>RP-05: the highest tension, at the top of the loaded side, as a share of the rating.</summary>
        public double TopTensionShare { get; }
        /// <summary>TW-01: the lowest chair-bottom clearance over the ground outside the load and unload zones, metres.</summary>
        public double LowestClearance { get; }
        public double LowestClearanceS { get; }
        /// <summary>TW-16: the rope's steepest angle leaving the bottom terminal and arriving at the top one (either load
        /// case), radians, positive climbing.</summary>
        public double BottomExitAngle { get; }
        public double TopEntryAngle { get; }
        /// <summary>TW-16: both terminals' rows can turn the rope to level.</summary>
        public bool TerminalsOk => Math.Abs(BottomExitAngle) <= Type.BottomExitMax + 1e-9 && Math.Abs(TopEntryAngle) <= Type.TopEntryMax + 1e-9;

        readonly TowerSolution[] _towers;
        readonly Span[] _loaded, _empty;

        /// <summary>TW-14's cap: 15°.</summary>
        public const double MaxLean = 15 * Math.PI / 180;
        /// <summary>TW-14's leans come in 2.5° steps, one tower base model each.</summary>
        public const double LeanStep = 2.5 * Math.PI / 180;
        /// <summary>TW-14 v2: a tower on a Leaned lift leans where the rope crosses its head at more than 10°.</summary>
        public const double LeanFrom = 10 * Math.PI / 180;
        /// <summary>LC-02's bend per sheave: 4.5°.</summary>
        public const double BendPerSheave = 4.5 * Math.PI / 180;
        /// <summary>TW-01: 15 ft.</summary>
        public const double Clearance = 4.572;

        /// <summary>
        /// Solves the rope for a straight line from <paramref name="bottomX"/>, <paramref name="bottomY"/> (the bottom
        /// bullwheel, local metres) to the top one, over <paramref name="ground"/>. The rope leaves the bottom terminal at
        /// (bottomExitS, bottomExitY) and reaches the top one at (length − topExitBack, topExitY).
        /// </summary>
        /// <summary>
        /// TW-16 for now (owner, task 07): where the ground under the bottom terminal is too steep for its hold-down row,
        /// the terminal stands higher, in 1 ft steps up to 30 ft, until the rope leaves within the row's limit. Derived, not
        /// stored, until task 09 makes terminal elevation an authored value (save v2).
        /// </summary>
        public static LiftRope SolveRaised(LiftType type, IHeightSource ground, double bottomX, double bottomY, double topX, double topY,
                                           IReadOnlyList<Tower> towers, TowerStance stance,
                                           double bottomExitS, double bottomExitY, double topExitBack, double topExitY, out double raise)
        {
            raise = 0;
            var rope = Solve(type, ground, bottomX, bottomY, topX, topY, towers, stance, bottomExitS, bottomExitY, topExitBack, topExitY);
            while (rope.BottomExitAngle > type.BottomExitMax && raise < MaxTerminalRaise - 1e-9)
            {
                raise += RaiseStep;
                rope = Solve(type, ground, bottomX, bottomY, topX, topY, towers, stance, bottomExitS, bottomExitY + raise, topExitBack, topExitY);
            }
            return rope;
        }

        /// <summary>TW-16's terminal raise: 1 ft steps, at most 30 ft.</summary>
        public const double RaiseStep = 0.3048, MaxTerminalRaise = 9.144;

        public static LiftRope Solve(LiftType type, IHeightSource ground, double bottomX, double bottomY, double topX, double topY,
                                     IReadOnlyList<Tower> towers, TowerStance stance,
                                     double bottomExitS, double bottomExitY, double topExitBack, double topExitY)
        {
            return new LiftRope(type, ground, bottomX, bottomY, topX, topY, towers, stance, bottomExitS, bottomExitY, topExitBack, topExitY);
        }

        LiftRope(LiftType type, IHeightSource ground, double bx, double by, double tx, double ty, IReadOnlyList<Tower> towers,
                 TowerStance stance, double bottomExitS, double bottomExitY, double topExitBack, double topExitY)
        {
            Type = type;
            double dx = tx - bx, dy = ty - by;
            LengthMetres = Math.Sqrt(dx * dx + dy * dy);
            double ex = dx / LengthMetres, ey = dy / LengthMetres;
            double GroundAt(double s) => ground.At(bx + ex * s, by + ey * s);

            int n = towers.Count;
            _towers = new TowerSolution[n];
            var ropeS = new double[n + 2];
            var ropeY = new double[n + 2];
            var lean = new double[n];
            ropeS[0] = bottomExitS;
            ropeY[0] = bottomExitY;
            ropeS[n + 1] = LengthMetres - topExitBack;
            ropeY[n + 1] = topExitY;
            var baseS = new double[n];
            var baseY = new double[n];
            for (int i = 0; i < n; i++)
            {
                baseS[i] = towers[i].AlongMm / 1000.0;
                baseY[i] = GroundAt(baseS[i]);
            }

            double t0 = type.CounterweightNewtons;
            double wl = type.LoadedNewtonsPerMetre, we = type.EmptyNewtonsPerMetre;
            _loaded = new Span[n + 1];
            _empty = new Span[n + 1];
            var heads = new Head[n];
            var bendL = new double[n];
            var bendE = new double[n];

            void Place()
            {
                for (int i = 0; i < n; i++)
                {
                    double height = towers[i].HeightMm / 1000.0;
                    ropeS[i + 1] = baseS[i] - height * Math.Sin(lean[i]);
                    ropeY[i + 1] = baseY[i] + height * Math.Cos(lean[i]);
                }
            }

            // RP-03: tension from the counterweight at the bottom plus the load per metre times the height gained; TW-04's
            // head from the rope's bend over each tower.
            void Solve()
            {
                for (int k = 0; k <= n; k++)
                {
                    _loaded[k] = Catenary(ropeS[k], ropeY[k], ropeS[k + 1], ropeY[k + 1], t0, wl, ropeY[0]);
                    _empty[k] = Catenary(ropeS[k], ropeY[k], ropeS[k + 1], ropeY[k + 1], t0, we, ropeY[0]);
                }
                for (int i = 0; i < n; i++)
                {
                    bendL[i] = Math.Atan(_loaded[i + 1].Slope(ropeS[i + 1])) - Math.Atan(_loaded[i].Slope(ropeS[i + 1]));
                    bendE[i] = Math.Atan(_empty[i + 1].Slope(ropeS[i + 1])) - Math.Atan(_empty[i].Slope(ropeS[i + 1]));
                    heads[i] = HeadFor(bendL[i], bendE[i], t0 + wl * (ropeY[i + 1] - ropeY[0]));
                }
            }

            // The rope's angle over a tower: the bisector of the arriving and departing chords.
            double Bisector(int i) => (Math.Atan2(ropeY[i + 1] - ropeY[i], ropeS[i + 1] - ropeS[i])
                                       + Math.Atan2(ropeY[i + 2] - ropeY[i + 1], ropeS[i + 2] - ropeS[i + 1])) / 2;

            Place();
            Solve();
            if (stance == TowerStance.Leaned)
            {
                // TW-14 v2 (owner, task 07): on a Leaned lift a tower leans where it's needed, where the rope crosses its
                // head at more than 10° or its head is a breakover, hold-down or combination (the heaviest loads); the rest
                // stay plumb. Which towers lean is decided on the plumb rope; a leaning mast then stands square to the rope,
                // bisecting its chords, capped at 15° and in the base models' 2.5° steps. Three passes settle the rope.
                var leans = new bool[n];
                for (int i = 0; i < n; i++)
                    leans[i] = Math.Abs(Bisector(i)) > LeanFrom || heads[i] == Head.B8 || heads[i] == Head.D8 || heads[i] == Head.C8;
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (!leans[i]) continue;
                        double bisect = Math.Max(-MaxLean, Math.Min(MaxLean, Bisector(i)));
                        lean[i] = Math.Round(bisect / LeanStep) * LeanStep;
                    }
                    Place();
                }
                Solve();
            }
            TopTensionShare = (t0 + wl * (ropeY[n + 1] - ropeY[0])) / type.RatedTensionNewtons;
            double Steepest(double a, double b) => Math.Abs(a) >= Math.Abs(b) ? a : b;
            BottomExitAngle = Steepest(Math.Atan(_loaded[0].Slope(ropeS[0])), Math.Atan(_empty[0].Slope(ropeS[0])));
            TopEntryAngle = Steepest(Math.Atan(_loaded[n].Slope(ropeS[n + 1])), Math.Atan(_empty[n].Slope(ropeS[n + 1])));
            for (int i = 0; i < n; i++)
                _towers[i] = new TowerSolution(baseS[i], baseY[i], ropeS[i + 1], ropeY[i + 1], lean[i], bendL[i], bendE[i], heads[i]);

            // TW-01, every metre (TW-08), both cases; the load and unload zones (from each station to where the chairs
            // first reach 15 ft) are exempt.
            double lowest = double.MaxValue, lowestS = 0;
            int steps = (int)Math.Floor(ropeS[n + 1] - ropeS[0]);
            var clear = new double[steps + 1];
            for (int j = 0; j <= steps; j++)
            {
                double s = ropeS[0] + j;
                clear[j] = Math.Min(RopeAt(_loaded, s), RopeAt(_empty, s)) - type.ChairDropMetres - GroundAt(s);
            }
            // The exempt zones end where the chairs first reach 15 ft, and at the first (last) line tower at the latest, so a
            // span between line towers that sags into the ground is never exempt. Station towers (TW-02 v2: within 100 ft
            // of a bullwheel) stand inside the zones.
            double zone = DesignUnits.StationZoneMm / 1000.0;
            int firstLine = 0;
            while (firstLine < n && baseS[firstLine] <= zone) firstLine++;
            int lastLine = n - 1;
            while (lastLine >= 0 && LengthMetres - baseS[lastLine] <= zone) lastLine--;
            int firstTower = firstLine < n ? (int)Math.Floor(ropeS[firstLine + 1] - ropeS[0]) : steps;
            int lastTower = lastLine >= 0 ? (int)Math.Ceiling(ropeS[lastLine + 1] - ropeS[0]) : 0;
            int first = 0, last = steps;
            while (first < Math.Min(steps, firstTower) && clear[first] < Clearance) first++;
            while (last > Math.Max(0, lastTower) && clear[last] < Clearance) last--;
            for (int j = first; j <= last; j++)
                if (clear[j] < lowest) { lowest = clear[j]; lowestS = ropeS[0] + j; }
            LowestClearance = lowest == double.MaxValue ? 0 : lowest;
            LowestClearanceS = lowestS;
        }

        /// <summary>The rope's elevation at s on one side's spans (the nearest span past either end).</summary>
        public static double RopeAt(IReadOnlyList<Span> spans, double s)
        {
            for (int k = 0; k < spans.Count; k++)
                if (s <= spans[k].To || k == spans.Count - 1) return spans[k].Y(s);
            return 0;
        }

        /// <summary>
        /// The catenary through both points whose horizontal tension is the RP-03 tension at the span's lower end,
        /// a = H / w, in closed form.
        /// </summary>
        public static Span Catenary(double s1, double y1, double s2, double y2, double t0, double w, double y0)
        {
            double chord = Math.Atan2(y2 - y1, s2 - s1);
            double horizontal = (t0 + w * (Math.Min(y1, y2) - y0)) * Math.Cos(chord);
            double a = horizontal / w;
            double half = (s2 - s1) / 2;
            double centre = (s1 + s2) / 2 - a * Asinh((y2 - y1) / (2 * a * Math.Sinh(half / a)));
            double c = y1 - a * Math.Cosh((s1 - centre) / a);
            return new Span(s1, s2, a, centre, c);
        }

        static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1));

        /// <summary>
        /// A simple TW-04 (task 07): the rope presses down where it turns down over the head in both cases (support,
        /// sized by the sheaves its bend and load need), pulls up where it turns up in both (hold-down), and flips
        /// between them otherwise (combination). LC-02: 4.5° and 1,500 lbf (6.67 kN) per sheave.
        /// </summary>
        public static Head HeadFor(double bendLoaded, double bendEmpty, double tension)
        {
            bool downL = bendLoaded < 0, downE = bendEmpty < 0;
            if (downL != downE) return Head.C8;
            double bend = Math.Max(Math.Abs(bendLoaded), Math.Abs(bendEmpty));
            double load = 2 * tension * Math.Sin(bend / 2);
            int sheaves = Math.Max((int)Math.Ceiling(bend / BendPerSheave - 1e-9), (int)Math.Ceiling(load / 6672 - 1e-9));
            if (sheaves > 8) return Head.TooSharp;
            if (!downL) return Head.D8;
            return sheaves <= 4 ? Head.S4 : sheaves <= 6 ? Head.S6 : Head.B8;
        }
    }
}
