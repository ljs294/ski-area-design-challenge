using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>What names a node on a run (owner NT43–NT45). Values are saved: never renumber.</summary>
    public enum AnchorKind : byte { Head = 1, Tail = 2, Lands = 3, Crosses = 4, Station = 5, Cut = 6 }

    /// <summary>
    /// A stable name for a node on one run (a piece), so a saved trail finds its sections again after other edits: the
    /// run's own head or tail; where another run's head or tail lands on it; the k-th crossing with another run along it;
    /// a lift station it passes (by a lift end the station holds); or a point the player cut (NT45). Immutable.
    /// </summary>
    public readonly struct Anchor : IEquatable<Anchor>
    {
        public readonly AnchorKind Kind;
        /// <summary>The other run (<see cref="AnchorKind.Lands"/>, <see cref="AnchorKind.Crosses"/>) or the lift
        /// (<see cref="AnchorKind.Station"/>); 0 otherwise.</summary>
        public readonly ulong Other;
        /// <summary>Lands: the other run's head (else its tail). Station: the lift's bottom (else its top).</summary>
        public readonly bool End;
        /// <summary>Crosses: which crossing with that run, counted from this run's head (0 first).</summary>
        public readonly int K;
        /// <summary>Cut: the point cut, site-centred mm.</summary>
        public readonly PointMm At;

        Anchor(AnchorKind kind, ulong other, bool end, int k, PointMm at)
        {
            Kind = kind;
            Other = other;
            End = end;
            K = k;
            At = at;
        }

        public static Anchor Head => new Anchor(AnchorKind.Head, 0, false, 0, default);
        public static Anchor Tail => new Anchor(AnchorKind.Tail, 0, false, 0, default);
        public static Anchor Lands(TrailId other, bool head) => new Anchor(AnchorKind.Lands, other.Value, head, 0, default);
        public static Anchor Crosses(TrailId other, int k) => new Anchor(AnchorKind.Crosses, other.Value, false, k, default);
        public static Anchor Station(LiftId lift, bool bottom) => new Anchor(AnchorKind.Station, lift.Value, bottom, 0, default);
        public static Anchor Cut(PointMm at) => new Anchor(AnchorKind.Cut, 0, false, 0, at);

        /// <summary>The anchor as read back from a file (checked by <see cref="DesignRules"/>).</summary>
        public static Anchor Of(AnchorKind kind, ulong other, bool end, int k, PointMm at) => new Anchor(kind, other, end, k, at);

        /// <summary>The order a node's anchors are preferred in when a span is saved: the most lasting first.</summary>
        internal int Rank => Kind == AnchorKind.Head || Kind == AnchorKind.Tail ? 0 : Kind == AnchorKind.Cut ? 1 : Kind == AnchorKind.Lands ? 2 : Kind == AnchorKind.Station ? 3 : 4;

        public bool Equals(Anchor o) => Kind == o.Kind && Other == o.Other && End == o.End && K == o.K && At.Equals(o.At);
        public override bool Equals(object obj) => obj is Anchor o && Equals(o);
        public override int GetHashCode() => ((int)Kind * 397) ^ Other.GetHashCode() ^ (End ? 1 : 0) ^ (K * 31) ^ At.GetHashCode();
        public override string ToString() => Kind + (Other != 0 ? ":" + Other.ToString("x16") : "") + (Kind == AnchorKind.Cut ? "@" + At : "");
    }

    /// <summary>
    /// A stretch of one run in a saved trail: from one node to another, each by its anchor, with where it was along the
    /// run when saved (mm from the head) to fall back on if an anchor is gone or moved far (NT46). A span holds every
    /// segment between its two nodes, so a junction appearing inside it keeps both halves.
    /// </summary>
    public readonly struct Span : IEquatable<Span>
    {
        public readonly TrailId Piece;
        public readonly Anchor From, To;
        public readonly int FromMm, ToMm;

        public Span(TrailId piece, Anchor from, Anchor to, int fromMm, int toMm)
        {
            Piece = piece;
            From = from;
            To = to;
            FromMm = fromMm;
            ToMm = toMm;
        }

        /// <summary>A whole run, head to tail (a v6 save's sections; a run carried on whole, NT38).</summary>
        public static Span Whole(TrailId piece) => new Span(piece, Anchor.Head, Anchor.Tail, 0, 0);

        public bool Equals(Span o) => Piece == o.Piece && From.Equals(o.From) && To.Equals(o.To) && FromMm == o.FromMm && ToMm == o.ToMm;
        public override bool Equals(object obj) => obj is Span o && Equals(o);
        public override int GetHashCode() => Piece.GetHashCode() ^ From.GetHashCode() * 7 ^ To.GetHashCode() * 13 ^ FromMm ^ ToMm * 3;
    }

    /// <summary>A point the player cut on a run (NT45): a node there. It never touches paint, footprint or ground.</summary>
    public readonly struct TrailCut : IEquatable<TrailCut>, IComparable<TrailCut>
    {
        public readonly TrailId Piece;
        public readonly PointMm At;

        public TrailCut(TrailId piece, PointMm at)
        {
            Piece = piece;
            At = at;
        }

        public bool Equals(TrailCut o) => Piece == o.Piece && At.Equals(o.At);
        public override bool Equals(object obj) => obj is TrailCut o && Equals(o);
        public override int GetHashCode() => Piece.GetHashCode() ^ At.GetHashCode();
        public int CompareTo(TrailCut o) => Piece != o.Piece ? Piece.CompareTo(o.Piece) : At.X != o.At.X ? At.X.CompareTo(o.At.X) : At.Y.CompareTo(o.At.Y);
    }

    /// <summary>
    /// A trail the player has shaped (owner NT24, NT43): a name and spans of runs, top first. Every segment in no saved
    /// trail belongs to its run's own trail, named as the run (<see cref="TrailSections"/>). Immutable.
    /// </summary>
    public sealed class TrailLine
    {
        public TrailLineId Id { get; }
        public string Name { get; }
        public IReadOnlyList<Span> Spans => _spans;

        readonly Span[] _spans;

        public TrailLine(TrailLineId id, string name, IReadOnlyList<Span> spans)
        {
            Id = id;
            Name = name ?? "";
            _spans = new Span[spans?.Count ?? 0];
            for (int i = 0; i < _spans.Length; i++) _spans[i] = spans[i];
        }

        public TrailLine WithName(string name) => new TrailLine(Id, name, _spans);
        public TrailLine WithSpans(IReadOnlyList<Span> spans) => new TrailLine(Id, Name, spans);
    }
}
