using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A pond's ID, keyed like <see cref="TrailId"/> (task 4.14, PD-08).</summary>
    public readonly struct PondId : IEquatable<PondId>, IComparable<PondId>
    {
        public readonly ulong Value;
        public PondId(ulong value) { Value = value; }
        public static PondId Make(ulong seed, long ordinal) => new PondId(DesignKey.Hash(seed, DesignKey.PondTag, (ulong)ordinal));
        public bool IsNone => Value == 0;
        public bool Equals(PondId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PondId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(PondId other) => Value.CompareTo(other.Value);
        public static bool operator ==(PondId a, PondId b) => a.Value == b.Value;
        public static bool operator !=(PondId a, PondId b) => a.Value != b.Value;
        public override string ToString() => "pond:" + Value.ToString("x16");
    }

    /// <summary>A dam's ID, keyed like <see cref="TrailId"/> (task 4.14, PD-08).</summary>
    public readonly struct DamId : IEquatable<DamId>, IComparable<DamId>
    {
        public readonly ulong Value;
        public DamId(ulong value) { Value = value; }
        public static DamId Make(ulong seed, long ordinal) => new DamId(DesignKey.Hash(seed, DesignKey.DamTag, (ulong)ordinal));
        public bool IsNone => Value == 0;
        public bool Equals(DamId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is DamId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(DamId other) => Value.CompareTo(other.Value);
        public static bool operator ==(DamId a, DamId b) => a.Value == b.Value;
        public static bool operator !=(DamId a, DamId b) => a.Value != b.Value;
        public override string ToString() => "dam:" + Value.ToString("x16");
    }

    /// <summary>
    /// A dug pond (task 4.14, PD-01…04, PD-08, PD-09): the ring is the waterline at full pool, in the local frame (TE5);
    /// the berm sits outboard and the bowl is dug down to <see cref="ExcavationMm"/> under full pool. Authored data only;
    /// its earthwork is derived (<see cref="PondEarthwork"/>). Immutable.
    /// </summary>
    public sealed class Pond
    {
        public PondId Id { get; }
        public string Name { get; }
        /// <summary>The waterline at full pool, open (the last point isn't the first again), at least three points.</summary>
        public IReadOnlyList<PointMm> Ring => _ring;
        /// <summary>Full pool, absolute whole millimetres in the package's height datum (like a lift's levels, SK14).</summary>
        public int FullPoolMm { get; }
        /// <summary>PD-02: how deep under full pool the bowl may be dug (0–50 ft).</summary>
        public int ExcavationMm { get; }
        /// <summary>WS-04: a spring the player says feeds it, gallons a minute (0: runoff only).</summary>
        public int SpringGpm { get; }
        /// <summary>WS-09 (owner finding F5): fills naturally (runoff and spring) or only from a pump house (lined).</summary>
        public PondFill Fill { get; }
        /// <summary>WS-10 (owner finding F5): the pond starts empty instead of full.</summary>
        public bool StartsEmpty { get; }

        readonly PointMm[] _ring;

        public Pond(PondId id, string name, IReadOnlyList<PointMm> ring, int fullPoolMm, int excavationMm, int springGpm = 0,
                    PondFill fill = PondFill.Natural, bool startsEmpty = false)
        {
            Id = id;
            Name = name ?? "";
            _ring = new PointMm[ring?.Count ?? 0];
            for (int i = 0; i < _ring.Length; i++) _ring[i] = ring[i];
            FullPoolMm = fullPoolMm;
            ExcavationMm = excavationMm;
            SpringGpm = springGpm;
            Fill = fill;
            StartsEmpty = startsEmpty;
        }

        public Pond WithName(string name) => new Pond(Id, name, _ring, FullPoolMm, ExcavationMm, SpringGpm, Fill, StartsEmpty);
        public Pond With(IReadOnlyList<PointMm> ring, int fullPoolMm, int excavationMm) => new Pond(Id, Name, ring, fullPoolMm, excavationMm, SpringGpm, Fill, StartsEmpty);
        public Pond WithSpring(int gpm) => new Pond(Id, Name, _ring, FullPoolMm, ExcavationMm, gpm, Fill, StartsEmpty);
        public Pond WithWater(PondFill fill, bool startsEmpty) => new Pond(Id, Name, _ring, FullPoolMm, ExcavationMm, SpringGpm, fill, startsEmpty);

        /// <summary>The same ground work (ring, pool, dig), whatever the name, spring or water options.</summary>
        public bool SameWork(Pond o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o == null || FullPoolMm != o.FullPoolMm || ExcavationMm != o.ExcavationMm || _ring.Length != o._ring.Length) return false;
            for (int i = 0; i < _ring.Length; i++) if (!_ring[i].Equals(o._ring[i])) return false;
            return true;
        }
    }

    /// <summary>WS-09: how a pond gains water.</summary>
    public enum PondFill : byte
    {
        /// <summary>Runoff from its catchment and the player's spring (WS-04), the default.</summary>
        Natural = 0,
        /// <summary>Lined: no runoff and no spring; only a pump house fills it (WS-11, 4.15).</summary>
        Pumped = 1,
    }

    /// <summary>Which bank a dam's spillway is cut into (PD-07).</summary>
    public enum SpillBank : byte
    {
        /// <summary>The first bank clicked (<see cref="Dam.BankA"/>), the default.</summary>
        First = 0,
        /// <summary>The far bank (<see cref="Dam.BankB"/>).</summary>
        Far = 1,
    }

    /// <summary>
    /// A dam across one stream (task 4.14, PD-05…07, PD-08): an embankment from <see cref="BankA"/> to <see cref="BankB"/>, both
    /// on the full-pool contour, crest 2 ft over full pool, with a spillway notch down to full pool at one bank. The water
    /// side is stored (<see cref="UpstreamLeft"/>), decided once when it was placed, so the ground never depends on the
    /// stream data. Immutable.
    /// </summary>
    public sealed class Dam
    {
        public DamId Id { get; }
        public string Name { get; }
        public PointMm BankA { get; }
        public PointMm BankB { get; }
        /// <summary>Full pool, absolute whole millimetres (the first bank's ground when placed, PD-05).</summary>
        public int FullPoolMm { get; }
        /// <summary>Whether the water sits left of the line from <see cref="BankA"/> to <see cref="BankB"/> (looking from A to B).</summary>
        public bool UpstreamLeft { get; }
        public SpillBank Spill { get; }
        /// <summary>The dammed stream's OSM way (WS-08), or 0 for the rasterised streams.</summary>
        public ulong StreamWay { get; }
        /// <summary>WS-10 (owner finding F5): the reservoir starts empty instead of full.</summary>
        public bool StartsEmpty { get; }

        public Dam(DamId id, string name, PointMm bankA, PointMm bankB, int fullPoolMm, bool upstreamLeft, SpillBank spill, ulong streamWay, bool startsEmpty = false)
        {
            Id = id;
            Name = name ?? "";
            BankA = bankA;
            BankB = bankB;
            FullPoolMm = fullPoolMm;
            UpstreamLeft = upstreamLeft;
            Spill = spill;
            StreamWay = streamWay;
            StartsEmpty = startsEmpty;
        }

        public Dam WithName(string name) => new Dam(Id, name, BankA, BankB, FullPoolMm, UpstreamLeft, Spill, StreamWay, StartsEmpty);
        public Dam WithSpill(SpillBank spill) => new Dam(Id, Name, BankA, BankB, FullPoolMm, UpstreamLeft, spill, StreamWay, StartsEmpty);
        public Dam WithStartsEmpty(bool startsEmpty) => new Dam(Id, Name, BankA, BankB, FullPoolMm, UpstreamLeft, Spill, StreamWay, startsEmpty);
        public Dam WithLine(PointMm bankA, PointMm bankB, int fullPoolMm, bool upstreamLeft) =>
            new Dam(Id, Name, bankA, bankB, fullPoolMm, upstreamLeft, Spill, StreamWay, StartsEmpty);

        /// <summary>The same ground work (line, pool, water side, spillway), whatever the name or water options.</summary>
        public bool SameWork(Dam o) => ReferenceEquals(this, o) || o != null && BankA.Equals(o.BankA) && BankB.Equals(o.BankB)
            && FullPoolMm == o.FullPoolMm && UpstreamLeft == o.UpstreamLeft && Spill == o.Spill;
    }

    /// <summary>
    /// What the player set on a natural lake (task 4.14; WS-01, LK-02, LK-10): the lake's ID in the area's
    /// <see cref="Water.WaterBodyTable"/>, their name for it ("" keeps OpenStreetMap's), their mean depth in tenths of a
    /// foot (0 keeps LK-02's estimate) and whether it is a snowmaking source. Kept only while something is set.
    /// </summary>
    public readonly struct LakeRecord : IEquatable<LakeRecord>, IComparable<LakeRecord>
    {
        public readonly ulong Lake;
        public readonly string Name;
        /// <summary>LK-02's override: the mean depth in tenths of a foot, or 0 for the estimate.</summary>
        public readonly int DepthTenthsFt;
        /// <summary>WS-01: switched on as a snowmaking source.</summary>
        public readonly bool Source;
        public LakeRecord(ulong lake, string name, int depthTenthsFt, bool source)
        {
            Lake = lake;
            Name = name ?? "";
            DepthTenthsFt = depthTenthsFt;
            Source = source;
        }
        /// <summary>Nothing set: the record leaves the design.</summary>
        public bool IsEmpty => (Name ?? "").Length == 0 && DepthTenthsFt == 0 && !Source;
        public LakeRecord WithName(string name) => new LakeRecord(Lake, name, DepthTenthsFt, Source);
        public LakeRecord WithDepth(int depthTenthsFt) => new LakeRecord(Lake, Name, depthTenthsFt, Source);
        public LakeRecord WithSource(bool source) => new LakeRecord(Lake, Name, DepthTenthsFt, source);
        public bool Equals(LakeRecord o) => Lake == o.Lake && (Name ?? "") == (o.Name ?? "") && DepthTenthsFt == o.DepthTenthsFt && Source == o.Source;
        public override bool Equals(object obj) => obj is LakeRecord o && Equals(o);
        public override int GetHashCode() => Lake.GetHashCode() * 31 + DepthTenthsFt;
        public int CompareTo(LakeRecord o) => Lake.CompareTo(o.Lake);
    }

    /// <summary>
    /// What the player set on a stream line (task 4.14; WS-03, WS-08, LK-10): its OpenStreetMap way, their name for it ("" keeps
    /// OpenStreetMap's) and their channel width in whole centimetres (0 keeps OpenStreetMap's or the class default). Every dam
    /// on the stream reads this width. Kept only while something is set.
    /// </summary>
    public readonly struct StreamRecord : IEquatable<StreamRecord>, IComparable<StreamRecord>
    {
        public readonly ulong Way;
        public readonly string Name;
        public readonly int WidthCm;
        public StreamRecord(ulong way, string name, int widthCm)
        {
            Way = way;
            Name = name ?? "";
            WidthCm = widthCm;
        }
        public bool IsEmpty => (Name ?? "").Length == 0 && WidthCm == 0;
        public StreamRecord WithName(string name) => new StreamRecord(Way, name, WidthCm);
        public StreamRecord WithWidth(int widthCm) => new StreamRecord(Way, Name, widthCm);
        public bool Equals(StreamRecord o) => Way == o.Way && (Name ?? "") == (o.Name ?? "") && WidthCm == o.WidthCm;
        public override bool Equals(object obj) => obj is StreamRecord o && Equals(o);
        public override int GetHashCode() => Way.GetHashCode() * 31 + WidthCm;
        public int CompareTo(StreamRecord o) => Way.CompareTo(o.Way);
    }
}
