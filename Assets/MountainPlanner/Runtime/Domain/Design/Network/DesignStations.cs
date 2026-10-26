using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>One lift terminal in a station: the lift, which end, and its bullwheel point.</summary>
    public readonly struct StationMember
    {
        public readonly LiftId Lift;
        public readonly bool Bottom;
        public readonly PointMm At;
        public StationMember(LiftId lift, bool bottom, PointMm at) { Lift = lift; Bottom = bottom; At = at; }
    }

    /// <summary>
    /// A station (NW-14, owner NT13): the area round one or more lift terminals that the network treats as one node. A
    /// lone terminal is a station of its own; terminals whose areas overlap share one (a base, or a transfer).
    /// </summary>
    public sealed class Station
    {
        /// <summary>JN-04: a keyed hash of its members' end keys, in order; a reload, or an unrelated lift, keeps it.</summary>
        public ulong Id { get; }
        /// <summary>The members' mean point, in whole mm.</summary>
        public PointMm Centre { get; }
        /// <summary>By lift ID, the bottom before the top.</summary>
        public IReadOnlyList<StationMember> Members { get; }

        internal Station(ulong id, PointMm centre, StationMember[] members)
        {
            Id = id;
            Centre = centre;
            Members = members;
        }

        public bool HasBottom
        {
            get
            {
                foreach (var m in Members) if (m.Bottom) return true;
                return false;
            }
        }

        /// <summary>The distance squared (mm²) from <paramref name="p"/> to the nearest member terminal.</summary>
        public long DistanceSquared(PointMm p)
        {
            long best = long.MaxValue;
            foreach (var m in Members) best = Math.Min(best, m.At.DistanceSquared(p));
            return best;
        }
    }

    /// <summary>
    /// NW-14's stations (owner NT13, 2026-10-26): every built lift's terminals, each reaching <see cref="ReachMm"/>
    /// (100 ft) from its bullwheel point; terminals within twice that (their areas overlap) merge, transitively.
    /// Deterministic: lifts in ID order, the bottom before the top; stations in ID order.
    /// </summary>
    public static class DesignStations
    {
        /// <summary>NW-14: a station reaches 100 ft (30.48 m) from each of its terminals.</summary>
        public static readonly int ReachMm = DesignUnits.FeetToMm(100);

        /// <param name="leaveOut">A lift to leave out (Edit lift's own, while its catchment previews, NT16).</param>
        public static List<Station> Of(DesignDocument doc, LiftId? leaveOut = null)
        {
            var lifts = new List<Lift>();
            foreach (var l in doc.Lifts) if (l.Stage == WorkStage.Built && l.Id != leaveOut) lifts.Add(l);
            lifts.Sort((a, b) => a.Id.CompareTo(b.Id));
            var ends = new List<StationMember>(lifts.Count * 2);
            foreach (var l in lifts)
            {
                ends.Add(new StationMember(l.Id, true, l.Bottom));
                ends.Add(new StationMember(l.Id, false, l.Top));
            }

            // Union-find over the terminals, areas overlapping (within 2 × reach) merging.
            var parent = new int[ends.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }
            long join = 2L * ReachMm;
            for (int i = 0; i < ends.Count; i++)
                for (int k = i + 1; k < ends.Count; k++)
                {
                    if (ends[i].Lift == ends[k].Lift) continue;   // a lift's own ends never share a station
                    if (!ends[i].At.Within(ends[k].At, (int)Math.Min(int.MaxValue, join))) continue;
                    int a = Find(i), b = Find(k);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }

            var groups = new SortedDictionary<int, List<StationMember>>();
            for (int i = 0; i < ends.Count; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var list)) groups.Add(r, list = new List<StationMember>());
                list.Add(ends[i]);
            }
            var stations = new List<Station>(groups.Count);
            foreach (var g in groups.Values)
            {
                ulong id = 0;
                long sx = 0, sy = 0;
                foreach (var m in g)
                {
                    ulong key = m.Bottom ? DesignNetwork.LiftBottomNode(doc.Seed, m.Lift) : DesignNetwork.LiftTopNode(doc.Seed, m.Lift);
                    id = id == 0 ? DesignKey.Hash(doc.Seed, DesignKey.StationTag, key) : DesignKey.Hash(doc.Seed, DesignKey.StationTag, id, key);
                    sx += m.At.X;
                    sy += m.At.Y;
                }
                var centre = new PointMm((int)Math.Round((double)sx / g.Count), (int)Math.Round((double)sy / g.Count));
                stations.Add(new Station(id, centre, g.ToArray()));
            }
            stations.Sort((a, b) => a.Id.CompareTo(b.Id));
            return stations;
        }

        /// <summary>The station within reach of <paramref name="p"/> (its nearest member), ties to the lower ID; null if none.</summary>
        public static Station At(IReadOnlyList<Station> stations, PointMm p)
        {
            Station best = null;
            long bestD = (long)ReachMm * ReachMm;
            foreach (var s in stations)   // in ID order, so a tie keeps the lower ID
            {
                long d = s.DistanceSquared(p);
                if (d > bestD || (d == bestD && best != null)) continue;
                best = s;
                bestD = d;
            }
            return best;
        }

        /// <summary>The station holding a lift's end; null when the lift isn't built.</summary>
        public static Station Of(IReadOnlyList<Station> stations, LiftId lift, bool bottom)
        {
            foreach (var s in stations)
                foreach (var m in s.Members)
                    if (m.Lift == lift && m.Bottom == bottom) return s;
            return null;
        }
    }
}
