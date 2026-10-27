using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>Adds a painted trail (TF-05's Break ground or Save plan, CF-06). Its ID is minted from the document.</summary>
    public sealed class AddTrail : DesignCommand
    {
        public string Name { get; }
        public TrailSettings Settings { get; }
        public IReadOnlyList<FootprintPart> Parts { get; }
        public PointMm? Head { get; }
        public PointMm? Tail { get; }
        public WorkStage Stage { get; }

        public AddTrail(long baseRevision, string name, TrailSettings settings, IReadOnlyList<FootprintPart> parts, PointMm? head, PointMm? tail, WorkStage stage)
            : base(baseRevision)
        {
            Name = name;
            Settings = settings;
            Parts = parts;
            Head = head;
            Tail = tail;
            Stage = stage;
        }

        public override string Label => "Add trail";

        protected override CommandResult Run(DesignDocument doc)
        {
            string problem = DesignRules.NameProblem(Name) ?? DesignRules.StageProblem(Stage) ?? DesignRules.PartsProblem(Parts)
                ?? DesignRules.EndsProblem(Head, Tail);
            if (problem != null) return Invalid(doc, problem);
            if (DesignRules.SettingsProblem(Settings) is string s) return CommandResult.Reject(doc, RejectionCode.NotAvailable, s);
            if (Stage == WorkStage.Built && !(Head.HasValue && Tail.HasValue)) return Invalid(doc, "A trail needs a head and a tail before it can be built.");

            var mint = new IdMint(doc);
            var id = mint.Trail();
            var trail = new Trail(id, Name, Stage, Settings, Parts, Head, Tail);
            return CommandResult.Accept(doc.WithOrdinal(mint.NextOrdinal).PutTrail(trail), id.Value);
        }
    }

    /// <summary>Replaces a trail's footprint (TF-07's repaint and islands). The tool sends the whole new footprint.</summary>
    public sealed class RepaintTrail : DesignCommand
    {
        public TrailId Trail { get; }
        public IReadOnlyList<FootprintPart> Parts { get; }

        public RepaintTrail(long baseRevision, TrailId trail, IReadOnlyList<FootprintPart> parts) : base(baseRevision)
        {
            Trail = trail;
            Parts = parts;
        }

        public override string Label => "Repaint trail";

        protected override CommandResult Run(DesignDocument doc)
        {
            var t = doc.FindTrail(Trail);
            if (t == null) return Missing(doc, "That trail");
            if (DesignRules.PartsProblem(Parts) is string p) return Invalid(doc, p);
            return CommandResult.Accept(doc.PutTrail(t.WithParts(Parts)));
        }
    }

    /// <summary>Sets or moves a trail's head and tail (TF-03, TF-07), as points (owner ruling P3-03 Q3).</summary>
    public sealed class SetTrailEnds : DesignCommand
    {
        public TrailId Trail { get; }
        public PointMm? Head { get; }
        public PointMm? Tail { get; }

        public SetTrailEnds(long baseRevision, TrailId trail, PointMm? head, PointMm? tail) : base(baseRevision)
        {
            Trail = trail;
            Head = head;
            Tail = tail;
        }

        public override string Label => "Move trail ends";

        protected override CommandResult Run(DesignDocument doc)
        {
            var t = doc.FindTrail(Trail);
            if (t == null) return Missing(doc, "That trail");
            if (DesignRules.EndsProblem(Head, Tail) is string p) return Invalid(doc, p);
            if (t.Stage == WorkStage.Built && !Head.HasValue) return Invalid(doc, "A built trail keeps its head and tail.");
            return CommandResult.Accept(doc.PutTrail(t.WithEnds(Head, Tail)));
        }
    }

    public sealed class RenameTrail : DesignCommand
    {
        public TrailId Trail { get; }
        public string Name { get; }

        public RenameTrail(long baseRevision, TrailId trail, string name) : base(baseRevision)
        {
            Trail = trail;
            Name = name;
        }

        public override string Label => "Rename trail";

        protected override CommandResult Run(DesignDocument doc)
        {
            var t = doc.FindTrail(Trail);
            if (t == null) return Missing(doc, "That trail");
            if (DesignRules.NameProblem(Name) is string p) return Invalid(doc, p);
            if (t.Name == Name) return Invalid(doc, "That's already its name.");
            return CommandResult.Accept(TrailLines.Rename(doc, Trail, Name));   // the whole trail (NT24)
        }
    }

    /// <summary>TR-06 / TF-01: Graded or Natural (kind and clearing wait for later phases).</summary>
    public sealed class SetTrailSettings : DesignCommand
    {
        public TrailId Trail { get; }
        public TrailSettings Settings { get; }

        public SetTrailSettings(long baseRevision, TrailId trail, TrailSettings settings) : base(baseRevision)
        {
            Trail = trail;
            Settings = settings;
        }

        public override string Label => "Change trail settings";

        protected override CommandResult Run(DesignDocument doc)
        {
            var t = doc.FindTrail(Trail);
            if (t == null) return Missing(doc, "That trail");
            if (DesignRules.SettingsProblem(Settings) is string p) return CommandResult.Reject(doc, RejectionCode.NotAvailable, p);
            if (t.Settings.Equals(Settings)) return Invalid(doc, "Nothing changes.");
            return CommandResult.Accept(doc.PutTrail(t.WithSettings(Settings)));
        }
    }

    public sealed class DeleteTrail : DesignCommand
    {
        public TrailId Trail { get; }

        public DeleteTrail(long baseRevision, TrailId trail) : base(baseRevision) { Trail = trail; }

        public override string Label => "Delete trail";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (doc.FindTrail(Trail) == null) return Missing(doc, "That trail");
            // NT33: the whole trail goes, every section of it.
            var next = doc;
            foreach (var s in doc.LineOf(Trail).Sections) next = next.RemoveTrail(s);
            return CommandResult.Accept(next);
        }
    }

    /// <summary>
    /// NW-10's Split, by a stored cut line (owner ruling P3-03 Q2). The trail keeps its ID, name and head and becomes
    /// the upper part, ending at the split; a new trail ("… lower") takes the part ahead, from the split to the old
    /// tail. Both keep every stroke, clipped by the cut, so <see cref="JoinTrails"/> can restore the trail exactly.
    /// The tool supplies the point and the centreline's direction there (head towards tail).
    /// </summary>
    public sealed class SplitTrail : DesignCommand
    {
        public TrailId Trail { get; }
        public PointMm At { get; }
        public int DirX { get; }
        public int DirY { get; }
        /// <summary>The lower part's name, or null for "&lt;name&gt; lower".</summary>
        public string LowerName { get; }

        public SplitTrail(long baseRevision, TrailId trail, PointMm at, int dirX, int dirY, string lowerName = null) : base(baseRevision)
        {
            Trail = trail;
            At = at;
            DirX = dirX;
            DirY = dirY;
            LowerName = lowerName;
        }

        public override string Label => "Split trail";

        public static string DefaultLowerName(string upper)
        {
            const string suffix = " lower";
            string baseName = upper.Length + suffix.Length > DesignRules.NameMaxLength
                ? upper.Substring(0, DesignRules.NameMaxLength - suffix.Length).TrimEnd()
                : upper;
            return baseName + suffix;
        }

        protected override CommandResult Run(DesignDocument doc)
        {
            var t = doc.FindTrail(Trail);
            if (t == null) return Missing(doc, "That trail");
            if (!t.HasEnds) return Invalid(doc, "A trail needs a head and a tail to be split.");
            if (DesignRules.CutProblem(At, DirX, DirY) is string c) return Invalid(doc, c);
            var behind = new Cut(At, DirX, DirY, false);
            if (behind.Side(t.Head.Value) >= 0 || behind.Side(t.Tail.Value) <= 0)
                return Invalid(doc, "Split between the head and the tail.");
            string lowerName = LowerName ?? DefaultLowerName(t.Name);
            if (DesignRules.NameProblem(lowerName) is string n) return Invalid(doc, n);
            foreach (var part in t.Parts)
                if (part.Cuts.Count >= DesignRules.MaxCutsPerPart) return Invalid(doc, "This trail has been split too many times.");

            var upperParts = new FootprintPart[t.Parts.Count];
            var lowerParts = new FootprintPart[t.Parts.Count];
            for (int i = 0; i < t.Parts.Count; i++)
            {
                upperParts[i] = t.Parts[i].WithCut(behind);
                lowerParts[i] = t.Parts[i].WithCut(behind.Opposite);
            }
            var mint = new IdMint(doc);
            var lowerId = mint.Trail();
            var upper = t.WithParts(upperParts).WithEnds(t.Head, At);
            var lower = new Trail(lowerId, lowerName, t.Stage, t.Settings, lowerParts, At, t.Tail);
            return CommandResult.Accept(doc.WithOrdinal(mint.NextOrdinal).PutTrail(upper).PutTrail(lower), lowerId.Value);
        }
    }

    /// <summary>
    /// NW-10's Join: where the upper trail's tail meets the lower trail's head (within 30 ft, TR-05), the two become
    /// one. It keeps the upper trail's ID, name and settings, its head and the lower trail's tail. Parts that are the
    /// two sides of one split merge back, removing the cut; others are kept side by side.
    /// </summary>
    public sealed class JoinTrails : DesignCommand
    {
        public TrailId Upper { get; }
        public TrailId Lower { get; }

        public JoinTrails(long baseRevision, TrailId upper, TrailId lower) : base(baseRevision)
        {
            Upper = upper;
            Lower = lower;
        }

        public override string Label => "Join trails";

        protected override CommandResult Run(DesignDocument doc)
        {
            var up = doc.FindTrail(Upper);
            var low = doc.FindTrail(Lower);
            if (up == null || low == null) return Missing(doc, "That trail");
            if (Upper == Lower) return Invalid(doc, "A trail can't join itself.");
            if (!up.HasEnds || !low.HasEnds) return Invalid(doc, "Both trails need a head and a tail.");
            if (up.Stage != low.Stage) return Invalid(doc, "A plan and a built trail can't be joined.");
            if (!up.Tail.Value.Within(low.Head.Value, DesignUnits.SnapMm))
                return Invalid(doc, $"{up.Name}'s tail must meet {low.Name}'s head.");
            if (up.Head.Value == low.Tail.Value) return Invalid(doc, "The joined trail would start where it ends.");

            var parts = new List<FootprintPart>();
            var used = new bool[low.Parts.Count];
            foreach (var u in up.Parts)
            {
                FootprintPart merged = null;
                for (int k = 0; k < low.Parts.Count && merged == null; k++)
                {
                    if (used[k]) continue;
                    merged = u.RejoinWith(low.Parts[k]);
                    if (merged != null) used[k] = true;
                }
                parts.Add(merged ?? u);
            }
            for (int k = 0; k < low.Parts.Count; k++) if (!used[k]) parts.Add(low.Parts[k]);
            if (parts.Count > DesignRules.MaxPartsPerTrail) return Invalid(doc, "The joined trail would have too many parts.");

            var joined = up.WithParts(parts).WithEnds(up.Head, low.Tail);
            return CommandResult.Accept(doc.RemoveTrail(Lower).PutTrail(joined));
        }
    }
}
