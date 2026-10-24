using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>LF-01. Values are saved: never renumber.</summary>
    public enum TowerMode : byte { Automatic = 1, ByHand = 2 }

    /// <summary>TW-14: plumb by default, or leaned square to the rope.</summary>
    public enum TowerStance : byte { Plumb = 1, Leaned = 2 }

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
        public static bool Buildable(string id) => id == SesselliftFgq4 || id == MontaFg4 || id == SesselliftD6;

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
            if (id == SesselliftFgq4 || id == MontaFg4) return true;
            if (id == null) return false;
            int colon = id.IndexOf(':');
            if (colon < 0) return false;
            return Array.IndexOf(Carriers, id.Substring(0, colon)) >= 0 && Array.IndexOf(Makers, id.Substring(colon + 1)) >= 0;
        }

        /// <summary>The carrier part ("fixed-quad" for the two models).</summary>
        public static string Carrier(string id)
        {
            if (id == SesselliftFgq4 || id == MontaFg4) return "fixed-quad";
            int colon = id?.IndexOf(':') ?? -1;
            return colon < 0 ? id : id.Substring(0, colon);
        }

        /// <summary>The maker part.</summary>
        public static string Maker(string id)
        {
            if (id == MontaFg4) return "monta";
            if (id == SesselliftFgq4) return "sessellift";
            int colon = id?.IndexOf(':') ?? -1;
            return colon < 0 ? "sessellift" : id.Substring(colon + 1);
        }

        /// <summary>TW-02 with LC-08: trams stand on towers up to 250 ft, every other lift up to 100 ft.</summary>
        public static int TowerMaxMm(string id) =>
            id != null && id.StartsWith("tram-", StringComparison.Ordinal) ? DesignUnits.TramTowerMaxMm : DesignUnits.TowerMaxMm;
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

        readonly Tower[] _towers;

        public Lift(LiftId id, string name, WorkStage stage, string catalogId, PointMm bottom, PointMm top, TowerMode towerMode, TowerStance stance, IReadOnlyList<Tower> towers,
                    LiftLevels? levels = null)
        {
            Levels = levels;
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

        public Lift WithName(string name) => new Lift(Id, name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, Levels);
        public Lift WithStage(WorkStage stage) => new Lift(Id, Name, stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, Levels);
        public Lift WithTowerMode(TowerMode mode) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, mode, Stance, _towers, Levels);
        public Lift WithStance(TowerStance stance) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, stance, _towers, Levels);
        public Lift WithLevels(LiftLevels? levels) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, _towers, levels);
        public Lift WithTowers(IReadOnlyList<Tower> towers) => new Lift(Id, Name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, towers, Levels);
    }
}
