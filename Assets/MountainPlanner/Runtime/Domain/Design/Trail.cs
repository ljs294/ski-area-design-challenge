using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>CF-06: a plan is drawn orange and changes no ground; Break ground builds it.</summary>
    public enum WorkStage : byte { Plan = 1, Built = 2 }

    /// <summary>TR-06. Values are saved: never renumber.</summary>
    public enum TrailGrading : byte { Graded = 1, Natural = 2 }

    /// <summary>TR-06: a Zone is a freeride or backcountry area. Phase 3 builds runs only.</summary>
    public enum TrailKind : byte { Run = 1, Glade = 2, Zone = 3 }

    /// <summary>TR-06. Phase 3 clears fully.</summary>
    public enum TrailClearing : byte { Clear = 1, Thin = 2, None = 3 }

    /// <summary>A trail's three settings (TR-06), held whole so freer forms come later without a format change.</summary>
    public readonly struct TrailSettings : System.IEquatable<TrailSettings>
    {
        public readonly TrailGrading Grading;
        public readonly TrailKind Kind;
        public readonly TrailClearing Clearing;

        public TrailSettings(TrailGrading grading, TrailKind kind = TrailKind.Run, TrailClearing clearing = TrailClearing.Clear)
        {
            Grading = grading;
            Kind = kind;
            Clearing = clearing;
        }

        public static TrailSettings GradedRun => new TrailSettings(TrailGrading.Graded);
        public static TrailSettings NaturalRun => new TrailSettings(TrailGrading.Natural);

        /// <summary>What Phase 3 can build (TR-06's last column): both gradings, runs only, clear only.</summary>
        public bool AvailableNow => Kind == TrailKind.Run && Clearing == TrailClearing.Clear
            && (Grading == TrailGrading.Graded || Grading == TrailGrading.Natural);

        internal bool Defined => (Grading == TrailGrading.Graded || Grading == TrailGrading.Natural)
            && Kind >= TrailKind.Run && Kind <= TrailKind.Zone && Clearing >= TrailClearing.Clear && Clearing <= TrailClearing.None;

        public bool Equals(TrailSettings o) => Grading == o.Grading && Kind == o.Kind && Clearing == o.Clearing;
        public override bool Equals(object obj) => obj is TrailSettings o && Equals(o);
        public override int GetHashCode() => ((int)Grading << 16) | ((int)Kind << 8) | (int)Clearing;
    }

    /// <summary>
    /// A painted trail (TR-02, TR-03, TR-06), authored data only (SH-05, NW-06): the footprint as drawn, the head and
    /// tail as points (owner ruling P3-03 Q3: what they attach to is derived), the name and the settings. The
    /// centreline, rating, grading and volumes are derived by task 08. Immutable.
    /// </summary>
    public sealed class Trail
    {
        public TrailId Id { get; }
        public string Name { get; }
        public WorkStage Stage { get; }
        public TrailSettings Settings { get; }
        public IReadOnlyList<FootprintPart> Parts => _parts;
        /// <summary>The top end (TR-03); null until step 2 sets it.</summary>
        public PointMm? Head { get; }
        /// <summary>The bottom end; null until step 2 sets it.</summary>
        public PointMm? Tail { get; }

        readonly FootprintPart[] _parts;

        public Trail(TrailId id, string name, WorkStage stage, TrailSettings settings, IReadOnlyList<FootprintPart> parts, PointMm? head, PointMm? tail)
        {
            Id = id;
            Name = name;
            Stage = stage;
            Settings = settings;
            _parts = new FootprintPart[parts?.Count ?? 0];
            for (int i = 0; i < _parts.Length; i++) _parts[i] = parts[i];
            Head = head;
            Tail = tail;
        }

        public bool HasEnds => Head.HasValue && Tail.HasValue;

        public Trail WithName(string name) => new Trail(Id, name, Stage, Settings, _parts, Head, Tail);
        public Trail WithStage(WorkStage stage) => new Trail(Id, Name, stage, Settings, _parts, Head, Tail);
        public Trail WithSettings(TrailSettings settings) => new Trail(Id, Name, Stage, settings, _parts, Head, Tail);
        public Trail WithParts(IReadOnlyList<FootprintPart> parts) => new Trail(Id, Name, Stage, Settings, parts, Head, Tail);
        public Trail WithEnds(PointMm? head, PointMm? tail) => new Trail(Id, Name, Stage, Settings, _parts, head, tail);
    }
}
