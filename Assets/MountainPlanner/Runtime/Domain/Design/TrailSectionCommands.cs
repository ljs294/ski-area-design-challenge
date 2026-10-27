using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// One Edit sections session as one undoable step (owner NT25, NT39, NT43–NT46): the saved trails it puts (new or
    /// replaced whole, by ID), the ones it removes, and the cut points it adds and takes away. The tool works out the spans
    /// through the nodes (<see cref="TrailSectionsEdit"/>); this checks only that the runs are there and the names fit, so
    /// it needs no geometry. Nothing here moves paint or ground (NT31, NT45).
    /// </summary>
    public sealed class ShapeTrails : DesignCommand
    {
        public IReadOnlyList<TrailLine> Put { get; }
        public IReadOnlyList<TrailLineId> Remove { get; }
        public IReadOnlyList<TrailCut> AddCuts { get; }
        public IReadOnlyList<TrailCut> RemoveCuts { get; }

        public ShapeTrails(long baseRevision, IReadOnlyList<TrailLine> put, IReadOnlyList<TrailLineId> remove = null,
                           IReadOnlyList<TrailCut> addCuts = null, IReadOnlyList<TrailCut> removeCuts = null) : base(baseRevision)
        {
            Put = put ?? Array.Empty<TrailLine>();
            Remove = remove ?? Array.Empty<TrailLineId>();
            AddCuts = addCuts ?? Array.Empty<TrailCut>();
            RemoveCuts = removeCuts ?? Array.Empty<TrailCut>();
        }

        public override string Label => "Edit sections";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Put.Count + Remove.Count + AddCuts.Count + RemoveCuts.Count == 0) return Invalid(doc, "Nothing changes.");
            var ids = new HashSet<ulong>();
            foreach (var line in Put)
            {
                if (DesignRules.LineProblem(doc, line) is string p) return Invalid(doc, p);
                if (!ids.Add(line.Id.Value)) return Invalid(doc, "A trail is listed twice.");
                if (doc.FindLine(line.Id) == null && doc.IdInUse(line.Id.Value)) return Invalid(doc, "That ID is taken.");
            }
            foreach (var id in Remove)
            {
                if (doc.FindLine(id) == null) return Missing(doc, "That trail");
                if (!ids.Add(id.Value)) return Invalid(doc, "A trail is listed twice.");
            }
            var cuts = new List<TrailCut>(doc.Cuts);
            foreach (var c in RemoveCuts)
                if (!cuts.Remove(c)) return Missing(doc, "That cut");
            foreach (var c in AddCuts)
            {
                if (doc.FindTrail(c.Piece) == null) return Missing(doc, "That run");
                if (DesignRules.PointProblem(c.At) is string p) return Invalid(doc, p);
                if (cuts.Contains(c)) return Invalid(doc, "That point is already cut.");
                cuts.Add(c);
            }
            var lines = new List<TrailLine>(doc.Lines.Count + Put.Count);
            foreach (var l in doc.Lines) if (!ids.Contains(l.Id.Value)) lines.Add(l);
            lines.AddRange(Put);
            return CommandResult.Accept(doc.WithLines(lines).WithCuts(cuts));
        }
    }
}
