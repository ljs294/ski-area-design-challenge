using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.12, the guests (p4-02 §7, GU-01…GU-18). Every skier on the hill is simulated at every speed, in columns with
    /// no allocation per tick (GU-01, BG-01). A skier arrives at a base through the morning (GU-06), picks its way over the
    /// directed network by conditions, steepness, width and crowd within its band (GU-07), queues, rides, skis and leaves,
    /// and keeps a record of its day (GU-02). Its satisfaction moves its archetype's reputation at closing (GU-18), and
    /// reputation sizes tomorrow's crowd (GU-03): that loop is the game.
    ///
    /// The work scales with what is *happening*, not with the cap, so the cap can rise far past Phase 4's 3,000:
    /// - the timing wheel wakes only the skiers whose turn it is (<see cref="GuestColumns"/>);
    /// - a party travels as one unit, scheduled once;
    /// - the routing search is rebuilt at most once a tick, however many lifts change at once;
    /// - the crowd counters a choice reads are last tick's, so the scoring can be split across a band pool later (SI-09).
    /// </summary>
    public sealed partial class SimWorld
    {
        GuestColumns _guests;

        // ---- the step's hand-overs from the lifts row (which runs before this one) ----
        int[] _release = Array.Empty<int>();
        int _releaseCount;
        int[] _releaseLift = Array.Empty<int>();
        int[] _dump = Array.Empty<int>();
        int _dumpCount;

        // ---- per network edge and node, allocated when a design is folded ----
        /// <summary>GU-07: skiers on each edge now, and the copy a choice reads (last tick's).</summary>
        int[] _onEdge = Array.Empty<int>(), _onEdgePrev = Array.Empty<int>();
        /// <summary>GU-07: each edge's comfortable crowd, skiers.</summary>
        int[] _edgeCrowd = Array.Empty<int>();
        /// <summary>GU-10: the tick a node may send the next party down a trail (skiers enter one at a time).</summary>
        long[] _nextEntryTick = Array.Empty<long>();
        /// <summary>SN-24: this hour's passes per edge per lateral band, for 4.07's wear.</summary>
        int[] _passes = Array.Empty<int>();
        /// <summary>GU-06: the base nodes a skier may arrive at and go home to, and how arrivals divide between them.</summary>
        int[] _baseNode = Array.Empty<int>(), _baseWeight = Array.Empty<int>();
        int _baseCount, _baseWeightTotal;

        /// <summary>
        /// The lifts the guest code may look at: the board's count, never past the design's own list. A save opened over
        /// a different design has the saved lifts' operations but the new design's lifts until the refold (SI-20).
        /// </summary>
        int LiftsNow => Math.Min(_liftCount, Design.Lifts.Length);

        /// <summary>An option that is a lift, not a ski edge: the lift's index is the rest of the value.</summary>
        const int LiftOption = 1 << 30;

        /// <summary>GU-02: the serial of the skier the player has selected, or 0. Selecting one changes no result.</summary>
        int _inspectSerial;
        long _inspectPublishedAt = long.MinValue;

        // ================= folding (SI-13, SI-20) =================

        /// <summary>
        /// SI-13: carries the skiers over to a new design. Node and edge **keys** do that, not indices: when the keys are
        /// the same (the common case, and the only affordable one at tens of thousands of skiers) the indices stand and
        /// nothing is touched. When they differ, each skier is put back on the edge or at the node with its old key; a
        /// skier whose trail is gone finishes on the natural ground, and a vanished lift's line and riders are let out at
        /// its bottom. Runs before <see cref="FoldLiftOps"/>, so the lines it reads are still the old design's.
        /// </summary>
        void FoldGuests(SimDesign old, SimDesign now)
        {
            if (_guests == null) _guests = new GuestColumns(Pacing);
            if (_release.Length != _guests.Cap)
            {
                _release = new int[Math.Max(1, _guests.Cap)];
                _releaseLift = new int[Math.Max(1, _guests.Cap)];
                _dump = new int[Math.Max(1, _guests.Cap)];
            }
            var net = now.Network;
            var wasNet = old?.Network;
            // A revision that arrives before its network is built carries no guest world: keep the one we have (the lifts
            // row keeps its own stand-in way down), so an edit mid-day never strands the hill.
            if (net == null) { _guestsNetwork = wasNet; AllocGuestNetwork(wasNet); return; }
            bool sameShape = wasNet != null && wasNet.ShapeHash == net.ShapeHash;
            if (!sameShape && wasNet != null && _guests.Count > 0) RemapGuests(wasNet, net);
            _guestsNetwork = net;
            AllocGuestNetwork(net);
            GuestBoardChanged();
        }

        /// <summary>The network the skiers are standing on: the newest revision that brought one (SI-21).</summary>
        SimNetwork _guestsNetwork;

        void AllocGuestNetwork(SimNetwork net)
        {
            int e = net?.EdgeFrom.Length ?? 0, n = net?.NodeCount ?? 0;
            if (_onEdge.Length != e)
            {
                _onEdge = new int[e]; _onEdgePrev = new int[e]; _edgeCrowd = new int[e];
                _passes = new int[e * Pacing.WearBands];
            }
            else Array.Clear(_onEdge, 0, e);
            if (_nextEntryTick.Length != n) _nextEntryTick = new long[n];
            for (int k = 0; k < e; k++) _edgeCrowd[k] = net.CapacityOf(k, Pacing.SkiersPerOpenAcre);
            // The counters are rebuilt from the skiers, so a remap or a reshape never leaves them adrift.
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (_guests.State[s] != (byte)GuestState.Skiing && _guests.State[s] != (byte)GuestState.Walking) continue;
                int edge = _guests.Edge[s];
                if (edge >= 0 && edge < e) _onEdge[edge]++;
            }
            Array.Copy(_onEdge, _onEdgePrev, e);
            AllocRouting(n, e);
            AllocChoice(n);
            AllocLedger(net?.TrailIds.Length ?? 0);
            FindBases(net);
        }

        /// <summary>SI-13: every skier back on the edge, or at the node, that carries its old key.</summary>
        void RemapGuests(SimNetwork was, SimNetwork net)
        {
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (!_guests.Alive(s)) continue;
                var state = (GuestState)_guests.State[s];
                int node = _guests.Node[s], edge = _guests.Edge[s];
                int toNode = node >= 0 && node < was.NodeCount ? FindNode(net, was.NodeIds[node]) : -1;
                if (state == GuestState.Skiing || state == GuestState.Walking)
                {
                    int to = edge >= 0 && edge < was.EdgeKeys.Length ? FindEdge(net, was.EdgeKeys[edge]) : -1;
                    if (to >= 0) { _guests.Edge[s] = to; _guests.Node[s] = net.EdgeTo[to]; continue; }
                    // SI-13: its trail is gone. It finishes on the natural ground beneath and chooses again.
                    _guests.Edge[s] = -1;
                    int at = toNode >= 0 ? toNode : LowestBase(net);
                    Stand(s, at);
                    if (_guests.Lead[s] == s) _guests.Schedule(s, Tick);
                    continue;
                }
                _guests.Node[s] = toNode >= 0 ? toNode : LowestBase(net);
                int home = _guests.Home[s];
                _guests.Home[s] = home >= 0 && home < was.NodeCount ? FindNode(net, was.NodeIds[home]) : -1;
                if (_guests.Home[s] < 0) _guests.Home[s] = _guests.Node[s];
                if (state == GuestState.Queued || state == GuestState.Riding) continue;   // the lifts row lets them out
                Stand(s, _guests.Node[s]);
                if (_guests.Lead[s] == s) _guests.Schedule(s, Tick);
            }
        }

        static int FindNode(SimNetwork net, ulong id)
        {
            for (int k = 0; k < net.NodeIds.Length; k++) if (net.NodeIds[k] == id) return k;
            return -1;
        }

        static int FindEdge(SimNetwork net, ulong key)
        {
            for (int k = 0; k < net.EdgeKeys.Length; k++) if (net.EdgeKeys[k] == key) return k;
            return -1;
        }

        /// <summary>
        /// GU-06: the bases — **every built lift's bottom station**.
        ///
        /// The first reading of GU-06 was "a lift-bottom station no trail leaves downhill", on the idea that such a
        /// station is the foot of the mountain. On a resort somebody has actually drawn that picks out the wrong
        /// stations: the real base usually has a run leaving it (on to another pod), while the stations with nothing
        /// leaving them are the odd corners nobody skis *to* either. On Jackson Hole it chose four stations that no
        /// descent reached, so only those four of seventy nodes counted as "home", every other node looked like a dead
        /// end, and the crowd stood at the bottom all day with nothing it was allowed to do.
        ///
        /// A lift bottom is the honest definition: it is where a skier can get on something, and therefore where a
        /// skier is never stranded. Which of them a skier *arrives* at is a separate question — see
        /// <see cref="ArrivalNode"/>, which only sends a band somewhere it has something to ski.
        /// </summary>
        void FindBases(SimNetwork net)
        {
            if (_baseNode.Length < Pacing.MaxLifts) { _baseNode = new int[Pacing.MaxLifts]; _baseWeight = new int[Pacing.MaxLifts]; }
            _baseCount = 0; _baseWeightTotal = 0;
            if (net == null) return;
            for (int i = 0; i < LiftsNow; i++)
            {
                int b = Design.Lifts[i].BottomNode;
                if (b < 0 || b >= net.NodeCount || !Design.Lifts[i].Built) continue;
                AddBase(b, Design.Lifts[i].RatedPerHour);
            }
            if (_baseCount == 0)
            {
                int low = LowestBase(net);
                if (low >= 0) AddBase(low, 1);
            }
        }

        void AddBase(int node, int weight)
        {
            for (int k = 0; k < _baseCount; k++)
                if (_baseNode[k] == node) { _baseWeight[k] += weight; _baseWeightTotal += weight; return; }
            if (_baseCount == _baseNode.Length) return;
            _baseNode[_baseCount] = node;
            _baseWeight[_baseCount] = weight;
            _baseCount++;
            _baseWeightTotal += weight;
        }

        /// <summary>The lowest built lift's bottom station, or −1.</summary>
        int LowestBase(SimNetwork net)
        {
            int best = -1, bestCm = int.MaxValue;
            for (int i = 0; i < LiftsNow; i++)
            {
                var l = Design.Lifts[i];
                if (!l.Built || l.BottomNode < 0 || net == null || l.BottomNode >= net.NodeCount) continue;
                if (best < 0 || l.BottomLevelCm < bestCm) { best = l.BottomNode; bestCm = l.BottomLevelCm; }
            }
            return best;
        }

        bool IsBase(int node)
        {
            for (int k = 0; k < _baseCount; k++) if (_baseNode[k] == node) return true;
            return false;
        }

        // ================= the row =================

        /// <summary>Whether any skier is on the hill (SI-11: a step then covers one tick, so arrivals never shift).</summary>
        internal bool AnySkier => _guests != null && _guests.Count > 0;

        internal void RunGuests()
        {
            if (_guests == null || _guests.Cap == 0) return;
            // GU-07: one rebuild however many lifts opened, closed, held or resumed since the last tick.
            if (_routeDirty) RebuildRoutes();
            // A choice reads last tick's crowd, so the scoring is a pure pass that a band pool can split later (SI-09).
            if (_onEdge.Length > 0) Array.Copy(_onEdge, _onEdgePrev, _onEdge.Length);
            DrainReleases();
            DrainDumps();
            long sod = FaceMs % DayMs;
            for (int k = 0; k < _stepTicks; k++)
            {
                long t = Tick + k;
                Arrivals(t);
                WakeDue(t);
            }
            UpdatePopularity();
            // GU-12: nobody is on the hill at night. At the stop the day is counted and the hill cleared (GU-18 reads it).
            if (_boundaryDoneAt == FaceMs && sod == _todayStop * 1000L) CloseTheDay();
        }

        void WakeDue(long t)
        {
            int s = _guests.TakeDue(t);
            while (s >= 0)
            {
                int next = _guests.WheelNext[s];
                _guests.InWheel[s] = 0;                    // the bucket is gone; the slot is out of the wheel
                if (_guests.State[s] != (byte)GuestState.Free)
                {
                    if (_guests.DueTick[s] > t) _guests.Schedule(s, _guests.DueTick[s]);   // further out than the wheel is long
                    else Wake(s, t);
                }
                s = next;
            }
        }

        void Wake(int s, long t)
        {
            switch ((GuestState)_guests.State[s])
            {
                case GuestState.Arriving:
                    _guests.Node[s] = _guests.Home[s];
                    _guests.PartyHere[s] = _guests.PartySize[s];
                    Choose(s, t);
                    break;
                case GuestState.Choosing:
                case GuestState.Dwelling:
                    Choose(s, t);
                    break;
                case GuestState.Skiing:
                    FinishEdge(s, t);
                    break;
                case GuestState.Walking:
                    FinishWalk(s, t);
                    break;
                case GuestState.Queued:
                    // GU-06: a skier who has queued LeaveWait goes home from the line.
                    if (t - _guests.JoinTick[s] >= LeaveWaitTicks) LeaveTheLine(s, t);
                    break;
            }
        }

        int LeaveWaitTicks => Math.Max(1, Pacing.LeaveWaitSeconds * 1000 / Pacing.TickMs);
        int BalkWaitMs => Pacing.BalkWaitSeconds * 1000;

        // ================= arrivals (GU-06) =================

        /// <summary>
        /// GU-06: the parties due this tick. The schedule is exact integer arithmetic over the window's weights, so it
        /// needs no list and no sort: by tick <paramref name="t"/> the day should have issued
        /// demand × (the weight so far) ÷ (the whole weight) skiers of each archetype, and the difference is this tick's.
        /// </summary>
        void Arrivals(long t)
        {
            if (_arrivalWindowTicks <= 0 || _arrivalFromTick == long.MaxValue || t < _arrivalFromTick) return;
            long n = t - _arrivalFromTick + 1;
            if (n > _arrivalWindowTicks) return;
            bool canCome = Open && Phase == SeasonPhase.Season && AnyLiftTakingSkiers();
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                long target = (long)_demand[a] * WeightSoFar(n) / _arrivalWeightTotal;
                int due = (int)(target - _issued[a]);
                while (due > 0)
                {
                    int size = PartySize(a, _partyOrdinal[a]);
                    if (size > due) size = due;
                    Admit(a, size, t, canCome);
                    _issued[a] += size;
                    _partyOrdinal[a]++;
                    due -= size;
                }
            }
        }

        bool AnyLiftTakingSkiers()
        {
            long sod = FaceMs % DayMs;
            if (sod >= _todayLastChair * 1000L) return false;
            for (int i = 0; i < LiftsNow; i++) if (LineJoinable(i, sod) && _wayDown[i] != 0) return true;
            return false;
        }

        /// <summary>
        /// GU-03's Cap line and GU-06: a party comes in at a base, is counted as arriving to a closed mountain, or finds
        /// the hill at its cap. Nobody is turned away in Phase 4 (there are no ticket caps yet), so a party that finds no
        /// slot is a Cap line on the stack, which the player can see.
        /// </summary>
        void Admit(int arch, int size, long t, bool canCome)
        {
            if (!canCome) { _arrivedClosed[arch] += size; return; }
            if (_guests.FreeCount < size) { _capped[arch] += size; return; }
            int node = ArrivalNode(arch, _partyOrdinal[arch]);
            if (node < 0) { _arrivedClosed[arch] += size; return; }
            int lead = -1, last = -1;
            int party = ++_partyCounter;
            for (int k = 0; k < size; k++)
            {
                int s = _guests.Take();
                int serial = ++_serialCounter;
                _guests.Serial[s] = serial;
                _guests.Arch[s] = (byte)arch;
                _guests.Band[s] = (byte)arch;                      // Phase 4: an archetype is a band (GU-05)
                _guests.Ability[s] = (short)AbilityOf(arch, serial);
                _guests.Home[s] = node;
                _guests.Node[s] = node;
                _guests.StayLaps[s] = (byte)StayLapsOf(serial);
                _guests.Sat[s] = (short)Pacing.StartSatisfaction;
                _guests.Speed[s] = (short)SpeedOf(_guests.Ability[s], serial);
                _guests.ArriveTick[s] = t;
                _guests.PartyId[s] = party;
                _guests.State[s] = (byte)GuestState.Arriving;
                if (lead < 0) { lead = s; _guests.Lead[s] = s; }
                else { _guests.Lead[s] = lead; _guests.Member[last] = s; }
                last = s;
                _arrived[arch]++;
            }
            _guests.PartySize[lead] = (byte)size;
            _guests.PartyHere[lead] = 0;
            // The party's speed is its slowest member's (it skis together).
            short slow = short.MaxValue;
            for (int s = lead; s >= 0; s = _guests.Member[s]) if (_guests.Speed[s] < slow) slow = _guests.Speed[s];
            for (int s = lead; s >= 0; s = _guests.Member[s]) _guests.Speed[s] = slow;
            _guests.Schedule(lead, t);
            Events.Add(t, SimEventType.SkierArrived, lead, size, (ushort)arch);
        }

        /// <summary>
        /// GU-06: where a party comes in, weighted by the capacity of the lifts there — but only among the bases where
        /// this band has something it may ski. A skier dropped at a base whose only lift serves terrain above its band
        /// has nowhere to go and would stand there until closing; nobody drives to a mountain to do that.
        /// </summary>
        int ArrivalNode(int arch, int ordinal)
        {
            if (_baseCount == 0) return -1;
            int band = arch < Pacing.Bands ? arch : 0;
            int total = 0;
            for (int k = 0; k < _baseCount; k++) if (HasSomethingToDo(_baseNode[k], band)) total += _baseWeight[k];
            if (total <= 0) return -1;                      // nothing on this mountain for this band today
            ulong key = SimKey.Hash(SeasonSeed, SimKey.ArrivalTag, (ulong)(uint)ordinal, (ulong)(uint)arch);
            int pick = (int)(key % (ulong)total);
            for (int k = 0; k < _baseCount; k++)
            {
                if (!HasSomethingToDo(_baseNode[k], band)) continue;
                pick -= _baseWeight[k];
                if (pick < 0) return _baseNode[k];
            }
            return -1;
        }

        /// <summary>GU-05: an ability inside the archetype's band.</summary>
        int AbilityOf(int arch, int serial)
        {
            ulong key = SimKey.Hash(SeasonSeed, SimKey.AbilityTag, (ulong)(uint)serial);
            int lo = arch == 0 ? 0 : BandFloor[arch];
            int hi = arch == Pacing.Bands - 1 ? 1000 : BandFloor[arch + 1];
            return lo + (int)(key % (ulong)Math.Max(1, hi - lo));
        }

        /// <summary>GU-05: the thousandths of ability at which each band begins (MapLibre's cut-offs, kept).</summary>
        static readonly int[] BandFloor = { 0, 250, 550, 820 };

        int StayLapsOf(int serial)
        {
            ulong key = SimKey.Hash(SeasonSeed, SimKey.StayTag, (ulong)(uint)serial);
            int span = Pacing.StayLapsMax - Pacing.StayLapsMin + 1;
            return Pacing.StayLapsMin + (int)(key % (ulong)span);
        }

        /// <summary>
        /// GU-10: (0.6 + 0.8 × ability) × (1 + 0.15 × Z), permille, Z a keyed near-normal clipped to ±3. Twelve keyed
        /// uniforms make Z, so there is no transcendental on the path and Mono, IL2CPP and CoreCLR agree exactly (SI-08).
        /// </summary>
        int SpeedOf(int abilityThousandths, int serial)
        {
            ulong key = SimKey.Hash(SeasonSeed, SimKey.SpeedTag, (ulong)(uint)serial);
            int sum = 0;
            for (int i = 0; i < 12; i++) { sum += (int)(key % 1000); key = key / 1000 + key * 6364136223846793005UL; }
            int z = sum - 6000;                                     // −6000…+6000 thousandths, mean 0, sd about 1000
            if (z > 3000) z = 3000; else if (z < -3000) z = -3000;
            int bodyPermille = 600 + 800 * abilityThousandths / 1000;
            int spread = 1000 + 15 * z / 100;
            int v = bodyPermille * spread / 1000;
            return v < 100 ? 100 : v;
        }

        int PartySize(int arch, int ordinal)
        {
            ulong key = SimKey.Hash(SeasonSeed, SimKey.PartyTag, (ulong)(uint)ordinal, (ulong)(uint)arch);
            int total = 0;
            for (int k = 0; k < Pacing.PartyMix.Length; k++) total += Pacing.PartyMix[k];
            if (total <= 0) return 1;
            int pick = (int)(key % (ulong)total);
            for (int k = 0; k < Pacing.PartyMix.Length; k++)
            {
                pick -= Pacing.PartyMix[k];
                if (pick < 0) return k + 1;
            }
            return 1;
        }

        // ================= moving (GU-07, GU-09, GU-10) =================

        /// <summary>
        /// Puts a skier at a node with nothing in hand. It does **not** schedule it: the caller either wakes it now
        /// (<see cref="Choose"/>) or schedules it, never both, so a skier is never in the wheel twice.
        /// </summary>
        void Stand(int s, int node)
        {
            _guests.State[s] = (byte)GuestState.Choosing;
            _guests.Edge[s] = -1;
            _guests.Lift[s] = -1;
            _guests.Node[s] = node;
        }

        /// <summary>
        /// GU-07: the party at a node takes its next edge, or goes home, or waits. Only a lead is ever asked, and it moves
        /// every member in one go, so a family skis together.
        /// </summary>
        void Choose(int lead, long t)
        {
            if (_guests.Arch[lead] == GuestColumns.FeederArch) return;
            if (_guests.PartyHere[lead] < _guests.PartySize[lead]) return;   // the party is still gathering at the top
            int node = _guests.Node[lead];
            if (node < 0) { Depart(lead, t); return; }
            long sod = FaceMs % DayMs;
            // GU-06, GU-11: past last chair everyone heads home, whatever the stay said.
            if (sod >= _todayLastChair * 1000L) _guests.Homeward[lead] = 1;
            if (_guests.Homeward[lead] != 0 && IsBase(node)) { Depart(lead, t); return; }
            int opt = _guests.Homeward[lead] != 0 ? HomeOption(lead, node) : BestOption(lead, node, t);
            if (opt == NoOption)
            {
                if (_guests.Homeward[lead] != 0)
                {
                    // GU-11: no open way down from here. It skis the natural ground to the lowest base and says so.
                    Strand(lead, t);
                    return;
                }
                // Nothing it may take. It waits a moment and looks again — a lift may open, a trail may be groomed —
                // but it does not wait all day: a skier with nothing to do goes home, and the hill never silently
                // fills with people standing about. Whichever way, the day's ledger stays honest.
                if (_guests.State[lead] != (byte)GuestState.Choosing) _guests.EntryTick[lead] = t;
                _guests.State[lead] = (byte)GuestState.Choosing;
                if (t - _guests.EntryTick[lead] >= LeaveWaitTicks)
                {
                    _guests.Homeward[lead] = 1;
                    if (IsBase(node)) { Depart(lead, t); return; }
                    if (HomeOption(lead, node) == NoOption) { Strand(lead, t); return; }
                    Choose(lead, t);
                    return;
                }
                _guests.Schedule(lead, t + Pacing.ChoiceRetryTicks);
                return;
            }
            TakeOption(lead, opt, t);
        }

        const int NoOption = int.MinValue;

        void TakeOption(int lead, int opt, long t)
        {
            if ((opt & LiftOption) != 0)
            {
                int lift = opt & ~LiftOption;
                int walkTicks = MsToTicks(Design.Lifts[lift].WalkInMs, 1000);
                if (walkTicks > 0)
                {
                    // GU-09: the walk across the Area to this lift's bottom.
                    for (int s = lead; s >= 0; s = _guests.Member[s])
                    {
                        _guests.State[s] = (byte)GuestState.Walking;
                        _guests.Edge[s] = -1;
                        _guests.Lift[s] = lift;
                        _guests.EntryTick[s] = t;
                        _guests.ExitTick[s] = t + walkTicks;
                    }
                    _guests.Schedule(lead, t + walkTicks);
                    return;
                }
                JoinParty(lead, lift, t);
                return;
            }
            EnterEdge(lead, opt, t);
        }

        /// <summary>GU-10: the party enters a trail segment or a path, one party at a time per node.</summary>
        void EnterEdge(int lead, int edge, long t)
        {
            int node = _guests.Node[lead];
            if (node >= 0 && node < _nextEntryTick.Length && t < _nextEntryTick[node])
            {
                _guests.State[lead] = (byte)GuestState.Choosing;
                _guests.Schedule(lead, _nextEntryTick[node]);
                return;
            }
            if (node >= 0 && node < _nextEntryTick.Length) _nextEntryTick[node] = t + Pacing.EntrySpacingTicks;
            var net = _guestsNetwork;
            bool path = net.EdgeKindOf[edge] == (byte)SimEdgeKind.Path;
            int ticks = EdgeTicks(edge, _guests.Speed[lead], path);
            int to = net.EdgeTo[edge];
            for (int s = lead; s >= 0; s = _guests.Member[s])
            {
                _guests.State[s] = (byte)(path ? GuestState.Walking : GuestState.Skiing);
                _guests.Edge[s] = edge;
                _guests.Lift[s] = -1;
                _guests.Node[s] = to;
                _guests.EntryTick[s] = t;
                _guests.ExitTick[s] = t + ticks;
                _guests.Lane[s] = (byte)LaneOf(s, edge);
                _onEdge[edge]++;
            }
            _guests.Schedule(lead, t + ticks);
        }

        /// <summary>
        /// GU-10: how many ticks the party takes over the edge: the submitter's reference time for the trail's band, over
        /// the party's speed and <see cref="Pacing.SkierSpeedFactor"/>. Integers only, and never less than one tick.
        /// </summary>
        int EdgeTicks(int edge, int speedPermille, bool path)
        {
            int baseMs = _guestsNetwork.EdgeBaseMs[edge];
            if (path) return MsToTicks(baseMs, 1000);
            return MsToTicks(baseMs, speedPermille);
        }

        int MsToTicks(int ms, int speedPermille)
        {
            if (ms <= 0) return 0;
            long real = (long)ms * 1000 / Math.Max(1, speedPermille) / Math.Max(1, Pacing.SkierSpeedFactor);
            long ticks = (real + Pacing.TickMs - 1) / Pacing.TickMs;
            return (int)Math.Max(1, Math.Min(int.MaxValue, ticks));
        }

        /// <summary>SN-24, GU-16: which lateral band of the trail this skier takes, keyed per skier and segment.</summary>
        int LaneOf(int s, int edge)
        {
            ulong key = SimKey.Hash(SeasonSeed, SimKey.LaneTag, (ulong)(uint)_guests.Serial[s], (ulong)(uint)edge);
            return (int)(key % (ulong)Pacing.WearBands);
        }

        /// <summary>The walk across an Area is over: the party joins the lift's line (or chooses again if it closed).</summary>
        void FinishWalk(int lead, long t)
        {
            int lift = _guests.Lift[lead];
            if (_guests.Edge[lead] >= 0) { FinishEdge(lead, t); return; }
            if (lift < 0 || !LineJoinable(lift, FaceMs % DayMs))
            {
                for (int s = lead; s >= 0; s = _guests.Member[s]) Stand(s, _guests.Node[lead]);
                _guests.PartyHere[lead] = _guests.PartySize[lead];
                Choose(lead, t);
                return;
            }
            JoinParty(lead, lift, t);
        }

        void JoinParty(int lead, int lift, long t)
        {
            // The party is about to scatter across carriers (LO-02), so the count of who is back together starts at
            // nothing here and climbs again as each member steps off at the top (Gather). It must be zeroed *once*, as
            // the party leaves, never per boarding: a party split across two carriers would otherwise have its count
            // reset after its first members had already arrived, and would wait at the top for ever.
            _guests.PartyHere[lead] = 0;
            for (int s = lead; s >= 0; s = _guests.Member[s]) Join(lift, s);
            // GU-06: the lead wakes when the party has waited long enough to give up (LO-02 boards it before that).
            _guests.Schedule(lead, t + LeaveWaitTicks);
        }

        /// <summary>The party has finished its segment: the record, the wear counter, the lap's snow, then it chooses.</summary>
        void FinishEdge(int lead, long t)
        {
            var net = _guestsNetwork;
            int edge = _guests.Edge[lead];
            if (edge < 0) { Stand(lead, _guests.Node[lead]); _guests.PartyHere[lead] = _guests.PartySize[lead]; Choose(lead, t); return; }
            int to = net.EdgeTo[edge];
            bool trail = net.EdgeKindOf[edge] == (byte)SimEdgeKind.TrailSegment;
            int quality = trail ? QualityOf(edge, _guests.Band[lead]) : 1000;
            int crowded = trail && _onEdgePrev[edge] > _edgeCrowd[edge] ? 1 : 0;
            int misrated = trail && Misrated(edge) ? 1 : 0;
            for (int s = lead; s >= 0; s = _guests.Member[s])
            {
                _guests.Record(s, SegmentCode(edge), _guests.EntryTick[s], t, _dayStartTick);
                _onEdge[edge]--;
                if (trail)
                {
                    int lane = _guests.Lane[s];
                    _passes[edge * Pacing.WearBands + lane]++;
                    int which = net.EdgeTrail[edge];
                    Metric(SimMetric.Passes, false, which, 1);
                    if (misrated != 0) Metric(SimMetric.MisratePenalty, false, which, 1);
                    if (crowded != 0) Metric(SimMetric.CrowdPenalty, false, which, 1);
                    int ticks = (int)Math.Max(1, t - _guests.EntryTick[s]);
                    _guests.LapQuality[s] += quality * ticks;
                    _guests.LapTicks[s] += ticks;
                    if (misrated != 0) _guests.LapFlags[s] |= 1;
                    if (crowded != 0) _guests.LapFlags[s] |= 4;
                }
                _guests.State[s] = (byte)GuestState.Choosing;
                _guests.Edge[s] = -1;
                _guests.Node[s] = to;
            }
            _guests.PartyHere[lead] = _guests.PartySize[lead];
            Choose(lead, t);
        }

        ushort SegmentCode(int edge) => (ushort)Math.Min(GuestColumns.LiftMark - 1, edge);
        ushort LiftCode(int lift) => (ushort)(GuestColumns.LiftMark | Math.Min(0x7FFF, lift));

        // ================= the lifts' hand-overs =================

        /// <summary>LO-06: a rider has stepped off at the top. The guests row picks it up in this same step.</summary>
        void ReleaseRider(int lift, int s)
        {
            if (_releaseCount < _release.Length) { _release[_releaseCount] = s; _releaseLift[_releaseCount] = lift; _releaseCount++; }
        }

        /// <summary>LO-07, LO-10: a line was let go (the stop, downloading, a closed mountain). Nobody is dropped.</summary>
        void DumpFromLine(int lift, int s)
        {
            if (_dumpCount < _dump.Length) _dump[_dumpCount++] = s;
        }

        void DrainReleases()
        {
            for (int k = 0; k < _releaseCount; k++)
            {
                int s = _release[k], lift = _releaseLift[k];
                if (!_guests.Alive(s)) continue;
                if (_guests.Arch[s] == GuestColumns.FeederArch) { _guests.Give(s); continue; }
                _guests.Record(s, LiftCode(lift), _guests.EntryTick[s], Tick, _dayStartTick);
                int top = Design.Lifts[lift].TopNode;
                _guests.Node[s] = top;
                _guests.Edge[s] = -1;
                _guests.Lift[s] = -1;
                _guests.State[s] = (byte)GuestState.Choosing;
                Gather(s);
            }
            _releaseCount = 0;
        }

        void DrainDumps()
        {
            for (int k = 0; k < _dumpCount; k++)
            {
                int s = _dump[k];
                if (!_guests.Alive(s)) continue;
                if (_guests.Arch[s] == GuestColumns.FeederArch) { _guests.Give(s); continue; }
                int lift = _guests.Lift[s];
                ChargeWait(s, Tick);
                // A dump can separate a party (some rode up, some did not), so each dumped skier goes its own way.
                SplitOff(s);
                _guests.Node[s] = lift >= 0 ? Design.Lifts[lift].BottomNode : _guests.Home[s];
                _guests.Lift[s] = -1;
                _guests.Edge[s] = -1;
                _guests.PartyHere[s] = 1;
                _guests.State[s] = (byte)GuestState.Choosing;
                _guests.Schedule(s, Tick);
            }
            _dumpCount = 0;
        }

        /// <summary>A member is back with its party; when they are all together the lead picks the next run.</summary>
        void Gather(int s)
        {
            int lead = _guests.Lead[s];
            if (_guests.PartyHere[lead] < byte.MaxValue) _guests.PartyHere[lead]++;
            if (_guests.PartyHere[lead] >= _guests.PartySize[lead]) _guests.Schedule(lead, Tick);
        }

        /// <summary>Makes a skier a party of its own (a dump or a stranding has separated it from the rest).</summary>
        void SplitOff(int s)
        {
            int lead = _guests.Lead[s];
            if (lead == s && _guests.PartySize[s] <= 1) return;
            if (lead == s)
            {
                // The lead leaves: the next member leads what is left.
                int heir = _guests.Member[s];
                int size = _guests.PartySize[s] - 1;
                for (int m = heir; m >= 0; m = _guests.Member[m]) _guests.Lead[m] = heir;
                if (heir >= 0) { _guests.PartySize[heir] = (byte)Math.Max(1, size); _guests.PartyHere[heir] = 0; }
                _guests.Member[s] = -1;
            }
            else
            {
                int prev = lead;
                while (prev >= 0 && _guests.Member[prev] != s) prev = _guests.Member[prev];
                if (prev >= 0) _guests.Member[prev] = _guests.Member[s];
                _guests.PartySize[lead] = (byte)Math.Max(1, _guests.PartySize[lead] - 1);
                if (_guests.PartyHere[lead] > _guests.PartySize[lead]) _guests.PartyHere[lead] = _guests.PartySize[lead];
                _guests.Member[s] = -1;
            }
            _guests.Lead[s] = s;
            _guests.PartySize[s] = 1;
            _guests.PartyHere[s] = 1;
        }

        /// <summary>LO-02, GU-13: a skier has boarded. A boarding is a lap: the lap's snow, its penalties and its wait land here.</summary>
        void OnBoarded(int lift, int s)
        {
            if (_guests.Arch[s] == GuestColumns.FeederArch) return;
            Metric(SimMetric.Boardings, true, lift, 1);
            ChargeWait(s, Tick);
            CloseLap(s);
            if (_guests.Laps[s] < byte.MaxValue) _guests.Laps[s]++;
            if (_guests.Laps[s] >= _guests.StayLaps[s]) _guests.Homeward[_guests.Lead[s]] = 1;
        }

        // ================= leaving (GU-06, GU-11) =================

        /// <summary>GU-06: the party gives up on the line and goes home (a WentHome, ledgered).</summary>
        void LeaveTheLine(int lead, long t)
        {
            int lift = _guests.Lift[lead];
            for (int s = lead; s >= 0; s = _guests.Member[s])
            {
                if (_guests.State[s] != (byte)GuestState.Queued) continue;
                if (lift >= 0) RemoveQueued(lift, s);
                ChargeWait(s, t);
                _wentHome[_guests.Arch[s]]++;
                CloseLap(s);
                Leave(s, t, SimEventType.SkierWentHome);
            }
        }

        /// <summary>GU-11: no open way down. It skis the natural ground to the lowest base and the tab counts it.</summary>
        void Strand(int lead, long t)
        {
            for (int s = lead; s >= 0; s = _guests.Member[s])
            {
                Delta(s, SatisfactionReason.Stranded, -Pacing.StrandedPenalty);
                _stranded[_guests.Arch[s]]++;
                CloseLap(s);
                Leave(s, t, SimEventType.SkierStranded);
            }
        }

        /// <summary>The visit is over at a base: its satisfaction goes to the day's verdict (GU-18) and the slot is freed.</summary>
        void Depart(int lead, long t)
        {
            for (int s = lead; s >= 0; s = _guests.Member[s])
            {
                CloseLap(s);
                Leave(s, t, SimEventType.SkierLeft);
            }
        }

        void Leave(int s, long t, SimEventType how)
        {
            if (!_guests.Alive(s)) return;      // a party's chain may hold a member that has already gone
            int a = _guests.Arch[s];
            if (a < Pacing.Archetypes)
            {
                _leftCount[a]++;
                _leftSatSum[a] += _guests.Sat[s];
            }
            if (_inspectSerial == _guests.Serial[s]) _inspectSerial = 0;
            if (how != SimEventType.SkierLeft) Events.Add(t, how, s, _guests.Serial[s], (ushort)a);
            _guests.Give(s);
        }

        /// <summary>
        /// GU-12: at the stop nobody is left on the hill. Everyone still out is counted as having left, the day's verdict
        /// moves each archetype's reputation (GU-18), and the hill is cleared for the night.
        /// </summary>
        void CloseTheDay()
        {
            long t = Tick;
            int atStop = 0;
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (!_guests.Alive(s)) continue;
                if (_guests.Arch[s] == GuestColumns.FeederArch) { _guests.Give(s); continue; }
                CloseLap(s);
                int a = _guests.Arch[s];
                if (a < Pacing.Archetypes) { _leftCount[a]++; _leftSatSum[a] += _guests.Sat[s]; }
                atStop++;
                _guests.Give(s);
            }
            // SI-18 at scale: one event for the sweep, not one per skier (a ring of 4,096 can't carry tens of thousands).
            if (atStop > 0) Events.Add(t, SimEventType.SkiersLeftAtStop, -1, atStop);
            for (int i = 0; i < _liftCount; i++)
            {
                _lineFirst[i] = _lineLast[i] = -1; _lineCount[i] = 0;
                _rideFirst[i] = _rideLast[i] = -1; _rideCount[i] = 0;
            }
            UpdateReputation();
            _guests.Reset();
            if (_onEdge.Length > 0) { Array.Clear(_onEdge, 0, _onEdge.Length); Array.Clear(_onEdgePrev, 0, _onEdgePrev.Length); }
            for (int k = 0; k < _nextEntryTick.Length; k++) _nextEntryTick[k] = 0;
        }

        /// <summary>LO-02: takes a skier out of a lift's line wherever it stands (it has given up, GU-06).</summary>
        void RemoveQueued(int lift, int s)
        {
            int prev = -1;
            for (int k = _lineFirst[lift]; k >= 0; k = _guests.LineNext[k])
            {
                if (k == s) { RemoveFromLine(lift, prev, s); _leftInLine[lift]++; return; }
                prev = k;
            }
        }

        // ================= reading (tests, the demo, 4.13) =================

        /// <summary>How many skiers are on the hill (GU-01).</summary>
        public int SkierCount => _guests?.Count ?? 0;

        /// <summary>How many skiers of each state are on the hill (the F1 panel; tests).</summary>
        public int SkiersInState(GuestState state)
        {
            if (_guests == null) return 0;
            int n = 0;
            for (int s = 0; s < _guests.Cap; s++) if (_guests.State[s] == (byte)state && _guests.Arch[s] != GuestColumns.FeederArch) n++;
            return n;
        }

        /// <summary>Every live skier's band and the edge or lift it is on (tests: the gate, the trajectories).</summary>
        public void ForEachSkier(Action<int, int, int, int, GuestState> each)
        {
            if (_guests == null) return;
            for (int s = 0; s < _guests.Cap; s++)
            {
                if (!_guests.Alive(s) || _guests.Arch[s] == GuestColumns.FeederArch) continue;
                each(_guests.Serial[s], _guests.Band[s], _guests.Edge[s], _guests.Lift[s], (GuestState)_guests.State[s]);
            }
        }
    }
}
