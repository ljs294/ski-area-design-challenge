using System;
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

        public SimDesign(long revision, ulong revisionHash, ulong resortSeed, DayHours hours, LiftInput[] lifts, int builtTrails)
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
        }

        /// <summary>The design-inputs builder (SI-21): from a design revision, on any thread.</summary>
        public static SimDesign From(DesignSnapshot snapshot)
        {
            var doc = snapshot.Document ?? throw new ArgumentNullException(nameof(snapshot));
            var lifts = new LiftInput[doc.Lifts.Count];
            for (int i = 0; i < lifts.Length; i++)
            {
                var l = doc.Lifts[i];
                lifts[i] = new LiftInput(l.Id.Value, l.Stage == WorkStage.Built, l.Schedule, l.Hours);
            }
            Array.Sort(lifts, (a, b) => a.Id.CompareTo(b.Id));
            int built = 0;
            foreach (var t in doc.Trails) if (t.Stage == WorkStage.Built) built++;
            return new SimDesign(snapshot.Revision, Pacing.Fnv64(DesignBytes.Encode(doc)), doc.Seed, doc.Hours, lifts, built);
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

        public LiftInput(ulong id, bool built, LiftSchedule schedule = LiftSchedule.Daily, DayHours? hours = null)
        {
            Id = id;
            Built = built;
            Schedule = schedule;
            Hours = hours;
        }
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
