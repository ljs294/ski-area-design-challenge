using System;
using System.IO;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.24 PR B, riding down (owner 2026-11-17; p4-02 RD-1…RD-5, LO-10 v2). A gondola carries riders both ways all
    /// day; a chair carries them down only while it is downloading (RD-1). Each such lift keeps a second line at its top
    /// station: every carrier that reaches the top unloads its up riders and then takes the front of the top line down
    /// (RD-2), so the descending seats are a budget of their own and the bottom line never waits on them (rule 4). A down
    /// rider is hidden for the compressed ride like an up rider (LO-06) and steps off at the bottom on the next carrier
    /// arrival there. The line at the top and the riders on the way down are chains over the same skier columns as the
    /// bottom line and the up riders (a skier is in one chain at a time), so they cost nothing per lift and allocate
    /// nothing per tick.
    /// </summary>
    public sealed partial class SimWorld
    {
        // ---- LO-10 v2: lifts by their top station, over the lifts row's network ----
        int[] _nodeTopL = Array.Empty<int>(), _topNextL = Array.Empty<int>();

        void FoldWayDownTops(SimNetwork net, int n)
        {
            _nodeTopL = new int[net.NodeCount];
            _topNextL = new int[n];
            for (int k = 0; k < _nodeTopL.Length; k++) _nodeTopL[k] = -1;
            for (int i = n - 1; i >= 0; i--)
            {
                int t = Design.Lifts[i].TopNode;
                _topNextL[i] = -1;
                if (t < 0 || t >= _nodeTopL.Length) continue;
                _topNextL[i] = _nodeTopL[t];
                _nodeTopL[t] = i;
            }
        }

        /// <summary>LO-10 v2: a descent that reaches the top of an open gondola has a way down — the ride.</summary>
        bool ReachesRideDown(int node)
        {
            if (node < 0 || node >= _nodeTopL.Length) return false;
            for (int j = _nodeTopL[node]; j >= 0; j = _topNextL[j])
                if (_wayDown[j] != 0 && LiftKinds.BothWays(Design.Lifts[j].Kind)) return true;
            return false;
        }

        /// <summary>
        /// RD-1: whether lift <paramref name="i"/> carries riders down now — a gondola, or a chair that is downloading — and
        /// so whether its top line may be joined: on the board, built, before its stop (RD-5: past last chair too, so
        /// nobody ends the day stuck at the top). A hold stops the rope, not the line.
        /// </summary>
        bool CarriesDown(int i, long sod) =>
            i >= 0 && i < _liftCount && _liftBuilt[i] && _liftOpen[i] != 0 && Phase == SeasonPhase.Season && sod < _lStop[i] * 1000L
            && LiftKinds.Compressed(Design.Lifts[i].Kind)
            && (LiftKinds.BothWays(Design.Lifts[i].Kind) || _downloading[i] != 0);

        bool CarriesDown(int i) => CarriesDown(i, FaceMs % DayMs);

        /// <summary>LO-04 for the top line: the wait for a skier joining it now, motion ms at 1×.</summary>
        long TopWaitMsOf(int i)
        {
            int seats = Math.Max(1, Design.Lifts[i].Seats);
            return (long)((_topCount[i] + seats - 1) / seats) * _intervalMs[i];
        }

        /// <summary>A chair has its way down again: its top line goes back to choosing at the top (they may ski now).</summary>
        void EndDownloading(int i)
        {
            if (LiftKinds.BothWays(Design.Lifts[i].Kind)) return;
            while (_topCount[i] > 0) DumpFromLine(i, PopTop(i));
            GuestBoardChanged();
        }

        // ================= boarding at the top (RD-2) =================

        /// <summary>
        /// RD-2: the carrier that unloaded at the top at <paramref name="topRun"/> takes the front of the top line, by
        /// LO-02's party rules (a party boards whole when it fits; the singles behind fill what it can't use).
        /// </summary>
        void BoardDown(int i, in LiftInput lift, long topRun, long iv)
        {
            long exit = ExitDown(lift, topRun, iv);
            int boarded = BoardFrom(_topFirst, _topLast, _topCount, i, lift.Seats, exit, down: true);
            _ridesDown[i] += boarded;
        }

        /// <summary>
        /// RD-2: the rope run time a rider who boarded at the top at <paramref name="topRun"/> steps off at the bottom: the
        /// ride ÷ the compression (LO-06), then the next carrier arrival at the bottom (the departures' own beat).
        /// </summary>
        long ExitDown(in LiftInput lift, long topRun, long iv)
        {
            long ride = lift.RideMs;
            if (!LiftKinds.Compressed(lift.Kind)) return topRun + ride;
            long due = topRun + ride / Pacing.RideCompression;
            long k = (due + iv - 1) / iv;
            return k * iv;
        }

        /// <summary>
        /// LO-02 over either line: one carrier loads from the front of the chain. A party boards whole when it fits, so a
        /// family rides together; a party too big for any carrier (four on a double, anyone on a T-bar) goes in
        /// carrier-sized groups on consecutive carriers and gathers again where it steps off. When the front party is too
        /// big for the seats *left*, the singles behind it fill them rather than the carrier leaving half empty. Returns
        /// how many boarded.
        /// </summary>
        int BoardFrom(int[] first, int[] last, int[] count, int i, int seats, long exitRun, bool down)
        {
            int room = seats, boarded = 0;
            while (room > 0 && count[i] > 0)
            {
                int front = first[i];
                int size = PartyInLine(i, front);
                if (size > room && size <= seats) break;       // it waits for a carrier of its own; the singles pass fills in
                int take = size <= room ? size : room;         // a party bigger than the carrier splits across carriers
                for (int k = 0; k < take; k++) { Board(i, PopChain(first, last, count, i), exitRun, down); boarded++; }
                room -= take;
            }
            // LO-02: the seats a waiting party can't use go to the singles behind it, within a bounded look down the line.
            // The walk steps party by party, so it never boards a member out of the middle of one.
            int prev = -1, s = first[i], steps = 0, limit = 4 * seats;
            while (room > 0 && s >= 0 && steps < limit)
            {
                int size = PartyInLine(i, s);
                if (size == 1)
                {
                    int next = _guests.LineNext[s];
                    RemoveFromChain(first, last, count, i, prev, s);
                    Board(i, s, exitRun, down);
                    boarded++; room--;
                    s = next; steps++;
                }
                else for (int k = 0; k < size && s >= 0; k++) { prev = s; s = _guests.LineNext[s]; steps++; }
            }
            return boarded;
        }

        void Board(int i, int s, long exitRun, bool down)
        {
            if (!down) { BoardOne(i, s, exitRun); return; }
            _guests.RideExitRunMs[s] = exitRun;
            OnBoardedDown(i, s);
            _guests.State[s] = (byte)GuestState.RidingDown;
            _guests.EntryTick[s] = Tick;
            _guests.Node[s] = Design.Lifts[i].BottomNode;   // where it is travelling to
            PushDown(i, s);
        }

        /// <summary>RD-5: at the lift's stop a skier still waiting at the top is carried down at once.</summary>
        void CarryDownAtOnce(int i, int s)
        {
            OnBoardedDown(i, s);
            _guests.State[s] = (byte)GuestState.RidingDown;
            _guests.Node[s] = Design.Lifts[i].BottomNode;
            _ridesDown[i]++;
            ReleaseRider(i, s);
        }

        /// <summary>RD-2: a skier joins the back of the top line (it never leaves it: there is nowhere to go).</summary>
        internal void JoinTop(int i, int skier)
        {
            _guests.JoinTick[skier] = Tick;
            _guests.State[skier] = (byte)GuestState.QueuedDown;
            _guests.Lift[skier] = i;
            _guests.Edge[skier] = -1;
            _guests.Node[skier] = Design.Lifts[i].TopNode;
            PushChain(_topFirst, _topLast, _topCount, i, skier);
        }

        // ---- the chains ----

        void PushChain(int[] first, int[] last, int[] count, int i, int s)
        {
            _guests.LineNext[s] = -1;
            if (first[i] < 0) first[i] = s; else _guests.LineNext[last[i]] = s;
            last[i] = s;
            count[i]++;
        }

        int PopChain(int[] first, int[] last, int[] count, int i)
        {
            int s = first[i];
            first[i] = _guests.LineNext[s];
            if (first[i] < 0) last[i] = -1;
            _guests.LineNext[s] = -1;
            count[i]--;
            return s;
        }

        void RemoveFromChain(int[] first, int[] last, int[] count, int i, int prev, int s)
        {
            int next = _guests.LineNext[s];
            if (prev < 0) first[i] = next; else _guests.LineNext[prev] = next;
            if (last[i] == s) last[i] = prev;
            _guests.LineNext[s] = -1;
            count[i]--;
        }

        int PopTop(int i) => PopChain(_topFirst, _topLast, _topCount, i);

        void PushDown(int i, int s)
        {
            _guests.RideNext[s] = -1;
            if (_downFirst[i] < 0) _downFirst[i] = s; else _guests.RideNext[_downLast[i]] = s;
            _downLast[i] = s;
            _downCount[i]++;
        }

        int PopDown(int i)
        {
            int s = _downFirst[i];
            _downFirst[i] = _guests.RideNext[s];
            if (_downFirst[i] < 0) _downLast[i] = -1;
            _guests.RideNext[s] = -1;
            _downCount[i]--;
            return s;
        }

        // ================= a lift's index moves (SI-13, SI-20) =================

        /// <summary>
        /// SI-20: the lifts were re-indexed by a new design. Every skier holding a lift (in a line, riding either way, or
        /// walking to one) moves to that lift's new index; one whose lift is gone is let go where it stands and chooses
        /// again, as the fold's own comment always promised (a vanished lift takes no one with it). Before 4.24 PR B the
        /// chains moved but the skiers' own lift index did not, so removing a lower-numbered lift mid-day left its
        /// neighbours' lines and riders pointing at the wrong lift.
        /// </summary>
        void ReindexLiftSkiers(int[] newIndex, int oldCount)
        {
            if (_guests == null) return;
            bool moved = false;
            for (int k = 0; k < oldCount && !moved; k++) if (newIndex[k] != k) moved = true;
            if (!moved) return;
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (!_guests.Alive(s)) continue;
                int was = _guests.Lift[s];
                if (was < 0) continue;
                int now = was < oldCount ? newIndex[was] : -1;
                _guests.Lift[s] = now;
                if (now >= 0) continue;
                var state = (GuestState)_guests.State[s];
                if (state != GuestState.Queued && state != GuestState.Riding && state != GuestState.QueuedDown && state != GuestState.RidingDown)
                    continue;   // a walker to a vanished lift finds it gone when it arrives (FinishWalk)
                _guests.LineNext[s] = -1;
                _guests.RideNext[s] = -1;
                if (_guests.Arch[s] == GuestColumns.FeederArch) { _guests.Give(s); continue; }
                _guests.State[s] = (byte)GuestState.Choosing;
                _guests.Edge[s] = -1;
                SplitOff(s);
                _guests.PartyHere[s] = 1;
                _guests.Schedule(s, Tick);
            }
        }

        // ================= the sidecar (v11's ride-down block) =================

        /// <summary>
        /// v11: per lift, the top line's and the down riders' chain ends and counts, and today's rides down. The chains
        /// themselves, the join ticks and the step-off times are the skiers' own columns, which the guest block saves.
        /// </summary>
        void WriteRideDownState(SidecarWriter o)
        {
            o.I32(_liftCount);
            for (int i = 0; i < _liftCount; i++)
            {
                o.I32(_topCount[i]); o.I32(_topFirst[i]); o.I32(_topLast[i]);
                o.I32(_downCount[i]); o.I32(_downFirst[i]); o.I32(_downLast[i]);
                o.I32(_ridesDown[i]);
            }
        }

        void ReadRideDownState(SidecarReader r)
        {
            int n = r.Count(Pacing.MaxLifts);
            if (n != _liftCount) throw new InvalidDataException("The ride-down block doesn't match the lifts.");
            for (int i = 0; i < n; i++)
            {
                _topCount[i] = r.Count(Pacing.SkierCap);
                _topFirst[i] = r.SlotOrNone(Pacing.SkierCap); _topLast[i] = r.SlotOrNone(Pacing.SkierCap);
                _downCount[i] = r.Count(Pacing.SkierCap);
                _downFirst[i] = r.SlotOrNone(Pacing.SkierCap); _downLast[i] = r.SlotOrNone(Pacing.SkierCap);
                _ridesDown[i] = r.Count(int.MaxValue);
                if ((_topCount[i] == 0) != (_topFirst[i] < 0) || (_downCount[i] == 0) != (_downFirst[i] < 0))
                    throw new InvalidDataException("A lift's top line or riders down don't match their counts.");
            }
        }

        // ================= reading (tests, the demo) =================

        /// <summary>RD-2: a lift's top line, riders on the way down and rides down today, by ID (tests, the report).</summary>
        public (int topLine, int ridingDown, int ridesDown) DownLedgerOf(ulong liftId)
        {
            int i = Array.BinarySearch(_liftIds, 0, _liftCount, liftId);
            return i < 0 || i >= _topCount.Length ? default : (_topCount[i], _downCount[i], _ridesDown[i]);
        }
    }
}
