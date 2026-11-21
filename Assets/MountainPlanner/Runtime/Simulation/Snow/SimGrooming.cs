using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Simulation
{
    /// <summary>GO-07: the job a trail gets tonight. Values are saved (sidecar v13): never renumber.</summary>
    public enum GroomJob : byte { None = 0, Till = 1, Push = 2 }

    /// <summary>
    /// GO-08: where a trail stands in tonight's plan (the Grooming tab's "Tonight" column), and, kept for the morning, what
    /// happened to it. Values are saved (sidecar v13): never renumber.
    /// </summary>
    public enum GroomRowState : byte
    {
        /// <summary>Nothing asked tonight (Autopilot found nothing to do, or the trail is When ordered and wasn't).</summary>
        None = 0,
        /// <summary>On a groomer's lane, not yet started.</summary>
        Planned = 1,
        /// <summary>Started (or part of it fits in tonight's shifts).</summary>
        Partial = 2,
        /// <summary>Out of hours: no crewed groomer reaches it.</summary>
        NotReached = 3,
        /// <summary>Groomed, but a pitch past <see cref="Pacing.GroomWinchGrade"/> was skipped (no W groomer).</summary>
        PitchSkipped = 4,
        /// <summary>Scheduled but too bare to till (SN-23 v4).</summary>
        TooBare = 5,
        /// <summary>Groomed in full.</summary>
        Done = 6,
        /// <summary>Autopilot keeps the powder (SN-22 v4).</summary>
        PowderHeld = 7,
        /// <summary>The trail's setting is Never.</summary>
        Never = 8,
        /// <summary>The resort has no groomer.</summary>
        NoGroomer = 9,
        /// <summary>The player skipped it tonight.</summary>
        Skipped = 10,
    }

    /// <summary>GO-10: Autopilot's four warnings (owner round 3). Values are saved (sidecar v13) and in events: never renumber.</summary>
    public enum GroomWarningKind : byte { WhalesBlockOpening = 1, TooBare = 2, PowderHeld = 3, PitchSkippedNoWinch = 4 }

    /// <summary>GO-07: the player's order for one trail tonight. Values are saved (sidecar v13): never renumber.</summary>
    public enum GroomOrder : byte { None = 0, Till = 1, Push = 2, Skip = 3 }

    /// <summary>
    /// Task 4.25 (GO-01, GO-05, SN-22 v4): <see cref="SimCommandKind.SetGrooming"/>'s payload, the design's grooming made
    /// plain for the worker: the groomers in number order, the crew pins (−1 Autopilot), and each run's resolved setting
    /// (never <see cref="GroomSetting.Default"/>) and priority, by trail id ascending. Immutable once made.
    /// </summary>
    public sealed class SimGrooming
    {
        public readonly int[] Numbers;
        public readonly GroomerModel[] Models;
        public readonly GroomerDrive[] Drives;
        public readonly bool[] Tillers;
        public readonly int SwingPin, NightPin, DayPin;
        public readonly ulong[] TrailIds;
        public readonly GroomSetting[] Settings;
        public readonly GroomPriority[] Priorities;

        public SimGrooming(int[] numbers, GroomerModel[] models, GroomerDrive[] drives, bool[] tillers, int swingPin, int nightPin, int dayPin,
                           ulong[] trailIds, GroomSetting[] settings, GroomPriority[] priorities)
        {
            Numbers = numbers ?? Array.Empty<int>(); Models = models ?? Array.Empty<GroomerModel>();
            Drives = drives ?? Array.Empty<GroomerDrive>(); Tillers = tillers ?? Array.Empty<bool>();
            SwingPin = swingPin; NightPin = nightPin; DayPin = dayPin;
            TrailIds = trailIds ?? Array.Empty<ulong>(); Settings = settings ?? Array.Empty<GroomSetting>();
            Priorities = priorities ?? Array.Empty<GroomPriority>();
        }

        /// <summary>The design's fleet and the given runs' grooming (each run's id with the setting the player sees on it,
        /// <see cref="TrailGrooming.Resolve"/>d).</summary>
        public static SimGrooming From(GroomFleet fleet, IReadOnlyList<KeyValuePair<ulong, TrailGrooming>> runs, Func<ulong, TrailKind> kindOf)
        {
            fleet ??= GroomFleet.Empty;
            int n = fleet.Groomers.Count;
            var num = new int[n]; var mod = new GroomerModel[n]; var drv = new GroomerDrive[n]; var til = new bool[n];
            for (int i = 0; i < n; i++) { var g = fleet.Groomers[i]; num[i] = g.Number; mod[i] = g.Model; drv[i] = g.Drive; til[i] = g.WideTiller; }
            var list = new List<KeyValuePair<ulong, TrailGrooming>>(runs ?? Array.Empty<KeyValuePair<ulong, TrailGrooming>>());
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            var ids = new List<ulong>(list.Count); var set = new List<GroomSetting>(list.Count); var pri = new List<GroomPriority>(list.Count);
            foreach (var kv in list)
            {
                if (ids.Count > 0 && ids[ids.Count - 1] == kv.Key) continue;
                ids.Add(kv.Key); set.Add(kv.Value.Resolve(kindOf(kv.Key))); pri.Add(kv.Value.Priority);
            }
            return new SimGrooming(num, mod, drv, til, fleet.Crews.Swing, fleet.Crews.Night, fleet.Crews.Day, ids.ToArray(), set.ToArray(), pri.ToArray());
        }

        /// <summary>A message when the payload isn't one the worker takes (lengths, order, values), else null.</summary>
        public string Problem()
        {
            int n = Numbers.Length;
            if (n > GroomFleet.MaxGroomers || Models.Length != n || Drives.Length != n || Tillers.Length != n) return "the groomers don't line up";
            for (int i = 0; i < n; i++)
            {
                if (!Groomer.Valid(Models[i]) || !Groomer.Valid(Drives[i])) return "a groomer isn't one this build knows";
                if (i > 0 && Numbers[i] <= Numbers[i - 1]) return "the groomers aren't in number order";
            }
            if (SwingPin < -1 || SwingPin > n || NightPin < -1 || NightPin > n || DayPin < -1 || DayPin > n) return "a crew pin is out of range";
            if (Settings.Length != TrailIds.Length || Priorities.Length != TrailIds.Length) return "the trails don't line up";
            for (int i = 0; i < TrailIds.Length; i++)
            {
                if (i > 0 && TrailIds[i] <= TrailIds[i - 1]) return "the trails aren't in id order";
                if (Settings[i] == GroomSetting.Default || !TrailGrooming.Valid(Settings[i]) || !TrailGrooming.Valid(Priorities[i])) return "a trail's grooming isn't resolved";
            }
            return null;
        }

        public bool SameAs(SimGrooming o)
        {
            if (o == null || o.Numbers.Length != Numbers.Length || o.TrailIds.Length != TrailIds.Length) return false;
            if (o.SwingPin != SwingPin || o.NightPin != NightPin || o.DayPin != DayPin) return false;
            for (int i = 0; i < Numbers.Length; i++)
                if (o.Numbers[i] != Numbers[i] || o.Models[i] != Models[i] || o.Drives[i] != Drives[i] || o.Tillers[i] != Tillers[i]) return false;
            for (int i = 0; i < TrailIds.Length; i++)
                if (o.TrailIds[i] != TrailIds[i] || o.Settings[i] != Settings[i] || o.Priorities[i] != Priorities[i]) return false;
            return true;
        }

        public static readonly SimGrooming None = new SimGrooming(null, null, null, null, -1, -1, -1, null, null, null);
    }

    /// <summary>Task 4.25 (GO-07): <see cref="SimCommandKind.SetGrooming"/> with A = 1: one trail's order for tonight.</summary>
    public sealed class SimGroomOrder
    {
        public readonly ulong TrailId;
        public readonly GroomOrder Order;
        public SimGroomOrder(ulong trailId, GroomOrder order) { TrailId = trailId; Order = order; }
    }

    /// <summary>
    /// Task 4.25 (GO-08): the Grooming tab's view of the worker's night, in each snapshot (triple-buffered with it, sized
    /// once: <see cref="SizeBytes"/>, about 11 KB at 1,024 trails, beside SI-16's 64 KB as 0.0 cleared). Per network trail
    /// (the snapshot's trail order): tonight's job, state, the groomer that starts it (number, 0 none), the planned start and
    /// end (minutes after the night's swing start, −1 none), the minutes it needs, and last night's state. Per groomer lane
    /// (fleet order): its number, model, tiller, crewed shifts (bit 0 swing, 1 night, 2 day) and minutes worked this season.
    /// </summary>
    public sealed class GroomView
    {
        public const int MaxWarnings = 16;
        public bool Live;
        public int NightDay = -1;
        public int SwingStartAbsMin, SwingEndAbsMin, NightEndAbsMin;
        public int CrewsSwing, CrewsNight, CrewsDay;
        public bool SnowingTonight;
        public int WantMinutes, CrewMinutes, ShortMinutes, CostUsd;
        public int LastNotReached, LastShortMinutes, LastWorkMinutes, LastCostUsd, LastNightDay = -1;
        public readonly GroomJob[] Job;
        public readonly GroomRowState[] State, LastState;
        public readonly byte[] Groomer;
        public readonly short[] StartMin, EndMin, NeedMin;
        public int GroomerCount;
        public readonly int[] Number, SeasonMinutes;
        public readonly GroomerModel[] Model;
        public readonly bool[] Tiller;
        public readonly byte[] Shifts;
        /// <summary>
        /// GO-13, per groomer lane: where it is now, for the dots on the map. It goes from A to B: a trail (the snapshot's
        /// trail order, −1 the depot at the lowest lift's foot) at a share of its work (ppm along its passes), at a face
        /// minute (absolute). Working a piece: A and B are the same trail, the piece's first and last share. Between pieces:
        /// the last piece's end and the next one's start (the depot before the first and after the last). Both −1: parked.
        /// </summary>
        public readonly short[] AtTrailA, AtTrailB;
        public readonly int[] AtPpmA, AtPpmB, AtMinA, AtMinB;
        public readonly GroomJob[] AtJob;
        public int WarningCount;
        public readonly GroomWarningKind[] WarningKind;
        public readonly short[] WarningTrail;

        public GroomView(int maxTrails)
        {
            Job = new GroomJob[maxTrails]; State = new GroomRowState[maxTrails]; LastState = new GroomRowState[maxTrails];
            Groomer = new byte[maxTrails]; StartMin = new short[maxTrails]; EndMin = new short[maxTrails]; NeedMin = new short[maxTrails];
            int g = GroomFleet.MaxGroomers;
            Number = new int[g]; SeasonMinutes = new int[g]; Model = new GroomerModel[g]; Tiller = new bool[g]; Shifts = new byte[g];
            AtTrailA = new short[g]; AtTrailB = new short[g]; AtPpmA = new int[g]; AtPpmB = new int[g]; AtMinA = new int[g]; AtMinB = new int[g];
            AtJob = new GroomJob[g];
            WarningKind = new GroomWarningKind[MaxWarnings]; WarningTrail = new short[MaxWarnings];
        }

        public int SizeBytes => 96 + Job.Length * (1 + 1 + 1 + 1 + 2 + 2 + 2) + Number.Length * (4 + 4 + 1 + 1 + 1 + 2 + 2 + 4 * 4 + 1) + MaxWarnings * 3;
    }
}
