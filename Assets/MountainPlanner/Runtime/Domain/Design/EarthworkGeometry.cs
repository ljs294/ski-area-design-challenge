using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// What gathering a design's pieces works out from a piece alone (task P3-11: every preview gathered every piece
    /// again, 30-55 ms on a resort): a trail's or lift's reach and tiles, kept with the very object they came from (pieces
    /// are immutable, so the same object always gives the same answer), and the ordinal maps by seed and next ordinal.
    /// Derived, never saved; a miss only works it out again.
    /// </summary>
    static class EarthworkGeometry
    {
        sealed class Entry
        {
            public EarthworkSite Site;
            public object Value;
        }

        static readonly ConditionalWeakTable<object, Entry> Kept = new ConditionalWeakTable<object, Entry>();

        /// <summary>The value worked out for <paramref name="piece"/> on <paramref name="site"/>, or <paramref name="make"/>'s.</summary>
        public static T Of<T>(object piece, EarthworkSite site, Func<T> make) where T : class
        {
            if (Kept.TryGetValue(piece, out var e) && Same(e.Site, site) && e.Value is T kept) return kept;
            var made = make();
            lock (Kept)
            {
                Kept.Remove(piece);
                Kept.Add(piece, new Entry { Site = site, Value = made });
            }
            return made;
        }

        static bool Same(EarthworkSite a, EarthworkSite b) =>
            a.Lattice.West == b.Lattice.West && a.Lattice.North == b.Lattice.North && a.OriginX == b.OriginX && a.OriginY == b.OriginY && a.Core.Equals(b.Core);

        sealed class Ordinals
        {
            public ulong Seed;
            public long Next;
            public Dictionary<ulong, long> Map;
        }

        static readonly Dictionary<string, Ordinals> LastOrdinals = new Dictionary<string, Ordinals>();

        /// <summary>
        /// The ordinal each ID of one kind was minted at (<paramref name="kind"/> names the kind, <paramref name="id"/>
        /// mints one), for this seed and next ordinal; the map is shared and never changed once made.
        /// </summary>
        public static Dictionary<ulong, long> OrdinalsOf(string kind, DesignDocument doc, Func<ulong, long, ulong> id)
        {
            lock (LastOrdinals)
                if (LastOrdinals.TryGetValue(kind, out var o) && o.Seed == doc.Seed && o.Next == doc.NextOrdinal) return o.Map;
            var map = new Dictionary<ulong, long>();
            for (long n = 0; n < doc.NextOrdinal; n++) map[id(doc.Seed, n)] = n;
            lock (LastOrdinals) LastOrdinals[kind] = new Ordinals { Seed = doc.Seed, Next = doc.NextOrdinal, Map = map };
            return map;
        }
    }
}
