using System;
using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>Where the season is (TM-11 v2, TM-14). Values are saved: never renumber.</summary>
    public enum SeasonPhase : byte
    {
        /// <summary>October 1 until Day 1: build time, the clock at the night rate.</summary>
        PreSeason = 1,
        /// <summary>From Day 1 until the mountain closes.</summary>
        Season = 2,
        /// <summary>After the season, until the next October 1.</summary>
        Summer = 3,
    }

    /// <summary>A skip the host is running (TM-06 v2, TM-14). Values are logged: never renumber.</summary>
    public enum SkipKind : byte { None = 0, Morning = 1, NextThird = 2, NextMonth = 3 }

    /// <summary>A lift as a snapshot shows it.</summary>
    public enum LiftRunState : byte
    {
        /// <summary>Not built yet: a plan.</summary>
        Planned = 0,
        /// <summary>Built, rope stopped: outside the hours, the mountain closed, a hold.</summary>
        Parked = 1,
        /// <summary>The rope runs.</summary>
        Running = 2,
    }

    /// <summary>
    /// TM-15: the day report at closing. Guests, snow and guns don't exist yet, so most numbers stay zero until their tasks
    /// fill them; the lifts' run and closed minutes are real. Allocated once per snapshot and overwritten in place.
    /// </summary>
    public sealed class DayReport
    {
        public int FaceDay = -1;
        public int SeasonDay;
        public SeasonPhase Phase;
        public DayType DayType;
        public int OpenSecond, StopSecond;
        /// <summary>Q10: the resort's hours were cut to the day's daylight.</summary>
        public bool Trimmed;
        public int Target, Arrived, TurnedAway, WentHome, Stranded;
        public int LiftCount;
        public readonly int[] RunMinutes;
        public readonly int[] ClosedMinutes;
        public readonly int[] Rides;
        /// <summary>LO-12: the day's peak line, mean wait (motion ms), minutes on hold, skiers left in line at the stop, and
        /// utilisation (rides ÷ what the lift could carry in its day, thousandths).</summary>
        public readonly int[] PeakLine, MeanWaitMs, HoldMinutes, LeftInLine, UtilisationPermille;

        public DayReport(int maxLifts)
        {
            RunMinutes = new int[maxLifts];
            ClosedMinutes = new int[maxLifts];
            Rides = new int[maxLifts];
            PeakLine = new int[maxLifts];
            MeanWaitMs = new int[maxLifts];
            HoldMinutes = new int[maxLifts];
            LeftInLine = new int[maxLifts];
            UtilisationPermille = new int[maxLifts];
        }

        public bool IsValid => FaceDay >= 0;

        public void CopyFrom(DayReport o)
        {
            FaceDay = o.FaceDay; SeasonDay = o.SeasonDay; Phase = o.Phase; DayType = o.DayType;
            OpenSecond = o.OpenSecond; StopSecond = o.StopSecond; Trimmed = o.Trimmed;
            Target = o.Target; Arrived = o.Arrived; TurnedAway = o.TurnedAway; WentHome = o.WentHome; Stranded = o.Stranded;
            LiftCount = o.LiftCount;
            Array.Copy(o.RunMinutes, RunMinutes, LiftCount);
            Array.Copy(o.ClosedMinutes, ClosedMinutes, LiftCount);
            Array.Copy(o.Rides, Rides, LiftCount);
            Array.Copy(o.PeakLine, PeakLine, LiftCount);
            Array.Copy(o.MeanWaitMs, MeanWaitMs, LiftCount);
            Array.Copy(o.HoldMinutes, HoldMinutes, LiftCount);
            Array.Copy(o.LeftInLine, LeftInLine, LiftCount);
            Array.Copy(o.UtilisationPermille, UtilisationPermille, LiftCount);
        }

        internal const int SizeBytesPerLift = 32;
    }

    /// <summary>
    /// SI-16: one immutable record of the simulation, stamped with the tick it describes and a sequence number. Three are
    /// allocated up front and reused in turn (<see cref="SnapshotBuffers"/>); nothing changes one after it's published, and
    /// readers hold the newest for a frame. Within 64 KB (<see cref="SizeBytes"/>).
    /// </summary>
    public sealed class SimSnapshot
    {
        public long Seq;
        public long Tick;
        /// <summary>Face milliseconds since 00:00 on October 1 of the start's label year.</summary>
        public long FaceMs;
        public int FaceDay;
        public int LabelYear;
        public int FaceDayOfYear;
        public int SecondOfDay;
        /// <summary>The sun's date (WX-04): the label date's real calendar year and day of year.</summary>
        public int ViewYear, ViewDayOfYear;
        public int Month, DayOfMonth;
        public MonthThird Third;
        public DayType DayType;

        public SeasonPhase Phase;
        public bool Open;
        public int SeasonNumber;
        /// <summary>Day N of the season (TM-08), 0 before Day 1.</summary>
        public int SeasonDay;
        public ulong SeasonSeed;
        public bool SeedPinned;
        /// <summary>Today's hours, trimmed to daylight.</summary>
        public int OpenSecond, LastChairSecond, StopSecond;
        public bool HoursTrimmed;
        /// <summary>True while the clock runs at the day rate (TM-02 v2).</summary>
        public bool DayRate;

        // The host's
        /// <summary>The speed, 1–4 (1×, 2×, 4×, 8×), and the ticks per real tick it asks for.</summary>
        public int Speed, TicksPerRealTick;
        public bool Paused;
        /// <summary>SI-11: the rate the machine sustains, in thousandths of the one asked for.</summary>
        public int SustainedPermille;
        public SkipKind Skip;
        /// <summary>The skip's progress in thousandths.</summary>
        public int SkipPermille;
        /// <summary>BG-01, measured by the host on the clock it's given (App's), never inside the step: µs per tick.</summary>
        public int TickMeanMicros, TickP99Micros;
        public long TicksRun;

        public long DesignRevision;

        // Per lift, in ID order
        public int LiftCount;
        public readonly ulong[] LiftIds;
        /// <summary>RP-07 v2: how long each lift's rope has run, motion milliseconds. A chair's place follows it.</summary>
        public readonly long[] RunMs;
        public readonly LiftRunState[] LiftStates;
        /// <summary>The board's public lift column (SI-19): open today.</summary>
        public readonly byte[] LiftOpen;
        /// <summary>LO-02: skiers in each lift's line.</summary>
        public readonly int[] LineCount;
        /// <summary>LO-04 v1: the wait for a skier joining now, motion ms at 1× (divide by the speed for real time).</summary>
        public readonly int[] WaitMs;
        /// <summary>The Status word's source (Planned, Closed, Scheduled, Open, On hold, Downloading).</summary>
        public readonly LiftOpStatus[] LiftStatus;
        /// <summary>LO-05: riders on the carriers, and today's rides so far (LO-12).</summary>
        public readonly int[] Aboard, RidesToday;
        /// <summary>LO-01: the motion ms between carriers, and the seats a carrier takes (LO-03: rated = seats × 3,600,000 ÷ interval).</summary>
        public readonly int[] IntervalMs;
        public readonly byte[] Seats;
        /// <summary>LO-07 v2: each lift's opening today, seconds of the day (a Scheduled lift says when).</summary>
        public readonly int[] LiftOpenSecond;
        public int TrailsReady;

        // Per network trail, in ID order (LO-10's stand-in for SN-20 until 4.07: Open when an open lift's descent runs along it)
        public int TrailCount;
        /// <summary>False until the app's network has reached the simulation: the trails' words stay placeholders.</summary>
        public bool TrailsKnown;
        public readonly ulong[] TrailIds;
        public readonly LiftOpStatus[] TrailStatus;

        public readonly DayReport LastDayReport;
        public int HistoryCount;

        // SI-18
        public long FirstEventSeq;
        public int EventCount;
        public readonly SimEvent[] Events;

        // SI-17: the staging ring's slots written so far (read them up to here), and whether the field has snow yet.
        public long StagedTiles;
        public bool SnowSet;

        /// <summary>GU-03, GU-13, GU-18: the crowd as the Guests tab shows it, and the selected skier's day (GU-02).</summary>
        public readonly GuestView Guests;

        // Task 4.05: the weather. False until the first face hour with weather.
        public bool WeatherKnown;
        /// <summary>The typical mountain's rows (the area has no weather history yet), and new rows waiting for October 1.</summary>
        public bool WeatherGeneric, WeatherWaiting;
        /// <summary>WX-30: the last face hour at zref.</summary>
        public Domain.Weather.WeatherHour WeatherHour;
        /// <summary>WX-35: the reference points' samples.</summary>
        public WeatherSample WeatherBase, WeatherMid, WeatherSummit;
        /// <summary>LO-11, WX-39: each lift's gust at its top station this hour, 0.1 mph (the weather's, not SetGust's override).</summary>
        public readonly int[] LiftGustTenths;

        public SimSnapshot(Pacing p)
        {
            Guests = new GuestView(p);
            LiftIds = new ulong[p.MaxLifts];
            RunMs = new long[p.MaxLifts];
            LiftStates = new LiftRunState[p.MaxLifts];
            LiftOpen = new byte[p.MaxLifts];
            LineCount = new int[p.MaxLifts];
            WaitMs = new int[p.MaxLifts];
            LiftStatus = new LiftOpStatus[p.MaxLifts];
            Aboard = new int[p.MaxLifts];
            RidesToday = new int[p.MaxLifts];
            IntervalMs = new int[p.MaxLifts];
            Seats = new byte[p.MaxLifts];
            LiftOpenSecond = new int[p.MaxLifts];
            LiftGustTenths = new int[p.MaxLifts];
            TrailIds = new ulong[p.MaxTrails];
            TrailStatus = new LiftOpStatus[p.MaxTrails];
            LastDayReport = new DayReport(p.MaxLifts);
            Events = new SimEvent[p.SnapshotEvents];
        }

        /// <summary>The lift's index here, or −1.</summary>
        public int IndexOfLift(ulong id)
        {
            int lo = 0, hi = LiftCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (LiftIds[mid] == id) return mid;
                if (LiftIds[mid] < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>SI-16's budget: the bytes this snapshot's arrays and fields take at their full size.</summary>
        public int SizeBytes => 256 + 256 + LiftIds.Length * (8 + 8 + 1 + 1 + 4 + 4 + 1 + 4 + 4 + 4 + 1 + 4 + 1 + 4 + DayReport.SizeBytesPerLift)
                                + TrailIds.Length * (8 + 1 + 1) + Events.Length * 24
                                + Guests.SizeBytes;

        /// <summary>The trail's index here, or −1.</summary>
        public int IndexOfTrail(ulong id)
        {
            int lo = 0, hi = TrailCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (TrailIds[mid] == id) return mid;
                if (TrailIds[mid] < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }
    }

    /// <summary>
    /// SI-16's triple buffer: the worker writes one snapshot while the newest waits and the reader holds a third, so neither
    /// side ever waits or sees a snapshot change. Latest wins: a reader that's slow skips snapshots, never events
    /// (<see cref="EventReader"/>).
    /// </summary>
    public sealed class SnapshotBuffers
    {
        readonly SimSnapshot[] _buffers;
        int _back = 0, _front = 2;
        int _middle = 1;           // the newest published; the top bit says it's new to the reader
        const int Fresh = 1 << 30;

        public SnapshotBuffers(Pacing p)
        {
            _buffers = new[] { new SimSnapshot(p), new SimSnapshot(p), new SimSnapshot(p) };
        }

        /// <summary>The worker's buffer to fill.</summary>
        public SimSnapshot Back => _buffers[_back];

        /// <summary>The worker publishes what it filled; its next buffer is the one the reader isn't holding.</summary>
        public void Publish()
        {
            int previous = Interlocked.Exchange(ref _middle, _back | Fresh);
            _back = previous & ~Fresh;
        }

        /// <summary>The reader's newest snapshot: a fresh one if the worker published since, else the one it holds (null before the first).</summary>
        public SimSnapshot Latest
        {
            get
            {
                if ((Volatile.Read(ref _middle) & Fresh) != 0)
                {
                    int previous = Interlocked.Exchange(ref _middle, _front);
                    _front = previous & ~Fresh;
                    _any = true;
                }
                return _any ? _buffers[_front] : null;
            }
        }

        bool _any;
    }
}
