using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>LF-01. Values are saved: never renumber.</summary>
    public enum TowerMode : byte { Automatic = 1, ByHand = 2 }

    /// <summary>TW-14: plumb by default, or leaned square to the rope.</summary>
    public enum TowerStance : byte { Plumb = 1, Leaned = 2 }

    /// <summary>
    /// LD9 (owner, 2026-11-02, drive at either end; save v10): which end of a fixed-grip lift holds the drive. Every fixed-grip
    /// terminal carries a head for each end, so the choice swaps which model stands where and which head it shows. Values are
    /// saved: never renumber.
    /// </summary>
    public enum LiftDrive : byte { Top = 1, Bottom = 2 }

    /// <summary>
    /// The catalog entries a lift can name (LC-01; LC-08, owner, task 09, LT15). Code names only (A2). The document stores
    /// just the ID: the fixed-grip quads by model (<see cref="SesselliftFgq4"/>, <see cref="MontaFg4"/>), every other type as
    /// <c>&lt;carrier&gt;:&lt;maker&gt;</c>, e.g. <c>detach-six:monta</c>. Only the fixed quads have their own models; the
    /// rest draw with them (the maker's quad, the Sessellift's for Chairworks) and carry placeholder numbers.
    /// </summary>
    public static class LiftCatalogIds
    {
        public const string SesselliftFgq4 = "sessellift-fgq4";
        public const string MontaFg4 = "monta-fg4";
        /// <summary>The Sessellift D6 detachable six-pack (LT41): the first detachable with its own models.</summary>
        public const string SesselliftD6 = "detach-six:sessellift";

        /// <summary>LT41 (owner, 2026-10-23): the types a player can build, the ones with their own models. Every other
        /// entry stays in the menu as a placeholder (greyed) until its models exist.</summary>
        public static bool Buildable(string id) => id == SesselliftFgq4 || id == MontaFg4 || id == SesselliftD6
            || id == SesselliftFgd2 || id == SesselliftFgt3 || id == MontaFg2 || id == MontaFg3 || id == SesselliftD4 || IsStern(id) || IsTow(id) || IsBelt(id);
        /// <summary>The Monta T-bar and J-bar (surface lifts, v0.3.2; the surface-lifts model thread's D1–D11): one rope low over
        /// the snow, riders towed up a track on the ground, a bottom drive. One carrier kind a line (D7), so the bar is in the id.</summary>
        public const string MontaTbar = "monta-tbar", MontaJbar = "monta-jbar";
        /// <summary>A surface tow: the riders stay on the snow (no chair clearance; the rope's height over the track is a band).</summary>
        public static bool IsTow(string id) => id == MontaTbar || id == MontaJbar;
        /// <summary>The Glidepath magic carpets (surface lifts PR B): single or double belt (C9), uncovered or under a gallery
        /// (C3, C10), each its own id so the save keeps them with no new field.</summary>
        public const string GlidepathCarpet = "glidepath-carpet", GlidepathCarpetCovered = "glidepath-carpet-covered",
                            GlidepathCarpet2 = "glidepath-carpet2", GlidepathCarpet2Covered = "glidepath-carpet2-covered";
        /// <summary>A belt lift: no rope, towers or carriers; its bed instead (<see cref="CarpetBed"/>).</summary>
        public static bool IsBelt(string id) => id == GlidepathCarpet || id == GlidepathCarpetCovered || id == GlidepathCarpet2 || id == GlidepathCarpet2Covered;
        public static bool IsDoubleBelt(string id) => id == GlidepathCarpet2 || id == GlidepathCarpet2Covered;
        public static bool IsCovered(string id) => id == GlidepathCarpetCovered || id == GlidepathCarpet2Covered;
        /// <summary>The Sessellift Stern, the second Sessellift fixed-grip style (v0.3.2, owner): quad, triple and double, a
        /// bottom drive-and-tension station and a fixed top return, ids by model as the fixed quads' are (save v10).</summary>
        public const string SesselliftStern4 = "sessellift-stern4", SesselliftStern3 = "sessellift-stern3", SesselliftStern2 = "sessellift-stern2";
        public static bool IsStern(string id) => id == SesselliftStern4 || id == SesselliftStern3 || id == SesselliftStern2;
        /// <summary>LD9: whether the player chooses the drive end. The fixed grips with a head for each end do; the Stern (bottom
        /// drive only), the detachables and the placeholders don't.</summary>
        public static bool DriveChoice(string id) => id == SesselliftFgq4 || id == MontaFg4 || id == SesselliftFgd2 || id == SesselliftFgt3
            || id == MontaFg2 || id == MontaFg3;
        /// <summary>LD9: the end that drives unless the player chooses: the Sessellift's top (as built since task 07), the Monta's
        /// and the Stern's bottom. The v10 migration gives every older lift this.</summary>
        public static LiftDrive DefaultDrive(string id) => Maker(id) == "monta" || IsStern(id) ? LiftDrive.Bottom : LiftDrive.Top;
        /// <summary>The Sessellift D4 detachable quad (v0.3.2, the D4 model thread).</summary>
        public const string SesselliftD4 = "detach-quad:sessellift";
        /// <summary>The fixed-grip doubles and triples (v0.3.2, owner LD1): Sessellift FGD-2 / FGT-3 and Monta FG2 / FG3.</summary>
        public const string SesselliftFgd2 = "fixed-double:sessellift", SesselliftFgt3 = "fixed-triple:sessellift",
                            MontaFg2 = "fixed-double:monta", MontaFg3 = "fixed-triple:monta";

        /// <summary>The carriers other than the fixed quad, in menu order (LC-08).</summary>
        public static readonly string[] Carriers =
        {
            "fixed-double", "fixed-triple", "detach-quad", "detach-six", "detach-eight", "gondola-6", "gondola-8", "gondola-10",
            "tram-60", "tram-100",
        };
        /// <summary>The code-name makers (A2).</summary>
        public static readonly string[] Makers = { "sessellift", "monta", "chairworks" };

        public static string Of(string carrier, string maker) => carrier + ":" + maker;

        public static bool Known(string id)
        {
            if (id == SesselliftFgq4 || id == MontaFg4 || IsStern(id) || IsTow(id) || IsBelt(id)) return true;
            if (id == null) return false;
            int colon = id.IndexOf(':');
            if (colon < 0) return false;
            return Array.IndexOf(Carriers, id.Substring(0, colon)) >= 0 && Array.IndexOf(Makers, id.Substring(colon + 1)) >= 0;
        }

        /// <summary>The carrier part ("fixed-quad" for the two models).</summary>
        public static string Carrier(string id)
        {
            if (id == SesselliftFgq4 || id == MontaFg4 || id == SesselliftStern4) return "fixed-quad";
            if (id == SesselliftStern3) return "fixed-triple";
            if (id == SesselliftStern2) return "fixed-double";
            if (id == MontaTbar) return "tbar";
            if (id == MontaJbar) return "jbar";
            if (IsBelt(id)) return "carpet";
            int colon = id?.IndexOf(':') ?? -1;
            return colon < 0 ? id : id.Substring(0, colon);
        }

        /// <summary>The maker part.</summary>
        public static string Maker(string id)
        {
            if (id == MontaFg4 || IsTow(id)) return "monta";
            if (id == SesselliftFgq4 || IsStern(id)) return "sessellift";
            if (IsBelt(id)) return "glidepath";
            int colon = id?.IndexOf(':') ?? -1;
            return colon < 0 ? "sessellift" : id.Substring(colon + 1);
        }

        /// <summary>TW-02 with LC-08: trams stand on towers up to 250 ft, every other lift up to 100 ft.</summary>
        public static int TowerMaxMm(string id) =>
            IsTow(id) ? DesignUnits.TowTowerMaxMm
            : id != null && id.StartsWith("tram-", StringComparison.Ordinal) ? DesignUnits.TramTowerMaxMm : DesignUnits.TowerMaxMm;
        /// <summary>TW-02: a line tower's least height (25 ft; a surface tow's 15 ft, its rope low over the snow).</summary>
        public static int TowerMinMm(string id) => IsTow(id) ? DesignUnits.TowTowerMinMm : DesignUnits.TowerMinMm;
        /// <summary>TW-02 v3: a station tower's least steel (15 ft; a surface tow's 10 ft).</summary>
        public static int StationTowerMinMm(string id) => IsTow(id) ? DesignUnits.TowStationTowerMinMm : DesignUnits.StationTowerMinMm;
    }

    /// <summary>
    /// TW-04 v3 (owner, LT44, second review E6): the kind of head the player chose for a tower set by hand. Auto is the
    /// game's choice from the rope's bend (TW-04, as before); the others fix the kind and the game sizes its sheaves. A
    /// kind that can't carry the rope there is red with its reason (TW-05), never changed. Saved (save v3).
    /// </summary>
    public enum TowerKind : byte
    {
        Auto = 0,
        Support = 1,
        HoldDown = 2,
        Combination = 3,
    }

    /// <summary>
    /// A tower as authored (TW-15): its position along the line from the bottom terminal's origin and its height, the
    /// rope's height above the ground there (TW-02), and the kind of head chosen for it (LT44; Auto almost always). Its
    /// head, forces and rope geometry are derived (SH-05).
    /// </summary>
    public readonly struct Tower : IEquatable<Tower>
    {
        public readonly TowerId Id;
        /// <summary>Horizontal distance along the line from the bottom terminal's origin, in mm.</summary>
        public readonly int AlongMm;
        /// <summary>25–100 ft in 5 ft steps (TW-02), in mm.</summary>
        public readonly int HeightMm;
        /// <summary>LT44: the head's kind as chosen, or Auto.</summary>
        public readonly TowerKind Kind;

        public Tower(TowerId id, int alongMm, int heightMm, TowerKind kind = TowerKind.Auto)
        {
            Id = id;
            AlongMm = alongMm;
            HeightMm = heightMm;
            Kind = kind;
        }

        /// <summary>The same tower elsewhere along the line, or at another height, or of another kind.</summary>
        public Tower WithAlong(int alongMm) => new Tower(Id, alongMm, HeightMm, Kind);
        public Tower WithHeight(int heightMm) => new Tower(Id, AlongMm, heightMm, Kind);
        public Tower WithKind(TowerKind kind) => new Tower(Id, AlongMm, HeightMm, kind);
        public Tower WithId(TowerId id) => new Tower(id, AlongMm, HeightMm, Kind);
        public TowerSpec Spec => new TowerSpec(AlongMm, HeightMm, Kind);

        public bool Equals(Tower o) => Id == o.Id && AlongMm == o.AlongMm && HeightMm == o.HeightMm && Kind == o.Kind;
        public override bool Equals(object obj) => obj is Tower o && Equals(o);
        public override int GetHashCode() => Id.GetHashCode() ^ AlongMm ^ (HeightMm << 7) ^ ((int)Kind << 29);
    }

    /// <summary>
    /// SK14, LF-12 v3 (task 09, save v2): the base elevation each terminal stands at, its load or unload level, as authored:
    /// the ground at its origin when it was built, raised for a steep site (LF-08 v2) or a last resort (TW-16 v3), or set by
    /// hand. <b>Absolute</b> elevation in whole millimetres in the package's height datum, unlike the plan points (TE5),
    /// which are millimetres from the site's centre. A raise isn't in the terrain (pads only cut), so it's stored.
    /// </summary>
    public readonly struct LiftLevels : IEquatable<LiftLevels>
    {
        public readonly int BottomMm;
        public readonly int TopMm;
        public LiftLevels(int bottomMm, int topMm) { BottomMm = bottomMm; TopMm = topMm; }
        public static LiftLevels FromMetres(double bottom, double top) =>
            new LiftLevels((int)Math.Round(bottom * 1000, MidpointRounding.AwayFromZero), (int)Math.Round(top * 1000, MidpointRounding.AwayFromZero));
        public double BottomMetres => BottomMm / 1000.0;
        public double TopMetres => TopMm / 1000.0;
        public bool Equals(LiftLevels o) => BottomMm == o.BottomMm && TopMm == o.TopMm;
        public override bool Equals(object obj) => obj is LiftLevels o && Equals(o);
        public override int GetHashCode() => BottomMm * 397 ^ TopMm;
    }

    /// <summary>A tower to place, before it has an ID.</summary>
    public readonly struct TowerSpec
    {
        public readonly int AlongMm;
        public readonly int HeightMm;
        public readonly TowerKind Kind;
        public TowerSpec(int alongMm, int heightMm, TowerKind kind = TowerKind.Auto) { AlongMm = alongMm; HeightMm = heightMm; Kind = kind; }
    }

    /// <summary>
    /// A fixed-grip chairlift (LF-07): one straight line, bottom terminal first, a catalog entry, the tower mode and
    /// stance, and its towers in order along the line (TW-15). Ropes, chairs, heads and clearances are derived by task
    /// 09. Immutable.
    /// </summary>
    public sealed class Lift
    {
        public LiftId Id { get; }
        public string Name { get; }
        public WorkStage Stage { get; }
        public string CatalogId { get; }
        /// <summary>The bottom terminal's origin, the centre of its bullwheel (LF-02).</summary>
        public PointMm Bottom { get; }
        /// <summary>The top terminal's origin (LF-03).</summary>
        public PointMm Top { get; }
        public TowerMode TowerMode { get; }
        public TowerStance Stance { get; }
        /// <summary>Sorted by <see cref="Tower.AlongMm"/>; no two share a position.</summary>
        public IReadOnlyList<Tower> Towers => _towers;
        /// <summary>The terminals' levels (save v2); null where they aren't known (a v1 save's lift: derived from the
        /// ground when it opens, as before).</summary>
        public LiftLevels? Levels { get; }
        /// <summary>LD9 (save v10): the end that drives; always the type's own end where the player doesn't choose
        /// (<see cref="LiftCatalogIds.DriveChoice"/>).</summary>
        public LiftDrive Drive { get; }
        /// <summary>LO-09 (design shape 12): every operating day, or peak days only. Read from task 4.11.</summary>
        public LiftSchedule Schedule { get; }
        /// <summary>LO-07 v2 (design shape 12): this lift's own hours, or null for the resort's. Read from task 4.11.</summary>
        public DayHours? Hours { get; }

        readonly Tower[] _towers;

        public Lift(LiftId id, string name, WorkStage stage, string catalogId, PointMm bottom, PointMm top, TowerMode towerMode, TowerStance stance, IReadOnlyList<Tower> towers,
                    LiftLevels? levels = null, LiftDrive? drive = null, LiftSchedule schedule = LiftSchedule.Daily, DayHours? hours = null)
        {
            Levels = levels;
            Schedule = schedule;
            Hours = hours;
            Drive = LiftCatalogIds.DriveChoice(catalogId) && drive.HasValue ? drive.Value : LiftCatalogIds.DefaultDrive(catalogId);
            Id = id;
            Name = name;
            Stage = stage;
            CatalogId = catalogId;
            Bottom = bottom;
            Top = top;
            TowerMode = towerMode;
            Stance = stance;
            _towers = new Tower[towers?.Count ?? 0];
            for (int i = 0; i < _towers.Length; i++) _towers[i] = towers[i];
            Array.Sort(_towers, (a, b) => a.AlongMm != b.AlongMm ? a.AlongMm.CompareTo(b.AlongMm) : a.Id.CompareTo(b.Id));
        }

        /// <summary>The horizontal length, bullwheel to bullwheel, squared (exact).</summary>
        public long LengthSquaredMm => Bottom.DistanceSquared(Top);
        public double LengthMetres => Math.Sqrt(LengthSquaredMm) / 1000.0;

        public int IndexOfTower(TowerId id)
        {
            for (int i = 0; i < _towers.Length; i++) if (_towers[i].Id == id) return i;
            return -1;
        }

        public Lift WithName(string name) => new Lift(Id, name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, Levels, Drive, Schedule, Hours);
        public Lift WithStage(WorkStage stage) => new Lift(Id, Name, stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, Levels, Drive, Schedule, Hours);
        public Lift WithTowerMode(TowerMode mode) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, mode, Stance, _towers, Levels, Drive, Schedule, Hours);
        public Lift WithStance(TowerStance stance) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, stance, _towers, Levels, Drive, Schedule, Hours);
        public Lift WithLevels(LiftLevels? levels) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, levels, Drive, Schedule, Hours);
        public Lift WithTowers(IReadOnlyList<Tower> towers) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, towers, Levels, Drive, Schedule, Hours);
        public Lift WithDrive(LiftDrive drive) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, Levels, drive, Schedule, Hours);
        public Lift WithSchedule(LiftSchedule schedule, DayHours? hours) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, Levels, Drive, schedule, hours);
    }
}
