using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Edit trail's one undoable change (TF-07, task 08): the trail's footprint, ends and settings together, as the Edit
    /// tool's review shows them, and its name; a planned trail may be built by it (CF-06). Its ID stays. Additive to task 03's
    /// commands: the document's shape doesn't change.
    /// </summary>
    public sealed class EditTrail : DesignCommand
    {
        public TrailId Trail { get; }
        public IReadOnlyList<FootprintPart> Parts { get; }
        public PointMm Head { get; }
        public PointMm Tail { get; }
        public TrailSettings Settings { get; }
        public WorkStage Stage { get; }
        public string Name { get; }

        public EditTrail(long baseRevision, TrailId trail, string name, IReadOnlyList<FootprintPart> parts, PointMm head, PointMm tail,
                         TrailSettings settings, WorkStage stage) : base(baseRevision)
        {
            Name = name;
            Trail = trail;
            Parts = parts;
            Head = head;
            Tail = tail;
            Settings = settings;
            Stage = stage;
        }

        public override string Label => "Edit trail";

        protected override CommandResult Run(DesignDocument doc)
        {
            var t = doc.FindTrail(Trail);
            if (t == null) return Missing(doc, "That trail");
            string problem = DesignRules.NameProblem(Name) ?? DesignRules.PartsProblem(Parts) ?? DesignRules.EndsProblem(Head, Tail) ?? DesignRules.StageProblem(Stage);
            if (problem != null) return Invalid(doc, problem);
            if (DesignRules.SettingsProblem(Settings) is string s) return CommandResult.Reject(doc, RejectionCode.NotAvailable, s);
            if (t.Stage == WorkStage.Built && Stage == WorkStage.Plan) return Invalid(doc, "A built trail stays built; remove it instead.");
            if (DesignRules.GradingProblem(t, Settings, Stage) is string g) return Invalid(doc, g);
            var edited = t.WithName(Name).WithParts(Parts).WithEnds(Head, Tail).WithSettings(Settings).WithStage(Stage);
            if (Same(t, edited)) return Invalid(doc, "Nothing changes.");
            var next = doc.PutTrail(edited);
            // NT32: building one run builds every run of the shaped trails it's in.
            if (Stage == WorkStage.Built && t.Stage == WorkStage.Plan)
                foreach (var piece in TrailLines.PiecesWith(doc, Trail))
                {
                    if (piece == Trail) continue;
                    var other = next.FindTrail(piece);
                    if (other.Stage == WorkStage.Built) continue;
                    if (!other.HasEnds) return Invalid(doc, $"{other.Name} needs a head and a tail before it can be built.");
                    next = next.PutTrail(other.WithStage(WorkStage.Built));
                }
            return CommandResult.Accept(next);
        }

        static bool Same(Trail a, Trail b)
        {
            if (a.Name != b.Name || a.Stage != b.Stage || !a.Settings.Equals(b.Settings) || a.Head != b.Head || a.Tail != b.Tail || a.Parts.Count != b.Parts.Count) return false;
            for (int i = 0; i < a.Parts.Count; i++) if (!a.Parts[i].Equals(b.Parts[i])) return false;
            return true;
        }
    }
}
