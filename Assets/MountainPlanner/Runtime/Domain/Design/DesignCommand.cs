using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>Why a command was turned away.</summary>
    public enum RejectionCode : byte
    {
        /// <summary>CF-05: the command named a revision that is no longer current.</summary>
        Stale = 1,
        /// <summary>The command names a trail, lift or tower that isn't there.</summary>
        NotFound = 2,
        /// <summary>The command would break a document rule.</summary>
        Invalid = 3,
        /// <summary>TR-06: a setting a later phase brings.</summary>
        NotAvailable = 4,
    }

    /// <summary>
    /// What a command did: a new document, or a rejection and the unchanged document. Never anything in between (CF-05).
    /// </summary>
    public sealed class CommandResult
    {
        public bool Accepted { get; }
        public DesignDocument Document { get; }
        public RejectionCode Code { get; }
        public string Reason { get; }
        /// <summary>The IDs this command minted, in the order made (trail, lift and tower values).</summary>
        public IReadOnlyList<ulong> Created { get; }

        CommandResult(bool accepted, DesignDocument doc, RejectionCode code, string reason, IReadOnlyList<ulong> created)
        {
            Accepted = accepted;
            Document = doc;
            Code = code;
            Reason = reason;
            Created = created ?? Array.Empty<ulong>();
        }

        public static CommandResult Accept(DesignDocument doc, params ulong[] created) => new CommandResult(true, doc, 0, null, created);
        internal static CommandResult Accept(DesignDocument doc, List<ulong> created) => new CommandResult(true, doc, 0, null, created.ToArray());
        internal static CommandResult Accept(DesignDocument doc, IReadOnlyList<ulong> created) => new CommandResult(true, doc, 0, null, created);
        public static CommandResult Reject(DesignDocument unchanged, RejectionCode code, string reason) => new CommandResult(false, unchanged, code, reason, null);
        public override string ToString() => Accepted ? "accepted" : $"rejected ({Code}): {Reason}";
    }

    /// <summary>
    /// A typed change to the design, against a named revision (CF-05). <see cref="Apply"/> checks everything first and
    /// only then builds a new immutable document, so a rejected command changes nothing. Commands are never saved, so
    /// later tasks add command types without touching the document format.
    /// </summary>
    public abstract class DesignCommand
    {
        /// <summary>The revision the command was made against (<see cref="DesignHistory.Revision"/>).</summary>
        public long BaseRevision { get; }

        protected DesignCommand(long baseRevision) { BaseRevision = baseRevision; }

        /// <summary>The words for the undo and redo menu items ("Add trail", "Move tower").</summary>
        public abstract string Label { get; }

        /// <summary>
        /// Applies the command to a document without any revision check, so a tool can test a command before
        /// confirming. <see cref="DesignHistory.Submit"/> adds the revision check and records undo.
        /// </summary>
        public CommandResult Apply(DesignDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var result = Run(doc);
            // NT24–NT28: whatever a command did to the trails, they stay whole (TrailLines.Normalise).
            if (!result.Accepted || !result.Document.TrailsDifferFrom(doc)) return result;
            var whole = TrailLines.Normalise(result.Document);
            return ReferenceEquals(whole, result.Document) ? result : CommandResult.Accept(whole, result.Created);
        }

        /// <summary>The command's own change, before the trails are mended: for commands built from other commands.</summary>
        internal CommandResult Raw(DesignDocument doc) => Run(doc);

        protected abstract CommandResult Run(DesignDocument doc);

        protected static CommandResult Invalid(DesignDocument doc, string reason) => CommandResult.Reject(doc, RejectionCode.Invalid, reason);
        protected static CommandResult Missing(DesignDocument doc, string what) => CommandResult.Reject(doc, RejectionCode.NotFound, what + " isn't in the design.");
    }

    /// <summary>
    /// CF-06's Break ground: turns saved plans into built work, all of them or none. Built work stays built; asking for
    /// it again is rejected so a double confirmation can't count twice (CF-04).
    /// </summary>
    public sealed class BreakGround : DesignCommand
    {
        public IReadOnlyList<TrailId> Trails { get; }
        public IReadOnlyList<LiftId> Lifts { get; }

        public BreakGround(long baseRevision, IReadOnlyList<TrailId> trails, IReadOnlyList<LiftId> lifts) : base(baseRevision)
        {
            Trails = trails ?? Array.Empty<TrailId>();
            Lifts = lifts ?? Array.Empty<LiftId>();
        }

        public override string Label => "Break ground";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Trails.Count + Lifts.Count == 0) return Invalid(doc, "Nothing to build.");
            foreach (var id in Trails)
            {
                var t = doc.FindTrail(id);
                if (t == null) return Missing(doc, "That trail");
                if (t.Stage != WorkStage.Plan) return Invalid(doc, $"{t.Name} is already built.");
                if (!t.HasEnds) return Invalid(doc, $"{t.Name} needs a head and a tail before it can be built.");
            }
            foreach (var id in Lifts)
            {
                var l = doc.FindLift(id);
                if (l == null) return Missing(doc, "That lift");
                if (l.Stage != WorkStage.Plan) return Invalid(doc, $"{l.Name} is already built.");
            }
            var seen = new HashSet<ulong>();
            foreach (var id in Trails) if (!seen.Add(id.Value)) return Invalid(doc, "A trail is listed twice.");
            foreach (var id in Lifts) if (!seen.Add(id.Value)) return Invalid(doc, "A lift is listed twice.");

            // NT32: a trail is built whole, every section of it.
            var sections = new List<TrailId>();
            var listed = new HashSet<ulong>();
            foreach (var id in Trails)
                foreach (var s in doc.LineOf(id).Sections)
                {
                    if (!listed.Add(s.Value)) continue;
                    var t = doc.FindTrail(s);
                    if (t.Stage != WorkStage.Plan) continue;
                    if (!t.HasEnds) return Invalid(doc, $"{t.Name} needs a head and a tail before it can be built.");
                    sections.Add(s);
                }
            var next = doc;
            foreach (var id in sections) next = next.PutTrail(next.FindTrail(id).WithStage(WorkStage.Built));
            foreach (var id in Lifts) next = next.PutLift(next.FindLift(id).WithStage(WorkStage.Built));
            return CommandResult.Accept(next);
        }
    }
}
