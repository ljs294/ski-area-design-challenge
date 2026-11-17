using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 4.14's Build for a pond (PD-01…04): a new pond from its shoreline, full pool and dig. One command, one undo step.
    /// The ground checks (the berm, the capacity) are the tool's, from <see cref="PondEarthwork.Analyse"/>, before Build.
    /// </summary>
    public sealed class AddPond : DesignCommand
    {
        public string Name { get; }
        public IReadOnlyList<PointMm> Ring { get; }
        public int FullPoolMm { get; }
        public int ExcavationMm { get; }
        public PondFill Fill { get; }
        public bool StartsEmpty { get; }

        public AddPond(long baseRevision, string name, IReadOnlyList<PointMm> ring, int fullPoolMm, int excavationMm,
                       PondFill fill = PondFill.Natural, bool startsEmpty = false) : base(baseRevision)
        {
            Name = name;
            Ring = ring ?? Array.Empty<PointMm>();
            FullPoolMm = fullPoolMm;
            ExcavationMm = excavationMm;
            Fill = fill;
            StartsEmpty = startsEmpty;
        }

        public override string Label => "Dig a pond";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (doc.Ponds.Count + doc.Dams.Count >= PondRules.MaxWater) return Invalid(doc, $"The design holds at most {PondRules.MaxWater} ponds and dams.");
            var mint = new IdMint(doc);
            var pond = new Pond(mint.Pond(), Name, Ring, FullPoolMm, ExcavationMm, 0, Fill, StartsEmpty);
            if (PondRules.PondProblem(pond) is string p) return Invalid(doc, p);
            if (PondRules.OverlapProblem(doc, pond, null) is string o) return Invalid(doc, o);
            return CommandResult.Accept(doc.PutPond(pond).WithOrdinal(mint.NextOrdinal), pond.Id.Value);
        }
    }

    /// <summary>Task 4.14's Edit for a pond: its name, shoreline, full pool, dig and spring, replaced whole (one undo step).</summary>
    public sealed class EditPond : DesignCommand
    {
        public PondId Id { get; }
        public string Name { get; }
        public IReadOnlyList<PointMm> Ring { get; }
        public int FullPoolMm { get; }
        public int ExcavationMm { get; }
        public int SpringGpm { get; }
        public PondFill Fill { get; }
        public bool StartsEmpty { get; }

        public EditPond(long baseRevision, PondId id, string name, IReadOnlyList<PointMm> ring, int fullPoolMm, int excavationMm, int springGpm,
                        PondFill fill = PondFill.Natural, bool startsEmpty = false) : base(baseRevision)
        {
            Fill = fill;
            StartsEmpty = startsEmpty;
            Id = id;
            Name = name;
            Ring = ring ?? Array.Empty<PointMm>();
            FullPoolMm = fullPoolMm;
            ExcavationMm = excavationMm;
            SpringGpm = springGpm;
        }

        public override string Label => "Edit a pond";

        protected override CommandResult Run(DesignDocument doc)
        {
            var old = doc.FindPond(Id);
            if (old == null) return Missing(doc, "That pond");
            var pond = new Pond(Id, Name, Ring, FullPoolMm, ExcavationMm, SpringGpm, Fill, StartsEmpty);
            if (PondRules.PondProblem(pond) is string p) return Invalid(doc, p);
            if (PondRules.OverlapProblem(doc, pond, null) is string o) return Invalid(doc, o);
            return CommandResult.Accept(doc.PutPond(pond));
        }
    }

    /// <summary>Task 4.14's Build for a dam (PD-05…07): the two banks, full pool, the water side and the stream it crosses.</summary>
    public sealed class AddDam : DesignCommand
    {
        public string Name { get; }
        public PointMm BankA { get; }
        public PointMm BankB { get; }
        public int FullPoolMm { get; }
        public bool UpstreamLeft { get; }
        public SpillBank Spill { get; }
        public ulong StreamWay { get; }
        public bool StartsEmpty { get; }
        /// <summary>WS-03: the dammed stream's width set in the same step (whole cm, 0: OpenStreetMap's), or −1 to leave it.</summary>
        public int StreamWidthCm { get; }

        public AddDam(long baseRevision, string name, PointMm bankA, PointMm bankB, int fullPoolMm, bool upstreamLeft, SpillBank spill, ulong streamWay,
                      bool startsEmpty = false, int streamWidthCm = -1)
            : base(baseRevision)
        {
            Name = name;
            BankA = bankA;
            BankB = bankB;
            FullPoolMm = fullPoolMm;
            UpstreamLeft = upstreamLeft;
            Spill = spill;
            StreamWay = streamWay;
            StartsEmpty = startsEmpty;
            StreamWidthCm = streamWidthCm;
        }

        public override string Label => "Build a dam";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (doc.Ponds.Count + doc.Dams.Count >= PondRules.MaxWater) return Invalid(doc, $"The design holds at most {PondRules.MaxWater} ponds and dams.");
            var mint = new IdMint(doc);
            var dam = new Dam(mint.Dam(), Name, BankA, BankB, FullPoolMm, UpstreamLeft, Spill, StreamWay, StartsEmpty);
            if (PondRules.DamProblem(dam) is string p) return Invalid(doc, p);
            if (PondRules.OverlapProblem(doc, null, dam) is string o) return Invalid(doc, o);
            if (DamCommands.WithStreamWidth(doc.PutDam(dam), StreamWay, StreamWidthCm, out var problem) is DesignDocument next)
                return CommandResult.Accept(next.WithOrdinal(mint.NextOrdinal), dam.Id.Value);
            return Invalid(doc, problem);
        }
    }

    /// <summary>Task 4.14's Edit for a dam: replaced whole (its line, pool and water side re-snapped by the tool; one undo step).</summary>
    public sealed class EditDam : DesignCommand
    {
        public Dam Next { get; }
        /// <summary>WS-03: the dammed stream's width set in the same step (whole cm), or −1 to leave it.</summary>
        public int StreamWidthCm { get; }

        public EditDam(long baseRevision, Dam next, int streamWidthCm = -1) : base(baseRevision)
        {
            Next = next ?? throw new ArgumentNullException(nameof(next));
            StreamWidthCm = streamWidthCm;
        }

        public override string Label => "Edit a dam";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (doc.FindDam(Next.Id) == null) return Missing(doc, "That dam");
            if (PondRules.DamProblem(Next) is string p) return Invalid(doc, p);
            if (PondRules.OverlapProblem(doc, null, Next) is string o) return Invalid(doc, o);
            if (DamCommands.WithStreamWidth(doc.PutDam(Next), Next.StreamWay, StreamWidthCm, out var problem) is DesignDocument next)
                return CommandResult.Accept(next);
            return Invalid(doc, problem);
        }
    }

    static class DamCommands
    {
        /// <summary>The stream's record with the width a dam's review set (WS-03, LK-10), or the document as it was.</summary>
        internal static DesignDocument WithStreamWidth(DesignDocument doc, ulong way, int widthCm, out string problem)
        {
            problem = null;
            if (widthCm < 0 || way == 0) return doc;
            var next = doc.FindStream(way).WithWidth(widthCm);
            problem = PondRules.StreamProblem(next);
            return problem == null ? doc.PutStream(next) : null;
        }
    }

    /// <summary>
    /// Task 4.14's Remove (PD-08, owner: fixed from MapLibre, whose berm stayed in the ground): the pond or dam leaves the
    /// design, so the fold puts the ground back exactly as it was under it.
    /// </summary>
    public sealed class RemoveWater : DesignCommand
    {
        public ulong Id { get; }

        public RemoveWater(long baseRevision, ulong id) : base(baseRevision) { Id = id; }

        public override string Label => "Remove water";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (doc.FindPond(new PondId(Id)) != null) return CommandResult.Accept(doc.RemovePond(new PondId(Id)));
            if (doc.FindDam(new DamId(Id)) != null) return CommandResult.Accept(doc.RemoveDam(new DamId(Id)));
            return Missing(doc, "That pond or dam");
        }
    }

    /// <summary>Task 4.14's Water source switch (WS-01, LK-02): a lake on or off as a source, with its depth override; its name stays.</summary>
    public sealed class SetLakeSource : DesignCommand
    {
        public ulong Lake { get; }
        public bool On { get; }
        public int DepthTenthsFt { get; }

        public SetLakeSource(long baseRevision, ulong lake, bool on, int depthTenthsFt = 0) : base(baseRevision)
        {
            Lake = lake;
            On = on;
            DepthTenthsFt = depthTenthsFt;
        }

        public override string Label => On ? "Use a lake for water" : "Stop using a lake";

        protected override CommandResult Run(DesignDocument doc)
        {
            var old = doc.FindLake(Lake);
            var next = old.WithSource(On).WithDepth(DepthTenthsFt);
            if (PondRules.LakeProblem(next) is string p) return Invalid(doc, p);
            if (next.Equals(old)) return Invalid(doc, "Nothing to change.");
            return CommandResult.Accept(doc.PutLake(next));
        }
    }

    /// <summary>LK-10: a lake's name ("" brings OpenStreetMap's back) or depth override, its source switch untouched.</summary>
    public sealed class SetLake : DesignCommand
    {
        public ulong Lake { get; }
        public string Name { get; }
        public int DepthTenthsFt { get; }

        public SetLake(long baseRevision, ulong lake, string name, int depthTenthsFt) : base(baseRevision)
        {
            Lake = lake;
            Name = name ?? "";
            DepthTenthsFt = depthTenthsFt;
        }

        public override string Label => "Change a lake";

        protected override CommandResult Run(DesignDocument doc)
        {
            var old = doc.FindLake(Lake);
            var next = old.WithName(Name).WithDepth(DepthTenthsFt);
            if (PondRules.LakeProblem(next) is string p) return Invalid(doc, p);
            if (next.Equals(old)) return Invalid(doc, "Nothing to change.");
            return CommandResult.Accept(doc.PutLake(next));
        }
    }

    /// <summary>LK-10, WS-03: a stream's name ("" brings OpenStreetMap's back) and width override (0: OpenStreetMap's or the default).</summary>
    public sealed class SetStream : DesignCommand
    {
        public ulong Way { get; }
        public string Name { get; }
        public int WidthCm { get; }

        public SetStream(long baseRevision, ulong way, string name, int widthCm) : base(baseRevision)
        {
            Way = way;
            Name = name ?? "";
            WidthCm = widthCm;
        }

        public override string Label => "Change a stream";

        protected override CommandResult Run(DesignDocument doc)
        {
            var old = doc.FindStream(Way);
            var next = new StreamRecord(Way, Name, WidthCm);
            if (PondRules.StreamProblem(next) is string p) return Invalid(doc, p);
            if (next.Equals(old)) return Invalid(doc, "Nothing to change.");
            return CommandResult.Accept(doc.PutStream(next));
        }
    }

    /// <summary>LK-10: a pond's or dam's name (its card's click or F2), no regrade.</summary>
    public sealed class RenameWater : DesignCommand
    {
        public ulong Id { get; }
        public string Name { get; }

        public RenameWater(long baseRevision, ulong id, string name) : base(baseRevision)
        {
            Id = id;
            Name = name ?? "";
        }

        public override string Label => "Rename water";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (DesignRules.NameProblem(Name) is string n) return Invalid(doc, n);
            var pond = doc.FindPond(new PondId(Id));
            if (pond != null) return pond.Name == Name ? Invalid(doc, "Nothing to change.") : CommandResult.Accept(doc.PutPond(pond.WithName(Name)));
            var dam = doc.FindDam(new DamId(Id));
            if (dam != null) return dam.Name == Name ? Invalid(doc, "Nothing to change.") : CommandResult.Accept(doc.PutDam(dam.WithName(Name)));
            return Missing(doc, "That pond or dam");
        }
    }
}
