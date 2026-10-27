using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A trail as the player knows it (owner NT24, 2026-10-27): a name and an ordered chain of sections, top first. A
    /// section is a <see cref="Trail"/> entity, the painted piece between two nodes. Only trails of two or more sections
    /// are saved (<see cref="DesignDocument.Lines"/>); a section in none is a trail on its own (<see cref="Saved"/> false),
    /// keyed by itself. Every section carries its trail's name, so a reader of one section reads the trail's. Immutable.
    /// </summary>
    public sealed class TrailLine
    {
        public TrailLineId Id { get; }
        public string Name { get; }
        /// <summary>The sections, top first, each one's tail on the next one's head (NT26).</summary>
        public IReadOnlyList<TrailId> Sections => _sections;
        /// <summary>False for a section that is a trail on its own (nothing saved for it).</summary>
        public bool Saved { get; }

        readonly TrailId[] _sections;

        public TrailLine(TrailLineId id, string name, IReadOnlyList<TrailId> sections) : this(id, name, sections, true) { }

        TrailLine(TrailLineId id, string name, IReadOnlyList<TrailId> sections, bool saved)
        {
            Id = id;
            Name = name ?? "";
            _sections = new TrailId[sections?.Count ?? 0];
            for (int i = 0; i < _sections.Length; i++) _sections[i] = sections[i];
            Saved = saved;
        }

        /// <summary>
        /// A section on its own as a trail. Its ID is keyed by itself; when a saved trail still holds that ID (it was founded
        /// on this section, which has since been taken out), the next free one, so no two trails share an ID.
        /// </summary>
        internal static TrailLine Single(DesignDocument doc, Trail t)
        {
            var id = TrailLineId.Of(doc.Seed, t.Id);
            for (ulong k = 1; doc.FindLine(id) != null; k++) id = TrailLineId.Of(doc.Seed, t.Id, k);
            return new TrailLine(id, t.Name, new[] { t.Id }, false);
        }

        public TrailId Top => _sections[0];
        public TrailId Bottom => _sections[_sections.Length - 1];

        public int IndexOf(TrailId section)
        {
            for (int i = 0; i < _sections.Length; i++) if (_sections[i] == section) return i;
            return -1;
        }

        public bool Holds(TrailId section) => IndexOf(section) >= 0;
    }

    /// <summary>
    /// The rules that keep trails whole (owner NT24–NT28, NT40): one line top to bottom, one stage, each section in one
    /// trail. <see cref="Normalise"/> runs after every command that touches trails (<see cref="DesignCommand.Apply"/>) and on
    /// load, so no document breaks them.
    /// </summary>
    public static class TrailLines
    {
        static readonly string[] Positions = { "Upper ", "Lower ", "Middle " };

        /// <summary>Every trail in the design, saved or a section on its own, in ID order.</summary>
        public static List<TrailLine> All(DesignDocument doc)
        {
            var all = new List<TrailLine>(doc.Trails.Count);
            foreach (var line in doc.Lines) all.Add(line);
            foreach (var t in doc.Trails)
            {
                var line = doc.LineOf(t.Id);
                if (!line.Saved) all.Add(line);
            }
            all.Sort((a, b) => a.Id.CompareTo(b.Id));
            return all;
        }

        /// <summary>
        /// Whether <paramref name="lower"/> carries on from <paramref name="upper"/> in <paramref name="doc"/>: the upper's
        /// tail and the lower's head in one hidden node (NT42: ends within 100 ft), one stage (NT26, NT32).
        /// </summary>
        public static bool Chains(DesignDocument doc, Trail upper, Trail lower)
        {
            if (!upper.Tail.HasValue || !lower.Head.HasValue || upper.Stage != lower.Stage) return false;
            var nodes = doc.Nodes;
            ulong a = nodes.TailNode(upper.Id);
            return a != 0 && a == nodes.HeadNode(lower.Id);
        }

        /// <summary>
        /// Puts the document right after a change (NT26–NT28): a saved trail that no longer runs end to end becomes
        /// "Upper …" and "Lower …" ("Middle …" between), the top piece keeping its ID; one with a single section left is a
        /// trail on its own again; one with none goes; and every section takes its trail's name. In ID order, so the same
        /// document always gives the same result. Returns the same document when nothing needs doing.
        /// </summary>
        public static DesignDocument Normalise(DesignDocument doc)
        {
            if (doc.Lines.Count == 0) return doc;
            bool changed = false;
            var claimed = new HashSet<ulong>();
            var usedIds = new HashSet<ulong>();
            foreach (var line in doc.Lines) usedIds.Add(line.Id.Value);
            var kept = new List<TrailLine>(doc.Lines.Count);
            var names = new SortedDictionary<ulong, string>();
            var members = new List<Trail>();
            var pieces = new List<List<Trail>>();
            foreach (var line in doc.Lines)
            {
                members.Clear();
                foreach (var s in line.Sections)
                {
                    var t = doc.FindTrail(s);
                    if (t == null || !claimed.Add(s.Value)) { changed = true; continue; }
                    members.Add(t);
                }
                pieces.Clear();
                foreach (var t in members)
                {
                    if (pieces.Count == 0 || !Chains(doc, pieces[pieces.Count - 1][pieces[pieces.Count - 1].Count - 1], t)) pieces.Add(new List<Trail>());
                    pieces[pieces.Count - 1].Add(t);
                }
                if (pieces.Count != 1 || members.Count != line.Sections.Count) changed = true;
                string[] pieceNames = pieces.Count <= 1 ? new[] { line.Name } : BreakNames(line.Name, pieces.Count);
                for (int i = 0; i < pieces.Count; i++)
                {
                    var piece = pieces[i];
                    foreach (var t in piece) names[t.Id.Value] = pieceNames[i];
                    if (piece.Count < 2) { changed = true; continue; }
                    if (pieces.Count == 1 && piece.Count == line.Sections.Count) { kept.Add(line); continue; }
                    var id = i == 0 ? line.Id : FreshId(doc, piece[0].Id, usedIds);
                    var ids = new TrailId[piece.Count];
                    for (int k = 0; k < ids.Length; k++) ids[k] = piece[k].Id;
                    kept.Add(new TrailLine(id, pieceNames[i], ids));
                }
            }
            var next = doc;
            foreach (var kv in names)
            {
                var t = next.FindTrail(new TrailId(kv.Key));
                if (t.Name != kv.Value) next = next.PutTrail(t.WithName(kv.Value));
            }
            if (!changed && ReferenceEquals(next, doc)) return doc;
            return next.WithLines(kept);
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

        /// <summary>NT27: the next free default name, "Run N", counting trails, never one already in use.</summary>
        public static string NextName(DesignDocument doc, ICollection<string> alsoTaken = null)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in doc.Trails) used.Add(t.Name);
            if (alsoTaken != null) foreach (string n in alsoTaken) used.Add(n);
            int n0 = All(doc).Count + 1;
            while (used.Contains("Run " + n0)) n0++;
            return "Run " + n0;
        }

        /// <summary>
        /// NT35's routed click: the sections to add, nearest first, so the trail reaches <paramref name="target"/> from its
        /// top (going up) or its bottom (going down), end to end and in its stage; the shorter way wins, down on a tie. Null
        /// when no such chain exists or the target is already part of the trail.
        /// </summary>
        public static List<TrailId> Route(DesignDocument doc, TrailLine line, TrailId target)
        {
            if (line.Holds(target) || doc.FindTrail(target) == null) return null;
            var down = Search(doc, line, doc.FindTrail(line.Bottom), target, true);
            var up = Search(doc, line, doc.FindTrail(line.Top), target, false);
            if (down == null) return up;
            if (up == null) return down;
            return up.Count < down.Count ? up : down;
        }

        static List<TrailId> Search(DesignDocument doc, TrailLine line, Trail from, TrailId target, bool down)
        {
            var came = new Dictionary<ulong, ulong>();
            var queue = new Queue<Trail>();
            queue.Enqueue(from);
            came[from.Id.Value] = 0;
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                foreach (var t in doc.Trails)   // ID order: a tie takes the lower ID
                {
                    if (came.ContainsKey(t.Id.Value) || line.Holds(t.Id)) continue;
                    if (!(down ? Chains(doc, at, t) : Chains(doc, t, at))) continue;
                    came[t.Id.Value] = at.Id.Value;
                    if (t.Id == target)
                    {
                        var path = new List<TrailId>();
                        for (ulong v = t.Id.Value; v != from.Id.Value; v = came[v]) path.Add(new TrailId(v));
                        path.Reverse();
                        return path;
                    }
                    queue.Enqueue(t);
                }
            }
            return null;
        }

        /// <summary>Renames a section's trail: every section of a saved trail, or the section on its own.</summary>
        internal static DesignDocument Rename(DesignDocument doc, TrailId section, string name)
        {
            var line = doc.LineOf(section);
            if (!line.Saved)
            {
                var t = doc.FindTrail(section);
                return t.Name == name ? doc : doc.PutTrail(t.WithName(name));
            }
            var lines = new List<TrailLine>(doc.Lines.Count);
            foreach (var l in doc.Lines) lines.Add(l.Id == line.Id ? new TrailLine(l.Id, name, l.Sections) : l);
            var next = doc.WithLines(lines);
            foreach (var s in line.Sections)
            {
                var t = next.FindTrail(s);
                if (t.Name != name) next = next.PutTrail(t.WithName(name));
            }
            return next;
        }

        /// <summary>
        /// Writes one trail's sections (<paramref name="members"/>, top first) under <paramref name="id"/>, taking them out of
        /// any other saved trail (which <see cref="Normalise"/> then mends). One section left is saved as nothing.
        /// </summary>
        internal static DesignDocument SetLine(DesignDocument doc, TrailLineId id, string name, IReadOnlyList<TrailId> members)
        {
            var mine = new HashSet<ulong>();
            foreach (var s in members) mine.Add(s.Value);
            var lines = new List<TrailLine>(doc.Lines.Count + 1);
            foreach (var l in doc.Lines)
            {
                if (l.Id == id) continue;
                bool overlap = false;
                foreach (var s in l.Sections) if (mine.Contains(s.Value)) { overlap = true; break; }
                if (!overlap) { lines.Add(l); continue; }
                var rest = new List<TrailId>(l.Sections.Count);
                foreach (var s in l.Sections) if (!mine.Contains(s.Value)) rest.Add(s);
                lines.Add(new TrailLine(l.Id, l.Name, rest));
            }
            if (members.Count >= 2) lines.Add(new TrailLine(id, name, members));
            var next = doc.WithLines(lines);
            foreach (var s in members)
            {
                var t = next.FindTrail(s);
                if (t.Name != name) next = next.PutTrail(t.WithName(name));
            }
            return next;
        }

        static TrailLineId FreshId(DesignDocument doc, TrailId founding, HashSet<ulong> used)
        {
            for (ulong k = 0; ; k++)
            {
                var id = TrailLineId.Of(doc.Seed, founding, k);
                if (used.Contains(id.Value) || doc.IdInUse(id.Value)) continue;
                used.Add(id.Value);
                return id;
            }
        }
    }
}
