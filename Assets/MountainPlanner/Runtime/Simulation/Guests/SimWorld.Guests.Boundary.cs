using System;
using System.IO;

namespace MountainPlanner.Simulation
{
    public sealed partial class SimWorld
    {
        /// <summary>GU-16: the render column 4.13 draws from, handed over beside the snapshot by a pointer swap.</summary>
        public SkierRenderBuffers SkierRender { get; private set; }

        // ================= the snapshot (SI-16) =================

        void WriteGuests(SimSnapshot s)
        {
            var v = s.Guests;
            v.Clear();
            if (_guests == null) return;
            UpdatePopularity();
            v.SkierCount = _guests.Count;
            for (int slot = 0; slot < _guests.Cap; slot++)
            {
                byte st = _guests.State[slot];
                if (st == (byte)GuestState.Free || _guests.Arch[slot] == GuestColumns.FeederArch) continue;
                if (st < v.ByState.Length) v.ByState[st]++;
            }
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                v.Demand[a] = _demandUncapped[a];
                v.Arrived[a] = _arrived[a];
                v.Reputation[a] = _rep[a];
                for (int k = 0; k < DemandLines; k++) v.Stack[a * DemandLines + k] = _stack[a * DemandLines + k];
                for (int k = 0; k < Reasons; k++) v.Reasons[a * Reasons + k] = _reasonSum[a * Reasons + k];
            }
            v.HeadlineReputation = Reputation;
            var g = GuestLedger();
            v.ArrivedClosed = g.arrivedClosed; v.Capped = g.capped; v.WentHome = g.wentHome; v.Stranded = g.stranded; v.Left = g.left;
            v.LiftCapacity = LiftCapacityToday();
            v.TerrainCapacity = TerrainCapacityToday();
            for (int b = 0; b < Pacing.Bands; b++)
            {
                var net = NetworkFor(b);
                v.Bases = net.bases; v.Nodes = net.nodes;
                v.Reachable[b] = net.reachable; v.UsableBases[b] = net.usableBases;
            }
            for (int i = 0; i < _liftCount && i < v.LiftPopularity.Length; i++) v.LiftPopularity[i] = (sbyte)LiftPopularity(i);
            int trails = Math.Min(_trailPop.Length, v.TrailPopularity.Length);
            for (int k = 0; k < trails; k++) v.TrailPopularity[k] = (sbyte)TrailPopularity(k);
            WriteSelected(v);
        }

        /// <summary>
        /// GU-02: the selected skier's day. The worker publishes it at most every
        /// <see cref="Pacing.InspectTicks"/>, and only this skier's; the other thousands keep their records in the worker,
        /// where the later ratings read them. Selecting a skier changes no result (it is not in the state hash).
        /// </summary>
        void WriteSelected(GuestView v)
        {
            if (_inspectSerial == 0) return;
            _inspected ??= new GuestView(Pacing);
            // The search for the slot is the only O(cap) thing a publish does, so it waits for the interval first: at a
            // big cap, watching a skier must not cost a scan of every slot on every tick. The block is *kept* here and
            // copied into every snapshot, though: a snapshot is cleared before it is filled and the screen reads
            // whichever one is newest, so filling it only on the tick it was re-read left the panel saying "nobody"
            // in nine readings out of ten — which is why a watched skier never appeared at all (QA, 2026-11-09).
            if (_inspectPublishedAt == long.MinValue || Tick - _inspectPublishedAt >= Pacing.InspectTicks)
            {
                int slot = SlotOfSerial(_inspectSerial);
                if (slot < 0) { _inspectSerial = 0; _inspected.SelectedSerial = 0; return; }
                _inspectPublishedAt = Tick;
                FillSelected(_inspected, slot);
            }
            CopySelected(_inspected, v);
        }

        /// <summary>The worker's copy of the selected skier's block, re-read at <see cref="Pacing.InspectTicks"/>.</summary>
        GuestView _inspected;

        void FillSelected(GuestView v, int slot)
        {
            v.SelectedSerial = _guests.Serial[slot];
            v.SelectedBand = _guests.Band[slot];
            v.SelectedArch = _guests.Arch[slot];
            v.SelectedState = _guests.State[slot];
            v.SelectedAbility = _guests.Ability[slot];
            v.SelectedLaps = _guests.Laps[slot];
            v.SelectedStayLaps = _guests.StayLaps[slot];
            v.SelectedSatisfaction = _guests.Sat[slot];
            v.SelectedWaitSeconds = (int)((long)_guests.WaitTicks[slot] * Pacing.TickMs / 1000);
            v.SelectedPartySize = _guests.PartySize[_guests.Lead[slot]];
            v.SelectedHereSinceTick = _guests.ArriveTick[slot];
            for (int k = 0; k < _whyFactors.Length && k < v.SelectedWhy.Length; k++) v.SelectedWhy[k] = _whyFactors[k];
            int count = Math.Min(_guests.RecCount[slot], v.SelectedRecordSegment.Length);
            v.SelectedRecordCount = count;
            for (int k = 0; k < count; k++)
            {
                _guests.ReadRecord(slot, k, out ushort seg, out ushort entry, out ushort exit);
                v.SelectedRecordSegment[k] = seg; v.SelectedRecordEntry[k] = entry; v.SelectedRecordExit[k] = exit;
            }
        }

        /// <summary>The kept block into this snapshot: about 600 B, so every snapshot can carry it (SI-16).</summary>
        static void CopySelected(GuestView from, GuestView to)
        {
            to.SelectedSerial = from.SelectedSerial;
            to.SelectedBand = from.SelectedBand;
            to.SelectedArch = from.SelectedArch;
            to.SelectedState = from.SelectedState;
            to.SelectedAbility = from.SelectedAbility;
            to.SelectedLaps = from.SelectedLaps;
            to.SelectedStayLaps = from.SelectedStayLaps;
            to.SelectedSatisfaction = from.SelectedSatisfaction;
            to.SelectedWaitSeconds = from.SelectedWaitSeconds;
            to.SelectedPartySize = from.SelectedPartySize;
            to.SelectedHereSinceTick = from.SelectedHereSinceTick;
            Array.Copy(from.SelectedWhy, to.SelectedWhy, Math.Min(from.SelectedWhy.Length, to.SelectedWhy.Length));
            int count = Math.Min(from.SelectedRecordCount, to.SelectedRecordSegment.Length);
            to.SelectedRecordCount = count;
            Array.Copy(from.SelectedRecordSegment, to.SelectedRecordSegment, count);
            Array.Copy(from.SelectedRecordEntry, to.SelectedRecordEntry, count);
            Array.Copy(from.SelectedRecordExit, to.SelectedRecordExit, count);
        }

        /// <summary>
        /// GU-02: the next skier to watch after this one — the smallest serial above it, or the smallest there is, so
        /// clicking goes round the hill in arrival order and ends up back at the first. Feeder skiers (4.11's test
        /// crowd) are skipped: they have no day to show.
        /// </summary>
        int NextSerialAfter(int after)
        {
            if (_guests == null) return 0;
            int first = 0, next = 0;
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (!_guests.Alive(s) || _guests.Arch[s] == GuestColumns.FeederArch) continue;
                int serial = _guests.Serial[s];
                if (serial <= 0) continue;
                if (first == 0 || serial < first) first = serial;
                if (serial > after && (next == 0 || serial < next)) next = serial;
            }
            return next != 0 ? next : first;
        }

        int SlotOfSerial(int serial)
        {
            if (_guests == null || serial == 0) return -1;
            for (int s = 0; s < _guests.Cap; s++) if (_guests.Alive(s) && _guests.Serial[s] == serial) return s;
            return -1;
        }

        /// <summary>
        /// GU-16: fills the render column and publishes it. Queued and riding skiers are in it too, because 4.13 draws a
        /// line as a serpentine inside the station's hull (LO-02) and a rider has to go somewhere.
        /// </summary>
        void PublishSkierRender()
        {
            if (_guests == null || SkierRender == null) return;
            var frame = SkierRender.Back;
            int n = 0;
            var cap = frame.Skiers.Length;
            for (int s = 0; s < _guests.Cap && n < cap; s++)
            {
                byte st = _guests.State[s];
                if (st == (byte)GuestState.Free) continue;
                int edge = _guests.Edge[s], lift = _guests.Lift[s];
                ushort segment = edge >= 0 ? SegmentCode(edge) : lift >= 0 ? LiftCode(lift) : (ushort)0;
                frame.Skiers[n++] = new SkierRender(
                    segment,
                    (ushort)(_guests.Serial[s] & 0xFFFF),
                    (uint)Math.Max(0, _guests.EntryTick[s] - _dayStartTick),
                    (uint)Math.Max(0, _guests.ExitTick[s] - _dayStartTick),
                    _guests.Lane[s], st, _guests.Band[s], 0);
            }
            frame.Count = n;
            frame.Tick = Tick;
            frame.Revision = Design.Revision;
            SkierRender.Publish();
        }

        // ================= commands (SI-03, GU-02, GU-15) =================

        bool ApplyGuestCommand(in SimCommand c)
        {
            switch (c.Kind)
            {
                case SimCommandKind.InspectSkier:
                    // A view command: it changes nothing the state hash sees, which is what makes GU-02's promise true.
                    // −1 is "whoever is next": the worker picks, because only it knows which serials are on the hill.
                    // The screen must not have to guess one — the render column carries serials cut to 16 bits for the
                    // dots (GU-16), and after about 65,000 visits a guess from there names the wrong skier or nobody.
                    _inspectSerial = c.A < 0 ? NextSerialAfter(_inspectSerial) : (int)Math.Min(int.MaxValue, c.A);
                    _inspectPublishedAt = long.MinValue;
                    return true;
                case SimCommandKind.SpawnSkiers:
                {
                    if (!(c.Payload is SkierSpawn spawn)) { Reject(c, SimRefusal.Unknown); return false; }
                    if (_guests == null) { Reject(c, SimRefusal.Unknown); return false; }
                    int arch = Math.Max(0, Math.Min(Pacing.Archetypes - 1, spawn.Archetype));
                    for (int k = 0; k < spawn.Count; k++)
                    {
                        if (_guests.FreeCount < 1) break;
                        _partyOrdinal[arch]++;
                        Admit(arch, 1, Tick, true);
                    }
                    return true;
                }
                default:
                    Reject(c, SimRefusal.Unknown);
                    return false;
            }
        }

        // ================= the hash (SI-08) =================

        void HashGuests(ref StateHasher h)
        {
            if (_guests == null) { h.Add(0); return; }
            h.Add(_guests.Count); h.Add(_serialCounter); h.Add(_partyCounter); h.Add(_demandDay);
            h.Add(_arrivalFromTick); h.Add(_arrivalWindowTicks); h.Add(_arrivalWeightTotal); h.Add(_dayStartTick);
            h.Add(_powderPull); h.Add(_forecastPull);
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                h.Add(_rep[a]); h.Add(_demand[a]); h.Add(_demandUncapped[a]); h.Add(_issued[a]); h.Add(_partyOrdinal[a]);
                h.Add(_arrived[a]); h.Add(_arrivedClosed[a]); h.Add(_capped[a]); h.Add(_wentHome[a]); h.Add(_stranded[a]);
                h.Add(_leftCount[a]); h.Add(_leftSatSum[a]);
                for (int k = 0; k < Reasons; k++) h.Add(_reasonSum[a * Reasons + k]);
                for (int k = 0; k < DemandLines; k++) h.Add(_stack[a * DemandLines + k]);
            }
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (_guests.State[s] == (byte)GuestState.Free) continue;
                // Every number that decides what this skier does next, and the running hash of what it has already done.
                h.Add(s); h.Add(_guests.Serial[s]); h.Add(_guests.Band[s]); h.Add(_guests.Arch[s]); h.Add(_guests.Ability[s]);
                h.Add(_guests.Home[s]); h.Add(_guests.StayLaps[s]); h.Add(_guests.ArriveTick[s]);
                h.Add(_guests.PartyId[s]); h.Add(_guests.Lead[s]); h.Add(_guests.Member[s]);
                h.Add(_guests.PartySize[s]); h.Add(_guests.PartyHere[s]);
                h.Add(_guests.State[s]); h.Add(_guests.Homeward[s]);
                h.Add(_guests.Node[s]); h.Add(_guests.Edge[s]); h.Add(_guests.Lift[s]);
                h.Add(_guests.EntryTick[s]); h.Add(_guests.ExitTick[s]); h.Add(_guests.DueTick[s]); h.Add(_guests.RideExitRunMs[s]);
                h.Add(_guests.Lane[s]); h.Add(_guests.Speed[s]); h.Add(_guests.Laps[s]);
                h.Add(_guests.WaitTicks[s]); h.Add(_guests.JoinTick[s]); h.Add(_guests.Ordinal[s]); h.Add(_guests.Sat[s]);
                h.Add(_guests.LapQuality[s]); h.Add(_guests.LapTicks[s]); h.Add(_guests.LapFlags[s]);
                h.Add(_guests.RecHash[s]); h.Add(_guests.RecCount[s]);
                h.Add(_guests.LineNext[s]); h.Add(_guests.RideNext[s]); h.Add(_guests.WheelNext[s]);
            }
            for (int b = 0; b < _guests.BucketCount; b++)
            {
                int head = _guests.BucketHead(b);
                if (head < 0) continue;
                h.Add(b); h.Add(head); h.Add(_guests.BucketTail(b));
            }
            for (int k = 0; k < _passes.Length; k++) if (_passes[k] != 0) { h.Add(k); h.Add(_passes[k]); }
            for (int k = 0; k < _nextEntryTick.Length; k++) if (_nextEntryTick[k] != 0) { h.Add(k); h.Add(_nextEntryTick[k]); }
            for (int k = 0; k < _liftMetric.Length; k++) if (_liftMetric[k] != 0) { h.Add(k); h.Add(_liftMetric[k]); }
            for (int k = 0; k < _trailMetric.Length; k++) if (_trailMetric[k] != 0) { h.Add(k); h.Add(_trailMetric[k]); }
        }

        // ================= the sidecar's v4 guest block (SI-12) =================

        /// <summary>
        /// The guests, appended at the end of the state as v2's lift block was: a reader of an older sidecar simply never
        /// gets here and opens on an empty hill. A later column (a wallet, needs, a ticket) appends again behind a further
        /// version, so growing this never rewrites a byte of it.
        /// </summary>
        internal void WriteGuestState(SidecarWriter o)
        {
            o.I32(Pacing.SkierCap);
            o.I32(Pacing.RecordSegments);
            o.I32(_serialCounter); o.I32(_partyCounter); o.I32(_demandDay);
            o.I64(_arrivalFromTick); o.I32(_arrivalWindowTicks); o.I64(_arrivalWeightTotal); o.I64(_dayStartTick);
            o.I32(_powderPull); o.I32(_forecastPull); o.I32(_inspectSerial); o.Bool(_popFromToday);
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                o.I32(_rep[a]); o.I32(_demand[a]); o.I32(_demandUncapped[a]); o.I32(_issued[a]); o.I32(_partyOrdinal[a]);
                o.I32(_arrived[a]); o.I32(_arrivedClosed[a]); o.I32(_capped[a]); o.I32(_wentHome[a]); o.I32(_stranded[a]);
                o.I32(_leftCount[a]); o.I64(_leftSatSum[a]);
                for (int k = 0; k < Reasons; k++) o.I64(_reasonSum[a * Reasons + k]);
                for (int k = 0; k < DemandLines; k++) o.I32(_stack[a * DemandLines + k]);
            }
            o.I32(_guests?.Count ?? 0);
            if (_guests != null)
                for (int s = 0; s < _guests.Cap; s++)
                {
                    if (_guests.State[s] == (byte)GuestState.Free) continue;
                    o.I32(s);
                    o.I32(_guests.Serial[s]); o.U8(_guests.Band[s]); o.U8(_guests.Arch[s]); o.U16(_guests.Ability[s]);
                    o.I32(_guests.Home[s]); o.U8(_guests.StayLaps[s]); o.I64(_guests.ArriveTick[s]);
                    o.I32(_guests.PartyId[s]); o.I32(_guests.Lead[s]); o.I32(_guests.Member[s]);
                    o.U8(_guests.PartySize[s]); o.U8(_guests.PartyHere[s]);
                    o.U8(_guests.State[s]); o.U8(_guests.Homeward[s]);
                    o.I32(_guests.Node[s]); o.I32(_guests.Edge[s]); o.I32(_guests.Lift[s]);
                    o.I64(_guests.EntryTick[s]); o.I64(_guests.ExitTick[s]); o.I64(_guests.DueTick[s]); o.I64(_guests.RideExitRunMs[s]);
                    o.U8(_guests.Lane[s]); o.U16(_guests.Speed[s]); o.U8(_guests.Laps[s]);
                    o.I32(_guests.WaitTicks[s]); o.I64(_guests.JoinTick[s]); o.I32(_guests.Ordinal[s]); o.U16(_guests.Sat[s]);
                    o.I32(_guests.LapQuality[s]); o.I32(_guests.LapTicks[s]); o.U8(_guests.LapFlags[s]);
                    o.U64(_guests.RecHash[s]);
                    o.I32(_guests.LineNext[s]); o.I32(_guests.RideNext[s]); o.I32(_guests.WheelNext[s]); o.U8(_guests.InWheel[s]);
                    // Only the segments it has actually skied, oldest first: a skier early in its day costs a handful of
                    // bytes rather than the whole ring, which is what keeps a save small when thousands are on the hill.
                    o.U8(_guests.RecCount[s]);
                    for (int k = 0; k < _guests.RecCount[s]; k++)
                    {
                        _guests.ReadRecord(s, k, out ushort seg, out ushort entry, out ushort exit);
                        o.U16(seg); o.U16(entry); o.U16(exit);
                    }
                }
            // The free stack and the wheel, so a restored game gives the same future, slot for slot (SI-12).
            o.I32(_guests?.FreeCount ?? 0);
            if (_guests != null) for (int k = 0; k < _guests.FreeCount; k++) o.I32(_guests.FreeAt(k));
            int buckets = 0;
            if (_guests != null) for (int b = 0; b < _guests.BucketCount; b++) if (_guests.BucketHead(b) >= 0) buckets++;
            o.I32(buckets);
            if (_guests != null)
                for (int b = 0; b < _guests.BucketCount; b++)
                {
                    if (_guests.BucketHead(b) < 0) continue;
                    o.I32(b); o.I32(_guests.BucketHead(b)); o.I32(_guests.BucketTail(b));
                }
            o.I32(_passes.Length);
            for (int k = 0; k < _passes.Length; k++) o.I32(_passes[k]);
            o.I32(_nextEntryTick.Length);
            for (int k = 0; k < _nextEntryTick.Length; k++) o.I64(_nextEntryTick[k]);
            o.I32(_liftMetric.Length);
            for (int k = 0; k < _liftMetric.Length; k++) o.I64(_liftMetric[k]);
            o.I32(_trailMetric.Length);
            for (int k = 0; k < _trailMetric.Length; k++) o.I64(_trailMetric[k]);
            o.I32(_liftPop.Length);
            for (int k = 0; k < _liftPop.Length; k++) o.U8(_liftPop[k]);
            o.I32(_trailPop.Length);
            for (int k = 0; k < _trailPop.Length; k++) o.U8(_trailPop[k]);
        }

        internal void ReadGuestState(SidecarReader r)
        {
            int cap = r.Count(1 << 20);
            int record = r.Count(4096);
            if (cap != Pacing.SkierCap || record != Pacing.RecordSegments)
                throw new InvalidDataException("The save's skier cap or day record doesn't match this Pacing table.");
            _serialCounter = r.I32(); _partyCounter = r.I32(); _demandDay = r.I32();
            _arrivalFromTick = r.I64(); _arrivalWindowTicks = r.I32(); _arrivalWeightTotal = r.I64(); _dayStartTick = r.I64();
            _powderPull = r.I32(); _forecastPull = r.I32(); _inspectSerial = r.I32(); _popFromToday = r.Bool();
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                _rep[a] = r.Ranged(0, 1000, "A reputation");
                _demand[a] = r.I32(); _demandUncapped[a] = r.I32(); _issued[a] = r.I32(); _partyOrdinal[a] = r.I32();
                _arrived[a] = r.I32(); _arrivedClosed[a] = r.I32(); _capped[a] = r.I32(); _wentHome[a] = r.I32(); _stranded[a] = r.I32();
                _leftCount[a] = r.I32(); _leftSatSum[a] = r.I64();
                for (int k = 0; k < Reasons; k++) _reasonSum[a * Reasons + k] = r.I64();
                for (int k = 0; k < DemandLines; k++) _stack[a * DemandLines + k] = r.I32();
            }
            _guests.Reset();
            int live = r.Count(Pacing.SkierCap);
            for (int k = 0; k < live; k++)
            {
                int s = r.Slot(Pacing.SkierCap);
                _guests.Serial[s] = r.I32(); _guests.Band[s] = r.Enum(0, Pacing.Bands - 1, "A skier's band");
                _guests.Arch[s] = r.U8(); _guests.Ability[s] = (short)r.U16();
                _guests.Home[s] = r.I32(); _guests.StayLaps[s] = r.U8(); _guests.ArriveTick[s] = r.I64();
                _guests.PartyId[s] = r.I32(); _guests.Lead[s] = r.Slot(Pacing.SkierCap); _guests.Member[s] = r.SlotOrNone(Pacing.SkierCap);
                _guests.PartySize[s] = r.U8(); _guests.PartyHere[s] = r.U8();
                _guests.State[s] = r.Enum(1, 7, "A skier's state"); _guests.Homeward[s] = r.Enum(0, 1, "A skier's way home");
                _guests.Node[s] = r.I32(); _guests.Edge[s] = r.I32(); _guests.Lift[s] = r.I32();
                _guests.EntryTick[s] = r.I64(); _guests.ExitTick[s] = r.I64(); _guests.DueTick[s] = r.I64(); _guests.RideExitRunMs[s] = r.I64();
                _guests.Lane[s] = r.U8(); _guests.Speed[s] = (short)r.U16(); _guests.Laps[s] = r.U8();
                _guests.WaitTicks[s] = r.I32(); _guests.JoinTick[s] = r.I64(); _guests.Ordinal[s] = r.I32(); _guests.Sat[s] = (short)r.U16();
                _guests.LapQuality[s] = r.I32(); _guests.LapTicks[s] = r.I32(); _guests.LapFlags[s] = r.U8();
                _guests.RecHash[s] = r.U64();
                _guests.LineNext[s] = r.SlotOrNone(Pacing.SkierCap); _guests.RideNext[s] = r.SlotOrNone(Pacing.SkierCap);
                _guests.WheelNext[s] = r.SlotOrNone(Pacing.SkierCap); _guests.InWheel[s] = r.Enum(0, 1, "A skier's place in the wheel");
                // The ring comes back oldest first with its head at nothing, which reads out in the same order it went in.
                int kept = r.Enum(0, (byte)record, "A skier's day record");
                _guests.RecCount[s] = (byte)kept; _guests.RecHead[s] = 0;
                for (int j = 0; j < kept; j++)
                {
                    int at = s * record + j;
                    _guests.RecSeg[at] = (ushort)r.U16(); _guests.RecEntry[at] = (ushort)r.U16(); _guests.RecExit[at] = (ushort)r.U16();
                }
            }
            int free = r.Count(Pacing.SkierCap);
            _guests.SetFree(free);
            for (int k = 0; k < free; k++) _guests.SetFreeAt(k, r.Slot(Pacing.SkierCap));
            int buckets = r.Count(_guests.BucketCount);
            for (int k = 0; k < buckets; k++)
            {
                int b = r.Ranged(0, _guests.BucketCount - 1, "A wheel bucket");
                int head = r.Slot(Pacing.SkierCap), tail = r.Slot(Pacing.SkierCap);
                _guests.SetBucket(b, head, tail);
            }
            int passes = r.Count(1 << 24);
            if (passes != _passes.Length) throw new InvalidDataException("The save's wear counters don't match the design's network.");
            for (int k = 0; k < passes; k++) _passes[k] = r.I32();
            int entry = r.Count(1 << 24);
            if (entry != _nextEntryTick.Length) throw new InvalidDataException("The save's entry gates don't match the design's network.");
            for (int k = 0; k < entry; k++) _nextEntryTick[k] = r.I64();
            int lm = r.Count(1 << 24);
            if (lm != _liftMetric.Length) throw new InvalidDataException("The save's lift ledger doesn't match this Pacing table.");
            for (int k = 0; k < lm; k++) _liftMetric[k] = r.I64();
            int tm = r.Count(1 << 24);
            if (tm != _trailMetric.Length) throw new InvalidDataException("The save's trail ledger doesn't match the design's trails.");
            for (int k = 0; k < tm; k++) _trailMetric[k] = r.I64();
            int lp = r.Count(1 << 24);
            if (lp != _liftPop.Length) throw new InvalidDataException("The save's lift popularity doesn't match this Pacing table.");
            for (int k = 0; k < lp; k++) _liftPop[k] = r.U8();
            int tp = r.Count(1 << 24);
            if (tp != _trailPop.Length) throw new InvalidDataException("The save's trail popularity doesn't match the design's trails.");
            for (int k = 0; k < tp; k++) _trailPop[k] = r.U8();
            RebuildEdgeCounts();
            _routeDirty = true;
        }

        /// <summary>The per-edge crowd counters, worked out again from where the skiers are (never saved: they are derived).</summary>
        void RebuildEdgeCounts()
        {
            if (_onEdge.Length == 0 || _guests == null) return;
            Array.Clear(_onEdge, 0, _onEdge.Length);
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (_guests.State[s] != (byte)GuestState.Skiing && _guests.State[s] != (byte)GuestState.Walking) continue;
                int edge = _guests.Edge[s];
                if (edge >= 0 && edge < _onEdge.Length) _onEdge[edge]++;
            }
            Array.Copy(_onEdge, _onEdgePrev, _onEdge.Length);
        }

        /// <summary>
        /// A save from before the guests (v2, v3) opens on an empty hill with every reputation at its start, as a new
        /// resort does: nothing is converted, which is what SI-12 asks of an older save.
        /// </summary>
        internal void ResetGuestsForOlderSave()
        {
            PrepareGuests(Design);
            _guests.Reset();
            for (int a = 0; a < Pacing.Archetypes; a++) _rep[a] = Pacing.StartRep;
            _demandDay = -1;
            _serialCounter = 0; _partyCounter = 0; _inspectSerial = 0; _popFromToday = false;
            ResetGuestLedger();
        }

        /// <summary>Sets the guest world up over a design's network before a save's skiers are read back into it.</summary>
        internal void PrepareGuests(SimDesign d)
        {
            if (_guests == null) _guests = new GuestColumns(Pacing);
            if (_release.Length != _guests.Cap)
            {
                _release = new int[Math.Max(1, _guests.Cap)];
                _releaseLift = new int[Math.Max(1, _guests.Cap)];
                _dump = new int[Math.Max(1, _guests.Cap)];
            }
            _guestsNetwork = d.Network;
            AllocGuestNetwork(d.Network);
        }
    }

    /// <summary>GU-15: a scripted spawn, for the style tile (4.10) and the tests.</summary>
    public sealed class SkierSpawn
    {
        public readonly int Archetype;
        public readonly int Count;
        public SkierSpawn(int archetype, int count)
        {
            if (count < 0 || count > 1_000_000) throw new ArgumentOutOfRangeException(nameof(count));
            Archetype = archetype; Count = count;
        }
    }
}
