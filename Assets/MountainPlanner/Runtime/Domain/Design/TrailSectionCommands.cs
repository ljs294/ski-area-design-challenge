using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>What one click in Edit sections did (owner NT25, NT35): add, take out, cut or rejoin.</summary>
    public enum SectionOpKind : byte { Add = 1, TakeOut = 2, Cut = 3, Rejoin = 4 }

    /// <summary>One step of an Edit sections session, replayed in order by <see cref="EditTrailSections"/>.</summary>
    public readonly struct SectionOp
    {
        public readonly SectionOpKind Kind;
        /// <summary>The section added, taken out or cut; for a rejoin, the upper of the two.</summary>
        public readonly TrailId Section;
        /// <summary>A rejoin's lower section.</summary>
        public readonly TrailId Lower;
        /// <summary>A cut's point and the centreline's direction there, head towards tail (<see cref="SplitTrail"/>).</summary>
        public readonly PointMm At;
        public readonly int DirX, DirY;

        SectionOp(SectionOpKind kind, TrailId section, TrailId lower, PointMm at, int dirX, int dirY)
        {
            Kind = kind;
            Section = section;
            Lower = lower;
            At = at;
            DirX = dirX;
            DirY = dirY;
        }

        public static SectionOp Add(TrailId section) => new SectionOp(SectionOpKind.Add, section, default, default, 0, 0);
        public static SectionOp TakeOut(TrailId section) => new SectionOp(SectionOpKind.TakeOut, section, default, default, 0, 0);
        public static SectionOp Cut(TrailId section, PointMm at, int dirX, int dirY) => new SectionOp(SectionOpKind.Cut, section, default, at, dirX, dirY);
        public static SectionOp Rejoin(TrailId upper, TrailId lower) => new SectionOp(SectionOpKind.Rejoin, upper, lower, default, 0, 0);
    }

    /// <summary>
    /// One Edit sections session as one undoable step (owner NT25, NT35, NT37, NT39, NT40). The trail is named by any of
    /// its sections when the session opened. Its clicks replay in order:
    /// <list type="bullet">
    /// <item><b>Add</b> takes a section onto the trail's top or bottom, end to end, out of whatever trail held it (which
    /// breaks into Upper and Lower if it lost a middle section).</item>
    /// <item><b>Take out</b> makes a section a trail of its own, named "Run N" (NT27), or splits the trail when it was a
    /// middle one.</item>
    /// <item><b>Cut</b> adds a node (<see cref="SplitTrail"/>): both pieces stay in the trail, so the ground stays put (NT2).</item>
    /// <item><b>Rejoin</b> removes a cut node; only the two sides of one cut, so the ground stays put (NT31).</item>
    /// </list>
    /// Names the player typed in the session's list (by each new trail's top section) win over the automatic ones.
    /// Nothing here moves the ground: cuts and rejoins keep <see cref="TrailChains"/>' grading as one.
    /// </summary>
    public sealed class EditTrailSections : DesignCommand
    {
        public TrailId Trail { get; }
        public IReadOnlyList<SectionOp> Ops { get; }
        /// <summary>The trail's own name at the end, or null to keep it.</summary>
        public string Name { get; }
        /// <summary>Names typed for the trails the session makes, by their top section (NT40).</summary>
        public IReadOnlyDictionary<TrailId, string> Names { get; }

        public EditTrailSections(long baseRevision, TrailId trail, IReadOnlyList<SectionOp> ops, string name = null,
                                 IReadOnlyDictionary<TrailId, string> names = null) : base(baseRevision)
        {
            Trail = trail;
            Ops = ops ?? Array.Empty<SectionOp>();
            Name = name;
            Names = names ?? new Dictionary<TrailId, string>();
        }

        public override string Label => "Edit sections";

        protected override CommandResult Run(DesignDocument doc)
        {
            var line = doc.LineOf(Trail);
            if (line == null) return Missing(doc, "That trail");
            if (Ops.Count == 0 && (Name == null || Name == line.Name) && Names.Count == 0) return Invalid(doc, "Nothing changes.");
            string name = Name ?? line.Name;
            if (DesignRules.NameProblem(name) is string bad) return Invalid(doc, bad);
            foreach (var kv in Names) if (DesignRules.NameProblem(kv.Value) is string p) return Invalid(doc, p);

            var work = doc;
            var members = new List<TrailId>(line.Sections);
            var released = new List<TrailId>();
            var created = new List<ulong>();
            foreach (var op in Ops)
            {
                var t = work.FindTrail(op.Section);
                if (t == null) return Missing(doc, "That section");
                int at = members.IndexOf(op.Section);
                switch (op.Kind)
                {
                    case SectionOpKind.Add:
                    {
                        if (at >= 0) return Invalid(doc, $"That section is already part of {name}.");
                        var top = work.FindTrail(members[0]);
                        var bottom = work.FindTrail(members[members.Count - 1]);
                        if (t.Stage != top.Stage) return Invalid(doc, "A trail is all planned or all built (NT32).");
                        if (TrailLines.Chains(work, bottom, t)) members.Add(op.Section);
                        else if (TrailLines.Chains(work, t, top)) members.Insert(0, op.Section);
                        else return Invalid(doc, $"That section doesn't meet {name}'s top or bottom end to end.");
                        released.Remove(op.Section);
                        break;
                    }
                    case SectionOpKind.TakeOut:
                        if (at < 0) return Invalid(doc, $"That section isn't part of {name}.");
                        if (members.Count == 1) return Invalid(doc, "A trail keeps at least one section; remove the trail instead.");
                        members.RemoveAt(at);
                        released.Add(op.Section);
                        break;
                    case SectionOpKind.Cut:
                    {
                        if (at < 0) return Invalid(doc, $"That section isn't part of {name}.");
                        var r = new SplitTrail(0, op.Section, op.At, op.DirX, op.DirY, name).Raw(work);
                        if (!r.Accepted) return Invalid(doc, r.Reason);
                        work = r.Document;
                        var lower = new TrailId(r.Created[0]);
                        members.Insert(at + 1, lower);
                        created.Add(lower.Value);
                        break;
                    }
                    case SectionOpKind.Rejoin:
                    {
                        int below = members.IndexOf(op.Lower);
                        if (at < 0 || below != at + 1) return Invalid(doc, "Only two neighbouring sections of the trail can be rejoined.");
                        var low = work.FindTrail(op.Lower);
                        if (!OneCut(t, low)) return Invalid(doc, "Only a cut can be rejoined: these sections were painted apart.");
                        var r = new JoinTrails(0, op.Section, op.Lower).Raw(work);
                        if (!r.Accepted) return Invalid(doc, r.Reason);
                        work = r.Document;
                        members.RemoveAt(below);
                        break;
                    }
                    default:
                        return Invalid(doc, "That isn't something Edit sections does.");
                }
                // Each step leaves the trail written, so the next one reads the document as the player sees it.
                work = TrailLines.SetLine(work, line.Id, name, members);
            }
            if (Ops.Count == 0) work = TrailLines.SetLine(work, line.Id, name, members);

            // NT27: a section taken out and taken by nothing is a trail of its own, "Run N" unless the player named it.
            // never a name a trail had when the session opened, so a merged trail's old name isn't handed straight back
            var taken = new List<string>();
            foreach (var t in doc.Trails) taken.Add(t.Name);
            foreach (var s in released)
            {
                if (work.FindTrail(s) == null) continue;
                string own = Names.TryGetValue(s, out var typed) ? typed : TrailLines.NextName(work, taken);
                taken.Add(own);
                // No ID: a section on its own saves nothing, and its own key may still be the trail it left (if it founded it).
                work = TrailLines.SetLine(work, default, own, new[] { s });
            }
            work = TrailLines.Normalise(work);
            // NT40: names typed for trails the session made (an Upper or Lower piece, a released section) win.
            foreach (var kv in Names)
            {
                var piece = work.LineOf(kv.Key);
                if (piece != null && piece.Top == kv.Key && piece.Name != kv.Value) work = TrailLines.Rename(work, kv.Key, kv.Value);
            }
            return CommandResult.Accept(work, created);
        }

        /// <summary>NT31: the two sections are the two sides of one cut, every part of each with its complement.</summary>
        static bool OneCut(Trail upper, Trail lower)
        {
            if (upper.Parts.Count != lower.Parts.Count) return false;
            var used = new bool[lower.Parts.Count];
            foreach (var u in upper.Parts)
            {
                bool found = false;
                for (int k = 0; k < lower.Parts.Count && !found; k++)
                {
                    if (used[k] || u.RejoinWith(lower.Parts[k]) == null) continue;
                    used[k] = found = true;
                }
                if (!found) return false;
            }
            return true;
        }
    }
}
