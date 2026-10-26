using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The ski network in the game (task P3-10; NW-01 to NW-13, PA-05 to PA-08; owner NT1–NT12, findings 1–7, R2-1–R2-3):
    /// rebuilt off the main thread whenever the built work is (never stored), it fills Analysis › Network and the status
    /// bar's counts, and while that tab is open draws the Network view (MapLayers' grey relief, trees away, the diagram)
    /// with its picks and hover.
    /// </summary>
    public sealed partial class MountainViewer
    {
        NetworkView _networkView;
        TrailNetworkGeometry _netGeometry;
        EarthworkSite _netSite;
        Task<NetworkReport> _networkJob;
        DesignDocument _networkJobDoc, _networkPending;
        NetworkReport _network;
        DesignDocument _networkDoc;
        bool _networkWired, _networkOn;
        readonly Dictionary<int, (ulong Trail, int StartMm)> _edgeKeys = new Dictionary<int, (ulong, int)>();

        // The pick (findings 2, 7; R2-2): a trail segment (by its trail and start, so it survives a rebuild), a lift or a node.
        ulong _pickTrail, _pickLift, _pickNode;
        int _pickStartMm = -1;
        ulong _netRowHover;
        Vector2 _netPress;
        bool _netPressed;

        /// <summary>The network last built (tests and captures); null until the first build.</summary>
        internal NetworkReport Network => _network;
        /// <summary>True while a network build is running or waiting (captures wait for it).</summary>
        bool NetworkBusy => _networkJob != null || _networkPending != null;

        /// <summary>Asks for the network of <paramref name="doc"/> (from UpdateBuiltWork, once the edits are laid).</summary>
        void RequestNetwork(DesignDocument doc)
        {
            WireNetwork();
            if (_networkJob != null) { _networkPending = doc; return; }
            StartNetwork(doc);
        }

        void StartNetwork(DesignDocument doc)
        {
            var earthwork = _edits?.Earthwork;
            if (earthwork == null || _builtFor == null) return;
            var site = earthwork.Site;
            if (_netGeometry == null || !SameSite(site, _netSite))
            {
                _netSite = site;
                _netGeometry = new TrailNetworkGeometry(site, _builtFor.Pipeline.Heights.Source, null,
                                                        t => earthwork.DetailOf(t.Id.Value) as TrailBuild);
            }
            var geometry = _netGeometry;
            _networkJobDoc = doc;
            _networkJob = Task.Run(() => NetworkReport.Build(doc, geometry));
        }

        static bool SameSite(EarthworkSite a, EarthworkSite b) =>
            a.Lattice.West == b.Lattice.West && a.Lattice.North == b.Lattice.North && a.OriginX == b.OriginX && a.OriginY == b.OriginY;

        void WireNetwork()
        {
            if (_networkWired || Hud == null) return;
            _networkWired = true;
            Hud.NetworkViewChanged += on => { _networkOn = on; ShowNetworkView(); };
            Hud.NetworkLiftChosen += id => PickNetwork(0, -1, id, 0, true);
            Hud.NetworkTrailChosen += id => PickNetwork(id, -1, 0, 0, true);   // from its head (no point clicked)
            Hud.NetworkRowHovered += id => { _netRowHover = id; _networkView?.SetHover(-1, LiftRow(id), 0, LiftRow(id) == 0 ? id : 0); };
            Hud.CardClosed += () => { if (_pickTrail != 0 || _pickLift != 0) ClearNetworkPick(); };   // NT11: the card's ✕
        }

        ulong LiftRow(ulong id) => _network != null && _network.Of(new LiftId(id)) != null ? id : 0;

        /// <summary>Each frame (after the camera moved): takes a finished build, follows the camera, hover and clicks.</summary>
        void TickNetwork(UnityEngine.Camera cam)
        {
            if (_networkJob != null && _networkJob.IsCompleted)
            {
                var job = _networkJob;
                var doc = _networkJobDoc;
                _networkJob = null;
                if (job.Status == TaskStatus.RanToCompletion) ApplyNetwork(doc, job.Result);
                else Debug.LogException(job.Exception);
                if (_networkPending != null)
                {
                    var next = _networkPending;
                    _networkPending = null;
                    StartNetwork(next);
                }
            }
            if (_networkView == null || !_networkOn || cam == null) return;
            HoverAndClickNetwork(cam);
            _networkView.Follow(cam);
        }

        void ShowNetworkView()
        {
            _layers?.SetNetworkView(_networkOn);
            if (_networkOn && _networkView == null && _resort != null)
            {
                _networkView = new NetworkView(_resort.Root.transform);
                if (_network != null) ApplyNetwork(_networkDoc, _network);
            }
            if (_networkView != null) _networkView.Visible = _networkOn;
            _mapLines?.SetVisible(!_networkOn && _liftsShown);   // the diagram takes the lifts' map lines' place
            if (!_networkOn) ClearNetworkPick();
        }

        // ---------- the network into the lists, the bar and the diagram ----------

        void ApplyNetwork(DesignDocument doc, NetworkReport report)
        {
            if (report == null || doc == null) return;
            _network = report;
            _networkDoc = doc;
            var graph = report.Graph;
            var site = _netSite;

            // The lists and the bar.
            var liftRows = new List<NetworkLiftRow>();
            foreach (var s in report.Lifts)
            {
                var l = doc.FindLift(s.Lift);
                liftRows.Add(new NetworkLiftRow { Id = s.Lift.Value, Name = l?.Name ?? "Lift", Reached = s.Reaches.Count, Connected = s.Connected });
            }
            var warnRows = new List<NetworkTrailRow>();
            foreach (var t in report.Trails)
            {
                if (t.Warning == NetworkWarning.None) continue;
                var trail = doc.FindTrail(t.Trail);
                double rating = double.NaN;
                if (_trailLayer != null && _trailLayer.TryGetFacts(t.Trail, out var f)) rating = f.Rating;
                warnRows.Add(new NetworkTrailRow { Id = t.Trail.Value, Name = trail?.Name ?? "Trail", RatingPercent = rating, Warning = NetworkReport.Text(t.Warning) });
            }
            int junctions = 0, paths = 0;
            foreach (var n in graph.Nodes) if (n.Kind == NodeKind.Junction) junctions++;
            var pathOwners = new HashSet<ulong>();
            foreach (var e in graph.Edges) if (e.Kind == EdgeKind.Path) pathOwners.Add(e.Owner);
            paths = pathOwners.Count;
            if (Hud != null)
            {
                Hud.SetNetwork(liftRows, warnRows, report.Trails.Count, junctions, paths);
                Hud.SetNetworkCounts(report.Lifts.Count, report.LiftWarnings, report.Trails.Count, report.TrailWarnings);
            }

            // The diagram (only while the view exists: it's made when the tab first opens).
            _edgeKeys.Clear();
            for (int i = 0; i < graph.Edges.Count; i++)
                if (graph.Edges[i].Kind == EdgeKind.TrailSegment) _edgeKeys[i] = (graph.Edges[i].Owner, graph.Edges[i].StartMm);
            if (_networkView == null || _resort == null) return;
            var surface = _resort.Surface;
            Vector3 Drape(double latticeX, double latticeY)
            {
                float x = (float)(latticeX - site.OriginX), z = (float)(latticeY - site.OriginY);
                float y = surface.HeightAt(x, z);
                return new Vector3(x, float.IsNaN(y) ? 0 : y + 2, z);
            }
            Vector3 DrapePoint(PointMm p) => Drape(site.OriginX + p.XMetres, site.OriginY + p.YMetres);

            var segments = new List<NetworkView.Segment>();
            var lastOf = new Dictionary<ulong, (int Edge, Vector3[] Points)>();
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                var e = graph.Edges[i];
                if (e.Kind != EdgeKind.TrailSegment) continue;
                var trail = doc.FindTrail(new TrailId(e.Owner));
                if (trail == null) continue;
                var line = _netGeometry.CentrelineOf(trail);
                var pts = Stretch(line, e.StartMm / 1000.0, e.EndMm / 1000.0, Drape);
                double rating = double.NaN;
                if (_trailLayer != null && _trailLayer.TryGetFacts(trail.Id, out var f)) rating = f.Rating;
                var status = report.Of(trail.Id);
                segments.Add(new NetworkView.Segment { Edge = i, Trail = e.Owner, Points = pts, Own = NetworkView.RatingColour(rating), Served = status == null || status.Served });
                if (!lastOf.TryGetValue(e.Owner, out var last) || graph.Edges[last.Edge].EndMm < e.EndMm) lastOf[e.Owner] = (i, pts);
            }
            var lifts = new List<NetworkView.LiftLine>();
            var liftEnds = new Dictionary<ulong, (ulong Lift, bool Bottom)>();
            foreach (var s in report.Lifts)
            {
                var l = doc.FindLift(s.Lift);
                if (l == null) continue;
                lifts.Add(new NetworkView.LiftLine { Id = s.Lift.Value, Bottom = DrapePoint(l.Bottom), Top = DrapePoint(l.Top), Connected = s.Connected });
                liftEnds[DesignNetwork.LiftBottomNode(doc.Seed, s.Lift)] = (s.Lift.Value, true);
                liftEnds[DesignNetwork.LiftTopNode(doc.Seed, s.Lift)] = (s.Lift.Value, false);
            }
            var deadTails = new HashSet<ulong>();
            var deadEnds = new List<NetworkView.DeadEnd>();
            foreach (var t in report.Trails)
            {
                if (!t.LeadsNowhere || !lastOf.TryGetValue(t.Trail.Value, out var last) || last.Points.Length < 2) continue;
                deadTails.Add(graph.Edges[last.Edge].To);
                var b = last.Points[last.Points.Length - 1];
                var a = last.Points[last.Points.Length - 2];
                deadEnds.Add(new NetworkView.DeadEnd { At = b, Dir = (b - a).normalized });
            }
            var nodes = new List<NetworkView.Node>();
            foreach (var n in graph.Nodes)
            {
                var kind = n.Kind == NodeKind.Junction ? NetworkView.NodeKind.Junction : n.Kind == NodeKind.PathEnd ? NetworkView.NodeKind.PathEnd
                    : n.Kind == NodeKind.LiftBottom || n.Kind == NodeKind.LiftTop ? NetworkView.NodeKind.LiftEnd : NetworkView.NodeKind.RunEnd;
                liftEnds.TryGetValue(n.Id, out var le);
                nodes.Add(new NetworkView.Node { Id = n.Id, Kind = kind, Lift = le.Lift, Bottom = le.Bottom, DeadEnd = deadTails.Contains(n.Id), At = DrapePoint(n.At) });
            }
            var links = new List<NetworkView.Link>();
            var seen = new HashSet<ulong>();
            foreach (var e in graph.Edges)
            {
                if (e.Kind != EdgeKind.Path || !seen.Add(e.Owner)) continue;
                if (graph.TryGetNode(e.From, out var a) && graph.TryGetNode(e.To, out var b)) links.Add(new NetworkView.Link { A = DrapePoint(a.At), B = DrapePoint(b.At) });
            }
            _networkView.Set(segments.ToArray(), lifts.ToArray(), nodes.ToArray(), links.ToArray(), deadEnds.ToArray());
            ApplyNetworkPick();
        }

        /// <summary>The centreline (lattice x, y pairs) between two distances along it, every 15 m or closer, draped.</summary>
        static Vector3[] Stretch(IReadOnlyList<double> line, double from, double to, Func<double, double, Vector3> drape)
        {
            var pts = new List<Vector3>();
            double run = 0;
            for (int i = 0; i + 3 < line.Count; i += 2)
            {
                double ax = line[i], ay = line[i + 1], bx = line[i + 2], by = line[i + 3];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                double s0 = run, s1 = run + len;
                run = s1;
                if (len <= 0 || s1 < from || s0 > to) continue;
                double f0 = Math.Max(0, (from - s0) / len), f1 = Math.Min(1, (to - s0) / len);
                int steps = Math.Max(1, (int)Math.Ceiling((f1 - f0) * len / 15));
                for (int k = pts.Count == 0 ? 0 : 1; k <= steps; k++)
                {
                    double f = f0 + (f1 - f0) * k / steps;
                    pts.Add(drape(ax + (bx - ax) * f, ay + (by - ay) * f));
                }
            }
            return pts.ToArray();
        }

        // ---------- picks and hover (findings 2, 6, 7; R2-2, R2-3; NT11) ----------

        int PickedEdge()
        {
            if (_pickTrail == 0) return -1;
            foreach (var kv in _edgeKeys) if (kv.Value.Trail == _pickTrail && kv.Value.StartMm == _pickStartMm) return kv.Key;
            return -1;
        }

        /// <summary>
        /// Picks a trail (by <paramref name="edge"/>, or from its head when -1), a lift or a node, or clears with nothing;
        /// a lift or trail opens its card, a node closes any. A second pick of the same thing clears it.
        /// </summary>
        void PickNetwork(ulong trail, int edge, ulong lift, ulong node, bool fromList = false)
        {
            if (_network == null) return;
            int startMm = -1;
            if (trail != 0)
            {
                if (edge < 0)
                {
                    var r = _network.Graph.Edges;
                    for (int i = 0; i < r.Count; i++) if (r[i].Kind == EdgeKind.TrailSegment && r[i].Owner == trail) { edge = i; break; }
                }
                if (edge >= 0) startMm = _network.Graph.Edges[edge].StartMm;
            }
            bool same = (trail != 0 && trail == _pickTrail && startMm == _pickStartMm) || (lift != 0 && lift == _pickLift) || (node != 0 && node == _pickNode);
            // The cards close first (their CardClosed clears the old pick), then the new pick, then its card.
            Hud?.CloseTrailCardIfOpen();
            Hud?.CloseLiftCardIfOpen();
            _pickTrail = same ? 0 : trail;
            _pickStartMm = same ? -1 : startMm;
            _pickLift = same ? 0 : lift;
            _pickNode = same ? 0 : node;
            if (_pickTrail != 0) Hud?.OpenTrailCardFor(_pickTrail);
            if (_pickLift != 0) Hud?.OpenLiftCardFor(_pickLift);
            ApplyNetworkPick();
        }

        /// <summary>Clears the pick (Esc, empty ground, the card's ✕); true when there was one.</summary>
        bool ClearNetworkPick()
        {
            if (_pickTrail == 0 && _pickLift == 0 && _pickNode == 0) return false;
            _pickTrail = _pickLift = _pickNode = 0;
            _pickStartMm = -1;
            Hud?.CloseTrailCardIfOpen();
            Hud?.CloseLiftCardIfOpen();
            ApplyNetworkPick();
            return true;
        }

        void ApplyNetworkPick()
        {
            Hud?.SetNetworkFocused(_pickLift != 0 ? _pickLift : _pickTrail);
            if (_networkView == null || _network == null) return;
            int edge = PickedEdge();
            NetworkReport.Reach reach = null;
            if (edge >= 0) reach = _network.ReachFromSegment(edge);
            else if (_pickLift != 0) reach = _network.ReachFrom(new LiftId(_pickLift));
            else if (_pickNode != 0) reach = _network.ReachFrom(_pickNode);
            var lifts = new List<ulong>();
            if (reach != null) foreach (var l in reach.Lifts) lifts.Add(l.Value);
            _networkView.SetPick(edge, _pickLift, _pickNode, reach?.Segments, lifts);
        }

        void HoverAndClickNetwork(UnityEngine.Camera cam)
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || PhotoOpen || (_tools != null && _tools.Armed)) { _networkView.SetHover(-1, 0, 0, 0); return; }
            var pos = mouse.position.ReadValue();
            bool overUi = PointerOverUi(pos);
            var hit = overUi ? new NetworkView.Hit { Edge = -1 } : _networkView.Pick(cam, pos);
            ulong rowLift = _netRowHover != 0 ? LiftRow(_netRowHover) : 0;
            if (overUi && _netRowHover != 0) _networkView.SetHover(-1, rowLift, 0, rowLift == 0 ? _netRowHover : 0);
            else _networkView.SetHover(hit.Edge, hit.Lift, hit.Node, 0);
            // A click (not a drag, which moves the camera) picks; on empty ground it clears.
            if (mouse.leftButton.wasPressedThisFrame && !overUi) { _netPressed = true; _netPress = pos; }
            if (!_netPressed || !mouse.leftButton.wasReleasedThisFrame) return;
            _netPressed = false;
            if ((pos - _netPress).sqrMagnitude > 36) return;
            if (!hit.Any) { ClearNetworkPick(); return; }
            PickNetwork(hit.Trail, hit.Edge, hit.Lift, hit.Node);
        }

        void DisposeNetworkView()
        {
            _networkView?.Dispose();
            _networkView = null;
        }
    }
}
