using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>One revision of the design: what the UI reads (snapshots out).</summary>
    public readonly struct DesignSnapshot
    {
        public readonly long Revision;
        public readonly DesignDocument Document;
        /// <summary>The committed terrain edits that go with this design (task 05, TE2), never null.</summary>
        public readonly TerrainEdits Terrain;
        /// <summary>Counts up whenever <see cref="Terrain"/> changes; never reused (CF-03).</summary>
        public readonly long TerrainRevision;

        public DesignSnapshot(long revision, DesignDocument document) : this(revision, document, TerrainEdits.Empty, 0) { }

        public DesignSnapshot(long revision, DesignDocument document, TerrainEdits terrain, long terrainRevision)
        {
            Revision = revision;
            Document = document;
            Terrain = terrain ?? TerrainEdits.Empty;
            TerrainRevision = terrainRevision;
        }
    }

    /// <summary>
    /// The session's design: the current snapshot, and undo and redo as stacks of earlier immutable documents (CF-05),
    /// so a restore is exact by construction. Commands come in through <see cref="Submit"/>; nothing else changes the
    /// design. Every change, an undo or redo included, gets a new revision number that is never reused, so a result
    /// computed for an older revision can never pass as current (CF-03). Owner ruling P3-03 Q1: 100 steps, kept for
    /// the session only, cleared by <see cref="Load"/>. Not thread-safe: the main thread owns it.
    ///
    /// Each step also holds the terrain edits that went with its document (owner ruling P3-05 TE2), so undo and redo
    /// restore heights and trees exactly by switching edit sets (CF-05, CL-07); the terrain pipeline rebuilds only the
    /// tiles that differ (<see cref="TerrainEdits.Changed"/>).
    /// </summary>
    public sealed class DesignHistory
    {
        public const int UndoDepth = 100;

        readonly List<Step> _undo = new List<Step>();
        readonly List<Step> _redo = new List<Step>();

        public long Revision { get; private set; }
        public DesignDocument Document { get; private set; }
        /// <summary>The committed terrain edits (GR-09); empty for a new area.</summary>
        public TerrainEdits Terrain { get; private set; } = TerrainEdits.Empty;
        /// <summary>The terrain revision analyses are stamped with (CF-03): it moves on whenever <see cref="Terrain"/> changes.</summary>
        public long TerrainRevision { get; private set; } = 1;
        public DesignSnapshot Current => new DesignSnapshot(Revision, Document, Terrain, TerrainRevision);

        /// <summary>Raised after every change with the new snapshot.</summary>
        public event Action<DesignSnapshot> Changed;

        public DesignHistory(DesignDocument document, long firstRevision = 1)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Revision = firstRevision;
        }

        /// <summary>How many times <see cref="Load"/> has replaced the design (task 08: the earthwork learns a loaded design again).</summary>
        public int Loads { get; private set; }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;
        /// <summary>The label of the change Undo would take back ("Add trail"), or null.</summary>
        public string UndoLabel => CanUndo ? _undo[_undo.Count - 1].Label : null;
        public string RedoLabel => CanRedo ? _redo[_redo.Count - 1].Label : null;

        /// <summary>
        /// Runs a command against the current revision. A command made against any other revision is rejected as
        /// stale (CF-05), which also turns away a second confirmation in the same tick (CF-04): the first one moved
        /// the revision on. A rejected command changes nothing, not even the revision.
        /// </summary>
        public CommandResult Submit(DesignCommand command) => Submit(command, null);

        /// <summary>
        /// Runs a command that also changes the ground: <paramref name="terrain"/> is the complete edit set once it is
        /// applied (Break ground's grading and clearing, a deletion's restore). Null keeps the current edits. Undo puts
        /// back both the document and the edits.
        /// </summary>
        public CommandResult Submit(DesignCommand command, TerrainEdits terrain)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.BaseRevision != Revision)
                return CommandResult.Reject(Document, RejectionCode.Stale,
                    $"The design changed since this was made (revision {command.BaseRevision}, now {Revision}).");
            var result = command.Apply(Document);
            if (!result.Accepted) return result;
            Push(_undo, new Step(Document, Terrain, command.Label));
            _redo.Clear();
            Move(result.Document, terrain ?? Terrain);
            return result;
        }

        public bool Undo()
        {
            if (!CanUndo) return false;
            var step = Pop(_undo);
            Push(_redo, new Step(Document, Terrain, step.Label));
            Move(step.Document, step.Terrain);
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;
            var step = Pop(_redo);
            Push(_undo, new Step(Document, Terrain, step.Label));
            Move(step.Document, step.Terrain);
            return true;
        }

        /// <summary>Replaces the design (a load or a new area), with its saved terrain edits, and clears undo and redo.</summary>
        public void Load(DesignDocument document, TerrainEdits terrain = null)
        {
            Loads++;
            _undo.Clear();
            _redo.Clear();
            Move(document ?? throw new ArgumentNullException(nameof(document)), terrain ?? TerrainEdits.Empty);
        }

        /// <summary>The stamp an analysis started now carries (CF-03).</summary>
        public AnalysisStamp Stamp(long terrainRevision, long inputSequence) => new AnalysisStamp(Revision, terrainRevision, inputSequence);

        /// <summary>The stamp an analysis started now carries, against the current terrain revision (CF-03).</summary>
        public AnalysisStamp Stamp(long inputSequence) => new AnalysisStamp(Revision, TerrainRevision, inputSequence);

        void Move(DesignDocument next, TerrainEdits terrain)
        {
            Document = next;
            Revision++;
            if (!ReferenceEquals(terrain, Terrain))
            {
                Terrain = terrain;
                TerrainRevision++;
            }
            Changed?.Invoke(Current);
        }

        static void Push(List<Step> stack, Step step)
        {
            if (stack.Count == UndoDepth) stack.RemoveAt(0);
            stack.Add(step);
        }

        static Step Pop(List<Step> stack)
        {
            var step = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return step;
        }

        readonly struct Step
        {
            public readonly DesignDocument Document;
            public readonly TerrainEdits Terrain;
            public readonly string Label;
            public Step(DesignDocument document, TerrainEdits terrain, string label) { Document = document; Terrain = terrain; Label = label; }
        }
    }

    /// <summary>
    /// What an analysis (a grading preview, a tower layout, a trail's rating) was computed against (CF-03): the design
    /// revision, the terrain revision (task 05 owns it) and the tool's own input sequence (its cursor or stroke count).
    /// A result whose stamp isn't the current one is dropped, never merged.
    /// </summary>
    public readonly struct AnalysisStamp : IEquatable<AnalysisStamp>
    {
        public readonly long DesignRevision;
        public readonly long TerrainRevision;
        public readonly long InputSequence;

        public AnalysisStamp(long designRevision, long terrainRevision, long inputSequence)
        {
            DesignRevision = designRevision;
            TerrainRevision = terrainRevision;
            InputSequence = inputSequence;
        }

        public bool Equals(AnalysisStamp o) => DesignRevision == o.DesignRevision && TerrainRevision == o.TerrainRevision && InputSequence == o.InputSequence;
        public override bool Equals(object obj) => obj is AnalysisStamp o && Equals(o);
        public override int GetHashCode() => DesignRevision.GetHashCode() ^ (TerrainRevision.GetHashCode() * 31) ^ (InputSequence.GetHashCode() * 17);
        public static bool operator ==(AnalysisStamp a, AnalysisStamp b) => a.Equals(b);
        public static bool operator !=(AnalysisStamp a, AnalysisStamp b) => !a.Equals(b);
        public override string ToString() => $"design r{DesignRevision}, terrain r{TerrainRevision}, input {InputSequence}";
    }

    /// <summary>An analysis result with the stamp it was computed against.</summary>
    public readonly struct Stamped<T>
    {
        public readonly AnalysisStamp Stamp;
        public readonly T Value;
        public Stamped(AnalysisStamp stamp, T value) { Stamp = stamp; Value = value; }

        /// <summary>CF-03: the value, only if it was computed against <paramref name="current"/>.</summary>
        public bool TryTake(AnalysisStamp current, out T value)
        {
            if (Stamp == current)
            {
                value = Value;
                return true;
            }
            value = default;
            return false;
        }
    }
}
