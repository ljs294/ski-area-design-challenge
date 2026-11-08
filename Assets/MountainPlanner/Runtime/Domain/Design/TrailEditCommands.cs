using System.Collections.Generic;
using MountainPlanner.Domain.Measure;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Edit trail's one undoable change (TF-07, task 08): the trail's clearing, ends and settings together, as the Edit
    /// tool's review shows them, and its name; a planned trail may be built by it (CF-06). Its ID stays. TD-07 (owner R2,
    /// 2026-11-08): the rating the player posts is part of the same step, for the run's own trail or, with
    /// <see cref="PostedLine"/>, the shaped trail the run is edited from; a regrade never changes it.
    /// </summary>
    public sealed class EditTrail : DesignCommand
    {
        public TrailId Trail { get; }
        public TrailShape Shape { get; }
        public PointMm Head { get; }
        public PointMm Tail { get; }
        public TrailSettings Settings { get; }
        public WorkStage Stage { get; }
        public string Name { get; }
        /// <summary>TD-07: the rating posted in the review, or null to leave the sign as it is.</summary>
        public SlopeBands.Band? Posted { get; }
        /// <summary>The shaped trail <see cref="Posted"/> is for; null for the run's own trail.</summary>
        public TrailLineId? PostedLine { get; }

        public EditTrail(long baseRevision, TrailId trail, string name, TrailShape shape, PointMm head, PointMm tail,
                         TrailSettings settings, WorkStage stage, SlopeBands.Band? posted = null, TrailLineId? postedLine = null) : base(baseRevision)
        {
            Name = name;
            Trail = trail;
            Shape = shape;
            Head = head;
            Tail = tail;
            Settings = settings;
            Stage = stage;
            Posted = posted;
            PostedLine = postedLine;
        }

        public override string Label => "Edit trail";

        protected override CommandResult Run(DesignDocument doc)
        {
            var t = doc.FindTrail(Trail);
            if (t == null) return Missing(doc, "That trail");
            string problem = DesignRules.NameProblem(Name) ?? DesignRules.ShapeProblem(Shape) ?? DesignRules.EndsProblem(Head, Tail) ?? DesignRules.StageProblem(Stage)
                ?? DesignRules.PostedProblem(Posted);
            if (problem != null) return Invalid(doc, problem);
            if (DesignRules.SettingsProblem(Settings) is string s) return CommandResult.Reject(doc, RejectionCode.NotAvailable, s);
            if (t.Stage == WorkStage.Built && Stage == WorkStage.Plan) return Invalid(doc, "A built trail stays built; remove it instead.");
            if (DesignRules.GradingProblem(t, Settings, Stage) is string g) return Invalid(doc, g);
            var edited = t.WithName(Name).WithShape(Shape).WithEnds(Head, Tail).WithSettings(Settings).WithStage(Stage);
            TrailLine line = null;
            if (PostedLine.HasValue)
            {
                line = doc.FindLine(PostedLine.Value);
                if (line == null) return Missing(doc, "That trail");
                bool holds = false;
                foreach (var sp in line.Spans) if (sp.Piece == Trail) { holds = true; break; }
                if (!holds) return Invalid(doc, "That trail doesn't hold this run.");
            }
            else if (Posted.HasValue) edited = edited.WithPosted(Posted);
            bool signChanges = Posted.HasValue && (line != null ? line.Posted != Posted : t.Posted != Posted);
            if (Same(t, edited) && !signChanges) return Invalid(doc, "Nothing changes.");
            var next = doc.PutTrail(edited);
            if (line != null && line.Posted != Posted) next = next.PutLine(line.WithPosted(Posted));
            // NT32: building one run builds every run of the shaped trails it's in.
            if (Stage == WorkStage.Built && t.Stage == WorkStage.Plan)
            {
                var built = new List<TrailId> { Trail };
                foreach (var piece in TrailLines.PiecesWith(doc, Trail))
                {
                    if (piece == Trail) continue;
                    var other = next.FindTrail(piece);
                    if (other.Stage == WorkStage.Built) continue;
                    if (!other.HasEnds) return Invalid(doc, $"{other.Name} needs a head and a tail before it can be built.");
                    next = next.PutTrail(other.WithStage(WorkStage.Built));
                    built.Add(piece);
                }
                // TD-07 (owner R1): all of it posted first; what has no sign takes the one picked here.
                next = DesignRules.PostForBuild(next, built, Posted, out string unposted);
                if (next == null) return Invalid(doc, unposted);
            }
            return CommandResult.Accept(next);
        }

        static bool Same(Trail a, Trail b)
        {
            if (a.Name != b.Name || a.Stage != b.Stage || !a.Settings.Equals(b.Settings) || a.Head != b.Head || a.Tail != b.Tail) return false;
            return a.Shape.Equals(b.Shape);
        }
    }
}
