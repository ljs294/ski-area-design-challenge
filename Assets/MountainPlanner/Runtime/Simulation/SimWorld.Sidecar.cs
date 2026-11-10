using System;
using System.IO;

namespace MountainPlanner.Simulation
{
    public sealed partial class SimWorld
    {
        /// <summary>
        /// SI-12 v2: the whole state as a sidecar, at a tick boundary. Commands staged for the next tick apply first: they
        /// would apply at this same tick before anything else ran, so applying them now changes nothing but where they wait.
        /// The event ring and the command log aren't state (nothing reads them back) and aren't saved.
        /// </summary>
        public byte[] SaveState()
        {
            RunCommands();
            return SimSidecar.Write(this);
        }

        /// <summary>
        /// SI-17: copies due snow tiles into <paramref name="ring"/> (the host calls it after its steps, and while paused so an
        /// edit's snow still shows). Not the simulation's state: the ring is the screen's.
        /// </summary>
        public int StageSnow(TileStagingRing ring, bool paused) =>
            Snow.Stage(ring, Tick, Pacing.StageTilesPerTick, Pacing.TileFlushTicks, paused);

        // The field order below is the sidecar's (SimSidecar, v2: v1's fields, then the lift-operations block, task 4.11):
        // append only through a version bump.

        internal void WriteState(SidecarWriter o)
        {
            o.I64(FaceMs);
            o.U8((byte)Phase); o.Bool(Open); o.I32(SeasonNumber); o.I32(SeasonDay); o.U64(SeasonSeed); o.Bool(SeedPinned);
            o.I32(_todayOpen); o.I32(_todayLastChair); o.I32(_todayStop); o.Bool(_todayTrimmed); o.I64(_boundaryDoneAt);
            o.I64(_weatherHour); o.I32(_weatherTenthsF); o.I32(_weatherWindMph); o.I32(_hourSliceCursor);
            o.I32(_trailsReady); o.I32(_trailsReadyNext);
            o.I32(_liftCount);
            for (int i = 0; i < _liftCount; i++)
            {
                o.U64(_liftIds[i]); o.Bool(_liftBuilt[i]); o.I64(_runMs[i]); o.Bool(_running[i]);
                o.U8(_liftOpen[i]); o.U8(_liftOpenNext[i]); o.U8(_liftHold[i]);
                o.I64(_runFaceMs[i]); o.I64(_closedFaceMs[i]);
            }
            o.I32(_ledgerDay); o.I32(_ledgerSeasonDay); o.U8((byte)_ledgerType); o.U8((byte)_ledgerPhase);
            var r = _lastReport;
            o.I32(r.FaceDay); o.I32(r.SeasonDay); o.U8((byte)r.Phase); o.U8((byte)r.DayType);
            o.I32(r.OpenSecond); o.I32(r.StopSecond); o.Bool(r.Trimmed);
            o.I32(r.Target); o.I32(r.Arrived); o.I32(r.TurnedAway); o.I32(r.WentHome); o.I32(r.Stranded);
            o.I32(r.LiftCount);
            for (int i = 0; i < r.LiftCount; i++) { o.I32(r.RunMinutes[i]); o.I32(r.ClosedMinutes[i]); o.I32(r.Rides[i]); }
            o.I32(_historyCount);
            for (int k = 0; k < _historyCount; k++)
            {
                o.I32(_historyFaceDay[k]); o.I32(_historySeasonDay[k]); o.I32(_historyRunMinutes[k]); o.I32(_historyLiftsRun[k]);
                o.U8(_historyType[k]); o.U8(_historyPhase[k]);
            }
            o.Bool(_newSeasonHistory);
            o.I64(_commandSeq); o.I64(HourIndex); o.U64(HourHash);
            o.Bool(Snow.Set); o.I32(Snow.Width); o.I32(Snow.Height);
            o.U16s(Snow.Cells);
            WriteLiftState(o);
            WriteGuestState(o);
        }

        internal void ReadState(SidecarReader r, SimSidecar.Header header)
        {
            Tick = header.Tick;
            if (Tick < 0) throw new InvalidDataException("The simulation's tick is negative.");
            FaceMs = r.I64();
            if (FaceMs < 0) throw new InvalidDataException("The simulation's face clock is negative.");
            Phase = (SeasonPhase)r.Enum(1, 3, "The season's phase");
            Open = r.Bool(); SeasonNumber = r.I32(); SeasonDay = r.I32(); SeasonSeed = r.U64(); SeedPinned = r.Bool();
            _todayOpen = r.I32(); _todayLastChair = r.I32(); _todayStop = r.I32(); _todayTrimmed = r.Bool(); _boundaryDoneAt = r.I64();
            _weatherHour = r.I64(); _weatherTenthsF = r.I32(); _weatherWindMph = r.I32(); _hourSliceCursor = r.I32();
            _trailsReady = r.I32(); _trailsReadyNext = r.I32();
            int n = r.Count(Pacing.MaxLifts);
            _liftCount = n;
            _liftIds = new ulong[n]; _liftBuilt = new bool[n]; _runMs = new long[n]; _running = new bool[n];
            _liftOpen = new byte[n]; _liftOpenNext = new byte[n]; _liftHold = new byte[n];
            _runFaceMs = new long[n]; _closedFaceMs = new long[n];
            for (int i = 0; i < n; i++)
            {
                _liftIds[i] = r.U64();
                if (i > 0 && _liftIds[i] <= _liftIds[i - 1]) throw new InvalidDataException("The simulation's lifts are out of order.");
                _liftBuilt[i] = r.Bool(); _runMs[i] = r.I64(); _running[i] = r.Bool();
                _liftOpen[i] = r.U8(); _liftOpenNext[i] = r.U8(); _liftHold[i] = r.U8();
                _runFaceMs[i] = r.I64(); _closedFaceMs[i] = r.I64();
            }
            // The lifts' operations are sized over the design as the state knew its lifts (by ID); a lift the design no longer
            // has runs on the catalog's defaults until the refold drops it.
            var real = Design;
            Design = real.AlignedTo(_liftIds, n);
            FoldLiftOps(Array.Empty<ulong>(), 0, _liftIds, n);
            _ledgerDay = r.I32(); _ledgerSeasonDay = r.I32();
            _ledgerType = (DayType)r.Enum(0, 3, "The ledger's day type");
            _ledgerPhase = (SeasonPhase)r.Enum(0, 3, "The ledger's phase");
            var rep = _lastReport;
            rep.FaceDay = r.I32(); rep.SeasonDay = r.I32();
            rep.Phase = (SeasonPhase)r.Enum(0, 3, "The day report's phase");
            rep.DayType = (DayType)r.Enum(0, 3, "The day report's day type");
            rep.OpenSecond = r.I32(); rep.StopSecond = r.I32(); rep.Trimmed = r.Bool();
            rep.Target = r.I32(); rep.Arrived = r.I32(); rep.TurnedAway = r.I32(); rep.WentHome = r.I32(); rep.Stranded = r.I32();
            rep.LiftCount = r.Count(Pacing.MaxLifts);
            for (int i = 0; i < rep.LiftCount; i++) { rep.RunMinutes[i] = r.I32(); rep.ClosedMinutes[i] = r.I32(); rep.Rides[i] = r.I32(); }
            _historyCount = r.Count(Pacing.HistoryDays);
            for (int k = 0; k < _historyCount; k++)
            {
                _historyFaceDay[k] = r.I32(); _historySeasonDay[k] = r.I32(); _historyRunMinutes[k] = r.I32(); _historyLiftsRun[k] = r.I32();
                _historyType[k] = r.Enum(0, 3, "A history line's day type"); _historyPhase[k] = r.Enum(0, 3, "A history line's phase");
            }
            _newSeasonHistory = r.Bool();
            _commandSeq = r.I64(); HourIndex = r.I64(); HourHash = r.U64();
            bool set = r.Bool();
            int width = r.Count(1 << 16), height = r.Count(1 << 16);
            if ((long)width * height > 64_000_000) throw new InvalidDataException("The snow field is too big.");
            var mm = new ushort[width * height];
            r.U16s(mm);
            Snow.Load(width, height, set, mm);
            // v1 → v2 (SimSidecar.Upgrade): no lift-operations block; lines and chairs empty, ledger zero, holds as saved.
            if (header.SidecarVersion >= 2) ReadLiftState(r, header.SidecarVersion);
            Design = real;
            // v2, v3 → v4: no guest block. The hill opens empty, every reputation at its start, as a new resort does.
            if (header.SidecarVersion >= 4)
            {
                PrepareGuests(Design);
                ReadGuestState(r);
            }
            else ResetGuestsForOlderSave();
        }

        /// <summary>
        /// SI-20: when the save's design isn't the one the state ran with (an edit the sidecar missed), each lift keeps its
        /// rope by ID, as any new revision does.
        /// </summary>
        internal void RefoldDesign() => FoldDesign(Design);
    }
}
