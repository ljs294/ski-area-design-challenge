using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.13, the guests on screen: the dots, their lines at the lifts, hover and click.
    ///
    /// - **Paths and lines** are built once for each design the worker runs (its revision and network), on the main thread,
    ///   from the same graph the worker's edges came from, so an edge index means the same piece of trail on both sides.
    /// - **Every frame**, after the camera has moved: each lift's rope time and line from the rope clock and the newest
    ///   snapshot, then <see cref="SkierLayer.Place"/> at the rope clock's tick (dots and chairs share one clock) and one draw.
    /// - **Picking** reads the dots' screen points from the same job: the nearest within 8 px; a click (not a drag) asks
    ///   the worker to inspect that skier, and the skier outranks the lift under the same pixel.
    /// </summary>
    public sealed partial class MountainViewer
    {
        SkierLayer _skiers;
        /// <summary>The design last sent to the worker, and the graph and document it was built from.</summary>
        SimDesign _skierDesign;
        NetworkGraph _skierGraph;
        DesignDocument _skierDoc;
        /// <summary>What the layer's paths were built for: the revision and the network object (a graph can arrive later).</summary>
        long _skierBuiltRevision = long.MinValue;
        SimNetwork _skierBuiltNetwork;
        bool _skierBuiltAny;
        long _skierSnowShown = -1;
        float _nextSkierSnow;
        uint _skierHover;
        uint _skierSelected;
        Vector2 _skierPress;
        uint _skierPressSerial;
        bool _skierPressed;
        bool _skiersShown = true;
        /// <summary>The last build's cost (F1), milliseconds: paths, then lines.</summary>
        double _skierPathsMs, _skierLinesMs;
        float[] _skierTableAlong = new float[256], _skierTableMs = new float[256], _skierTableCaution = new float[256];

        /// <summary>Kept beside every <see cref="SendDesign"/>: the screen builds its paths from exactly what the worker got.</summary>
        void NoteSkierDesign(SimDesign design, DesignDocument doc)
        {
            _skierDesign = design;
            _skierDoc = doc;
            _skierGraph = SimGraphFor(doc);
        }

        bool _dotsOnTop;
        /// <summary>
        /// A scripted capture or benchmark is running: keys, clicks and the camera's own input are ignored, because the
        /// player's window comes to the front and whatever is typed elsewhere on the machine lands in it (a stray Space
        /// paused a capture at 11:03).
        /// </summary>
        bool _scriptedRun;

        void BeginScriptedRun()
        {
            _scriptedRun = true;
            if (Camera != null) Camera.InputEnabled = false;
        }

        SkierLayer EnsureSkierLayer()
        {
            if (_skiers != null) return _skiers;
            _dotsOnTop = Array.IndexOf(Environment.GetCommandLineArgs(), "-dotsontop") >= 0;
            var shader = Resources.Load<Shader>(SkierLayer.ShaderResource);
            if (shader == null) { Debug.LogWarning("[Skiers] the dot shader is missing: " + SkierLayer.ShaderResource); return null; }
            var args = Environment.GetCommandLineArgs();
            int stress = DotStressArgument(args);
            _skiers = new SkierLayer(shader, Math.Max(1, Math.Max(Pacing.Default.SkierCap, stress))) { StressTo = stress };
            if (SkierProofArgument(args, out bool lod0))
            {
                var proofShader = Resources.Load<Shader>(SkierProof.ShaderResource);
                if (proofShader == null) Debug.LogWarning("[SkierProof] the stand-in shader is missing: " + SkierProof.ShaderResource);
                else _skiers.Proof = new SkierProof(proofShader, Math.Max(1, Math.Max(Pacing.Default.SkierCap, stress))) { AllLod0 = lod0 };
            }
            if (stress > 0 || _skiers.Proof != null)
                Debug.Log($"[SkierBench] dot stress {(stress > 0 ? stress.ToString() : "off")}, skier proof {(_skiers.Proof == null ? "off" : lod0 ? "lod0" : "lod")}");
            return _skiers;
        }

        /// <summary>Once a frame from LateUpdate, after the camera moved: place and draw every dot.</summary>
        void TickSkiers()
        {
            if (_sim == null || _resort == null || TitleMode) return;
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (cam == null) return;
            var layer = EnsureSkierLayer();
            if (layer == null) return;
            BuildSkierPaths(layer);
            CopySkierSnow(layer);
            var s = _simNow;
            var design = _skierDesign;
            if (design != null && s != null)
            {
                // The snapshot's lifts are the design's, by id (both sorted): match by id so a design change between
                // the two never mixes one lift's line with another's.
                int k = 0;
                for (int i = 0; i < design.Lifts.Length; i++)
                {
                    ulong id = design.Lifts[i].Id;
                    while (k < s.LiftCount && s.LiftIds[k] < id) k++;
                    int line = k < s.LiftCount && s.LiftIds[k] == id ? s.LineCount[k] : 0;
                    layer.SetLift(i, _ropes != null ? _ropes.Seconds(id) : 0, !LiftKinds.Compressed(design.Lifts[i].Kind), line);
                }
            }
            int perSecond = s == null || s.Paused ? 0 : s.TicksPerRealTick * (1000 / Math.Max(1, Pacing.Default.TickMs));
            layer.Selected = _skierSelected;
            layer.Hovered = _skierHover;
            layer.UiScale = MountainPlanner.UI.UiPreferences.Scale;
            layer.Visible = _skiersShown && !_riding && !PhotoOpen;
            layer.OnTop = _dotsOnTop;
            _skierFrame = _sim.SkierRender.Latest;   // read once a frame: the Traffic layer shares it (task 4.13 PR C)
            layer.Place(_skierFrame, _ropes != null ? _ropes.DrawTick : 0, cam, Mathf.Min(0.25f, Time.unscaledDeltaTime), perSecond);
            layer.Draw(cam);
            TickSkierCard();
        }

        // ---------- paths and lines, once per design ----------

        void BuildSkierPaths(SkierLayer layer)
        {
            var design = _skierDesign;
            if (design == null) return;
            var network = design.Network;
            if (_skierBuiltAny && design.Revision == _skierBuiltRevision && ReferenceEquals(network, _skierBuiltNetwork)) return;
            var doc = _skierDoc;
            var graph = _skierGraph;
            var site = _edits?.Earthwork?.Site;
            // The worker's edges need the graph they came from and the trails' centrelines; until both are here the
            // dots wait (a design with no network has nobody on it anyway).
            if (network != null && (graph == null || _netGeometry == null || site == null)) return;
            _skierBuiltAny = true;
            _skierBuiltRevision = design.Revision;
            _skierBuiltNetwork = network;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Func<float, float, float> ground = ReadoutHeight;
            var edges = new List<SkierPaths.EdgeInput>(network?.EdgeFrom.Length ?? 0);
            var runs = new List<LineLayout.RunInput>();
            var nodes = new List<Vector3>(graph?.Nodes.Count ?? 0);
            if (graph != null) foreach (var n in graph.Nodes) nodes.Add(Local(n.At));
            if (network != null)
            {
                var es = site.Value;
                Vector3 Plain(double latticeX, double latticeY) => new Vector3((float)(latticeX - es.OriginX), float.NaN, (float)(latticeY - es.OriginY));
                var lines = new Dictionary<ulong, IReadOnlyList<double>>();
                for (int e = 0; e < network.EdgeFrom.Length; e++)
                {
                    var input = new SkierPaths.EdgeInput { HalfWidth = network.EdgeWidthCm[e] / 200f };
                    int t = network.EdgeTrail[e];
                    if (t < 0)
                    {
                        // A path: straight between its nodes (GU-09's walk).
                        int a = network.EdgeFrom[e], b = network.EdgeTo[e];
                        input.Line = new[] { nodes[a], nodes[b] };
                        input.HalfWidth = 1.5f;
                    }
                    else
                    {
                        ulong trailId = network.TrailIds[t];
                        var trail = doc?.FindTrail(new TrailId(trailId));
                        if (trail != null && !lines.TryGetValue(trailId, out var centre)) lines[trailId] = centre = _netGeometry.CentrelineOf(trail);
                        lines.TryGetValue(trailId, out var line);
                        input.Line = line != null ? Stretch(line, network.EdgeStartMm[e] / 1000.0, network.EdgeEndMm[e] / 1000.0, Plain) : new[] { nodes[network.EdgeFrom[e]], nodes[network.EdgeTo[e]] };
                        // GU-10 v3: the same tables the worker's exit ticks were summed from, one per skier band (its
                        // confidence factor scales the whole edge, so a band's normalised table fits every skier of it).
                        if (trail != null && _trailLayer != null && _trailLayer.TryGetFacts(trail.Id, out var facts))
                        {
                            input.TableMs = new float[SkierPaths.Bands][];
                            input.TableCaution = new float[SkierPaths.Bands][];
                            int n = 0;
                            for (byte band = 0; band < SkierPaths.Bands; band++)
                            {
                                n = SkiTimeTable.Fill(facts.Result, network.EdgeTrueBand[e], band, network.EdgeStartMm[e], network.EdgeEndMm[e],
                                                      network.EdgeLengthMm[e], _skierTableAlong, _skierTableMs, _skierTableCaution);
                                input.TableMs[band] = (float[])_skierTableMs.Clone();
                                input.TableCaution[band] = (float[])_skierTableCaution.Clone();
                            }
                            input.TableAlong = (float[])_skierTableAlong.Clone();
                            input.TableCount = n;
                        }
                        var flat = new Vector2[input.Line.Length];
                        for (int i = 0; i < flat.Length; i++) flat[i] = new Vector2(input.Line[i].x, input.Line[i].z);
                        runs.Add(new LineLayout.RunInput { Line = flat, HalfWidth = input.HalfWidth });
                    }
                    edges.Add(input);
                }
            }
            var liftLines = new List<Vector3[]>(design.Lifts.Length);
            var liftInputs = new List<LineLayout.LiftInput>(design.Lifts.Length);
            foreach (var li in design.Lifts)
            {
                var lift = doc?.FindLift(new LiftId(li.Id));
                if (lift == null) { liftLines.Add(Array.Empty<Vector3>()); liftInputs.Add(default); continue; }
                var bottom = DesignFrame.ToLocal(lift.Bottom);
                var top = DesignFrame.ToLocal(lift.Top);
                liftLines.Add(new[] { new Vector3(bottom.x, float.NaN, bottom.y), new Vector3(top.x, float.NaN, top.y) });
                var type = LiftTypes.Measured(LiftType.Of(lift), Lifts);
                var b = type.Bottom;
                var tp = type.TopFootprint ?? type.Top;
                liftInputs.Add(new LineLayout.LiftInput
                {
                    Bottom = bottom, Top = top, Seats = Math.Max(1, li.Seats), Built = li.Built,
                    Behind = (float)(b?.Behind ?? 3), Ahead = (float)(b?.Ahead ?? 3), HalfWidth = (float)(b?.HalfWidth ?? 3),
                    TopBehind = (float)(tp?.Behind ?? 3), TopAhead = (float)(tp?.Ahead ?? 3), TopHalfWidth = (float)(tp?.HalfWidth ?? 3),
                    LaneHalfWidth = (float)LiftLanes.HalfWidth(type),
                });
            }
            var paths = SkierPaths.Build(design.Revision, edges, liftLines, nodes, ground);
            _skierPathsMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var layout = LineLayout.Build(design.Revision, liftInputs, runs, ground, CoverBlocks());
            _skierLinesMs = watch.Elapsed.TotalMilliseconds;
            layer.SetPaths(paths);
            layer.SetLines(layout);
            Debug.Log($"[Skiers] paths for revision {design.Revision}: {edges.Count} edges, {liftLines.Count} lifts in {_skierPathsMs:F1} ms; " +
                      $"lines in {_skierLinesMs:F1} ms" + (layout.Diagnosis.Length > 0 ? " · " + layout.Diagnosis : ""));
        }

        static Vector3 Local(PointMm p) => new Vector3((float)p.XMetres, float.NaN, (float)p.YMetres);

        /// <summary>
        /// The ground a line can't stand on that the layout can't see from the design: forest that hasn't been cleared
        /// (the cover the ground is planted from, not the planted trees, so it's the same with or without Burst).
        /// </summary>
        Func<float, float, bool> CoverBlocks()
        {
            var pipeline = _edits?.Pipeline;
            var site = _edits?.Earthwork?.Site;
            if (pipeline == null || site == null || pipeline.Trees == null) return null;
            var trees = pipeline.Trees;
            var edits = pipeline.Current;
            var lattice = pipeline.Lattice;
            double ox = site.Value.OriginX, oy = site.Value.OriginY;
            return (x, z) =>
            {
                double lx = x + ox, ly = z + oy;
                lattice.CellAt(lx, ly, out int c, out int r);
                if (edits != null && edits.IsClearedAt(c, r)) return false;
                return trees.TreeShare(lx, ly) > 0.3;
            };
        }

        /// <summary>SI-17: the dots stand on the snow, copied from the screen's field when tiles come in (at most every 2 s).</summary>
        void CopySkierSnow(SkierLayer layer)
        {
            var field = _resort.States?.Snow;
            if (field == null || Time.unscaledTime < _nextSkierSnow || _simTilesShown == _skierSnowShown) return;
            _nextSkierSnow = Time.unscaledTime + 2f;
            _skierSnowShown = _simTilesShown;
            layer.SetSnow(field.Width, field.Height, new Vector2(_resort.Ring.xMin, _resort.Ring.yMin), field.CellMetres, field.Cells);
        }

        // ---------- hover and click ----------

        /// <summary>Once a frame from Update, before the lifts' hover: the dot under the pointer, and a click on one.</summary>
        void UpdateSkierHover()
        {
            _skierHover = 0;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var layer = _skiers;
            bool can = layer != null && layer.Visible && mouse != null && !_scriptedRun && !PhotoOpen && !_riding && !_networkOn && (_tools == null || !_tools.Armed);
            if (!can) { _skierPressed = false; return; }
            var pos = mouse.position.ReadValue();
            if (!PointerOverUi(pos) && layer.Pick(pos, out uint serial, out _)) _skierHover = serial;
            if (mouse.leftButton.wasPressedThisFrame && !PointerOverUi(pos))
            {
                _skierPressed = _skierHover != 0;
                _skierPress = pos;
                _skierPressSerial = _skierHover;
            }
            if (!_skierPressed || !mouse.leftButton.wasReleasedThisFrame) return;
            _skierPressed = false;
            if ((pos - _skierPress).sqrMagnitude > 36) return;   // a drag moved the camera
            SelectSkier(_skierHover != 0 ? _skierHover : _skierPressSerial);
        }

        /// <summary>Whether a press this frame went to a skier (the lift card leaves it alone).</summary>
        bool SkierTookPress => _skierPressed || _skierHover != 0;

        /// <summary>GU-02: watches <paramref name="serial"/> (0: nobody); the worker answers on the tick it applies it.</summary>
        void SelectSkier(uint serial)
        {
            bool same = serial == _skierSelected;
            _skierSelected = serial;
            _skierGoneSince = -1;
            if (!same) _skierAsked = Time.unscaledTime;
            if (_sim != null) _sim.Send(SimCommand.Of(SimCommandKind.InspectSkier, serial));
            _watching = serial != 0;
            if (serial == 0) { StopFollowingSkier(); Hud?.CloseSkierCard(); return; }
            WireSkierCard();
            _nextSkierCard = 0;   // the header fills now, from the dot
            FillSkierCard();
        }

        // ---------- the card and Follow ----------

        bool _skierCardWired, _skierFollowing, _skierNextAsked;
        float _nextSkierCard, _skierAsked, _skierGoneSince = -1;
        const float SkierCardSeconds = 0.25f;   // four times a second at most
        readonly string[] _skierRuns = new string[5];

        void WireSkierCard()
        {
            if (_skierCardWired || Hud == null) return;
            _skierCardWired = true;
            Hud.SkierFollowChosen += on => { if (on) StartFollowingSkier(); else StopFollowingSkier(); _nextSkierCard = 0; };
            Hud.SkierNextChosen += () =>
            {
                // -1: the worker's next serial on the hill; the screen learns who from the snapshot.
                _skierNextAsked = true;
                _skierNextFrom = _skierSelected;
                _sim?.Send(SimCommand.Of(SimCommandKind.InspectSkier, -1));
            };
            Hud.SkierNextInLineChosen += () =>
            {
                uint next = _skiers?.NextInLine(_skierSelected) ?? 0;
                if (next == 0) return;
                bool follow = _skierFollowing;
                SelectSkier(next);
                if (follow) StartFollowingSkier();
            };
            Hud.SkierCardClosed += () =>
            {
                if (_skierSelected == 0) return;
                _skierSelected = 0;
                StopFollowingSkier();
                _sim?.Send(SimCommand.Of(SimCommandKind.InspectSkier, 0));
                _watching = false;
            };
            if (Camera != null) Camera.FollowBroken += () => { _skierFollowing = false; _nextSkierCard = 0; };
        }

        uint _skierNextFrom;

        void StartFollowingSkier()
        {
            if (_skierSelected == 0 || Camera == null || _skiers == null) return;
            if (!_skiers.TryFind(_skierSelected, out var at, out _)) return;
            _skierFollowing = true;
            Camera.Follow(at);
        }

        void StopFollowingSkier()
        {
            _skierFollowing = false;
            if (Camera != null && Camera.Following) Camera.StopFollowing();
        }

        /// <summary>Once a frame after the dots are placed: the camera's target, and the card at most four times a second.</summary>
        void TickSkierCard()
        {
            var g = _simNow?.Guests;
            // "Next skier": take the worker's pick as the selection once it answers.
            if (_skierNextAsked && g != null && g.SelectedSerial != 0 && (uint)g.SelectedSerial != _skierNextFrom)
            {
                _skierNextAsked = false;
                bool follow = _skierFollowing;
                _skierSelected = (uint)g.SelectedSerial;
                _skierAsked = Time.unscaledTime;
                _skierGoneSince = -1;
                if (follow) StartFollowingSkier();
                _nextSkierCard = 0;
            }
            if (_skierSelected == 0) return;
            Vector3 at = default;
            bool found = _skiers != null && _skiers.TryFind(_skierSelected, out at, out _);
            if (found) _skierGoneSince = -1;
            else if (_skierGoneSince < 0) _skierGoneSince = Time.unscaledTime;
            if (_skierFollowing && Camera != null)
            {
                if (found) Camera.FollowPoint = at;
                else if (Time.unscaledTime - _skierGoneSince > 1f) StopFollowingSkier();   // gone home: the camera stays at the base
            }
            if (Time.unscaledTime < _nextSkierCard) return;
            _nextSkierCard = Time.unscaledTime + SkierCardSeconds;
            FillSkierCard();
        }

        void FillSkierCard()
        {
            if (Hud == null || _skierSelected == 0) return;
            var g = _simNow?.Guests;
            var design = _skierDesign;
            var doc = _skierDoc;
            SkierRender rec = default;
            bool drawn = _skiers != null && _skiers.TryFind(_skierSelected, out _, out rec);
            bool answered = g != null && g.SelectedSerial == (int)_skierSelected;
            var card = new MountainPlanner.UI.MountainHud.SkierCard
            {
                Serial = _skierSelected,
                Band = answered ? g.SelectedBand : rec.Band,
                Title = "Skier " + _skierSelected.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture),
                Following = _skierFollowing,
                Runs = _skierRuns,
            };
            int band = Math.Min(3, card.Band);
            card.Kind = BandWords(band) + (answered && g.SelectedPartySize > 1 ? " · party of " + g.SelectedPartySize : "");
            bool gone = !drawn && _skierGoneSince >= 0 && Time.unscaledTime - _skierGoneSince > 1f;
            if (gone) card.Now = "Gone home for the day";
            else if (drawn)
            {
                card.Now = NowWords(rec, design, doc);
                card.Queued = rec.State == (byte)GuestState.Queued;
            }
            if (answered)
            {
                card.Today = $"Lap {g.SelectedLaps} of {g.SelectedStayLaps} · waited {WaitClock(g.SelectedWaitSeconds)}";
                card.Mood = $"{Math.Max(0, Math.Min(100, g.SelectedSatisfaction / 10))}% happy";
                card.Why = TopWhy(g);
                int n = 0;
                for (int k = g.SelectedRecordCount - 1; k >= 0 && n < _skierRuns.Length; k--)
                {
                    ushort seg = g.SelectedRecordSegment[k];
                    string name = (seg & GuestView.LiftMark) != 0 ? LiftName(seg & ~GuestView.LiftMark, design, doc) : RunName(seg, design, doc);
                    if (name == null || (n > 0 && _skierRuns[n - 1] == name)) continue;
                    _skierRuns[n++] = name;
                }
                card.RunCount = n;
            }
            Hud.ShowSkierCard(card);
        }

        static string BandWords(int band) => band == 0 ? "Beginner (green)" : band == 1 ? "Intermediate (blue)" : band == 2 ? "Advanced (black)" : "Expert";

        static string WaitClock(int seconds) => seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:D2}:{seconds % 60:D2}" : $"{seconds / 60}:{seconds % 60:D2}";

        string NowWords(in SkierRender r, SimDesign design, DesignDocument doc)
        {
            switch ((GuestState)r.State)
            {
                case GuestState.Skiing:
                    return "Skiing " + (RunName(r.Index, design, doc) ?? "a run");
                case GuestState.Walking:
                    return r.OnALift ? "Walking to " + (LiftName(r.Index, design, doc) ?? "a lift") : "Walking " + (RunName(r.Index, design, doc) ?? "a path");
                case GuestState.Queued:
                {
                    int place = r.Rank + 1;
                    return $"In line at {LiftName(r.Index, design, doc) ?? "a lift"} · {Ordinal(place)}{LineWait(r.Index)}";
                }
                case GuestState.Riding:
                {
                    bool compressed = design != null && r.Index < design.Lifts.Length && LiftKinds.Compressed(design.Lifts[r.Index].Kind);
                    return "Riding " + (LiftName(r.Index, design, doc) ?? "a lift") + (compressed ? " (time compressed)" : "");
                }
                case GuestState.Choosing: return "Deciding where to go next";
                case GuestState.Dwelling: return "Taking a break";
                default: return "On the way";
            }
        }

        /// <summary>LO-04 v1's wait for a lift's line, " · ~1:45" (real seconds at the clock's speed), or "" with none.</summary>
        string LineWait(int liftIndex)
        {
            var s = _simNow;
            var design = _skierDesign;
            if (s == null || design == null || liftIndex >= design.Lifts.Length) return "";
            ulong id = design.Lifts[liftIndex].Id;
            var speeds = Pacing.Default.Speeds;
            int factor = s.Paused || s.Speed < 1 || s.Speed > speeds.Length ? 1 : speeds[s.Speed - 1];
            for (int i = 0; i < s.LiftCount; i++)
                if (s.LiftIds[i] == id) return " · ~" + WaitClock((int)(s.WaitMs[i] / 1000 / factor));
            return "";
        }

        static string Ordinal(int n)
        {
            int t = n % 100;
            string suffix = t >= 11 && t <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + suffix;
        }

        static string LiftName(int index, SimDesign design, DesignDocument doc)
        {
            if (design == null || doc == null || index < 0 || index >= design.Lifts.Length) return null;
            return doc.FindLift(new LiftId(design.Lifts[index].Id))?.Name ?? "Lift";
        }

        string RunName(int edge, SimDesign design, DesignDocument doc)
        {
            var net = design?.Network;
            if (net == null || doc == null || edge < 0 || edge >= net.EdgeTrail.Length) return null;
            int t = net.EdgeTrail[edge];
            if (t < 0) return "a path";
            var id = new TrailId(net.TrailIds[t]);
            // The player's line holding this stretch, else the run's own name.
            var line = _sections?.TrailOf(_sections.SegmentAt(id, (net.EdgeStartMm[edge] + net.EdgeEndMm[edge]) / 2));
            return line?.Name ?? doc.FindTrail(id)?.Name ?? "Trail";
        }

        /// <summary>GU-07's factor that moved the last choice most (furthest from ×1), in words.</summary>
        static string TopWhy(GuestView g)
        {
            int best = -1, far = 0;
            for (int f = 0; f < g.SelectedWhy.Length; f++)
            {
                int v = g.SelectedWhy[f];
                if (v == 0) continue;
                int d = Math.Abs(v - 1000);
                if (best < 0 || d > far) { best = f; far = d; }
            }
            if (best < 0) return null;
            return "Chose its last run mostly for its " + ((ChoiceFactor)best).ToString().ToLowerInvariant();
        }
    }
}
