using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Adds a lift (LF-06's Break ground or Save plan): the two terminals, bottom first (LF-02, LF-03), its type, the
    /// tower mode and stance, and its towers (TW-15). Siting, spacing and clearance are task 09's analysis.
    /// </summary>
    public sealed class AddLift : DesignCommand
    {
        public string Name { get; }
        public string CatalogId { get; }
        public PointMm Bottom { get; }
        public PointMm Top { get; }
        public TowerMode TowerMode { get; }
        public TowerStance Stance { get; }
        public IReadOnlyList<TowerSpec> Towers { get; }
        public WorkStage Stage { get; }
        /// <summary>The terminals' levels as the build settled them (SK14, save v2); null leaves them to the ground.</summary>
        public LiftLevels? Levels { get; }
        /// <summary>LD9 (save v10): the end that drives; null for the type's own.</summary>
        public LiftDrive? Drive { get; }
        /// <summary>Shape 13: the plate; null takes the lowest free number (<see cref="DesignRules.NextPlate"/>).</summary>
        public string Plate { get; }

        public AddLift(long baseRevision, string name, string catalogId, PointMm bottom, PointMm top, TowerMode towerMode, TowerStance stance,
            IReadOnlyList<TowerSpec> towers, WorkStage stage, LiftLevels? levels = null, LiftDrive? drive = null, string plate = null) : base(baseRevision)
        {
            Plate = plate;
            Drive = drive;
            Levels = levels;
            Name = name;
            CatalogId = catalogId;
            Bottom = bottom;
            Top = top;
            TowerMode = towerMode;
            Stance = stance;
            Towers = towers ?? System.Array.Empty<TowerSpec>();
            Stage = stage;
        }

        public override string Label => "Add lift";

        protected override CommandResult Run(DesignDocument doc)
        {
            string problem = DesignRules.NameProblem(Name) ?? DesignRules.StageProblem(Stage)
                ?? DesignRules.LiftLineProblem(CatalogId, Bottom, Top, TowerMode, Stance) ?? DesignRules.BuildableProblem(CatalogId);
            if (problem != null) return Invalid(doc, problem);
            if (Plate != null && (DesignRules.PlateProblem(Plate) ?? PlateTaken(doc, Plate, default)) is string pp) return Invalid(doc, pp);
            var mint = new IdMint(doc);
            var created = new List<ulong>();
            var id = mint.Lift();
            created.Add(id.Value);
            var towers = LiftTowers.Mint(ref mint, Towers, created);
            var lift = new Lift(id, Name, Stage, CatalogId, Bottom, Top, TowerMode, Stance, towers, Levels, Drive, plate: Plate ?? DesignRules.NextPlate(doc));
            if (DesignRules.TowersProblem(lift.LengthSquaredMm, lift.Towers, lift.CatalogId) is string t) return Invalid(doc, t);
            return CommandResult.Accept(doc.WithOrdinal(mint.NextOrdinal).PutLift(lift), created);
        }

        /// <summary>Why another lift (not <paramref name="self"/>) already wears <paramref name="plate"/>, or null.</summary>
        internal static string PlateTaken(DesignDocument doc, string plate, LiftId self)
        {
            foreach (var l in doc.Lifts)
                if (l.Id != self && string.Equals(l.Plate, plate, StringComparison.OrdinalIgnoreCase)) return $"{l.Name} already has plate {l.Plate}.";
            return null;
        }
    }

    /// <summary>LF-04: places one tower by hand.</summary>
    public sealed class AddTower : DesignCommand
    {
        public LiftId Lift { get; }
        public TowerSpec Tower { get; }

        public AddTower(long baseRevision, LiftId lift, TowerSpec tower) : base(baseRevision)
        {
            Lift = lift;
            Tower = tower;
        }

        public override string Label => "Place tower";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            var mint = new IdMint(doc);
            var id = mint.Tower();
            var towers = new List<Tower>(l.Towers) { new Tower(id, Tower.AlongMm, Tower.HeightMm, Tower.Kind) };
            var next = l.WithTowers(towers);
            if (DesignRules.TowersProblem(next.LengthSquaredMm, next.Towers, next.CatalogId) is string p) return Invalid(doc, p);
            return CommandResult.Accept(doc.WithOrdinal(mint.NextOrdinal).PutLift(next), id.Value);
        }
    }

    /// <summary>LF-04: slides a tower along the line and/or re-heights it (Page Up / Page Down's ±5 ft).</summary>
    public sealed class EditTower : DesignCommand
    {
        public TowerId Tower { get; }
        public int AlongMm { get; }
        public int HeightMm { get; }

        public EditTower(long baseRevision, TowerId tower, int alongMm, int heightMm) : base(baseRevision)
        {
            Tower = tower;
            AlongMm = alongMm;
            HeightMm = heightMm;
        }

        public override string Label => "Move tower";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLiftOfTower(Tower);
            if (l == null) return Missing(doc, "That tower");
            int i = l.IndexOfTower(Tower);
            if (l.Towers[i].AlongMm == AlongMm && l.Towers[i].HeightMm == HeightMm) return Invalid(doc, "Nothing changes.");
            var towers = new List<Tower>(l.Towers);
            towers[i] = new Tower(Tower, AlongMm, HeightMm, l.Towers[i].Kind);
            var next = l.WithTowers(towers);
            if (DesignRules.TowersProblem(next.LengthSquaredMm, next.Towers, next.CatalogId) is string p) return Invalid(doc, p);
            return CommandResult.Accept(doc.PutLift(next));
        }
    }

    /// <summary>LF-04: Delete on the selected tower.</summary>
    public sealed class DeleteTower : DesignCommand
    {
        public TowerId Tower { get; }

        public DeleteTower(long baseRevision, TowerId tower) : base(baseRevision) { Tower = tower; }

        public override string Label => "Delete tower";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLiftOfTower(Tower);
            if (l == null) return Missing(doc, "That tower");
            var towers = new List<Tower>(l.Towers);
            towers.RemoveAt(l.IndexOfTower(Tower));
            return CommandResult.Accept(doc.PutLift(l.WithTowers(towers)));
        }
    }

    /// <summary>
    /// Replaces every tower at once, optionally switching the mode in the same step: Automatic placement, Re-place
    /// towers (LF-06) and Fill the rest (LF-04, the kept towers listed first). New towers get new IDs.
    /// </summary>
    public sealed class ReplaceTowers : DesignCommand
    {
        public LiftId Lift { get; }
        /// <summary>Towers kept as they are (by ID); the rest are removed.</summary>
        public IReadOnlyList<TowerId> Keep { get; }
        public IReadOnlyList<TowerSpec> Added { get; }
        /// <summary>The mode afterwards, or null to keep it.</summary>
        public TowerMode? Mode { get; }

        public ReplaceTowers(long baseRevision, LiftId lift, IReadOnlyList<TowerId> keep, IReadOnlyList<TowerSpec> added, TowerMode? mode = null)
            : base(baseRevision)
        {
            Lift = lift;
            Keep = keep ?? System.Array.Empty<TowerId>();
            Added = added ?? System.Array.Empty<TowerSpec>();
            Mode = mode;
        }

        public override string Label => "Place towers";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            if (Mode.HasValue && Mode.Value != TowerMode.Automatic && Mode.Value != TowerMode.ByHand) return Invalid(doc, "Unknown tower mode.");
            var towers = new List<Tower>();
            var seen = new HashSet<ulong>();
            foreach (var id in Keep)
            {
                int i = l.IndexOfTower(id);
                if (i < 0) return Missing(doc, "That tower");
                if (!seen.Add(id.Value)) return Invalid(doc, "A tower is listed twice.");
                towers.Add(l.Towers[i]);
            }
            var mint = new IdMint(doc);
            var created = new List<ulong>();
            towers.AddRange(LiftTowers.Mint(ref mint, Added, created));
            var next = l.WithTowers(towers);
            if (Mode.HasValue) next = next.WithTowerMode(Mode.Value);
            if (DesignRules.TowersProblem(next.LengthSquaredMm, next.Towers, next.CatalogId) is string p) return Invalid(doc, p);
            return CommandResult.Accept(doc.WithOrdinal(mint.NextOrdinal).PutLift(next), created);
        }
    }

    /// <summary>LF-01: Automatic or By hand. Switching keeps the towers; re-placing them is <see cref="ReplaceTowers"/>.</summary>
    public sealed class SetTowerMode : DesignCommand
    {
        public LiftId Lift { get; }
        public TowerMode Mode { get; }

        public SetTowerMode(long baseRevision, LiftId lift, TowerMode mode) : base(baseRevision)
        {
            Lift = lift;
            Mode = mode;
        }

        public override string Label => "Change tower mode";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            if (Mode != TowerMode.Automatic && Mode != TowerMode.ByHand) return Invalid(doc, "Unknown tower mode.");
            if (l.TowerMode == Mode) return Invalid(doc, "Nothing changes.");
            return CommandResult.Accept(doc.PutLift(l.WithTowerMode(Mode)));
        }
    }

    /// <summary>TW-14: Towers stand Plumb or Leaned.</summary>
    public sealed class SetTowerStance : DesignCommand
    {
        public LiftId Lift { get; }
        public TowerStance Stance { get; }

        public SetTowerStance(long baseRevision, LiftId lift, TowerStance stance) : base(baseRevision)
        {
            Lift = lift;
            Stance = stance;
        }

        public override string Label => "Change tower stance";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            if (Stance != TowerStance.Plumb && Stance != TowerStance.Leaned) return Invalid(doc, "Unknown tower stance.");
            if (l.Stance == Stance) return Invalid(doc, "Nothing changes.");
            return CommandResult.Accept(doc.PutLift(l.WithStance(Stance)));
        }
    }

    /// <summary>
    /// LO-07 v2 (owner, 2026-11-08; shape 12): sets the resort's opening, last chair and stop. Whole minutes; the simulation
    /// trims them to each day's daylight (it never refuses a day for being dark). No player UI before 4.17; the F1 panel and
    /// tests use it.
    /// </summary>
    public sealed class SetResortHours : DesignCommand
    {
        public DayHours Hours { get; }

        public SetResortHours(long baseRevision, DayHours hours) : base(baseRevision) { Hours = hours; }

        public override string Label => "Change the resort's hours";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Hours.Problem is string p) return Invalid(doc, char.ToUpperInvariant(p[0]) + p.Substring(1) + ".");
            if (doc.Hours == Hours) return Invalid(doc, "Those are already the resort's hours.");
            return CommandResult.Accept(doc.WithHours(Hours));
        }
    }

    /// <summary>
    /// LO-09 (shape 12): a lift's schedule (daily, or peak days only) and its own hours, or none to follow the resort's.
    /// One undo step. No player UI before 4.11; the F1 panel and tests use it.
    /// </summary>
    public sealed class SetLiftSchedule : DesignCommand
    {
        public LiftId Lift { get; }
        public LiftSchedule Schedule { get; }
        public DayHours? Hours { get; }

        public SetLiftSchedule(long baseRevision, LiftId lift, LiftSchedule schedule, DayHours? hours) : base(baseRevision)
        {
            Lift = lift;
            Schedule = schedule;
            Hours = hours;
        }

        public override string Label => "Change lift schedule";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            if (Schedule != LiftSchedule.Daily && Schedule != LiftSchedule.PeakDays) return Invalid(doc, "That isn't a schedule.");
            if (Hours is DayHours h && h.Problem is string p) return Invalid(doc, char.ToUpperInvariant(p[0]) + p.Substring(1) + ".");
            if (l.Schedule == Schedule && Nullable.Equals(l.Hours, Hours)) return Invalid(doc, "Nothing changes.");
            return CommandResult.Accept(doc.PutLift(l.WithSchedule(Schedule, Hours)));
        }
    }

    public sealed class RenameLift : DesignCommand
    {
        public LiftId Lift { get; }
        public string Name { get; }

        public RenameLift(long baseRevision, LiftId lift, string name) : base(baseRevision)
        {
            Lift = lift;
            Name = name;
        }

        public override string Label => "Rename lift";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            if (DesignRules.NameProblem(Name) is string p) return Invalid(doc, p);
            if (l.Name == Name) return Invalid(doc, "That's already its name.");
            return CommandResult.Accept(doc.PutLift(l.WithName(Name)));
        }
    }

    /// <summary>
    /// Task 4.11 PR B (owner 2026-11-08, shape 13): a lift's plate, one to three letters or digits, unique among the lifts
    /// (letters compared without case). One undo step, like a rename.
    /// </summary>
    public sealed class SetLiftPlate : DesignCommand
    {
        public LiftId Lift { get; }
        public string Plate { get; }

        public SetLiftPlate(long baseRevision, LiftId lift, string plate) : base(baseRevision)
        {
            Lift = lift;
            Plate = plate;
        }

        public override string Label => "Change lift plate";

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            if (DesignRules.PlateProblem(Plate) is string p) return Invalid(doc, p);
            if (l.Plate == Plate) return Invalid(doc, "That's already its plate.");
            if (AddLift.PlateTaken(doc, Plate, l.Id) is string t) return Invalid(doc, t);
            return CommandResult.Accept(doc.PutLift(l.WithPlate(Plate)));
        }
    }

    public sealed class DeleteLift : DesignCommand
    {
        public LiftId Lift { get; }

        public DeleteLift(long baseRevision, LiftId lift) : base(baseRevision) { Lift = lift; }

        public override string Label => "Delete lift";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (doc.FindLift(Lift) == null) return Missing(doc, "That lift");
            return CommandResult.Accept(doc.RemoveLift(Lift));
        }
    }

    /// <summary>What an <see cref="EditLift"/> changes, for its undo label.</summary>
    public enum LiftEdit : byte { Edit, MoveTerminal, ChangeType, TerminalLevel, DriveEnd }

    /// <summary>
    /// Edit lift's Apply (task 09, LT2, LT9): one step that changes a built or planned lift's line, type, mode, stance,
    /// towers and terminal levels together, so one Ctrl+Z undoes the whole edit (CF-05). Covers moving either terminal
    /// anywhere (LF-05 v2), changing the type (P3-06 round 2), setting a terminal's level (SK14) and moving towers along the
    /// line and in height. Towers listed in <see cref="Kept"/> keep their IDs (with their new place and height); the rest are
    /// removed; <see cref="Added"/> get new IDs. A <see cref="Name"/> renames it in the same step (F5, LT39).
    /// </summary>
    public sealed class EditLift : DesignCommand
    {
        public LiftId Lift { get; }
        public string CatalogId { get; }
        public PointMm Bottom { get; }
        public PointMm Top { get; }
        public TowerMode TowerMode { get; }
        public TowerStance Stance { get; }
        public IReadOnlyList<Tower> Kept { get; }
        public IReadOnlyList<TowerSpec> Added { get; }
        public LiftLevels? Levels { get; }
        public LiftEdit Kind { get; }
        /// <summary>The lift's new name, or null to keep it.</summary>
        public string Name { get; }
        /// <summary>LD9: the end that drives, or null to keep it (the type's own where the player doesn't choose).</summary>
        public LiftDrive? Drive { get; }

        public EditLift(long baseRevision, LiftId lift, string catalogId, PointMm bottom, PointMm top, TowerMode towerMode, TowerStance stance,
                        IReadOnlyList<Tower> kept, IReadOnlyList<TowerSpec> added, LiftLevels? levels, LiftEdit kind = LiftEdit.Edit, string name = null,
                        LiftDrive? drive = null)
            : base(baseRevision)
        {
            Drive = drive;
            Name = name;
            Lift = lift;
            CatalogId = catalogId;
            Bottom = bottom;
            Top = top;
            TowerMode = towerMode;
            Stance = stance;
            Kept = kept ?? System.Array.Empty<Tower>();
            Added = added ?? System.Array.Empty<TowerSpec>();
            Levels = levels;
            Kind = kind;
        }

        /// <summary>The lift as it stands, changed only where the caller says: the tool's starting point for a draft.</summary>
        public static EditLift From(long baseRevision, Lift l, LiftEdit kind = LiftEdit.Edit) =>
            new EditLift(baseRevision, l.Id, l.CatalogId, l.Bottom, l.Top, l.TowerMode, l.Stance, l.Towers, null, l.Levels, kind, null, l.Drive);

        public override string Label => Kind switch
        {
            LiftEdit.MoveTerminal => "Move terminal",
            LiftEdit.ChangeType => "Change lift type",
            LiftEdit.TerminalLevel => "Change terminal level",
            LiftEdit.DriveEnd => "Change drive end",
            _ => "Edit lift",
        };

        protected override CommandResult Run(DesignDocument doc)
        {
            var l = doc.FindLift(Lift);
            if (l == null) return Missing(doc, "That lift");
            if (DesignRules.LiftLineProblem(CatalogId, Bottom, Top, TowerMode, Stance) is string p) return Invalid(doc, p);
            // LT41: a lift already in the design keeps its type; changing to another needs one that can be built.
            if (CatalogId != l.CatalogId && DesignRules.BuildableProblem(CatalogId) is string b) return Invalid(doc, b);
            if (Name != null && DesignRules.NameProblem(Name) is string n) return Invalid(doc, n);
            var towers = new List<Tower>();
            var seen = new HashSet<ulong>();
            foreach (var t in Kept)
            {
                if (l.IndexOfTower(t.Id) < 0) return Missing(doc, "That tower");
                if (!seen.Add(t.Id.Value)) return Invalid(doc, "A tower is listed twice.");
                towers.Add(t);
            }
            var mint = new IdMint(doc);
            var created = new List<ulong>();
            towers.AddRange(LiftTowers.Mint(ref mint, Added, created));
            var next = new Lift(l.Id, Name ?? l.Name, l.Stage, CatalogId, Bottom, Top, TowerMode, Stance, towers, Levels, Drive ?? l.Drive, l.Schedule, l.Hours, l.Plate);
            if (DesignRules.TowersProblem(next.LengthSquaredMm, next.Towers, next.CatalogId) is string t2) return Invalid(doc, t2);
            if (DesignBytes.Same(l, next)) return Invalid(doc, "Nothing changes.");
            return CommandResult.Accept(doc.WithOrdinal(mint.NextOrdinal).PutLift(next), created);
        }
    }

    static class LiftTowers
    {
        public static List<Tower> Mint(ref IdMint mint, IReadOnlyList<TowerSpec> specs, List<ulong> created)
        {
            var towers = new List<Tower>(specs.Count);
            foreach (var s in specs)
            {
                var id = mint.Tower();
                created.Add(id.Value);
                towers.Add(new Tower(id, s.AlongMm, s.HeightMm, s.Kind));
            }
            return towers;
        }
    }
}
