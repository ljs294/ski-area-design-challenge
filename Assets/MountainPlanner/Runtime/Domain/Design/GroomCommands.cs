using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// SN-22 v4 (task 4.25): the grooming setting and/or priority on runs' own trails and shaped trails, many in one undo
    /// step. A shaped trail's grooming wins over its runs' (as its switch does). A null part keeps each trail's own.
    /// </summary>
    public sealed class SetTrailGrooming : DesignCommand
    {
        public IReadOnlyList<TrailId> Runs { get; }
        public IReadOnlyList<TrailLineId> Lines { get; }
        public GroomSetting? Setting { get; }
        public GroomPriority? Priority { get; }

        public SetTrailGrooming(long baseRevision, IReadOnlyList<TrailId> runs, IReadOnlyList<TrailLineId> lines,
                                GroomSetting? setting, GroomPriority? priority) : base(baseRevision)
        {
            Runs = runs ?? Array.Empty<TrailId>();
            Lines = lines ?? Array.Empty<TrailLineId>();
            Setting = setting;
            Priority = priority;
        }

        /// <summary>For one trail as the player sees it: its shaped trail's, else its run's own.</summary>
        public static SetTrailGrooming For(long baseRevision, ResolvedTrail trail, GroomSetting? setting, GroomPriority? priority) =>
            trail.Line != null
                ? new SetTrailGrooming(baseRevision, null, new[] { trail.Line.Id }, setting, priority)
                : new SetTrailGrooming(baseRevision, new[] { trail.Segments[0].Piece }, null, setting, priority);

        public override string Label => Setting.HasValue ? "Set grooming" : "Set grooming priority";

        TrailGrooming Apply(TrailGrooming g) => new TrailGrooming(Setting ?? g.Setting, Priority ?? g.Priority);

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Setting.HasValue && !TrailGrooming.Valid(Setting.Value)) return Invalid(doc, "That isn't a grooming setting.");
            if (Priority.HasValue && !TrailGrooming.Valid(Priority.Value)) return Invalid(doc, "That isn't a grooming priority.");
            if (!Setting.HasValue && !Priority.HasValue) return Invalid(doc, "Nothing changes.");
            var next = doc;
            foreach (var id in Runs)
            {
                var t = next.FindTrail(id);
                if (t == null) return Missing(doc, "That trail");
                var g = Apply(t.Grooming);
                if (!g.Equals(t.Grooming)) next = next.PutTrail(t.WithGrooming(g));
            }
            foreach (var id in Lines)
            {
                var l = next.FindLine(id);
                if (l == null) return Missing(doc, "That trail");
                var g = Apply(l.Grooming);
                if (!g.Equals(l.Grooming)) next = next.PutLine(l.WithGrooming(g));
            }
            return CommandResult.Accept(next);
        }

        /// <summary>
        /// Whether <paramref name="b"/> is <paramref name="a"/> with only grooming changed (trail settings, groomers, crew
        /// pins) and at least something: the game then sends the grooming to the simulation without a new network.
        /// </summary>
        public static bool OnlyGroomingDiffers(DesignDocument a, DesignDocument b)
        {
            if (a == null || b == null || ReferenceEquals(a, b)) return false;
            if (a.Trails.Count != b.Trails.Count || a.Lines.Count != b.Lines.Count) return false;
            var x = a.WithGrooming(b.Grooming);
            foreach (var t in b.Trails)
            {
                var o = x.FindTrail(t.Id);
                if (o == null) return false;
                if (!o.Grooming.Equals(t.Grooming)) x = x.PutTrail(o.WithGrooming(t.Grooming));
            }
            foreach (var l in b.Lines)
            {
                var o = x.FindLine(l.Id);
                if (o == null) return false;
                if (!o.Grooming.Equals(l.Grooming)) x = x.PutLine(o.WithGrooming(l.Grooming));
            }
            return !DesignBytes.Same(DesignBytes.Encode(a), DesignBytes.Encode(b)) && DesignBytes.Same(DesignBytes.Encode(x), DesignBytes.Encode(b));
        }
    }

    /// <summary>GO-01 (task 4.25): buy a groomer; it arrives at once (owner round 2) and takes the next number.</summary>
    public sealed class BuyGroomer : DesignCommand
    {
        public GroomerModel Model { get; }
        public GroomerDrive Drive { get; }

        public BuyGroomer(long baseRevision, GroomerModel model, GroomerDrive drive) : base(baseRevision) { Model = model; Drive = drive; }

        public override string Label => "Buy groomer";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (!Groomer.Valid(Model) || !Groomer.Valid(Drive)) return Invalid(doc, "That isn't a groomer the maker sells.");
            var f = doc.Grooming;
            if (f.Groomers.Count >= GroomFleet.MaxGroomers) return Invalid(doc, $"A resort owns at most {GroomFleet.MaxGroomers} groomers.");
            return CommandResult.Accept(doc.WithGrooming(f.WithAdded(new Groomer(f.NextNumber, Model, Drive))));
        }
    }

    /// <summary>GO-01: sell a groomer (its number isn't given to the next one while a higher one stands).</summary>
    public sealed class SellGroomer : DesignCommand
    {
        public int Number { get; }

        public SellGroomer(long baseRevision, int number) : base(baseRevision) { Number = number; }

        public override string Label => "Sell groomer";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (doc.Grooming.Find(Number) == null) return Missing(doc, "That groomer");
            return CommandResult.Accept(doc.WithGrooming(doc.Grooming.WithRemoved(Number)));
        }
    }

    /// <summary>GO-02: fit (or take off) a groomer's wider tiller.</summary>
    public sealed class FitGroomerTiller : DesignCommand
    {
        public int Number { get; }
        public bool Fitted { get; }

        public FitGroomerTiller(long baseRevision, int number, bool fitted) : base(baseRevision) { Number = number; Fitted = fitted; }

        public override string Label => Fitted ? "Fit wider tiller" : "Remove wider tiller";

        protected override CommandResult Run(DesignDocument doc)
        {
            var g = doc.Grooming.Find(Number);
            if (g == null) return Missing(doc, "That groomer");
            if (g.WideTiller == Fitted) return Invalid(doc, "Nothing changes.");
            return CommandResult.Accept(doc.WithGrooming(doc.Grooming.WithReplaced(g.WithTiller(Fitted))));
        }
    }

    /// <summary>GO-05: pin (or free, <see cref="GroomCrews.Auto"/>) each shift's crew count.</summary>
    public sealed class SetGroomCrews : DesignCommand
    {
        public GroomCrews Crews { get; }

        public SetGroomCrews(long baseRevision, GroomCrews crews) : base(baseRevision) { Crews = crews; }

        public override string Label => "Set grooming crews";

        protected override CommandResult Run(DesignDocument doc)
        {
            int n = doc.Grooming.Groomers.Count;
            for (int s = 0; s < 3; s++)
                if (Crews[s] < GroomCrews.Auto || Crews[s] > n) return Invalid(doc, "A shift can't crew more groomers than the resort owns.");
            if (Crews.Equals(doc.Grooming.Crews)) return Invalid(doc, "Nothing changes.");
            return CommandResult.Accept(doc.WithGrooming(doc.Grooming.WithCrews(Crews)));
        }
    }
}
