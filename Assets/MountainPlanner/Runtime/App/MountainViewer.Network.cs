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
        System.Diagnostics.Stopwatch _networkWatch;
        DesignDocument _networkJobDoc, _networkPending;
        NetworkReport _network;
        DesignDocument _networkDoc;
        bool _networkWired, _networkOn;
        readonly Dictionary<int, (ulong Trail, int StartMm)> _edgeKeys = new Dictionary<int, (ulong, int)>();

        // The pick (findings 2, 7; R2-2): a trail segment (by its trail and start, so it survives a rebuild), a lift, a node
        // or an Area (NW-15, by its hub).
        ulong _pickTrail, _pickLift, _pickNode, _pickArea;
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
            if (earthwork == null || _builtFor == null) { Debug.LogWarning("[Network] no earthwork yet: the network waits for the next rebuild"); return; }
            var site = earthwork.Site;
            if (_netGeometry == null || !SameSite(site, _netSite))
            {
                _netSite = site;
                _netGeometry = new TrailNetworkGeometry(site, _builtFor.Pipeline.Heights.Source, null,
                                                        t => earthwork.DetailOf(t.Id.Value) as TrailBuild);
            }
            var geometry = _netGeometry;
            _networkJobDoc = doc;
            _networkWatch = System.Diagnostics.Stopwatch.StartNew();
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
            Hud.NetworkAreaChosen += id => PickNetwork(0, -1, 0, 0, true, id);
            // NT19: the Area card's rename, one undoable step; a new name keeps the hub's ends now.
            Hud.AreaRenamed = (id, name) =>
            {
                var history = Design;
                var area = _network?.AreaAt(id)?.Area;
                if (history == null) return "No design is open";
                if (area == null) return "That area has changed: pick it again";
                var r = history.Submit(new RenameArea(history.Revision, area.Record?.Id ?? default, area.Ends(), name));
                return r.Accepted ? null : r.Reason;
            };
            Hud.NetworkRowHovered += id => { _netRowHover = id; _networkView?.SetHover(-1, LiftRow(id), 0, LiftRow(id) == 0 ? id : 0); };
            Hud.CardClosed += () => { if (_pickTrail != 0 || _pickLift != 0 || _pickArea != 0) ClearNetworkPick(); };   // NT11: the card's ✕
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
                if (job.Status == TaskStatus.RanToCompletion)
                {
                    var r = job.Result;
                    Debug.Log($"[Network] {r.Lifts.Count} lifts, {r.Trails.Count} trails, {r.Graph.Nodes.Count} nodes, {r.Graph.Edges.Count} edges " +
                              $"in {_networkWatch?.Elapsed.TotalMilliseconds:F0} ms off the main thread; {r.LiftWarnings} lift and {r.TrailWarnings} trail warnings");
                    ApplyNetwork(doc, r);
                }
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
            // NW-15: the Areas, their rows and their names on the map.
            var areaRows = new List<NetworkAreaRow>();
            _areaLabelSpots.Clear();
            foreach (var a in report.Areas)
            {
                var what = new List<string>();
                foreach (var m in a.Area.Station.Members) what.Add($"{doc.FindLift(m.Lift)?.Name ?? "Lift"} {(m.Bottom ? "loads" : "unloads")}");
                areaRows.Add(new NetworkAreaRow { Id = a.Area.Station.Id, Name = a.Area.Name, Named = a.Area.Named, Lifts = what.ToArray(), TrailsHere = a.TrailsHere.Count,
                                                  TrailsReached = a.Reach.Trails.Count, LiftsReached = a.Reach.Lifts.Count });
                if (_resort != null)
                {
                    var c = a.Area.Station.Centre;
                    float cx = (float)c.XMetres, cz = (float)c.YMetres;
                    _areaLabelSpots.Add((a.Area.Name, new Vector3(cx, _resort.Surface.HeightAt(cx, cz) + 12, cz)));
                }
            }
            if (Hud != null && _resort != null)
            {
                // NT7: an Area's name places after the lifts', tried on its centre, then just above, then just below.
                _areaLabels ??= new LandmarkLabelOverlay(Hud.ContourLabelLayer, _labelPlacement, LabelPlacement.Areas,
                                                         nudges: new[] { Vector2.zero, new Vector2(0, -24), new Vector2(0, 24) });
                _areaLabels.SetLandmarks(_areaLabelSpots);
            }
            if (_pickArea != 0 && report.AreaAt(_pickArea) == null) _pickArea = 0;   // the hub changed: its pick goes
            if (Hud != null)
            {
                Hud.SetNetwork(liftRows, warnRows, report.Trails.Count, junctions, paths, areaRows);
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
            foreach (var s in report.Lifts)
            {
                var l = doc.FindLift(s.Lift);
                if (l == null) continue;
                lifts.Add(new NetworkView.LiftLine { Id = s.Lift.Value, Bottom = DrapePoint(l.Bottom), Top = DrapePoint(l.Top), Connected = s.Connected });
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
            var areas = new List<NetworkView.StationArea>();
            foreach (var n in graph.Nodes)
            {
                if (n.Kind == NodeKind.Station)
                {
                    // NW-14 v2 (NT17): a station is one rounded shape round its terminals; each terminal picks the station.
                    var station = graph.StationById(n.Id);
                    if (station == null) continue;
                    areas.Add(StationShape(station, site, Drape, false));
                    foreach (var m in station.Members)
                        nodes.Add(new NetworkView.Node { Id = n.Id, Kind = NetworkView.NodeKind.LiftEnd, Lift = m.Lift.Value, Bottom = m.Bottom, At = DrapePoint(m.At) });
                    continue;
                }
                var kind = n.Kind == NodeKind.Junction ? NetworkView.NodeKind.Junction : n.Kind == NodeKind.PathEnd ? NetworkView.NodeKind.PathEnd : NetworkView.NodeKind.RunEnd;
                nodes.Add(new NetworkView.Node { Id = n.Id, Kind = kind, DeadEnd = deadTails.Contains(n.Id), At = DrapePoint(n.At) });
            }
            var links = new List<NetworkView.Link>();
            var seen = new HashSet<ulong>();
            foreach (var e in graph.Edges)
            {
                if (e.Kind != EdgeKind.Path || !seen.Add(e.Owner)) continue;
                if (graph.TryGetNode(e.From, out var a) && graph.TryGetNode(e.To, out var b)) links.Add(new NetworkView.Link { A = DrapePoint(a.At), B = DrapePoint(b.At) });
            }
            _networkView.Set(segments.ToArray(), lifts.ToArray(), nodes.ToArray(), links.ToArray(), deadEnds.ToArray(), areas.ToArray());
            ApplyNetworkPick();
        }

        /// <summary>
        /// A station's shape (NW-14 v2, NT17): its rounded hull's outline (<see cref="DesignStations.Outline"/>), its
        /// straight edges cut every 5 m so the rim follows the ground, draped; the fan's centre is the hull's.
        /// </summary>
        static NetworkView.StationArea StationShape(Station station, EarthworkSite site, Func<double, double, Vector3> drape, bool candidate)
        {
            var outline = DesignStations.Outline(station);
            var rim = new List<Vector3>(outline.Count);
            for (int i = 0; i + 1 < outline.Count; i += 2)
            {
                double x = outline[i], y = outline[i + 1];
                if (i >= 2)
                {
                    double px = outline[i - 2], py = outline[i - 1];
                    int steps = (int)Math.Ceiling(Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)) / 5);
                    for (int k = 1; k < steps; k++) rim.Add(drape(site.OriginX + px + (x - px) * k / steps, site.OriginY + py + (y - py) * k / steps));
                }
                rim.Add(drape(site.OriginX + x, site.OriginY + y));
            }
            var lifts = new ulong[station.Lifts.Count];
            for (int k = 0; k < lifts.Length; k++) lifts[k] = station.Lifts[k].Value;
            return new NetworkView.StationArea { Station = station.Id, Lifts = lifts, Area = station.IsArea, Candidate = candidate,
                                                 Centre = drape(site.OriginX + station.Centre.XMetres, site.OriginY + station.Centre.YMetres), Rim = rim.ToArray() };
        }

        // ---------- the Areas' names on the map (NW-15, NT19), with the lift labels ----------

        LandmarkLabelOverlay _areaLabels;
        readonly List<(string Name, Vector3 At)> _areaLabelSpots = new List<(string, Vector3)>();

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
        /// Picks a trail (by <paramref name="edge"/>, or from its head when -1), a lift, a node or an Area, or clears with
        /// nothing; a lift, trail or Area opens its card, a node closes any. A second pick of the same thing clears it.
        /// </summary>
        void PickNetwork(ulong trail, int edge, ulong lift, ulong node, bool fromList = false, ulong area = 0)
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
            // NW-15: a terminal of an Area picks the Area, one node where everything interacts.
            if (node != 0 && _network.AreaAt(node) != null) { area = node; node = 0; }
            bool same = (trail != 0 && trail == _pickTrail && startMm == _pickStartMm) || (lift != 0 && lift == _pickLift) || (node != 0 && node == _pickNode)
                        || (area != 0 && area == _pickArea);
            // The cards close first (their CardClosed clears the old pick), then the new pick, then its card.
            Hud?.CloseTrailCardIfOpen();
            Hud?.CloseLiftCardIfOpen();
            Hud?.CloseAreaCardIfOpen();
            _pickTrail = same ? 0 : trail;
            _pickStartMm = same ? -1 : startMm;
            _pickLift = same ? 0 : lift;
            _pickNode = same ? 0 : node;
            _pickArea = same ? 0 : area;
            if (_pickTrail != 0) Hud?.OpenTrailCardFor(_pickTrail);
            if (_pickLift != 0) Hud?.OpenLiftCardFor(_pickLift);
            if (_pickArea != 0) Hud?.OpenAreaCardFor(_pickArea);
            ApplyNetworkPick();
        }

        /// <summary>Clears the pick (Esc, empty ground, the card's ✕); true when there was one.</summary>
        bool ClearNetworkPick()
        {
            if (_pickTrail == 0 && _pickLift == 0 && _pickNode == 0 && _pickArea == 0) return false;
            _pickTrail = _pickLift = _pickNode = _pickArea = 0;
            _pickStartMm = -1;
            Hud?.CloseTrailCardIfOpen();
            Hud?.CloseLiftCardIfOpen();
            Hud?.CloseAreaCardIfOpen();
            ApplyNetworkPick();
            return true;
        }

        void ApplyNetworkPick()
        {
            Hud?.SetNetworkFocused(_pickLift != 0 ? _pickLift : _pickArea != 0 ? _pickArea : _pickTrail);
            if (_networkView == null || _network == null) return;
            int edge = PickedEdge();
            NetworkReport.Reach reach = null;
            if (edge >= 0) reach = _network.ReachFromSegment(edge);
            else if (_pickLift != 0) reach = _network.ReachFrom(new LiftId(_pickLift));
            else if (_pickNode != 0) reach = _network.ReachFrom(_pickNode);
            else if (_pickArea != 0) reach = _network.ReachFromArea(_pickArea);   // NW-15: its lifts ridden too
            var lifts = new List<ulong>();
            if (reach != null) foreach (var l in reach.Lifts) lifts.Add(l.Value);
            _networkView.SetPick(edge, _pickLift, _pickNode != 0 ? _pickNode : _pickArea, reach?.Segments, lifts, reach?.Nodes);
        }

        void HoverAndClickNetwork(UnityEngine.Camera cam)
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || PhotoOpen || (_tools != null && _tools.Armed)) { _networkView.SetHover(-1, 0, 0, 0); return; }
            if (_netHoverHeld) return;
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
            PickNetwork(hit.Trail, hit.Edge, hit.Lift, hit.Node, false, hit.Area);
        }

        // ---------- -networkshots <folder>: the Network view for review (with -styletile, and -extralift for a lift with no way down) ----------

        void StartNetworkShots(string[] args)
        {
            int i = Array.IndexOf(args, "-networkshots");
            bool demo = Array.IndexOf(args, "-networkdemo") >= 0;
            if (demo) { _netDemoPending = true; StartCoroutine(NetworkDemoTrail()); }
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(NetworkShots(args[i + 1]));
        }

        /// <summary>
        /// -networkdemo (demo.bat 70, captures): with -styletile and -extralift, a run painted beside the second lift from its
        /// top to its bottom (its ends within 30 ft of the lift's), so the network shows a connected lift and a trail it reaches.
        /// </summary>
        bool _netDemoPending;

        System.Collections.IEnumerator NetworkDemoTrail()
        {
            try { yield return NetworkDemoTrailSteps(); }
            finally { _netDemoPending = false; }
        }

        System.Collections.IEnumerator NetworkDemoTrailSteps()
        {
            while (_resort == null || _edits == null || !_edits.EarthworkReady || WorkBusy) yield return null;
            DesignDocument doc;
            for (int f = 0; ; f++)
            {
                doc = _edits.History.Document;
                if (doc.Lifts.Count >= 2 || f > 3000) break;
                yield return null;
            }
            if (doc.Lifts.Count < 2) { Debug.LogWarning("[Network] -networkdemo needs -styletile and -extralift"); yield break; }
            var lift = doc.Lifts[doc.Lifts.Count - 1];
            foreach (var l in doc.Lifts) if (l.Name == "Check Quad") lift = l;
            // 20 ft to the side of the line, the ends 20 ft in from the lift's ends: both within TR-05's 30 ft.
            var b = DesignFrame.ToLocal(lift.Bottom);
            var t = DesignFrame.ToLocal(lift.Top);
            var along = (t - b).normalized;
            var side = new Vector2(along.y, -along.x) * 6.1f;
            var head = DesignFrame.ToDesign(t - along * 6.1f + side);
            var tail = DesignFrame.ToDesign(b + along * 6.1f + side);
            var parts = new[] { new FootprintPart(new[] { new Stroke(DesignUnits.FeetToMm(60), false, new[] { head, tail }) }) };
            var history = _edits.History;
            var cmd = new AddTrail(history.Revision, "Lift Line", TrailSettings.NaturalRun, parts, head, tail, WorkStage.Built);
            var after = cmd.Apply(history.Document);
            if (!after.Accepted) { Debug.LogWarning("[Network] -networkdemo: " + after); yield break; }
            var rebuilt = _edits.Earthwork.Rebuild(after.Document, history.Terrain);
            history.Submit(cmd, rebuilt.Edits);
            _edits.Earthwork.Accept(rebuilt);
            Debug.Log("[Network] -networkdemo: Lift Line painted beside " + lift.Name);
        }

        /// <summary>Analysis › Network opened over the built work: the view, then a trail's lower segment, a lift and a
        /// junction picked, then a hovered segment, a picture each (1-network.png and on), then quits.</summary>
        System.Collections.IEnumerator NetworkShots(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            for (int f = 0; _resort == null || !_resort.CoverReady.IsCompleted || _edits == null || WorkBusy || _network == null || NetworkBusy || _netDemoPending; f++)
            {
                if (f % 300 == 299)
                    Debug.Log($"[NetworkShots] waiting: resort {_resort != null}, cover {_resort?.CoverReady.IsCompleted}, edits {_edits != null}, " +
                              $"edits busy {EditsBusy}, built busy {BuiltBusy}, network {_network != null}, network busy {NetworkBusy}");
                yield return null;
            }
            Debug.Log("[NetworkShots] the network is in; opening Analysis › Network");
            for (int k = 0; k < 30; k++) yield return null;
            while (WorkBusy || NetworkBusy) yield return null;
            Hud.SetAnalysisTab("network");
            Hud.SetAnalysis(true);
            yield return Shot(dir, "1-network");
            var edges = _network.Graph.Edges;
            int lastSegment = -1, firstSegment = -1;
            for (int k = 0; k < edges.Count; k++)
                if (edges[k].Kind == EdgeKind.TrailSegment) { if (firstSegment < 0) firstSegment = k; lastSegment = k; }
            if (lastSegment >= 0)
            {
                PickNetwork(edges[lastSegment].Owner, lastSegment, 0, 0);
                yield return Shot(dir, "2-segment");
            }
            if (_network.Lifts.Count > 0)
            {
                PickNetwork(0, -1, _network.Lifts[0].Lift.Value, 0);
                yield return Shot(dir, "3-lift");
            }
            foreach (var n in _network.Graph.Nodes)
            {
                if (n.Kind != NodeKind.Junction && !(n.Kind == NodeKind.Station && _network.Graph.StationById(n.Id)?.HasBottom == true)) continue;
                PickNetwork(0, -1, 0, n.Id);
                yield return Shot(dir, "4-node");
                break;
            }
            ClearNetworkPick();
            if (firstSegment >= 0)
            {
                _networkView.SetHover(firstSegment, 0, 0, 0);
                _netHoverHeld = true;
                yield return Shot(dir, "5-hover");
                _netHoverHeld = false;
            }
            // NW-15 (NT19): the first Area picked from its row: what it reaches lit, its card open.
            if (_network.Areas.Count > 0)
            {
                foreach (var a in _network.Areas)
                    Debug.Log($"[NetworkShots] area {a.Area.Name}: {a.Area.Station.Lifts.Count} lifts, {a.TrailsHere.Count} trails here, reaches {a.Reach.Trails.Count} trails and {a.Reach.Lifts.Count} lifts");
                PickNetwork(0, -1, 0, 0, true, _network.Areas[0].Area.Station.Id);
                yield return Shot(dir, "7-area");
                ClearNetworkPick();
            }
            // NT16, NT20: the lift tool's catchment on the trail map, as if the pointer held a bottom terminal there: 40 m out
            // past the first Area's farthest terminal (the hub grows to take it in), else 25 m beside the first lift's top
            // (a transfer).
            if (_network.Lifts.Count > 0 && _networkDoc?.FindLift(_network.Lifts[0].Lift) is Lift first)
            {
                Hud.SetAnalysis(false);
                var top = DesignFrame.ToLocal(first.Top);
                var beside = DesignFrame.ToLocal(first.Bottom) - top;
                _catchShot = top + new Vector2(-beside.y, beside.x).normalized * 25;
                if (_network.Areas.Count > 0)
                {
                    var hub = _network.Areas[0].Area.Station;
                    var centre = DesignFrame.ToLocal(hub.Centre);
                    var far = centre;
                    foreach (var p in hub.Hull) if ((DesignFrame.ToLocal(p) - centre).sqrMagnitude >= (far - centre).sqrMagnitude) far = DesignFrame.ToLocal(p);
                    var out_ = far - centre;
                    _catchShot = far + (out_.sqrMagnitude > 1 ? out_.normalized : Vector2.right) * 40;
                }
                for (int k = 0; k < 10; k++) yield return null;
                yield return Shot(dir, "6-catchment");
                Debug.Log("[NetworkShots] catchment: " + _catchmentNote);
                _catchShot = null;
            }
            Debug.Log($"[NetworkShots] {_network.Lifts.Count} lifts, {_network.Trails.Count} trails, {_network.Graph.Nodes.Count} nodes, {edges.Count} edges; " +
                      $"warnings: {_network.LiftWarnings} lifts, {_network.TrailWarnings} trails; diagram {_networkView?.VertexCount} vertices");
            Application.Quit();
        }

        /// <summary>A capture holds the hover (the pointer isn't on the map).</summary>
        bool _netHoverHeld;

        void DisposeNetworkView()
        {
            _networkView?.Dispose();
            _networkView = null;
        }
    }
}
