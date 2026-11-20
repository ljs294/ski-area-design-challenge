using System;

namespace MountainPlanner.Simulation
{
    public sealed partial class SimWorld
    {
        /// <summary>A node no band can ski home from.</summary>
        const int NoWayHome = int.MaxValue;

        // ---- per band and node, allocated when a design is folded ----
        /// <summary>GU-07: ticks from each node to the nearest base, per band (<see cref="NoWayHome"/> for none).</summary>
        int[] _dist = Array.Empty<int>();
        /// <summary>GU-11: the edge or lift to take from each node toward home, per band, or <see cref="NoOption"/>.</summary>
        int[] _homeStep = Array.Empty<int>();
        /// <summary>GU-07: the best board-only score of a run leaving each node, per band — what a lift's top is worth.</summary>
        int[] _bestAtNode = Array.Empty<int>();
        /// <summary>
        /// RD-3 (task 4.24 PR B): 1 where this band can get home from the node **without riding down** — by trails, paths
        /// and lifts upward, as before PR B. A party is offered the ride down only where this is 0 (rule 1: no fitting way
        /// down from the top).
        /// </summary>
        byte[] _skisHome = Array.Empty<byte>();
        /// <summary>Lifts by their top station, as a chain, so the reverse search can ride a lift upward.</summary>
        int[] _nodeTop = Array.Empty<int>(), _topNext = Array.Empty<int>();
        /// <summary>GU-05, GU-17: the band a skier must be to take each edge, and the band its sign claims.</summary>
        byte[] _gateBand = Array.Empty<byte>(), _fitBand = Array.Empty<byte>();
        /// <summary>The ticks a reference skier takes over each edge, and over each lift's ride.</summary>
        int[] _edgeCost = Array.Empty<int>(), _liftCost = Array.Empty<int>();

        // the heap the search runs on (allocated once, never per search)
        int[] _heapNode = Array.Empty<int>(), _heapPos = Array.Empty<int>();
        int _heapCount;

        /// <summary>GU-07: set when anything that could change a route changed; the guests row rebuilds once, next tick.</summary>
        bool _routeDirty = true;

        /// <summary>
        /// Something that could change where a band can go has changed: a lift opened or closed at the hour's flip, began
        /// downloading or rode down, a trail opened or closed (SN-20 v3), or the design moved. One flag, so a holiday's many
        /// changes cost one rebuild. A hold doesn't call it: a held lift's line stays joinable (LO-07), so routes don't change.
        /// </summary>
        void GuestBoardChanged() => _routeDirty = true;

        void AllocRouting(int nodes, int edges)
        {
            int perBand = Pacing.Bands * Math.Max(1, nodes);
            if (_dist.Length != perBand)
            {
                _dist = new int[perBand];
                _homeStep = new int[perBand];
                _bestAtNode = new int[perBand];
                _skisHome = new byte[perBand];
            }
            if (_heapNode.Length != nodes + 1) { _heapNode = new int[nodes + 1]; _heapPos = new int[Math.Max(1, nodes)]; }
            if (_gateBand.Length != edges) { _gateBand = new byte[edges]; _fitBand = new byte[edges]; _edgeCost = new int[edges]; }
            if (_liftCost.Length != Pacing.MaxLifts) _liftCost = new int[Pacing.MaxLifts];
            if (_nodeTop.Length != Math.Max(1, nodes)) _nodeTop = new int[Math.Max(1, nodes)];
            if (_topNext.Length != Pacing.MaxLifts) _topNext = new int[Pacing.MaxLifts];
            var net = _guestsNetwork;
            for (int k = 0; k < _nodeTop.Length; k++) _nodeTop[k] = -1;
            for (int i = LiftsNow - 1; i >= 0; i--)
            {
                var l = Design.Lifts[i];
                _liftCost[i] = MsToTicks(LiftKinds.Compressed(l.Kind) ? l.RideMs / Math.Max(1, Pacing.RideCompression) : l.RideMs, 1000);
                _topNext[i] = -1;
                int top = l.TopNode;
                if (top < 0 || net == null || top >= net.NodeCount) continue;
                _topNext[i] = _nodeTop[top];
                _nodeTop[top] = i;
            }
            if (net == null) return;
            for (int e = 0; e < edges; e++)
            {
                bool path = net.EdgeKindOf[e] == (byte)SimEdgeKind.Path;
                byte t = net.EdgeTrueBand[e], p = net.EdgePostedBand[e];
                // GU-17: the gate reads the harder of the sign and the truth; fit reads the sign. With no sign (an old
                // save), both read the truth and no misrate penalty can arise (4.21's R1, the owner's answer to Q3).
                byte posted = p == TrailEdgeFacts.NoPosting ? t : p;
                _gateBand[e] = path ? (byte)0 : (byte)Math.Max(t, posted);
                _fitBand[e] = path ? (byte)0 : posted;
                _edgeCost[e] = MsToTicks(net.EdgeBaseMs[e], 1000);
            }
            _routeDirty = true;
        }

        /// <summary>GU-17: whether this edge's trail is posted easier than it really is (a lap on it costs satisfaction).</summary>
        bool Misrated(int edge)
        {
            var net = _guestsNetwork;
            byte p = net.EdgePostedBand[edge];
            return p != TrailEdgeFacts.NoPosting && p < net.EdgeTrueBand[edge];
        }

        /// <summary>
        /// 4.07's seam: the quality of the snow on an edge for a band, thousandths. Until the snow model lands every open
        /// trail reads the same stand-in, so the choice weights are already in place for it to fill.
        /// </summary>
        const int StandInQuality = 900;

        internal int QualityOf(int edge, int band) =>
            EdgeTableLive && edge * Pacing.Bands + band < _edgeQuality.Length ? _edgeQuality[edge * Pacing.Bands + band] : StandInQuality;

        /// <summary>Whether this band may take this edge at all: its gate, and the trail being open (SN-20).</summary>
        bool EdgeAllowed(int edge, int band)
        {
            var net = _guestsNetwork;
            if (_gateBand[edge] > band) return false;
            return EdgeOpen(edge);
        }

        /// <summary>
        /// SN-20 v4: whether an edge of the guests' network is open to skiers: a path always; a trail's piece by its switch
        /// and its trail's board, and only once a lift's way down reaches the trail (see <see cref="TrailOpen"/>).
        /// </summary>
        bool EdgeOpen(int edge)
        {
            var net = _guestsNetwork;
            int t = net.EdgeTrail[edge];
            if (t < 0) return true;                       // a path is always open
            return EdgeReady(net, edge) && (t >= _trailReached.Length || _trailReached[t] != 0);
        }

        /// <summary>
        /// Whether a trail is open, by the number **the guests' network** gives it.
        ///
        /// The lifts row sizes <c>_trailReached</c> from the newest revision's network and empties it when a revision
        /// arrives before its network is built, while the guests keep standing on the last network they had (SI-21), so
        /// for that moment the two disagree about how many trails there are. Opening an older save posts its trails,
        /// which is one such revision, and the unguarded read crashed the worker thread on the owner's Gilford save
        /// (2026-11-09). Until the network catches up, readiness is the stand-in's "every built trail".
        /// </summary>
        bool TrailOpen(int t) =>
            t >= 0 && TrailReady(t) && (t >= _trailReached.Length || _trailReached[t] != 0);

        /// <summary>LO-02, LO-07: whether a skier could join this lift's line right now.</summary>
        bool LiftOpenToSkiers(int lift) => LineJoinable(lift, FaceMs % DayMs);

        /// <summary>
        /// GU-07's reachability and GU-11's way home, in one search per band, backwards from the bases over the edges and
        /// lifts that band may use. It gives three things a choice needs in O(1): whether a node can still be skied home
        /// from (so a beginner is never sent into a pod whose only way out is blue), which step to take when heading home,
        /// and what the runs leaving a node are worth (so a lift is worth what is at the top of it).
        /// </summary>
        void RebuildRoutes()
        {
            _routeDirty = false;
            var net = _guestsNetwork;
            if (net == null || _dist.Length == 0) return;
            int n = net.NodeCount;
            // RD-1: the lifts that carry riders down now. With none (a mountain of chairs, none downloading) the search is
            // exactly the one before PR B, run once per band.
            bool anyDown = false;
            for (int i = 0; i < LiftsNow && !anyDown; i++) anyDown = CarriesDown(i);
            for (int band = 0; band < Pacing.Bands; band++)
            {
                int at = band * n;
                SearchHome(net, band, at, n, rideDown: false);
                for (int k = 0; k < n; k++) _skisHome[at + k] = (byte)(_dist[at + k] != NoWayHome ? 1 : 0);
                if (anyDown) SearchHome(net, band, at, n, rideDown: true);
                // What the runs leaving each node are worth to this band, from the board alone (no crowd, no wait), so a
                // lift's score can be looked up rather than searched (A2: the choice stays O(its own edges)).
                for (int v = 0; v < n; v++)
                {
                    int best = 0;
                    int e0 = net.FirstEdge[v];
                    if (e0 >= 0)
                        for (int e = e0; e < e0 + net.EdgeCount[v]; e++)
                        {
                            if (net.EdgeKindOf[e] != (byte)SimEdgeKind.TrailSegment) continue;
                            if (!EdgeAllowed(e, band)) continue;
                            if (_dist[at + net.EdgeTo[e]] == NoWayHome) continue;
                            int score = BoardScore(e, band);
                            if (score > best) best = score;
                        }
                    _bestAtNode[at + v] = best;
                }
                // Task 4.24 PR B: a node is also worth what an open lift leaving it leads to, so a lift that only feeds
                // another (a gondola to a mid-station whose one way on is a chair) is worth that chair's runs, not
                // nothing. Without it the arrival gate (GU-05) turned every band away from the gondola's bottom. Lift
                // chains are short: one pass per lift reaches the fixed point.
                for (int pass = 0; pass < LiftsNow; pass++)
                {
                    bool changed = false;
                    for (int i = 0; i < LiftsNow; i++)
                    {
                        if (!LiftOpenToSkiers(i)) continue;
                        int bottom = Design.Lifts[i].BottomNode, top = Design.Lifts[i].TopNode;
                        if (bottom < 0 || bottom >= n || top < 0 || top >= n || _dist[at + top] == NoWayHome) continue;
                        if (_bestAtNode[at + top] > _bestAtNode[at + bottom]) { _bestAtNode[at + bottom] = _bestAtNode[at + top]; changed = true; }
                    }
                    if (!changed) break;
                }
            }
        }

        /// <summary>
        /// One band's search backwards from the bases. With <paramref name="rideDown"/>, a lift that carries riders down
        /// (RD-1) is also an edge from its top to its bottom (RD-3: the ride down counts on the way home).
        /// </summary>
        void SearchHome(SimNetwork net, int band, int at, int n, bool rideDown)
        {
            for (int k = 0; k < n; k++) { _dist[at + k] = NoWayHome; _homeStep[at + k] = NoOption; }
            _heapCount = 0;
            for (int k = 0; k < _baseCount; k++)
            {
                int b = _baseNode[k];
                if (b < 0 || b >= n || _dist[at + b] == 0) continue;
                _dist[at + b] = 0;
                HeapPush(at, b);
            }
            while (_heapCount > 0)
            {
                int v = HeapPop(at);
                int dv = _dist[at + v];
                // Edges that run into v: skiing one takes you from its from-node to v.
                for (int k = 0; k < net.IntoCount[v]; k++)
                {
                    int e = net.IntoEdge[net.IntoFirst[v] + k];
                    if (!EdgeAllowed(e, band)) continue;
                    Relax(at, net.EdgeFrom[e], dv + _edgeCost[e], e, v, n);
                }
                // A lift whose top is v: riding it takes you from its bottom to v (LO-10's way down, per band).
                for (int i = _nodeTop[v]; i >= 0; i = _topNext[i])
                {
                    if (!LiftOpenToSkiers(i)) continue;
                    int from = Design.Lifts[i].BottomNode;
                    if (from < 0 || from >= n) continue;
                    Relax(at, from, dv + _liftCost[i], LiftOption | i, v, n);
                }
                // RD-3: a lift whose bottom is v and which carries riders down: riding it takes you from its top to v.
                if (!rideDown) continue;
                for (int i = v < _nodeBottom.Length ? _nodeBottom[v] : -1; i >= 0; i = _bottomNext[i])
                {
                    if (i >= LiftsNow || !CarriesDown(i)) continue;
                    int from = Design.Lifts[i].TopNode;
                    if (from < 0 || from >= n) continue;
                    Relax(at, from, dv + _liftCost[i], DownOption | i, v, n);
                }
            }
        }

        void Relax(int at, int u, int cost, int step, int toward, int n)
        {
            if (u < 0 || u >= n || cost < 0) return;
            if (cost >= _dist[at + u]) return;
            _dist[at + u] = cost;
            _homeStep[at + u] = step;
            HeapPush(at, u);
        }

        // ---- a binary heap over nodes, keyed by distance; the arrays are allocated once ----

        void HeapPush(int at, int node)
        {
            int i = _heapPos[node];
            if (i > 0 && i <= _heapCount && _heapNode[i] == node) { HeapUp(at, i); return; }
            _heapNode[++_heapCount] = node;
            _heapPos[node] = _heapCount;
            HeapUp(at, _heapCount);
        }

        int HeapPop(int at)
        {
            int top = _heapNode[1];
            _heapPos[top] = 0;
            _heapNode[1] = _heapNode[_heapCount--];
            if (_heapCount > 0) { _heapPos[_heapNode[1]] = 1; HeapDown(at, 1); }
            return top;
        }

        void HeapUp(int at, int i)
        {
            while (i > 1)
            {
                int p = i >> 1;
                if (_dist[at + _heapNode[p]] <= _dist[at + _heapNode[i]]) break;
                Swap(i, p);
                i = p;
            }
        }

        void HeapDown(int at, int i)
        {
            while (true)
            {
                int l = i << 1, r = l + 1, best = i;
                if (l <= _heapCount && _dist[at + _heapNode[l]] < _dist[at + _heapNode[best]]) best = l;
                if (r <= _heapCount && _dist[at + _heapNode[r]] < _dist[at + _heapNode[best]]) best = r;
                if (best == i) break;
                Swap(i, best);
                i = best;
            }
        }

        void Swap(int i, int j)
        {
            int a = _heapNode[i], b = _heapNode[j];
            _heapNode[i] = b; _heapNode[j] = a;
            _heapPos[b] = i; _heapPos[a] = j;
        }

        /// <summary>GU-07: whether this band can still ski home from a node (the dead-end rule).</summary>
        /// <summary>RD-3: whether this band can get home from a node without riding a lift down.</summary>
        bool SkisHome(int node, int band)
        {
            if (_guestsNetwork == null || node < 0 || node >= _guestsNetwork.NodeCount) return false;
            return _skisHome[band * _guestsNetwork.NodeCount + node] != 0;
        }

        bool CanReachBase(int node, int band)
        {
            if (_guestsNetwork == null || node < 0 || node >= _guestsNetwork.NodeCount) return false;
            return _dist[band * _guestsNetwork.NodeCount + node] != NoWayHome;
        }

        /// <summary>
        /// What the network looks like to a band: how many nodes it can ski home from, and how many of the places it
        /// could arrive at offer it anything at all. A band with arrival places that offer it nothing is a mountain
        /// where part of the crowd stands about all day, which is invisible unless it is counted.
        /// </summary>
        public (int bases, int reachable, int nodes, int usableBases) NetworkFor(int band)
        {
            var net = _guestsNetwork;
            if (net == null || _dist.Length == 0 || band < 0 || band >= Pacing.Bands) return (0, 0, 0, 0);
            int n = net.NodeCount, at = band * n, reachable = 0;
            for (int k = 0; k < n; k++) if (_dist[at + k] != NoWayHome) reachable++;
            int usable = 0;
            for (int k = 0; k < _baseCount; k++) if (HasSomethingToDo(_baseNode[k], band)) usable++;
            return (_baseCount, reachable, n, usable);
        }

        /// <summary>
        /// The guests' own reading of the network, for a line in the log: how many edges each band may take, how many
        /// run into an arrival place, and how far the search got. When a mountain's crowd has nothing to do, this says
        /// whether it is the gate, the trails being shut, or the search failing to find its way home.
        /// </summary>
        public string GuestNetworkReport()
        {
            var net = _guestsNetwork;
            if (net == null) return "[GuestRouting] no network";
            var sb = new System.Text.StringBuilder();
            sb.Append("[GuestRouting] ").Append(net.EdgeFrom.Length).Append(" edges, ").Append(net.NodeCount).Append(" nodes, ")
              .Append(_baseCount).Append(" bases, ").Append(_trailReached.Length).Append(" trails (")
              .Append(ReachedTrails()).Append(" reached)");
            for (int k = 0; k < _baseCount; k++)
            {
                int b = _baseNode[k];
                sb.Append("; base ").Append(b).Append(" in=").Append(b < net.NodeCount ? net.IntoCount[b] : -1)
                  .Append(" out=").Append(b < net.NodeCount ? net.EdgeCount[b] : -1);
            }
            for (int band = 0; band < Pacing.Bands; band++)
            {
                int allowed = 0, gated = 0, shut = 0;
                for (int e = 0; e < net.EdgeFrom.Length; e++)
                {
                    if (_gateBand[e] > band) { gated++; continue; }
                    if (!EdgeOpen(e)) { shut++; continue; }
                    allowed++;
                }
                var r = NetworkFor(band);
                sb.Append("; band ").Append(band).Append(": allowed ").Append(allowed).Append(", too hard ").Append(gated)
                  .Append(", shut ").Append(shut).Append(", can ski home from ").Append(r.reachable);
            }
            return sb.ToString();
        }

        int ReachedTrails()
        {
            int n = 0;
            for (int k = 0; k < _trailReached.Length; k++) if (_trailReached[k] != 0) n++;
            return n;
        }

        /// <summary>Whether this band has any run or lift it may take from a node (GU-07's options, without the crowd).</summary>
        bool HasSomethingToDo(int node, int band)
        {
            var net = _guestsNetwork;
            if (net == null || node < 0 || node >= net.NodeCount) return false;
            int e0 = net.FirstEdge[node];
            if (e0 >= 0)
                for (int e = e0; e < e0 + net.EdgeCount[node]; e++)
                    if (EdgeAllowed(e, band) && CanReachBase(net.EdgeTo[e], band)) return true;
            for (int i = node < _nodeBottom.Length ? _nodeBottom[node] : -1; i >= 0; i = _bottomNext[i])
            {
                if (!LiftOpenToSkiers(i)) continue;
                int top = Design.Lifts[i].TopNode;
                if (top >= 0 && top < net.NodeCount && CanReachBase(top, band) && _bestAtNode[band * net.NodeCount + top] > 0)
                    return true;
            }
            return false;
        }

        /// <summary>GU-11: the step home from a node for this band, or <see cref="NoOption"/>.</summary>
        int HomeOption(int lead, int node)
        {
            if (_guestsNetwork == null || node < 0 || node >= _guestsNetwork.NodeCount) return NoOption;
            int band = _guests.Band[lead];
            int step = _homeStep[band * _guestsNetwork.NodeCount + node];
            if (step == NoOption) return NoOption;
            // The step may be a lift that has just closed to boarding; then there is no way home from here.
            if ((step & DownOption) != 0) return CarriesDown(step & ~DownOption) ? step : NoOption;   // RD-3
            if ((step & LiftOption) != 0 && !LiftOpenToSkiers(step & ~LiftOption)) return NoOption;
            if ((step & LiftOption) == 0 && !EdgeAllowed(step, band)) return NoOption;
            return step;
        }
    }
}
