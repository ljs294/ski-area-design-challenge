using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A stroke as painted, before it has an ID: its settings and dabs.</summary>
    public readonly struct StrokePainting
    {
        public readonly BrushSettings Settings;
        public readonly IReadOnlyList<BrushDab> Dabs;
        public StrokePainting(BrushSettings settings, IReadOnlyList<BrushDab> dabs) { Settings = settings; Dabs = dabs; }
    }

    /// <summary>
    /// TB-08's Confirm: puts a session's pending strokes in, in the order painted, as one command (one undo step). Their
    /// IDs are minted in that order, so the replay folds them in it (TB-14).
    /// </summary>
    public sealed class AddTerrainStrokes : DesignCommand
    {
        public IReadOnlyList<StrokePainting> Strokes { get; }

        public AddTerrainStrokes(long baseRevision, IReadOnlyList<StrokePainting> strokes) : base(baseRevision)
        {
            Strokes = strokes ?? Array.Empty<StrokePainting>();
        }

        public override string Label
        {
            get
            {
                if (Strokes.Count == 0) return "Shape ground";
                var kind = Strokes[0].Settings.Kind;
                foreach (var s in Strokes) if (s.Settings.Kind != kind) return "Shape ground";
                return kind switch
                {
                    BrushKind.Raise => "Raise ground",
                    BrushKind.Lower => "Lower ground",
                    BrushKind.Flatten => "Flatten ground",
                    BrushKind.Smooth => "Smooth ground",
                    _ => "Shape a slope",
                };
            }
        }

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Strokes.Count == 0) return Invalid(doc, "Nothing to build.");
            if (doc.Strokes.Count + Strokes.Count > DesignRules.MaxStrokes) return Invalid(doc, "The design holds too many terrain strokes.");
            foreach (var s in Strokes)
                if (DesignRules.StrokeProblem(s.Settings, s.Dabs) is string p) return Invalid(doc, p);
            var mint = new IdMint(doc);
            var made = new List<ulong>(Strokes.Count);
            var next = doc;
            foreach (var s in Strokes)
            {
                var id = mint.Stroke();
                next = next.PutStroke(new TerrainStroke(id, s.Settings, s.Dabs));
                made.Add(id.Value);
            }
            return CommandResult.Accept(next.WithOrdinal(mint.NextOrdinal), made);
        }
    }
}
