using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A lift type's numbers (LC-01, LC-02 as tuned at task 07, decision SK8; detachables LT41), in SI units.</summary>
    public sealed class LiftType
    {
        public string CatalogId { get; private set; }
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
        /// <summary>The terminal at each end of the line (TW-16 v3): its model, its row and what that row needs.</summary>
        public TerminalEnd Bottom { get; private set; }
        public TerminalEnd Top { get; private set; }
        /// <summary>LD9 (drive at either end): the end that drives, as the lift chose it or the type's own.</summary>
        public LiftDrive Drive { get; private set; } = LiftDrive.Top;
        /// <summary>
        /// LD9: the top end's rows, fewest sheaves first (4, 6, 8), where its terminal carries a head for each; null where the top
        /// has one row. Not saved: the rope takes the first whose row carries it (<see cref="LiftRope.Solve"/>), so a steeper
        /// summit gets a longer row, as tower heads do (TW-04). <see cref="Top"/> is the first until a rope picks another.
        /// </summary>
        public IReadOnlyList<TerminalEnd> TopTrains { get; private set; }
        /// <summary>TW-01 v2: half the chair's width across the rope (the Sessellift chair's footrest rods, 1,020 mm).</summary>
        public double ChairHalfWidthMetres { get; private set; } = 1.05;
        /// <summary>LC-08: the type's name in the menu ("Fixed-grip quad", "Detachable six"…) and its rise family.</summary>
        public string Name { get; private set; } = "Fixed-grip quad";
        /// <summary>LC-07 (owner, task 09, LT13): how much vertical the lift may climb, metres; infinite for a tram.</summary>
        public double RiseLimitMetres { get; private set; } = 2200 * 0.3048;
        /// <summary>TW-02 with LC-08: the tallest tower (100 ft; a tram's 250 ft).</summary>
        public double TowerMaxMetres { get; private set; } = DesignUnits.TowerMaxMm / 1000.0;
        /// <summary>LC-03: people an hour; a tram's is its own (one cabin each way), not seats × speed ÷ spacing.</summary>
        double? _capacity;

        // ---------- detachables (LT41) ----------

        /// <summary>The carriers leave the rope in the stations and run round them on a rail, slowly (LT41).</summary>
        public bool Detachable { get; private set; }
        /// <summary>LT41: a detachable's loading interval, seconds between carriers; its spacing on the line follows from the
        /// line speed (<see cref="LineSpacing"/>). A fixed grip's is its spacing over its speed.</summary>
        public double IntervalSeconds => _interval ?? ChairSpacingMetres / SpeedMetresPerSecond;
        double? _interval;
        /// <summary>LT41: station speeds as fractions of the line speed (1.0 and 1.4 m/s at 1,000 fpm; a maker's published boarding
        /// and deboarding figures), held within realistic bands, so a line-speed slider scales them by itself.</summary>
        public const double BoardFraction = 1.0 / 5.08, AlightFraction = 1.4 / 5.08;
        public const double BoardMin = 0.8, BoardMax = 1.2, AlightMin = 1.1, AlightMax = 1.6;
        /// <summary>LT41: the tyre conveyors slow the carriers in and speed them out at this rate, m/s². A game choice: no
        /// source gives it; 1.5 (was 1.0) slows them within about 8 m of the station's entry, so on the D6's 40 m rail most of
        /// the way round is slow and about five chairs stand in a terminal, as on the owner's photo (finding E7).</summary>
        public const double RampAccel = 1.5;
        /// <summary>Finding E7: round the bullwheel the carriers go slower still, a fraction of the line speed held within a
        /// band (0.55 m/s at 1,000 fpm), so they bunch there about 4 m apart at the D6's 7.2 s, as a real terminal's do (a
        /// detachable quad's terminal: about 4.1 m at 0.8 m/s with a 5.1 s interval). The band's floor keeps G4's 3.5 m.</summary>
        public const double TurnFraction = 0.55 / 5.08, TurnMin = 0.5, TurnMax = 0.8;
        /// <summary>The speed round a station's bullwheel at a line speed; a fixed grip keeps line speed.</summary>
        public double TurnSpeed(double line) => Detachable ? Math.Max(TurnMin, Math.Min(TurnMax, line * TurnFraction)) : line;
        /// <summary>Boarding speed (the bottom station) at a line speed; a fixed grip loads at line speed.</summary>
        public double BoardSpeed(double line) => Detachable ? Math.Max(BoardMin, Math.Min(BoardMax, line * BoardFraction)) : line;
        /// <summary>Deboarding speed (the top station) at a line speed.</summary>
        public double AlightSpeed(double line) => Detachable ? Math.Max(AlightMin, Math.Min(AlightMax, line * AlightFraction)) : line;
        /// <summary>The spacing on the line at a line speed: speed × interval (LT41).</summary>
        public double LineSpacing(double line) => line * IntervalSeconds;
        /// <summary>The length a ramp takes between the line speed and a station speed at <see cref="RampAccel"/>, metres.</summary>
        public static double RampLength(double line, double station) => Math.Max(0, line * line - station * station) / (2 * RampAccel);
        /// <summary>
        /// The ride, seconds, over <paramref name="lineMetres"/> of slope: at line speed, plus the time the ramps at each end
        /// lose against it (the chair speeds up out of the bottom station and slows into the top one).
        /// </summary>
        public double RideSeconds(double lineMetres)
        {
            double v = SpeedMetresPerSecond, t = lineMetres / v;
            if (!Detachable) return t;
            foreach (double s in new[] { BoardSpeed(v), AlightSpeed(v) }) t += (v - s) * (v - s) / (2 * RampAccel * v);
            return t;
        }
        /// <summary>The tower heads its kit has (TW-04 v2, LT41): the quads' up to 8 sheaves, the D6's wide ones up to 10.</summary>
        public TowerForces.HeadKit Heads { get; private set; } = TowerForces.HeadKit.Quad;
        /// <summary>The prefix of its tower parts (G2): heads from its own kit, the mast and bases from the quad's where it
        /// has none.</summary>
        public string TowerKit { get; private set; } = "sessellift_fgq4_";
        /// <summary>
        /// Task P3-16 (BB2): the terminals' bullwheels show, so the view spins them with the rope. The fixed-grip quads'
        /// stand open or under a raised hood; the D6's sit inside its tub (glimpses from above only), so they stay still.
        /// </summary>
        public bool BullwheelVisible { get; private set; } = true;

        // ---------- the line's rules, per type (a surface tow's differ; v0.3.2) ----------

        /// <summary>A surface tow (the T-bar, J-bar): its riders are towed up a track on the snow, so it has no chairs to clear
        /// the ground; the rope's height over the track is a band (<see cref="ClearanceMetres"/> to <see cref="RopeMaxMetres"/>).</summary>
        public bool Tow { get; private set; }
        /// <summary>A belt lift (the magic carpets): no rope, towers or carriers; its bed instead (<see cref="CarpetBed"/>).</summary>
        public bool Belt { get; private set; }
        /// <summary>A carpet under its gallery (C3, C10).</summary>
        public bool Covered { get; private set; }
        /// <summary>A carpet's deck, half its width from the line, metres (past the outer belt on a double).</summary>
        public double DeckHalfWidthMetres { get; private set; }
        /// <summary>A carpet station's plate top over its base (glidepath_carpet.json top/bottom plateTop).</summary>
        public double PlateTopMetres { get; private set; } = 0.372;

        /// <summary>TW-01: the least height of the carriers' lowest point over the ground under them (the chairs' 15 ft; a
        /// tow's rope over its track, its retracted bars clear of skiers passing under).</summary>
        public double ClearanceMetres { get; private set; } = LiftRope.Clearance;
        /// <summary>A tow's greatest rope height over the track, where its recoil cords still reach a rider; infinite for a
        /// chair.</summary>
        public double RopeMaxMetres { get; private set; } = double.PositiveInfinity;
        /// <summary>TW-02: a line tower's least height and a station tower's least steel (25 and 15 ft; a tow's 15 and 10).</summary>
        public double TowerMinMetres { get; private set; } = DesignUnits.TowerMinMm / 1000.0;
        public double StationTowerMinMetres { get; private set; } = DesignUnits.StationTowerMinMm / 1000.0;
        /// <summary>TW-12 v2: the steel automatic placement tries for a line tower, feet, modest first; and the modest height it
        /// brings each tower down toward (35 ft; a tow's 20).</summary>
        public IReadOnlyList<double> PlanFeet { get; private set; } = ChairPlanFeet;
        public double ModestFeet { get; private set; } = 35;
        /// <summary>TW-12: the line towers' height where no plan fits (35 ft; a tow's 25).</summary>
        public double LineHeightMetres { get; private set; } = 10.668;
        /// <summary>TW-12 v3: the longest span the plan takes (300 ft; a tow's 250, its towers 60–80 m apart).</summary>
        public double PlanMaxSpanMetres { get; private set; } = 91.44;
        static readonly double[] ChairPlanFeet = { 35, 45, 60, 80, 100 };

        /// <summary>LT41: whether a player can build it (<see cref="LiftCatalogIds.Buildable"/>).</summary>
        public bool Buildable => LiftCatalogIds.Buildable(CatalogId);

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
        public double Capacity => _capacity ?? Seats * SpeedMetresPerSecond * 3600 / ChairSpacingMetres;

        /// <summary>
        /// LC-02 v2 for both quads (rope 1¾ in, owner, task 07, SK17): 450 fpm, 45 ft spacing, a 1¾ in (44.5 mm) 6x19
        /// fibre-core rope, 5.15 lb/ft (7.66 kg/m), breaking strength 248,000 lbf (1,104 kN, the 1⅝ in table value scaled by
        /// diameter²), rated at a fifth, 49,600 lbf (220.8 kN); counterweight 18,000 lbf per side; 350 lb chairs, 170 lb
        /// riders; the chair hangs 8.7 ft (2.66 m, measured on the model) below the rope; the Sessellift's gauge is 13.5 ft
        /// (4.12 m), and the Monta's the same (owner, task 09, LT12: the Monta was narrowed to it rather than given wider tower heads). RP-05's rise is now (49,600 − 18,000) ÷ 28.0 lb/ft, about 1,130 ft.
        /// </summary>
        public static LiftType Of(Lift lift) => Of(lift.CatalogId, lift.Drive);

        public static LiftType Of(string catalogId, LiftDrive? drive = null)
        {
            if (LiftCatalogIds.IsStern(catalogId)) return Stern(catalogId);
            if (LiftCatalogIds.IsTow(catalogId)) return TowOf(catalogId);
            if (LiftCatalogIds.IsBelt(catalogId)) return CarpetOf(catalogId);
            var type = Catalog(catalogId);
            if (!LiftCatalogIds.DriveChoice(catalogId)) return type;
            return Driven(type, LiftCatalogIds.Maker(catalogId) == "monta", drive ?? LiftCatalogIds.DefaultDrive(catalogId));
        }

        static LiftType Catalog(string catalogId)
        {
            bool monta = LiftCatalogIds.Maker(catalogId) == "monta";
            double gauge = 4.12;
            var type = new LiftType(catalogId, 4, 2.286, 13.716, 7.66, 220_800, 80_070, 158.8, 77.1, 2.66, gauge, 0.04445);
            string carrier = LiftCatalogIds.Carrier(catalogId);
            if (carrier != "fixed-quad" && Carriers.TryGetValue(carrier, out var c))
            {
                // LC-08's placeholders (the mockup's LIFT_TYPES, owner, LT15): speed, spacing, the rope (its weight scaled from
                // the quad's 1¾ in by diameter²), counterweight and carrier weights; drawn with the quad's models.
                const double Lb = 0.45359237, Lbf = 4.448222, Ft = 0.3048;
                double ropeKg = 7.66 * (c.RopeCm * c.RopeCm) / (4.445 * 4.445);
                double rider = (c.FullLb - c.EmptyLb) / c.Seats * Lb;
                type = new LiftType(catalogId, c.Seats, c.Fpm * Ft / 60, c.SpacingFt * Ft, ropeKg, double.PositiveInfinity, c.CounterweightLbf * Lbf,
                                    c.EmptyLb * Lb, rider, 2.66, gauge, c.RopeCm / 100);
                type.Name = c.Name;
                type.RiseLimitMetres = c.RiseFt * Ft;
                type._capacity = c.Capacity;
                // LT41: detachable chairs and gondolas leave the rope in the stations; their interval is their spacing over the
                // line speed (the catalog's spacing is that interval at 1,000 fpm).
                if (carrier.StartsWith("detach-", StringComparison.Ordinal) || carrier.StartsWith("gondola-", StringComparison.Ordinal))
                {
                    type.Detachable = true;
                    type._interval = c.SpacingFt * Ft / (c.Fpm * Ft / 60);
                }
                if (carrier.StartsWith("tram-", StringComparison.Ordinal)) type.TowerMaxMetres = DesignUnits.TramTowerMaxMm / 1000.0;
            }
            // Each end is the terminal model standing there (TW-16 v3; the owner's ruling, 2026-10-18: terminals face where
            // their bullwheels are). The Sessellift drives at the top: its return's hold-down row (8 sheaves) at the bottom,
            // its drive's support row (4) at the top. The Monta drives at the bottom: its drive's hold-down rows (12 sheaves,
            // the carrier and link-bar trees) at the bottom, and its return's single deflection sheave at the top (LT6: a
            // fixed row, its range a sheave's 4.5°). Both tension at the bottom (LT11, RP-03). Footprints, exits and origins
            // are the models' (the view measures them again: LiftTypes.Measured).
            if (catalogId == LiftCatalogIds.SesselliftD6) return D6(type);
            if (catalogId == LiftCatalogIds.SesselliftD4) return D4(type);
            if (monta)
            {
                type.Bottom = new TerminalEnd("monta_fg4_drive", TerminalRow.HoldDown, MontaExitMin, MontaExitMax, 0.9 * MontaExitMax,
                                              5.042, 11.075, 5.475, 10.869, 3.555, 0, 12, ownStation: true);   // TW-16 v4, LT32; LT36 half-width
                type.Top = new TerminalEnd("monta_fg4_return", TerminalRow.Support, -TowerForces.SheaveBendLimit, 0, 0,
                                           2.165, 3.782, 2.165, 1.922, 3.039, 0, 1);
            }
            else
            {
                type.Bottom = new TerminalEnd("sessellift_fgq4_return", TerminalRow.HoldDown, ReturnExitMin, ReturnExitMax, 0.9 * ReturnExitMax,
                                              2.332, 8.059, 3.022, 7.880, 3.543, 1.990, 8);
                type.Top = new TerminalEnd("sessellift_fgq4_drive", TerminalRow.Support, -DriveEntryMax, 0, 0, 2.530, 7.218, 3.328, 7.021, 3.039, 3.230, 4);
            }
            if (carrier == "fixed-double" || carrier == "fixed-triple") return FixedSmall(type, catalogId, carrier == "fixed-triple", monta);
            return type;
        }

        /// <summary>
        /// The fixed-grip double and triple (owner, 2026-11-01, LD1–LD7), Sessellift FGD-2 / FGT-3 and Monta FG2 / FG3: the quad's
        /// 450 fpm and 6 s (45 ft spacing, LD2: 1,200 and 1,800 an hour); 11 ft (3.35 m) and 12 ft (3.66 m) gauges; the design
        /// rope (LD7, physics only: every lift draws the same cable) 1¼ in and 1⅜ in, its weight, rating and counterweight
        /// scaled from the quad's 1¾ in by diameter²; 200 lb and 280 lb chairs (LC-08) hanging as the quad's (one hanger, the
        /// models' 2,687 mm drop measured at the same point); half a chair across the rope scaled from the quad's 1.05 m by
        /// the models' widths (1,190 / 923 / 655 mm inboard); LT13's rise limits. The terminals are the quad's family at the
        /// narrower gauge (LD5, Q1), so their rows and housings are the quad's; towers from the narrow kits, which the Monta
        /// shares (Q4).
        /// </summary>
        static LiftType FixedSmall(LiftType quad, string catalogId, bool triple, bool monta)
        {
            const double Lb = 0.45359237, Ft = 0.3048;
            double rope = triple ? 0.034925 : 0.03175, r2 = rope / 0.04445 * (rope / 0.04445);
            var t = new LiftType(catalogId, triple ? 3 : 2, 2.286, 13.716, 7.66 * r2, 220_800 * r2, 80_070 * r2, (triple ? 280 : 200) * Lb, 77.1,
                                 2.66, triple ? 3.658 : 3.353, rope)
            {
                Name = triple ? "Fixed-grip triple" : "Fixed-grip double",
                RiseLimitMetres = (triple ? 2500 : 2800) * Ft,
                ChairHalfWidthMetres = 1.05 * (triple ? 923.0 : 655.0) / 1190.0,
                TowerKit = triple ? "sessellift_fgt3_" : "sessellift_fgd2_",
            };
            string from = monta ? "monta_fg4_" : "sessellift_fgq4_", to = monta ? (triple ? "monta_fg3_" : "monta_fg2_") : t.TowerKit;
            t.Bottom = quad.Bottom.WithModel(quad.Bottom.Model.Replace(from, to));
            t.Top = quad.Top.WithModel(quad.Top.Model.Replace(from, to));
            return t;
        }

        /// <summary>
        /// The Sessellift D6 detachable six-pack (LT41; the D6 findings' G3, G4): a 2 in (50.8 mm) compacted haul rope, about
        /// 10 kg/m (the quad's 1¾ in scaled by diameter²), rated at 444 kN (its 2,000 kN breaking strength over 4.5); a 5.6 m
        /// gauge; the six-seat chair (1,300 lb, 170 lb riders) hanging 3.27 m below the rope, 1.5 m either side of it; 1,000 fpm
        /// and a 7.2 s interval (3,000 an hour, 120 ft apart on the line). Its return stands at the bottom (it tensions), its
        /// drive at the top. The rope leaves each station level over the bend sheaves inside the hood (three each side), so
        /// each end is a support row: the first tower takes the rope on from level (TW-16 v3's station tower), never pulling
        /// it up off them. Footprints and exits are the models' (the hood; the view measures them again).
        /// </summary>
        static LiftType D6(LiftType placeholder)
        {
            const double Lb = 0.45359237, Ft = 0.3048;
            double speed = 1000 * Ft / 60, interval = 7.2;
            var t = new LiftType(LiftCatalogIds.SesselliftD6, 6, speed, speed * interval, 10.0, 444_000, placeholder.CounterweightNewtons,
                                 1300 * Lb, 170 * Lb, 3.27, 5.6, 0.0508)
            {
                Name = "Detachable six", RiseLimitMetres = placeholder.RiseLimitMetres, Detachable = true, _interval = interval,
                ChairHalfWidthMetres = 1.5, Heads = TowerForces.HeadKit.Wide, TowerKit = "sessellift_d6_", BullwheelVisible = false,
            };
            double level = TowerForces.SheaveBendLimit;
            // LT42: a detachable's pads end at the hood's middle (LF-12 v5).
            t.Bottom = new TerminalEnd("sessellift_d6_return", TerminalRow.Support, -level, 0, 0, 9.108, 12.816, 4.45, 12.816, 3.6, 0, 3, padToMiddle: true);
            t.Top = new TerminalEnd("sessellift_d6_drive", TerminalRow.Support, -level, 0, 0, 8.784, 13.137, 4.45, 13.137, 3.6, 0, 3, padToMiddle: true);
            return t;
        }

        /// <summary>
        /// The Sessellift D4 detachable quad (owner, 2026-11-02, the D4 model thread's DQ rulings): 1,000 fpm at a 6 s interval
        /// (2,400 an hour, 100 ft apart on the line), a 4.8 m gauge, the quad chair on the D6's hanger (3.27 m drop, 1.16 m
        /// either side of the rope), 900 lb; a 1⅝ in (41.3 mm) design rope (LD7, physics only; quads mostly 38–42 mm), its
        /// weight and rating scaled from the D6's 2 in by diameter². Unlike the D6 its bottom station holds the rope down: the
        /// first tower is built into it (DQ5), its hold-down rows posing for exits of 5–16° (DQ11), so no station tower stands
        /// after it. The top is a level support exit as the D6's. Heads from the quad's kit at 4.8 m (s4–c8, not the wide ones).
        /// </summary>
        static LiftType D4(LiftType placeholder)
        {
            const double Lb = 0.45359237, Deg = Math.PI / 180;
            double speed = 1000 * 0.3048 / 60, interval = 6, rope = 0.041275, r2 = rope / 0.0508 * (rope / 0.0508);
            var t = new LiftType(LiftCatalogIds.SesselliftD4, 4, speed, speed * interval, 10.0 * r2, 444_000 * r2, placeholder.CounterweightNewtons,
                                 900 * Lb, 170 * Lb, 3.272, 4.8, rope)
            {
                Name = "Detachable quad", RiseLimitMetres = placeholder.RiseLimitMetres, Detachable = true, _interval = interval,
                ChairHalfWidthMetres = 1.158, Heads = TowerForces.HeadKit.Quad, TowerKit = "sessellift_d4_", BullwheelVisible = false,
            };
            double level = TowerForces.SheaveBendLimit;
            t.Bottom = new TerminalEnd("sessellift_d4_bottom", TerminalRow.HoldDown, 5 * Deg, 16 * Deg, 0.9 * 16 * Deg, 6.0, 14.858, 4.45, 14.858, 3.872, 0, 8,
                                       ownStation: true, padToMiddle: true);
            t.Top = new TerminalEnd("sessellift_d4_top", TerminalRow.Support, -level, 0, 0, 6.0, 9.716, 4.45, 9.716, 3.6, 0, 3, padToMiddle: true);
            return t;
        }

        /// <summary>The heads a fixed-grip terminal's top end comes with (LD9): 4, 6 and 8 sheaves a side.</summary>
        static readonly (string Key, int Sheaves)[] TopHeads = { ("top", 4), ("top6", 6), ("top8", 8) };

        /// <summary>
        /// LD9 (owner, 2026-11-02, drive at either end): the fixed grip with its drive at <paramref name="drive"/>. Each terminal
        /// carries a head for each end, so the drive end swaps which model stands where and which head it shows:
        /// - Sessellift, drive at the top (as since task 07): the return's 8-sheave hold-down at the bottom, the drive at the top.
        /// - Sessellift, drive at the bottom: the drive's own hold-down head at the bottom, the return's top heads at the top.
        /// - Monta, drive at the bottom (as since task 09): its drive with its exit trees, and its one-sheave top station.
        /// - Monta, drive at the top: the kit 8-sheave hold-down station (<c>return_bottom</c>) at the bottom, the drive's top
        ///   heads at the top.
        /// Every hold-down bottom takes the Sessellift return's exits (TW-16 v2); every top with heads gets its 4, 6 and 8-sheave
        /// rows (<see cref="TopTrains"/>). The numbers are the models' (the view measures them again).
        /// </summary>
        static LiftType Driven(LiftType t, bool monta, LiftDrive drive)
        {
            var d = (LiftType)t.MemberwiseClone();
            d.Drive = drive;
            if (monta && drive == LiftDrive.Bottom) return d;
            string family = monta ? t.Bottom.Model.Substring(0, t.Bottom.Model.Length - "drive".Length)
                                  : t.Bottom.Model.Substring(0, t.Bottom.Model.Length - "return".Length);
            var heads = TerminalHeads.Of(family);
            if (!monta && drive == LiftDrive.Top)
            {
                d.Bottom = heads.ReturnBottom;
                d.TopTrains = heads.DriveTop;
            }
            else if (!monta)
            {
                d.Bottom = heads.DriveBottom;
                d.TopTrains = heads.ReturnTop;
            }
            else
            {
                d.Bottom = heads.ReturnBottom;
                d.TopTrains = heads.DriveTop;
            }
            d.Top = d.TopTrains[0];
            return d;
        }

        /// <summary>
        /// The Sessellift Stern (owner, v0.3.2): the second Sessellift fixed-grip style, a bottom drive-and-tension station and a
        /// fixed top return, never the other way round (no drive-end choice). Quad, triple and double at the FGQ-4 family's
        /// gauges with its chairs, towers and numbers (LD5: the housing keeps the quad's size); the drive's 8-sheave hold-down
        /// rows on the station arc take the Sessellift return's exits, and the return's top heads come with 4, 6 and 8 sheaves.
        /// </summary>
        static LiftType Stern(string catalogId)
        {
            string basis = catalogId == LiftCatalogIds.SesselliftStern4 ? LiftCatalogIds.SesselliftFgq4
                : catalogId == LiftCatalogIds.SesselliftStern3 ? LiftCatalogIds.SesselliftFgt3 : LiftCatalogIds.SesselliftFgd2;
            var t = (LiftType)Of(basis, LiftDrive.Top).MemberwiseClone();
            t.CatalogId = catalogId;
            t.Drive = LiftDrive.Bottom;
            var heads = TerminalHeads.Of("sessellift_stern" + catalogId.Substring(catalogId.Length - 1) + "_");
            t.Bottom = heads.DriveBottom;
            t.TopTrains = heads.ReturnTop;
            t.Top = t.TopTrains[0];
            return t;
        }

        /// <summary>
        /// The Monta T-bar and J-bar (surface lifts, v0.3.2; the model thread's D1–D11 and the owner's go on the game wiring,
        /// 2026-11-05). One 20 mm rope on a 3 m gauge, driven at the bottom (D1, no choice) and tensioned out of sight at the top
        /// (D2): the drive's hold-down trains turn the rope up out of the loading area at 3.4 m (D8), the return's 8-sheave
        /// breakover row brings it in at 4.5 m (Q4). 2.5 m/s with a bar every 15 m (owner Q2: 1,200 an hour on a T's two riders,
        /// 600 on a J's one; real lines carry 600–1,200). Its riders stay on the snow, so the loaded rope carries the pull a
        /// towed rider puts on it (a third of their weight, a game choice: a rider on a 30 % track) and the 25 kg carriers.
        /// The rope runs 17–33 ft over the track (5.2–10 m): its retracted bars hang 3.2 m under it (the recoil box, cord gap,
        /// stem and bar, monta_tbar.json), 2 m clear of a skier; past 10 m the cords no longer reach a rider. Its towers stand
        /// 15–30 ft on the tow kit's heads (s4, s6, d4; D10), up to 250 ft apart. The rise limit is 400 m (1,300 ft).
        /// The rope's numbers scale the quad's 1¾ in by diameter², as LD7's do; rated 50 kN, 25 kN counterweight.
        /// </summary>
        static LiftType TowOf(string catalogId)
        {
            const double Deg = Math.PI / 180, Ft = 0.3048;
            bool j = catalogId == LiftCatalogIds.MontaJbar;
            double rope = 0.020, r2 = rope / 0.04445 * (rope / 0.04445);
            var t = new LiftType(catalogId, j ? 1 : 2, 2.5, 15, 7.66 * r2, 50_000, 25_000, 25, 77.1 / 3, 0, 3.0, rope)
            {
                Name = j ? "J-bar" : "T-bar",
                Tow = true,
                Drive = LiftDrive.Bottom,
                RiseLimitMetres = 400,
                ChairHalfWidthMetres = 0.75,   // the track under the uphill rope, 1.5 m (monta_tbar.json trackWidth)
                ClearanceMetres = 5.2,
                RopeMaxMetres = 10.0,
                TowerMinMetres = DesignUnits.TowTowerMinMm / 1000.0,
                StationTowerMinMetres = DesignUnits.TowStationTowerMinMm / 1000.0,
                TowerMaxMetres = DesignUnits.TowTowerMaxMm / 1000.0,
                PlanFeet = new double[] { 20, 25, 30 },
                ModestFeet = 20,
                LineHeightMetres = 25 * Ft,
                PlanMaxSpanMetres = 250 * Ft,
                Heads = TowerForces.HeadKit.Tow,
                TowerKit = "monta_tbar_",
            };
            // The view measures footprints, exits and origins again on the models (LiftTypes.Measured).
            t.Bottom = new TerminalEnd("monta_tbar_drive", TerminalRow.HoldDown, 2 * Deg, 25 * Deg, 10 * Deg, 1.83, 6.4, 1.75, 5.9, 3.4, 0, 8);
            t.Top = new TerminalEnd("monta_tbar_return", TerminalRow.Support, -8 * LiftRope.BendPerSheave, 0, 0, 1.6, 6.0, 1.6, 5.5, 4.5, 0, 8);
            return t;
        }

        /// <summary>
        /// The Glidepath carpets (surface lifts PR B; glidepath_carpet.json, glidepath_carpet2.json): a 750 mm belt (two on the
        /// double, 1,450 mm apart) at 160 fpm (C11), driven at the top (C4), 1,500 riders an hour a belt. No rope, towers or
        /// carriers (<see cref="Belt"/>): the bed is laid by <see cref="CarpetBed"/>. Each station's footprint: its plate's
        /// outer edge behind its origin (0.6 m at the bottom, 1 m at the top), its belt joint 1.8 m ahead, its widest part
        /// (the tension unit, the motor cover) either side.
        /// </summary>
        static LiftType CarpetOf(string catalogId)
        {
            bool two = LiftCatalogIds.IsDoubleBelt(catalogId), covered = LiftCatalogIds.IsCovered(catalogId);
            double deck = two ? 1.53 : 0.805;
            var t = new LiftType(catalogId, two ? 2 : 1, 0.813, 2.0, 0, 0, 0, 0, 77.1, 0, two ? 1.45 : 0, 0)
            {
                Name = (two ? "Double carpet" : "Carpet") + (covered ? ", covered" : ""),
                Belt = true,
                Covered = covered,
                Drive = LiftDrive.Top,
                RiseLimitMetres = CarpetBed.RiseLimitMetres,
                DeckHalfWidthMetres = deck,
                ChairHalfWidthMetres = deck,
                TowerKit = two ? "glidepath_carpet2_" : "glidepath_carpet_",
                BullwheelVisible = false,
            };
            t._capacity = two ? 3000 : 1500;
            double station = deck + 0.8;
            t.Bottom = new TerminalEnd((two ? "glidepath_carpet2_" : "glidepath_carpet_") + "bottom", TerminalRow.Support, 0, 0, 0, 0.6, 1.8, station, 1.8, 0, 0, 0);
            t.Top = new TerminalEnd((two ? "glidepath_carpet2_" : "glidepath_carpet_") + "top", TerminalRow.Support, 0, 0, 0, 1.0, 1.8, station, 1.8, 0, 0, 0);
            return t;
        }

        /// <summary>
        /// LD9: the top terminal's footprint on the ground, whichever of its rows the rope picks: the station's own, as its
        /// shortest row stands it (its pad and clearing are graded before the rope is solved). A longer row's extra sheaves
        /// reach out over the line in the air, above the corridor's clearing, so they neither widen the pad nor move the
        /// station tower: a gentle summit grades and clears as it did before the heads. Behind and either side, the widest.
        /// </summary>
        public TerminalEnd TopFootprint
        {
            get
            {
                if (TopTrains == null) return Top;
                double behind = 0, half = 0;
                foreach (var t in TopTrains)
                {
                    behind = Math.Max(behind, t.Behind);
                    half = Math.Max(half, t.HalfWidth);
                }
                var station = TopTrains[0];
                return station.WithFootprint(behind, station.Ahead, half);
            }
        }

        /// <summary>The same type with another of its top rows (LD9; <see cref="LiftRope.Solve"/> picks it).</summary>
        public LiftType WithTop(TerminalEnd top)
        {
            var copy = (LiftType)MemberwiseClone();
            copy.Top = top;
            return copy;
        }

        /// <summary>One placeholder type (LC-08): menu name, speed (fpm), seats, spacing (ft), rope diameter (cm),
        /// counterweight (lbf), carrier empty and full (lb), rise limit (ft, LC-07) and any capacity of its own.</summary>
        sealed class CarrierSpec
        {
            public string Name; public double Fpm; public int Seats; public double SpacingFt, RopeCm, CounterweightLbf, EmptyLb, FullLb, RiseFt;
            public double? Capacity;
            public CarrierSpec(string name, double fpm, int seats, double spacing, double rope, double cw, double empty, double full, double rise, double? capacity = null)
            {
                Name = name; Fpm = fpm; Seats = seats; SpacingFt = spacing; RopeCm = rope; CounterweightLbf = cw; EmptyLb = empty; FullLb = full; RiseFt = rise; Capacity = capacity;
            }
        }

        /// <summary>LC-07 and LC-08 (owner, task 09, LT13, LT15): the mockup's LIFT_TYPES and rise families.</summary>
        static readonly Dictionary<string, CarrierSpec> Carriers = new Dictionary<string, CarrierSpec>
        {
            ["fixed-double"] = new CarrierSpec("Fixed-grip double", 450, 2, 45, 3.6, 14000, 200, 540, 2800),
            ["fixed-triple"] = new CarrierSpec("Fixed-grip triple", 450, 3, 45, 4.0, 16000, 280, 790, 2500),
            // LT41: spacing = 1,000 fpm × the interval (quad 6 s, 2,400 an hour; six 7.2 s, 3,000; eight 8 s, 3,600).
            ["detach-quad"] = new CarrierSpec("Detachable quad", 1000, 4, 100, 5.2, 30000, 900, 1580, 3600),
            ["detach-six"] = new CarrierSpec("Detachable six", 1000, 6, 120, 6.0, 36000, 1300, 2320, 3300),
            ["detach-eight"] = new CarrierSpec("Detachable eight", 1000, 8, 133.333, 6.4, 40000, 1700, 3060, 3000),
            ["gondola-6"] = new CarrierSpec("Gondola, 6 person", 1000, 6, 150, 5.2, 32000, 1300, 2320, 4500),
            ["gondola-8"] = new CarrierSpec("Gondola, 8 person", 1000, 8, 160, 6.0, 36000, 1700, 3060, 4500),
            ["gondola-10"] = new CarrierSpec("Gondola, 10 person", 1000, 10, 167, 6.4, 40000, 2000, 3700, 4500),
            ["tram-60"] = new CarrierSpec("Aerial tram, 60 person", 2000, 60, 5000, 15, 450000, 18000, 28200, double.PositiveInfinity, 500),
            ["tram-100"] = new CarrierSpec("Aerial tram, 100 person", 2000, 100, 5000, 15, 500000, 24000, 41000, double.PositiveInfinity, 750),
        };

        /// <summary>
        /// TW-16 v2: the Sessellift return's hold-down row turns the rope up from level in the loading area, and it leaves at
        /// 8.3° to 15.9° (radians): 15.9° on the station arc is the design limit, and below about 8.3° the row can't keep
        /// all eight sheaves on the rope with the rope level at the bullwheel (the model thread's pose sweep, task 07).
        /// </summary>
        public static readonly double ReturnExitMin = 8.3 * Math.PI / 180, ReturnExitMax = 15.9 * Math.PI / 180;
        /// <summary>The Monta drive's hold-down rows keep every sheave on the rope from 6° to 16° (the model thread's pose
        /// sweep, P3-07 Monta trains: 5–16° left, 6–16° right); placement aims at 90%.</summary>
        public static readonly double MontaExitMin = 6 * Math.PI / 180, MontaExitMax = 16 * Math.PI / 180;
        /// <summary>TW-16: the Sessellift drive's 4-sheave support row brings the rope level to the bullwheel; it can take at
        /// most 4 × 4.5° = 18° (LC-02) of arriving rope angle.</summary>
        public static readonly double DriveEntryMax = 18 * Math.PI / 180;

        /// <summary>The same type with its ends (and chair) measured on the real models; the catalog entry is untouched.</summary>
        public LiftType With(TerminalEnd bottom, TerminalEnd top, double chairHalfWidth, IReadOnlyList<TerminalEnd> topTrains = null)
        {
            var copy = (LiftType)MemberwiseClone();
            copy.Bottom = bottom;
            copy.Top = top;
            copy.ChairHalfWidthMetres = chairHalfWidth;
            if (topTrains != null) copy.TopTrains = topTrains;
            return copy;
        }
    }

    /// <summary>
    /// A lift's rope (task 07, decision SK6; round 3): each span a catenary between the rope's support points, with RP-03's
    /// tension (the counterweight at the bottom, plus the load per metre times the height gained), for the loaded uphill
    /// side and the empty downhill side; the head each tower needs from the rope's bend (a simple form of TW-04); TW-14's
    /// lean; TW-16 v3's station towers, whose foundations stand as high as their terminal needs (TW-02 v3); TW-01 v2's
    /// clearance under each rope. Task 09 replaces the head choice with TW-04's per-sheave forces.
    ///
    /// The profile frame: s is the horizontal distance from the bottom bullwheel along the line, y the elevation (both
    /// metres). Terminal rope exits are given by the caller (the models' sockets).
    /// </summary>
    public sealed class LiftRope
    {
        /// <summary>A tower's head (TW-04). The wide kit's 10-sheave heads (G6, LT41) come after TooSharp so no value moves; the
        /// tow kit's 4-sheave hold-down (D4, the T-bar) after them.</summary>
        public enum Head : byte { S4, S6, B8, D8, C8, TooSharp, S10, B10, D10, D4 }

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
            /// <summary>TW-02 v3: how far a station tower's foundation stands above the ground, under its 5 ft steel (metres; 0
            /// for a line tower).</summary>
            public readonly double Reveal;
            /// <summary>TW-04/TW-05: what the head carries in both load cases (task 09).</summary>
            public readonly TowerForces.Loads Loads;

            public TowerSolution(double baseS, double baseY, double ropeS, double ropeY, double lean, double bendLoaded, double bendEmpty,
                                 Head head, double reveal, TowerForces.Loads loads)
            {
                Loads = loads;
                BaseS = baseS;
                BaseY = baseY;
                RopeS = ropeS;
                RopeY = ropeY;
                Lean = lean;
                BendLoaded = bendLoaded;
                BendEmpty = bendEmpty;
                Head = head;
                Reveal = reveal;
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
        /// <summary>TW-01 v2: the lowest chair clearance under either rope outside the low zones, metres, and where.</summary>
        public double LowestClearance { get; }
        public double LowestClearanceS { get; }
        /// <summary>TW-01 v2: the most the chairs fall short of the rule anywhere (the low zones' rising line, 15 ft past
        /// them), metres, and where; 0 when they never do.</summary>
        public double Shortfall { get; }
        public double ShortfallS { get; }
        /// <summary>TW-01 v2 holds everywhere, to TW-08's 0.1 ft.</summary>
        public bool ClearanceOk => Shortfall <= ClearanceTolerance;
        /// <summary>A tow's loaded rope at its highest over the track between the station zones, metres, and where (0 for a
        /// chair, which has no upper limit).</summary>
        public double HighestOverTrack { get; }
        public double HighestOverTrackS { get; }
        /// <summary>A tow's rope higher over the track than its cords reach, metres (0 when it never is).</summary>
        public double Overshoot => Type.Tow ? Math.Max(0, HighestOverTrack - Type.RopeMaxMetres) : 0;
        /// <summary>The rope within the tow's band everywhere (always for a chair).</summary>
        public bool BandOk => Overshoot <= ClearanceTolerance;
        /// <summary>TW-16: the rope's angle leaving the bottom terminal and arriving at the top one in each load case,
        /// radians, positive climbing up the line; and the steeper of the two (for reports).</summary>
        public double BottomExitLoaded { get; }
        public double BottomExitEmpty { get; }
        public double TopEntryLoaded { get; }
        public double TopEntryEmpty { get; }
        public double BottomExitAngle => Steepest(BottomExitLoaded, BottomExitEmpty);
        public double TopEntryAngle => Steepest(TopEntryLoaded, TopEntryEmpty);
        /// <summary>TW-16 v3: each end's row takes the rope in both load cases (in the end's own frame, so a support row is
        /// never pulled up).</summary>
        public bool TerminalsOk => Type.Bottom.Takes(BottomExitLoaded) && Type.Bottom.Takes(BottomExitEmpty)
                                   && Type.Top.Takes(-TopEntryLoaded) && Type.Top.Takes(-TopEntryEmpty);
        /// <summary>TW-05 at each terminal's own row: every sheave within LC-02's limits in both load cases (task 09).</summary>
        public bool RowsOk { get; }
        /// <summary>RP-03: the tension at the top terminal, loaded and empty, newtons (the bottom's is the counterweight).</summary>
        public double TopTensionLoaded { get; }
        public double TopTensionEmpty { get; }
        /// <summary>RP-05: the top of the loaded side within the rope's rating. Shown, no longer blocking (v2, LT13).</summary>
        public bool TensionOk => TopTensionShare <= 1 + 1e-9;
        /// <summary>LC-07: the lift's rise, top terminal level over the bottom one's, metres.</summary>
        public double RiseMetres { get; }
        /// <summary>LC-07 (owner, task 09, LT13): the rise within the type's limit (a tram has none).</summary>
        public bool RiseOk => RiseMetres <= Type.RiseLimitMetres + 1e-9;

        readonly TowerSolution[] _towers;
        readonly Span[] _loaded, _empty;
        readonly double _first;
        readonly double[] _clear, _need;

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
        /// <summary>TW-08: clearance is checked to 0.1 ft.</summary>
        public const double ClearanceTolerance = 0.03;
        /// <summary>TW-02 v3: a station tower's foundation shows up to just under 5 ft (the next 5 ft of steel takes over).</summary>
        public const double MaxReveal = 1.523;
        /// <summary>The terminal raise's steps and limit (TW-16 v3's last resort, LF-12 v2: the pad fills up to it): 1 ft, 30 ft.</summary>
        public const double RaiseStep = 0.3048, MaxTerminalRaise = 9.144;

        /// <summary>
        /// Solves the rope for a straight line from <paramref name="bottomX"/>, <paramref name="bottomY"/> (the bottom
        /// bullwheel, local metres) to the top one, over <paramref name="ground"/>. The rope leaves the bottom terminal at
        /// (bottomExitS, bottomExitY) and reaches the top one at (length − topExitBack, topExitY). <paramref name="line"/>
        /// is the ground sampled under the line (sampled here when not given).
        /// </summary>
        public static LiftRope Solve(LiftType type, IHeightSource ground, double bottomX, double bottomY, double topX, double topY,
                                     IReadOnlyList<Tower> towers, TowerStance stance,
                                     double bottomExitS, double bottomExitY, double topExitBack, double topExitY, LineGround line = null,
                                     IReadOnlyList<double> baseY = null)
        {
            line ??= LineGround.Sample(ground, type, bottomX, bottomY, topX, topY);
            var trains = type.TopTrains;
            if (trains == null)
                return new LiftRope(type, ground, bottomX, bottomY, topX, topY, towers, stance, bottomExitS, bottomExitY, topExitBack, topExitY, line, baseY);
            // LD9: the top row with the fewest sheaves that carries the rope (TW-04's rule for heads, at the terminal): each row's
            // exit stands where its head has it, the terminal's level unchanged; the longest row where none does (its problem
            // then says what the rope needs).
            LiftRope rope = null;
            foreach (var train in trains)
            {
                var t = ReferenceEquals(train, type.Top) ? type : type.WithTop(train);
                rope = new LiftRope(t, ground, bottomX, bottomY, topX, topY, towers, stance, bottomExitS, bottomExitY,
                                    topExitBack - type.Top.ExitAlong + train.ExitAlong, topExitY - type.Top.ExitRise + train.ExitRise, line, baseY);
                if (rope.TopCarried) break;
            }
            return rope;
        }

        /// <summary>LD9: the top row takes the rope in both load cases and every sheave in it carries it (TW-16 v3, TW-05).</summary>
        public bool TopCarried => Type.Top.Takes(-TopEntryLoaded) && Type.Top.Takes(-TopEntryEmpty) && TopRowOk;
        bool TopRowOk { get; }

        /// <summary>
        /// TW-02 v4 (task 15, BR11): the rope with each tower's base where its lift's own earthwork left it
        /// (<paramref name="baseY"/>, from <see cref="LiftBuild.Works.Rope"/>), so hand grading at a base never moves the head:
        /// the tower's steel follows the ground instead (<see cref="AsBuilt"/>). The clearance is still read over
        /// <paramref name="ground"/>.
        /// </summary>
        public static LiftRope SolveWithBases(LiftType type, IHeightSource ground, double bottomX, double bottomY, double topX, double topY,
                                              IReadOnlyList<Tower> towers, TowerStance stance, double bottomExitS, double bottomExitY,
                                              double topExitBack, double topExitY, IReadOnlyList<double> baseY) =>
            Solve(type, ground, bottomX, bottomY, topX, topY, towers, stance, bottomExitS, bottomExitY, topExitBack, topExitY, null, baseY);

        /// <summary>
        /// TW-02 v4: the steel and foundation a tower needs to carry its fixed head from ground at <paramref name="groundY"/>:
        /// 5 ft steps of steel along the mast and up to just under 5 ft of foundation (metres). With the ground the head was
        /// solved over, it gives the stored height and the solved reveal back.
        /// </summary>
        public static void AsBuilt(TowerSolution tower, double groundY, out double steelMetres, out double revealMetres)
        {
            double step = DesignUnits.FiveFeetMm / 1000.0, cos = Math.Cos(tower.Lean);
            double rise = tower.RopeY - groundY;
            double steel = Math.Floor(rise / cos / step + 1e-6) * step;
            if (steel < step) steel = step;
            double reveal = rise - steel * cos;
            if (reveal < 0) reveal = 0;
            steelMetres = steel;
            revealMetres = Math.Min(reveal, MaxReveal);
        }

        LiftRope(LiftType type, IHeightSource ground, double bx, double by, double tx, double ty, IReadOnlyList<Tower> towers,
                 TowerStance stance, double bottomExitS, double bottomExitY, double topExitBack, double topExitY, LineGround line,
                 IReadOnlyList<double> fixedBaseY = null)
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
            var reveal = new double[n];
            ropeS[0] = bottomExitS;
            ropeY[0] = bottomExitY;
            ropeS[n + 1] = LengthMetres - topExitBack;
            ropeY[n + 1] = topExitY;
            var baseS = new double[n];
            var baseY = new double[n];
            for (int i = 0; i < n; i++)
            {
                baseS[i] = towers[i].AlongMm / 1000.0;
                baseY[i] = fixedBaseY != null && i < fixedBaseY.Count ? fixedBaseY[i] : GroundAt(baseS[i]);
            }
            // TW-02 v3: the first and last towers within the station zone (100 ft of their bullwheel) are station towers.
            double zone = DesignUnits.StationZoneMm / 1000.0;
            // TW-16 v4: a terminal with its own station (the Monta drive) has none after it.
            bool bottomStation = !type.Bottom.OwnStation && n > 0 && baseS[0] <= zone + 1e-9;
            bool topStation = !type.Top.OwnStation && n > (bottomStation ? 1 : 0) && LengthMetres - baseS[n - 1] <= zone + 1e-9;

            double t0 = type.CounterweightNewtons;
            double wl = type.LoadedNewtonsPerMetre, we = type.EmptyNewtonsPerMetre;
            _loaded = new Span[n + 1];
            _empty = new Span[n + 1];
            var heads = new Head[n];
            var loads = new TowerForces.Loads[n];
            var bendL = new double[n];
            var bendE = new double[n];

            void Place()
            {
                for (int i = 0; i < n; i++)
                {
                    double height = towers[i].HeightMm / 1000.0;
                    ropeS[i + 1] = baseS[i] - height * Math.Sin(lean[i]);
                    ropeY[i + 1] = baseY[i] + reveal[i] + height * Math.Cos(lean[i]);
                }
            }

            // TW-16 v3: a station tower's foundation stands as high as its terminal needs, so the empty rope leaves the
            // station at the end's aim: a hold-down row's 90% climb, a support row's level last span. Whole millimetres,
            // erring inside the row's range (the loaded rope is then flatter at a hold-down row and presses harder on a
            // support row).
            void Aim()
            {
                Place();
                if (bottomStation) reveal[0] = Reveal(0, true);
                if (topStation) reveal[n - 1] = Reveal(n - 1, false);
                Place();
            }
            double Reveal(int i, bool bottom)
            {
                double target = bottom ? Math.Tan(type.Bottom.Aim) : -Math.Tan(type.Top.Aim);
                double Slope(int mm)
                {
                    double y = ropeY[i + 1] - reveal[i] + mm / 1000.0;
                    return bottom
                        ? Catenary(ropeS[0], ropeY[0], ropeS[1], y, t0, we, ropeY[0]).Slope(ropeS[0])
                        : Catenary(ropeS[n], y, ropeS[n + 1], ropeY[n + 1], t0, we, ropeY[0]).Slope(ropeS[n + 1]);
                }
                // The bottom exit steepens as the tower rises; the top arrival flattens. Keep the side inside the range.
                bool Inside(int mm) => bottom ? Slope(mm) <= target : Slope(mm) >= target;
                int lo = 0, hi = (int)(MaxReveal * 1000);
                if (!Inside(lo)) return 0;
                if (Inside(hi)) return hi / 1000.0;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) / 2;
                    if (Inside(mid)) lo = mid; else hi = mid;
                }
                return lo / 1000.0;
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
                    double h = ropeY[i + 1] - ropeY[0];
                    loads[i] = new TowerForces.Loads(t0 + wl * h, t0 + we * h, bendL[i], bendE[i], type.Heads, towers[i].Kind);
                    heads[i] = loads[i].Head;
                }
            }

            // The rope's angle over a tower: the bisector of the arriving and departing chords.
            double Bisector(int i) => (Math.Atan2(ropeY[i + 1] - ropeY[i], ropeS[i + 1] - ropeS[i])
                                       + Math.Atan2(ropeY[i + 2] - ropeY[i + 1], ropeS[i + 2] - ropeS[i + 1])) / 2;

            Aim();
            Solve();
            if (stance == TowerStance.Leaned)
            {
                // TW-14 v2 (owner, task 07): on a Leaned lift a tower leans where it's needed, where the rope crosses its
                // head at more than 10° or its head is a breakover, hold-down or combination (the heaviest loads); the rest
                // stay plumb. Which towers lean is decided on the plumb rope; a leaning mast then stands square to the rope,
                // bisecting its chords, capped at 15° and in the base models' 2.5° steps. Three passes settle the rope, the
                // station foundations re-aimed with each.
                var leans = new bool[n];
                for (int i = 0; i < n; i++)
                    leans[i] = Math.Abs(Bisector(i)) > LeanFrom || heads[i] == Head.B8 || heads[i] == Head.D8 || heads[i] == Head.C8
                               || heads[i] == Head.B10 || heads[i] == Head.D10;   // the D6's (LT41)
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (!leans[i]) continue;
                        double bisect = Math.Max(-MaxLean, Math.Min(MaxLean, Bisector(i)));
                        lean[i] = Math.Round(bisect / LeanStep) * LeanStep;
                    }
                    Aim();
                }
                Solve();
            }
            RiseMetres = (topExitY - type.Top.ExitRise) - (bottomExitY - type.Bottom.ExitRise);
            TopTensionLoaded = t0 + wl * (ropeY[n + 1] - ropeY[0]);
            TopTensionEmpty = t0 + we * (ropeY[n + 1] - ropeY[0]);
            TopTensionShare = TopTensionLoaded / type.RatedTensionNewtons;
            BottomExitLoaded = Math.Atan(_loaded[0].Slope(ropeS[0]));
            BottomExitEmpty = Math.Atan(_empty[0].Slope(ropeS[0]));
            TopEntryLoaded = Math.Atan(_loaded[n].Slope(ropeS[n + 1]));
            TopEntryEmpty = Math.Atan(_empty[n].Slope(ropeS[n + 1]));
            TopRowOk = TowerForces.RowCarries(type.Top, TopTensionLoaded, TopEntryLoaded) && TowerForces.RowCarries(type.Top, TopTensionEmpty, TopEntryEmpty);
            RowsOk = TowerForces.RowCarries(type.Bottom, t0, BottomExitLoaded) && TowerForces.RowCarries(type.Bottom, t0, BottomExitEmpty) && TopRowOk;
            for (int i = 0; i < n; i++)
                _towers[i] = new TowerSolution(baseS[i], baseY[i], ropeS[i + 1], ropeY[i + 1], lean[i], bendL[i], bendE[i], heads[i], reveal[i], loads[i]);

            // TW-01 v2, every metre (TW-08), under each rope across the chair: the uphill rope loaded and empty, the downhill
            // rope empty (RP-04). On a terminal's pad (its footprint plus LF-12's 5 ft, the loading and unloading area) the
            // terminal governs. From the pad's edge the chairs climb at least along a straight line to 15 ft at the station
            // zone's edge (100 ft from the bullwheel); past the zones, 15 ft.
            Zones(type, LengthMetres, out double frontB, out double edgeB, out double edgeT, out double frontT);
            int j0 = (int)Math.Ceiling(Math.Max(frontB, ropeS[0])), j1 = (int)Math.Floor(Math.Min(frontT, ropeS[n + 1]));
            j1 = Math.Min(j1, line.Count - 1);
            _first = j0;
            int count = Math.Max(0, j1 - j0 + 1);
            _clear = new double[count];
            _need = new double[count];
            int kl = 0, ke = 0;
            double drop = type.ChairDropMetres;
            for (int j = 0; j < count; j++)
            {
                double s = j0 + j;
                while (kl < n && s > _loaded[kl].To) kl++;
                while (ke < n && s > _empty[ke].To) ke++;
                double up = Math.Min(_loaded[kl].Y(s), _empty[ke].Y(s)), down = _empty[ke].Y(s);
                _clear[j] = Math.Min(up - drop - line.UnderUp[j0 + j], down - drop - line.UnderDown[j0 + j]);
            }
            // The rising line starts from the chairs' height over their terminal's own level (its pad) at the pad's edge.
            double Lower(int k, double s) => Math.Min(_loaded[k].Y(s), _empty[k].Y(s));
            double clearance = type.ClearanceMetres;
            double cB = count > 0 ? Math.Max(0, Math.Min(clearance, Lower(0, j0) - drop - (bottomExitY - type.Bottom.ExitRise))) : 0;
            double cT = count > 0 ? Math.Max(0, Math.Min(clearance, Lower(n, j0 + count - 1) - drop - (topExitY - type.Top.ExitRise))) : 0;
            double lowest = double.MaxValue, lowestS = 0, worst = 0, worstS = 0, highest = double.MinValue, highestS = 0;
            kl = 0;
            for (int j = 0; j < count; j++)
            {
                double s = j0 + j;
                while (kl < n && s > _loaded[kl].To) kl++;
                double need = clearance;
                if (s < edgeB) need = Math.Min(need, cB + (clearance - cB) * Math.Max(0, s - j0) / (edgeB - j0));
                if (s > edgeT) need = Math.Min(need, cT + (clearance - cT) * Math.Max(0, j0 + count - 1 - s) / (j0 + count - 1 - edgeT));
                _need[j] = need;
                if (need - _clear[j] > worst) { worst = need - _clear[j]; worstS = s; }
                if (s >= edgeB && s <= edgeT && _clear[j] < lowest) { lowest = _clear[j]; lowestS = s; }
                // A tow's band (v0.3.2): the loaded rope over the riders' track, past the station zones, no higher than its
                // cords reach.
                if (type.Tow && s >= edgeB && s <= edgeT)
                {
                    double over = _loaded[kl].Y(s) - line.UnderUp[j0 + j];
                    if (over > highest) { highest = over; highestS = s; }
                }
            }
            LowestClearance = lowest == double.MaxValue ? clearance : lowest;
            LowestClearanceS = lowestS;
            Shortfall = worst;
            ShortfallS = worstS;
            HighestOverTrack = highest == double.MinValue ? 0 : highest;
            HighestOverTrackS = highestS;
        }

        /// <summary>
        /// TW-01 v2: the most the chairs fall short of the rule between <paramref name="from"/> and <paramref name="to"/>
        /// (metres along the line), and where; 0 when they never do.
        /// </summary>
        public double ShortfallBetween(double from, double to, out double at)
        {
            at = 0;
            double worst = 0;
            for (int j = 0; j < _clear.Length; j++)
            {
                double s = _first + j;
                if (s < from || s > to) continue;
                if (_need[j] - _clear[j] > worst) { worst = _need[j] - _clear[j]; at = s; }
            }
            return worst;
        }

        /// <summary>TW-01 v2 at <paramref name="s"/>: the chairs' clearance under the lower rope, and what the rule asks
        /// there (metres); false inside a terminal's footprint.</summary>
        public bool ClearanceAt(double s, out double clearance, out double need)
        {
            int j = (int)Math.Round(s - _first);
            clearance = need = 0;
            if (j < 0 || j >= _clear.Length) return false;
            clearance = _clear[j];
            need = _need[j];
            return true;
        }

        /// <summary>
        /// TW-01 v2's low zones on a line <paramref name="length"/> metres long: each runs from its terminal's pad edge
        /// (footprint plus LF-12's 5 ft; <paramref name="frontB"/>, <paramref name="frontT"/>) to the station zone's edge, 100 ft
        /// from the bullwheel (<paramref name="edgeB"/>, <paramref name="edgeT"/>), metres along the line from the bottom.
        /// </summary>
        public static void Zones(LiftType type, double length, out double frontB, out double edgeB, out double edgeT, out double frontT)
        {
            double zone = DesignUnits.StationZoneMm / 1000.0;
            frontB = type.Bottom.Ahead + TerminalPad.MarginMetres;
            frontT = length - type.Top.Ahead - TerminalPad.MarginMetres;
            edgeB = Math.Max(zone, frontB + 1);
            edgeT = Math.Min(length - zone, frontT - 1);
        }

        static double Steepest(double a, double b) => Math.Abs(a) >= Math.Abs(b) ? a : b;

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
    }
}
