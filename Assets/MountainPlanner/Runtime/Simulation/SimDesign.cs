using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-20, SI-21: what the simulation reads of a design revision, built by the submitter off the worker and delivered
    /// whole in a <see cref="SimCommandKind.SetDesign"/> command. Immutable arrays only: the worker swaps it in and folds its
    /// own state, and never computes geometry. Task 4.03 reads the lifts (built or not, their schedule and hours), the
    /// number of built trails and the resort's hours; later tasks add their own derived inputs here.
    /// </summary>
    public sealed class SimDesign
    {
        public readonly long Revision;
        /// <summary>SI-22: a hash of the document's canonical bytes (<see cref="DesignBytes"/>).</summary>
        public readonly ulong RevisionHash;
        /// <summary>The design's seed: the resort's seed every season seed is drawn from (SI-15).</summary>
        public readonly ulong ResortSeed;
        public readonly DayHours Hours;
        /// <summary>Every lift, in ID order.</summary>
        public readonly LiftInput[] Lifts;
        public readonly int BuiltTrails;
        /// <summary>LO-10's descent graph, or null before the app's network build arrives: then every built lift has a way down
        /// (4.03's stand-in) and every built trail counts as reached.</summary>
        public readonly SimNetwork Network;

        public SimDesign(long revision, ulong revisionHash, ulong resortSeed, DayHours hours, LiftInput[] lifts, int builtTrails,
                         SimNetwork network = null)
        {
            if (hours.Problem is string p) throw new ArgumentException("The resort's hours: " + p, nameof(hours));
            Revision = revision;
            RevisionHash = revisionHash;
            ResortSeed = resortSeed;
            Hours = hours;
            Lifts = lifts ?? Array.Empty<LiftInput>();
            for (int i = 1; i < Lifts.Length; i++)
                if (Lifts[i - 1].Id >= Lifts[i].Id) throw new ArgumentException("Lifts must be in ID order, each once.", nameof(lifts));
            BuiltTrails = builtTrails;
            Network = network;
            if (network != null)
                foreach (var l in Lifts)
                    if (l.TopNode >= network.NodeCount || l.BottomNode >= network.NodeCount)
                        throw new ArgumentException("A lift's station is outside the network.", nameof(lifts));
        }

        /// <summary>The design-inputs builder (SI-21): from a design revision, on any thread.</summary>
        /// <param name="built">The app's built network for this same document (LO-10), or null while it's still being built.</param>
        public static SimDesign From(DesignSnapshot snapshot, NetworkGraph built = null, INetworkFacts facts = null)
        {
            var doc = snapshot.Document ?? throw new ArgumentNullException(nameof(snapshot));
            var sorted = new List<Lift>(doc.Lifts);
            sorted.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            var ids = new ulong[sorted.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = sorted[i].Id.Value;
            SimNetwork network = null;
            int[] tops = null, bottoms = null;
            if (built != null) network = SimNetwork.From(built, ids, out tops, out bottoms, facts);
            var lifts = new LiftInput[sorted.Count];
            for (int i = 0; i < lifts.Length; i++)
            {
                int bottomNode = bottoms != null ? bottoms[i] : -1;
                // GU-09: an Area is one station node (NW-15), so the walk in is from that node to this lift's own bottom.
                int walkMs = 0;
                if (built != null && bottomNode >= 0)
                    walkMs = WalkMs(built.Nodes[bottomNode].At, sorted[i].Bottom);
                lifts[i] = Input(sorted[i], tops != null ? tops[i] : -1, bottomNode, walkMs);
            }
            int builtTrails = 0;
            foreach (var t in doc.Trails) if (t.Stage == WorkStage.Built) builtTrails++;
            return new SimDesign(snapshot.Revision, Pacing.Fnv64(DesignBytes.Encode(doc)), doc.Seed, doc.Hours, lifts, builtTrails, network);
        }

        /// <summary>
        /// LO-01, LO-03, LO-06: a lift's catalog numbers as whole milliseconds, worked out here once so the worker never
        /// touches the catalog's doubles or the line's geometry (SI-21).
        /// </summary>
        /// <summary>GU-09: walking <paramref name="from"/> to <paramref name="to"/> at 3 mph, real ms at 1×.</summary>
        static int WalkMs(PointMm from, PointMm to)
        {
            double mm = Math.Sqrt(from.DistanceSquared(to));
            // 3 mph is 1.341 m/s: ms = mm ÷ 1.341.
            return (int)Math.Min(int.MaxValue, Math.Round(mm / 1.341));
        }

        public static LiftInput Input(Lift l, int topNode = -1, int bottomNode = -1, int walkInMs = 0)
        {
            var type = LiftType.Of(l);
            string id = l.CatalogId ?? "";
            var kind = type.Belt ? LiftKind.Carpet
                : type.Tow ? LiftKind.Tow
                : id.StartsWith("tram-", StringComparison.Ordinal) ? LiftKind.Tram
                : id.StartsWith("gondola-", StringComparison.Ordinal) ? LiftKind.Gondola
                : type.Detachable ? LiftKind.DetachableChair
                : LiftKind.FixedGripChair;
            int seats = Math.Max(1, Math.Min(255, type.Seats));
            // LO-08: a tram's interval is its catalog capacity's (one cabin each way), not its spacing over its speed.
            double interval = kind == LiftKind.Tram ? seats * 3600.0 / type.Capacity : type.IntervalSeconds;
            double rise = l.Levels.HasValue ? l.Levels.Value.TopMetres - l.Levels.Value.BottomMetres : 0;
            double along = Math.Sqrt(l.LengthMetres * l.LengthMetres + rise * rise);
            double ride = type.RideSeconds(along);
            int intervalMs = (int)Math.Max(100, Math.Min(int.MaxValue / 4, Math.Round(interval * 1000)));
            int rideMs = (int)Math.Max(1000, Math.Min(int.MaxValue / 4, Math.Round(ride * 1000)));
            int bottomLevelCm = l.Levels.HasValue ? l.Levels.Value.BottomMm / 10 : 0;
            int topFeet = l.Levels.HasValue ? (int)Math.Round(l.Levels.Value.TopMetres / 0.3048) : 0;
            return new LiftInput(l.Id.Value, l.Stage == WorkStage.Built, l.Schedule, l.Hours, kind, seats, intervalMs, rideMs,
                                 topNode, bottomNode, walkInMs, bottomLevelCm, topFeet);
        }

        /// <summary>
        /// The same design with its lifts lined up with <paramref name="ids"/> (a sidecar's own lifts, SI-20): each found by ID,
        /// any other on the catalog's defaults. The network goes with it only when every lift is found.
        /// </summary>
        internal SimDesign AlignedTo(ulong[] ids, int n)
        {
            bool same = n == Lifts.Length;
            for (int i = 0; same && i < n; i++) same = Lifts[i].Id == ids[i];
            if (same) return this;
            var lifts = new LiftInput[n];
            bool all = true;
            for (int i = 0; i < n; i++)
            {
                int at = -1;
                for (int k = 0; k < Lifts.Length; k++) if (Lifts[k].Id == ids[i]) { at = k; break; }
                if (at >= 0) lifts[i] = Lifts[at];
                else { lifts[i] = new LiftInput(ids[i], false); all = false; }
            }
            return new SimDesign(Revision, RevisionHash, ResortSeed, Hours, lifts, BuiltTrails, all ? Network : null);
        }

        /// <summary>A design with no lifts and no trails (tests, the title).</summary>
        public static SimDesign Empty(ulong resortSeed) => new SimDesign(0, 0, resortSeed, DayHours.Default, Array.Empty<LiftInput>(), 0);

        /// <summary>
        /// TM-11 v2's stand-in until 4.07 and 4.11: a lift serves a run when any lift is built and any trail is built (every
        /// built trail is Ready, every built lift has a way down).
        /// </summary>
        public bool LiftServesRun
        {
            get
            {
                if (BuiltTrails == 0) return false;
                foreach (var l in Lifts) if (l.Built) return true;
                return false;
            }
        }
    }

    /// <summary>A lift as the simulation reads it (SI-21).</summary>
    public readonly struct LiftInput
    {
        public readonly ulong Id;
        public readonly bool Built;
        public readonly LiftSchedule Schedule;
        public readonly DayHours? Hours;
        public readonly LiftKind Kind;
        /// <summary>LO-02: the line is this many wide; a carrier takes this many.</summary>
        public readonly int Seats;
        /// <summary>LO-01: motion ms between carriers at 1x (before <see cref="Pacing.ChairSpeedFactor"/>).</summary>
        public readonly int IntervalMs;
        /// <summary>LO-06: the catalog ride, bottom to top, motion ms (detachables with their ramps).</summary>
        public readonly int RideMs;
        /// <summary>LO-10: the lift's top and bottom stations in <see cref="SimDesign.Network"/>, or -1.</summary>
        public readonly int TopNode, BottomNode;
        /// <summary>GU-09: how long a skier takes to walk across its Area to this lift's bottom, real ms at 1× (0: none).</summary>
        public readonly int WalkInMs;
        /// <summary>GU-06: the bottom station's level, centimetres, which picks the lowest base when no station qualifies.</summary>
        public readonly int BottomLevelCm;
        /// <summary>WX-39 (task 4.05): the top station's height, ft, where the weather samples the lift's gust (0: unknown, the summit).</summary>
        public readonly int TopFeet;

        public LiftInput(ulong id, bool built, LiftSchedule schedule = LiftSchedule.Daily, DayHours? hours = null,
                         LiftKind kind = LiftKind.FixedGripChair, int seats = 4, int intervalMs = 6000, int rideMs = 360_000,
                         int topNode = -1, int bottomNode = -1, int walkInMs = 0, int bottomLevelCm = 0, int topFeet = 0)
        {
            if (seats < 1 || seats > 255) throw new ArgumentOutOfRangeException(nameof(seats));
            if (intervalMs < 1) throw new ArgumentOutOfRangeException(nameof(intervalMs));
            if (rideMs < 1) throw new ArgumentOutOfRangeException(nameof(rideMs));
            if (hours.HasValue && hours.Value.Problem is string p) throw new ArgumentException("The lift's hours: " + p, nameof(hours));
            Id = id;
            Built = built;
            Schedule = schedule;
            Hours = hours;
            Kind = kind;
            Seats = seats;
            IntervalMs = intervalMs;
            RideMs = rideMs;
            TopNode = topNode;
            BottomNode = bottomNode;
            WalkInMs = walkInMs < 0 ? 0 : walkInMs;
            BottomLevelCm = bottomLevelCm;
            TopFeet = topFeet < 0 ? 0 : topFeet;
        }

        /// <summary>LO-03: rated capacity, people an hour, at 1x.</summary>
        public int RatedPerHour => (int)(Seats * 3_600_000L / IntervalMs);
    }

    /// <summary>Where the mountain is, for the daylight that bounds the resort's hours (owner, 2026-11-08; question 10).</summary>
    public readonly struct SimSite
    {
        public readonly double Latitude, Longitude;
        public readonly bool Known;
        public SimSite(double latitude, double longitude) { Latitude = latitude; Longitude = longitude; Known = true; }
        /// <summary>No site: the hours are never trimmed (tests).</summary>
        public static SimSite None => default;
    }
}
