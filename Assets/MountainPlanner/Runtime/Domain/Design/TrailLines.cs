using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The saved trails' own housekeeping (owner NT24–NT46), with no geometry: <see cref="Normalise"/> runs after every
    /// command that touches the runs, the trails or the cut points (<see cref="DesignCommand.Apply"/>) and on load. Which
    /// segments a trail holds, and its gaps, are worked out from the nodes by <see cref="TrailSections"/>.
    /// </summary>
    public static class TrailLines
    {
        static readonly string[] Positions = { "Upper ", "Lower ", "Middle " };

        /// <summary>
        /// Drops spans of runs that are gone, trails left with no spans, and cut points on runs that are gone (or twice).
        /// The same document when nothing needs doing.
        /// </summary>
        public static DesignDocument Normalise(DesignDocument doc)
        {
            bool changed = false;
            var lines = new List<TrailLine>(doc.Lines.Count);
            foreach (var line in doc.Lines)
            {
                var spans = new List<Span>(line.Spans.Count);
                foreach (var sp in line.Spans) if (doc.FindTrail(sp.Piece) != null) spans.Add(sp);
                if (spans.Count == line.Spans.Count) { lines.Add(line); continue; }
                changed = true;
                if (spans.Count > 0) lines.Add(line.WithSpans(spans));
            }
            var cuts = new List<TrailCut>(doc.Cuts.Count);
            for (int i = 0; i < doc.Cuts.Count; i++)
            {
                var c = doc.Cuts[i];
                if (doc.FindTrail(c.Piece) == null || (i > 0 && c.Equals(doc.Cuts[i - 1]))) { changed = true; continue; }
                cuts.Add(c);
            }
            if (!changed) return doc;
            return doc.WithLines(lines).WithCuts(cuts);
        }

        /// <summary>A run and every run of the shaped trails it's in (NT32: built together).</summary>
        public static List<TrailId> PiecesWith(DesignDocument doc, TrailId piece)
        {
            var list = new List<TrailId> { piece };
            foreach (var line in doc.Lines)
            {
                bool holds = false;
                foreach (var sp in line.Spans) if (sp.Piece == piece) { holds = true; break; }
                if (!holds) continue;
                foreach (var sp in line.Spans) if (!list.Contains(sp.Piece)) list.Add(sp.Piece);
            }
            return list;
        }

        /// <summary>
        /// NT28, NT40: the names of a trail left in <paramref name="pieces"/> pieces, top first: "Upper X" and "Lower X",
        /// with "Middle X" (or "Middle 1 X", "Middle 2 X") between. A position already in the name is replaced, never
        /// stacked, and the name is shortened to fit.
        /// </summary>
        public static string[] BreakNames(string name, int pieces)
        {
            string stem = Stem(name);
            var names = new string[pieces];
            for (int i = 0; i < pieces; i++)
            {
                string position = i == 0 ? "Upper " : i == pieces - 1 ? "Lower " : pieces == 3 ? "Middle " : $"Middle {i} ";
                names[i] = Fit(position, stem);
            }
            return names;
        }

        /// <summary>A name without its position: "Upper Run 4" and "Middle 2 Run 4" are both "Run 4".</summary>
        public static string Stem(string name)
        {
            foreach (string p in Positions)
            {
                if (!name.StartsWith(p, StringComparison.Ordinal)) continue;
                string rest = name.Substring(p.Length);
                if (p == "Middle ")
                {
                    int d = 0;
                    while (d < rest.Length && rest[d] >= '0' && rest[d] <= '9') d++;
                    if (d > 0 && d < rest.Length && rest[d] == ' ') rest = rest.Substring(d + 1);
                }
                return rest.Length > 0 ? rest : name;
            }
            return name;
        }

        static string Fit(string position, string stem)
        {
            int room = DesignRules.NameMaxLength - position.Length;
            if (stem.Length > room) stem = stem.Substring(0, room).TrimEnd();
            return position + stem;
        }

        /// <summary>NT27: the next free default name, "Run N" (N one past the trails there are), never one in use.</summary>
        public static string NextName(int trails, ICollection<string> taken)
        {
            int n = trails + 1;
            while (taken != null && taken.Contains("Run " + n)) n++;
            return "Run " + n;
        }

        /// <summary>A new saved trail's ID: keyed by its founding run, the next free one (no ordinal spent).</summary>
        public static TrailLineId FreshId(DesignDocument doc, TrailId founding, ICollection<ulong> alsoTaken = null)
        {
            for (ulong k = 0; ; k++)
            {
                var id = TrailLineId.Of(doc.Seed, founding, k);
                if (doc.IdInUse(id.Value) || (alsoTaken != null && alsoTaken.Contains(id.Value))) continue;
                return id;
            }
        }
    }
}
