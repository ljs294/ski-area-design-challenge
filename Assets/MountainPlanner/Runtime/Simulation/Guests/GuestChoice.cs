using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// GU-07: the factors a choice is the product of. The ids are stable so the skier card can say *why* it went that way
    /// and a later term (an amenity's pull, a park, fatigue, the walk home) is a new id rather than a new shape.
    /// </summary>
    public enum ChoiceFactor : ushort
    {
        None = 0,
        /// <summary>How well the run's posted rating suits this band: beginners want gentle, experts want steep.</summary>
        Fit = 1,
        /// <summary>The snow on it, with this band's taste (4.07 fills the conditions).</summary>
        Snow = 2,
        /// <summary>How wide it is (beginners and intermediates care; the rest don't).</summary>
        Width = 3,
        /// <summary>How many skiers are already on it, against its comfortable density.</summary>
        Crowd = 4,
        /// <summary>For a lift: how long its line is (LO-04's readout).</summary>
        Wait = 5,
        Amenity = 6,
        Park = 7,
        Fatigue = 8,
        Homing = 9,
    }

    public sealed partial class SimWorld
    {
        /// <summary>How many factors a choice records (every <see cref="ChoiceFactor"/>, by id).</summary>
        public const int ChoiceFactors = 12;

        int[] _optEdge = Array.Empty<int>(), _optScore = Array.Empty<int>();
        int _optCount;
        /// <summary>The factors of the last choice the inspected skier made, permille, for the skier card's "why".</summary>
        int[] _whyFactors = new int[ChoiceFactors];
        int _whyOption = NoOption;

        void AllocChoice(int nodes)
        {
            var net = _guestsNetwork;
            int maxDegree = 0;
            if (net != null)
                for (int v = 0; v < nodes; v++) if (net.EdgeCount[v] > maxDegree) maxDegree = net.EdgeCount[v];
            int room = maxDegree + Pacing.MaxLifts + 1;
            if (_optEdge.Length < room) { _optEdge = new int[room]; _optScore = new int[room]; }
        }

        /// <summary>The band every member of the party may ski: its weakest member's (GU-05's gate by the weakest).</summary>
        int PartyBand(int lead)
        {
            int band = _guests.Band[lead];
            for (int s = _guests.Member[lead]; s >= 0; s = _guests.Member[s]) if (_guests.Band[s] < band) band = _guests.Band[s];
            return band;
        }

        /// <summary>
        /// GU-07 v1: what the party does next. Every run and lift leaving the node is scored
        /// **fit × snow × width × crowd** (a lift by what is at the top of it, over its wait), and the pick is a keyed
        /// weighted draw. Three things never happen: a run above the party's band (GU-05's hard gate), a run into a pod
        /// this band cannot ski home from (GU-07's reachability), and a lift that is not taking skiers.
        /// </summary>
        int BestOption(int lead, int node, long t)
        {
            var net = _guestsNetwork;
            if (net == null || node < 0 || node >= net.NodeCount) return NoOption;
            int band = PartyBand(lead);
            int size = _guests.PartySize[lead];
            _optCount = 0;
            long total = 0;
            int e0 = net.FirstEdge[node];
            if (e0 >= 0)
                for (int e = e0; e < e0 + net.EdgeCount[node] && _optCount < _optEdge.Length; e++)
                {
                    if (!EdgeAllowed(e, band)) continue;
                    if (!CanReachBase(net.EdgeTo[e], band)) continue;     // never into a pod it can't leave
                    int score = EdgeScore(e, band);
                    if (score <= 0) continue;
                    _optEdge[_optCount] = e; _optScore[_optCount] = score; _optCount++;
                    total += score;
                }
            // Lifts whose bottom is this node (an Area's lifts all stand at one station, NW-15).
            int bestWaitMs = int.MaxValue, bestWaitLift = -1;
            int liftFrom = _optCount;
            for (int i = _nodeBottom.Length > node ? _nodeBottom[node] : -1; i >= 0 && _optCount < _optEdge.Length; i = _bottomNext[i])
            {
                if (!LiftOpenToSkiers(i)) continue;
                int top = Design.Lifts[i].TopNode;
                if (top < 0 || top >= net.NodeCount) continue;
                if (!CanReachBase(top, band)) continue;                   // LO-10: no way down for this band up there
                int atTop = _bestAtNode[band * net.NodeCount + top];
                if (atTop <= 0) continue;
                long waitMs = WaitMsOf(i);
                if (waitMs < bestWaitMs) { bestWaitMs = (int)Math.Min(int.MaxValue, waitMs); bestWaitLift = i; }
                int score = (int)Math.Max(1, (long)atTop * WaitFactor(waitMs) / 1000);
                _optEdge[_optCount] = LiftOption | i; _optScore[_optCount] = score; _optCount++;
                total += score;
            }
            // GU-08: a lift whose wait is past BalkWait is dropped while anything else is on offer, which is balking: the
            // party walks to the other lift in the Area, or skis somewhere else. With nothing else it joins and waits.
            if (bestWaitMs != int.MaxValue && bestWaitMs > BalkWaitMs)
            {
                int kept = 0;
                long keptTotal = 0;
                for (int k = 0; k < _optCount; k++)
                {
                    bool isLift = (_optEdge[k] & LiftOption) != 0;
                    bool over = isLift && WaitMsOf(_optEdge[k] & ~LiftOption) > BalkWaitMs;
                    if (over) continue;
                    _optEdge[kept] = _optEdge[k]; _optScore[kept] = _optScore[k]; kept++;
                    keptTotal += _optScore[k];
                }
                if (kept > 0)
                {
                    if (kept != _optCount) Events.Add(t, SimEventType.SkierBalked, lead, bestWaitMs / 1000);
                    _optCount = kept; total = keptTotal;
                }
                else if (bestWaitLift >= 0)
                {
                    _optEdge[0] = LiftOption | bestWaitLift; _optScore[0] = 1; _optCount = 1; total = 1;
                }
            }
            if (_optCount == 0 || total <= 0) return NoOption;
            // SI-06: a keyed weighted draw, on the party and its own count of choices (never a running counter).
            int ordinal = _guests.Ordinal[lead]++;
            for (int s = _guests.Member[lead]; s >= 0; s = _guests.Member[s]) _guests.Ordinal[s]++;
            ulong key = SimKey.Hash(SeasonSeed, SimKey.ChoiceTag, (ulong)(uint)_guests.PartyId[lead], (ulong)(uint)ordinal);
            long pick = (long)(key % (ulong)total);
            int chosen = _optEdge[_optCount - 1];
            for (int k = 0; k < _optCount; k++)
            {
                pick -= _optScore[k];
                if (pick < 0) { chosen = _optEdge[k]; break; }
            }
            if (_inspectSerial != 0 && _guests.Serial[lead] == _inspectSerial) RecordWhy(chosen, band);
            return chosen;
        }

        /// <summary>GU-07: the board-only part of a run's score (no crowd, no wait): what a lift's top is worth.</summary>
        int BoardScore(int edge, int band)
        {
            long v = FitFactor(edge, band);
            v = v * SnowFactor(edge, band) / 1000;
            v = v * WidthFactor(edge, band) / 1000;
            return (int)Math.Max(1, v);
        }

        int EdgeScore(int edge, int band)
        {
            var net = _guestsNetwork;
            if (net.EdgeKindOf[edge] == (byte)SimEdgeKind.Path) return 400;   // a path is a way, not a draw
            long v = BoardScore(edge, band);
            v = v * CrowdFactor(edge) / 1000;
            return (int)Math.Max(1, v);
        }

        /// <summary>GU-07: 1 − |the sign's band − its own| ÷ 2, floored so an expert can still ski a green home.</summary>
        int FitFactor(int edge, int band)
        {
            int gap = Math.Abs(_fitBand[edge] - band);
            int v = 1000 - gap * 500;
            return v < Pacing.FitFloor ? Pacing.FitFloor : v;
        }

        /// <summary>GU-07: the snow, with the band's taste. 4.07's conditions fill the taste; until then it is flat.</summary>
        int SnowFactor(int edge, int band) => QualityOf(edge, band);

        /// <summary>GU-07: beginners and intermediates want room; the rest don't mind.</summary>
        int WidthFactor(int edge, int band)
        {
            if (band > 1) return 1000;
            int cm = _guestsNetwork.EdgeWidthCm[edge];
            int v = cm * 1000 / 3048;                      // 100 ft is 1.0
            return v < 500 ? 500 : v > 1200 ? 1200 : v;
        }

        /// <summary>GU-07: 1 ÷ (1 + skiers ÷ its comfortable density), from last tick's counters.</summary>
        int CrowdFactor(int edge)
        {
            int cap = _edgeCrowd[edge];
            int on = _onEdgePrev[edge];
            return (int)((long)cap * 1000 / (cap + on));
        }

        /// <summary>GU-07: 1 ÷ (1 + the wait ÷ five minutes), from LO-04's readout.</summary>
        int WaitFactor(long waitMs)
        {
            const long FiveMinutesMs = 5 * 60 * 1000;
            return (int)(FiveMinutesMs * 1000 / (FiveMinutesMs + Math.Max(0, waitMs)));
        }

        /// <summary>GU-02: the factors behind the inspected skier's last choice, for the card's "why".</summary>
        void RecordWhy(int option, int band)
        {
            Array.Clear(_whyFactors, 0, _whyFactors.Length);
            _whyOption = option;
            if ((option & LiftOption) != 0)
            {
                int lift = option & ~LiftOption;
                _whyFactors[(int)ChoiceFactor.Wait] = WaitFactor(WaitMsOf(lift));
                return;
            }
            _whyFactors[(int)ChoiceFactor.Fit] = FitFactor(option, band);
            _whyFactors[(int)ChoiceFactor.Snow] = SnowFactor(option, band);
            _whyFactors[(int)ChoiceFactor.Width] = WidthFactor(option, band);
            _whyFactors[(int)ChoiceFactor.Crowd] = CrowdFactor(option);
        }
    }
}
