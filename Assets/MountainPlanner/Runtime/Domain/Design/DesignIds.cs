using System;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Keyed hashes for design IDs (SH-04, JN-04): a SplitMix64 finalizer chain over a tag and its inputs. The design's
    /// only source of identity and randomness; kept separate from the forest's hash so neither can move the other's
    /// goldens.
    /// </summary>
    public static class DesignKey
    {
        // Domain tags: one per kind of thing an ID names. Never reuse or renumber one (the IDs are saved).
        public const ulong TrailTag = 1;
        public const ulong LiftTag = 2;
        public const ulong TowerTag = 3;
        public const ulong LiftBottomTag = 4;
        public const ulong LiftTopTag = 5;
        public const ulong TrailHeadTag = 6;
        public const ulong TrailTailTag = 7;
        public const ulong JunctionTag = 8;
        public const ulong PathTag = 9;
        public const ulong PathEndTag = 10;
        /// <summary>CL-02's ragged edge: whether a cell in a trail's edge band is cleared (task 07).</summary>
        public const ulong ClearingEdgeTag = 11;
        /// <summary>A terrain brush stroke (task 15, TB-14).</summary>
        public const ulong TerrainStrokeTag = 12;
        /// <summary>NW-14's stations (task 10, owner NT13).</summary>
        public const ulong StationTag = 13;
        /// <summary>NW-15's named Areas (task 10, owner NT19).</summary>
        public const ulong AreaTag = 14;
        /// <summary>A trail made of sections (task 10, owner NT24): keyed by its founding section, never by an ordinal.</summary>
        public const ulong TrailLineTag = 15;

        public static ulong Hash(ulong seed, ulong tag, ulong a) => Hash(seed, tag, a, 0);

        public static ulong Hash(ulong seed, ulong tag, ulong a, ulong b)
        {
            ulong h = Mix(seed ^ tag * 0x9E3779B97F4A7C15UL);
            h = Mix(h ^ a * 0xBF58476D1CE4E5B9UL);
            h = Mix(h ^ b * 0x94D049BB133111EBUL);
            return h == 0 ? 1 : h; // 0 is "none"
        }

        public static ulong Hash(ulong seed, ulong tag, ulong a, ulong b, ulong c) => Hash(Hash(seed, tag, a, b), tag, c);

        static ulong Mix(ulong h)
        {
            h ^= h >> 30;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            return h;
        }
    }

    /// <summary>A trail's ID: <see cref="DesignKey"/> of the design seed and the document ordinal it was made at.</summary>
    public readonly struct TrailId : IEquatable<TrailId>, IComparable<TrailId>
    {
        public readonly ulong Value;
        public TrailId(ulong value) { Value = value; }
        public static TrailId Make(ulong seed, long ordinal) => new TrailId(DesignKey.Hash(seed, DesignKey.TrailTag, (ulong)ordinal));
        public bool IsNone => Value == 0;
        public bool Equals(TrailId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TrailId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(TrailId other) => Value.CompareTo(other.Value);
        public static bool operator ==(TrailId a, TrailId b) => a.Value == b.Value;
        public static bool operator !=(TrailId a, TrailId b) => a.Value != b.Value;
        public override string ToString() => "trail:" + Value.ToString("x16");
    }

    /// <summary>
    /// A trail's ID as the player knows it (owner NT24): a keyed hash of the section it was founded on, so a one-section
    /// trail has it without saving anything, and keeps it when a second section joins. <c>k</c> rises only on a collision.
    /// </summary>
    public readonly struct TrailLineId : IEquatable<TrailLineId>, IComparable<TrailLineId>
    {
        public readonly ulong Value;
        public TrailLineId(ulong value) { Value = value; }
        public static TrailLineId Of(ulong seed, TrailId founding, ulong k = 0) => new TrailLineId(DesignKey.Hash(seed, DesignKey.TrailLineTag, founding.Value, k));
        public bool IsNone => Value == 0;
        public bool Equals(TrailLineId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TrailLineId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(TrailLineId other) => Value.CompareTo(other.Value);
        public static bool operator ==(TrailLineId a, TrailLineId b) => a.Value == b.Value;
        public static bool operator !=(TrailLineId a, TrailLineId b) => a.Value != b.Value;
        public override string ToString() => "line:" + Value.ToString("x16");
    }

    /// <summary>A lift's ID, keyed like <see cref="TrailId"/>.</summary>
    public readonly struct LiftId : IEquatable<LiftId>, IComparable<LiftId>
    {
        public readonly ulong Value;
        public LiftId(ulong value) { Value = value; }
        public static LiftId Make(ulong seed, long ordinal) => new LiftId(DesignKey.Hash(seed, DesignKey.LiftTag, (ulong)ordinal));
        public bool IsNone => Value == 0;
        public bool Equals(LiftId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is LiftId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(LiftId other) => Value.CompareTo(other.Value);
        public static bool operator ==(LiftId a, LiftId b) => a.Value == b.Value;
        public static bool operator !=(LiftId a, LiftId b) => a.Value != b.Value;
        public override string ToString() => "lift:" + Value.ToString("x16");
    }

    /// <summary>A tower's ID, keyed like <see cref="TrailId"/>; unique across the whole design.</summary>
    public readonly struct TowerId : IEquatable<TowerId>, IComparable<TowerId>
    {
        public readonly ulong Value;
        public TowerId(ulong value) { Value = value; }
        public static TowerId Make(ulong seed, long ordinal) => new TowerId(DesignKey.Hash(seed, DesignKey.TowerTag, (ulong)ordinal));
        public bool IsNone => Value == 0;
        public bool Equals(TowerId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TowerId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(TowerId other) => Value.CompareTo(other.Value);
        public static bool operator ==(TowerId a, TowerId b) => a.Value == b.Value;
        public static bool operator !=(TowerId a, TowerId b) => a.Value != b.Value;
        public override string ToString() => "tower:" + Value.ToString("x16");
    }
}
