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
        /// <summary>GO-15 (sidecar v14): no trail leads there from the depot (groomers drive on trails only).</summary>
        NoRoute = 11,
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
        /// <summary>GO-14 v3: the night below is tonight's preview (no night is planned yet: the day before the swing).</summary>
        public bool Preview;
        public int NightDay = -1;
        public int SwingStartAbsMin, SwingEndAbsMin, NightEndAbsMin;
        public int CrewsSwing, CrewsNight, CrewsDay;
        public bool SnowingTonight;
        public int WantMinutes, CrewMinutes, ShortMinutes, CostUsd;
        public int LastNotReached, LastShortMinutes, LastWorkMinutes, LastCostUsd, LastNightDay = -1;
        /// <summary>GO-14: last night's first shift start and night shift end (absolute face minutes), the Last night strip's axis.</summary>
        public int LastSwingStartAbsMin, LastNightEndAbsMin;
        public readonly GroomJob[] Job;
        public readonly GroomRowState[] State, LastState;
        public readonly byte[] Groomer;
        public readonly short[] StartMin, EndMin, NeedMin;
        /// <summary>GO-14, per trail: last night's lane (the groomer's index in the fleet, minutes from that night's first shift; −1 none; not saved, so
        /// empty after a reopen until the next morning) and the hours since its last groom (255: none, or snowed over).</summary>
        public readonly uint[] Lane;
        /// <summary>Packs <see cref="Lane"/>: groomer 5 bits, start and end 10 bits each (end 0: no lane), hours 7 bits (126: that long
        /// or more, 127: none).</summary>
        public static uint PackLane(int groomer, int start, int end, int hours)
        {
            uint h = hours < 0 ? 127u : (uint)Math.Min(126, hours);   // hours < 0: never groomed
            if (end <= 0 || groomer < 0) return h;
            return ((uint)(groomer & 31) << 27) | ((uint)Math.Min(1023, Math.Max(0, start)) << 17) | ((uint)Math.Min(1023, end) << 7) | h;
        }
        public int LastGroomerAt(int k) => LastEndAt(k) > 0 ? (int)(Lane[k] >> 27) : -1;
        public int LastStartAt(int k) => LastEndAt(k) > 0 ? (int)((Lane[k] >> 17) & 1023) : -1;
        public int LastEndAt(int k) => (int)((Lane[k] >> 7) & 1023);
        /// <summary>Hours since trail <paramref name="k"/>'s last groom (126: that long or more), or −1 (none, or snowed over).</summary>
        public int SinceHoursAt(int k) { int h = (int)(Lane[k] & 127); return h == 127 ? -1 : h; }
        public int GroomerCount;
        public readonly int[] Number, SeasonMinutes;
        public readonly GroomerModel[] Model;
        public readonly GroomerDrive[] Drive;
        public readonly bool[] Tiller;
        public readonly byte[] Shifts;
        /// <summary>GO-14, per lane (groomer × 3 + shift: swing, night, day): minutes worked plus still planned. Screen only.</summary>
        public readonly short[] PlanMin;
        /// <summary>GO-15, per lane: minutes driven plus still to drive (to, between and from the trails). Screen only.</summary>
        public readonly short[] TransitMin;
        /// <summary>GO-15: the night strip's transit blocks: the groomer's index in the fleet and the minutes from the strip's
        /// zero (as <see cref="StartMin"/>); the first may start before it (the drive out leaves during the open hours).</summary>
        public const int MaxTransit = 48;   // within 0.0's ~16 KB side ring
        public int TransitCount;
        /// <summary>GO-15: the lift whose foot is the depot (its id; 0 when none), so the dots park where the sim's drives start.</summary>
        public ulong DepotLift;
        public readonly byte[] TransitLane;
        public readonly short[] TransitFrom, TransitTo;
        /// <summary>GO-14: per piece above, -1 for a drive, else the network trail it works (a work piece: a run worked in two
        /// pieces is two blocks on the night strip).</summary>
        public readonly short[] TransitTrail;
        /// <summary>
        /// GO-13, per groomer lane: where it is now, for the dots on the map. It goes from A to B: a trail (the snapshot's
        /// trail order, −1 the depot at the lowest lift's foot) at a share of its work (ppm along its passes), at a face
        /// minute (absolute). Working a piece: A and B are the same trail, the piece's first and last share. Between pieces:
        /// the last piece's end and the next one's start (the depot before the first and after the last). Both −1: parked.
        /// </summary>
        public readonly short[] AtTrailA, AtTrailB;
        public readonly int[] AtPpmA, AtPpmB, AtMinA, AtMinB;
        public readonly GroomJob[] AtJob;
        /// <summary>GO-16 v2: bit 0, A's task started at its trail's tail; bit 1, B's did (the passes run from that end).</summary>
        public readonly byte[] AtTail;
        /// <summary>GO-15: between pieces, the drive's minutes (it leaves at <see cref="AtMinA"/>); 0 when none is laid.</summary>
        public readonly short[] AtDrive;
        /// <summary>GO-16 v2 (screen only): stretches of runs groomed on the way this night or day.</summary>
        public int WayPasses;
        public int WarningCount;
        public readonly GroomWarningKind[] WarningKind;
        public readonly short[] WarningTrail;

        public GroomView(int maxTrails)
        {
            Job = new GroomJob[maxTrails]; State = new GroomRowState[maxTrails]; LastState = new GroomRowState[maxTrails];
            Groomer = new byte[maxTrails]; StartMin = new short[maxTrails]; EndMin = new short[maxTrails]; NeedMin = new short[maxTrails];
            Lane = new uint[maxTrails];
            int g = GroomFleet.MaxGroomers;
            Number = new int[g]; SeasonMinutes = new int[g]; Model = new GroomerModel[g]; Drive = new GroomerDrive[g]; Tiller = new bool[g]; Shifts = new byte[g];
            AtTrailA = new short[g]; AtTrailB = new short[g]; AtPpmA = new int[g]; AtPpmB = new int[g]; AtMinA = new int[g]; AtMinB = new int[g];
            AtJob = new GroomJob[g]; AtTail = new byte[g]; AtDrive = new short[g];
            PlanMin = new short[g * 3]; TransitMin = new short[g * 3];
            TransitLane = new byte[MaxTransit]; TransitFrom = new short[MaxTransit]; TransitTo = new short[MaxTransit];
            TransitTrail = new short[MaxTransit];
            WarningKind = new GroomWarningKind[MaxWarnings]; WarningTrail = new short[MaxWarnings];
        }

        public int SizeBytes => 113 + Job.Length * (1 + 1 + 1 + 1 + 2 + 2 + 2 + 4) + Number.Length * (4 + 4 + 1 + 1 + 1 + 1 + 2 + 2 + 4 * 4 + 1 + 1 + 2 + 3 * 2 * 2) + MaxTransit * 7 + MaxWarnings * 3;
    }
}
