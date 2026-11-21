using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI;
using MountainPlanner.UI.Hud;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.25 PR B: Analysis › Grooming from the newest snapshot. Twice a second (as the lifts' live cells) the tab's
    /// model is built again only when the night, the trails or the crew pins moved (<see cref="GroomingTabModel.Signature"/>),
    /// so the HUD never rebuilds per tick. The tab's choices go through the design's history (setting, priority, buy, sell,
    /// tiller, crews: one undoable step each) or straight to the worker (tonight's orders). While the tab is open the drawn
    /// trails take tonight's colours in a third <see cref="NetworkView"/>, with its key in the legend; the morning's
    /// shortfall comes as a toast that opens Last night.
    /// </summary>
    public sealed partial class MountainViewer
    {
        bool _groomTabWired, _groomMapOn, _groomForce;
        float _nextGroomTab;
        long _groomTabSig = long.MinValue, _groomMapSig = long.MinValue;
        int _groomToastNight = int.MinValue;
        NetworkView _groomMap;
        GroomingTabModel _groomTabModel;

        // The player's trails for the tab, built again when the design, its trails or the snapshot's trails change.
        readonly List<GroomTrailInfo> _groomInfos = new List<GroomTrailInfo>();
        readonly List<int> _groomRowOfK = new List<int>();
        readonly Dictionary<ulong, int> _groomInfoAt = new Dictionary<ulong, int>();
        object _groomInfoDoc, _groomInfoSections;
        long _groomInfoKey = long.MinValue;
        int _groomInfoVersion;

        void WireGroomingTab()
        {
            if (_groomTabWired || Hud == null) return;
            _groomTabWired = true;
            Hud.GroomingViewChanged += on => { _groomMapOn = on; _groomMapSig = long.MinValue; _groomForce = true; ShowGroomMap(); };
            Hud.GroomingHovered += id => { if (_groomMapOn) _groomMap?.SetHover(-1, 0, 0, id); };
            Hud.GroomingShowMe += ShowGroomTrail;
            Hud.GroomingRefreshWanted += () => _groomForce = true;
            Hud.GroomTrailChosen = ChooseTrailGrooming;
            Hud.GroomOrderChosen = ChooseGroomOrder;
            Hud.GroomerBuyChosen = (model, drive) => GroomEdit(d => new BuyGroomer(d, model, drive));
            Hud.GroomerSellChosen = n => GroomEdit(d => new SellGroomer(d, n));
            Hud.GroomerTillerChosen = (n, on) => GroomEdit(d => new FitGroomerTiller(d, n, on));
            Hud.GroomCrewsChosen = ChooseGroomCrews;
            Hud.TrailGroomingOf = TrailGroomingNow;
        }

        string GroomEdit(Func<long, DesignCommand> make)
        {
            var history = Design;
            if (history == null) return "There's no resort open.";
            string problem = SubmitGrooming(make(history.Revision));
            _groomForce = true;
            return problem;
        }

        // ---------- the tab, from the snapshot ----------

        /// <summary>From <see cref="UpdateHud"/>: the tab's model twice a second, when anything it shows moved.</summary>
        void UpdateGroomingTab()
        {
            var s = _simNow;
            if (Hud == null) return;
            WireGroomingTab();
            if (!_groomForce && Time.unscaledTime < _nextGroomTab) return;
            _nextGroomTab = Time.unscaledTime + LiftLiveSeconds;
            _groomForce = false;
            int count = s != null && s.TrailsKnown ? s.TrailCount : 0;
            var v = _groomCaptureView != null ? CaptureNight(_groomCaptureView.Value, count) : s?.Grooming;
            RefreshGroomInfos(s, count);
            var pins = Design?.Document?.Grooming?.Crews ?? GroomCrews.AllAuto;
            int second = s?.SecondOfDay ?? 0;
            long sig = GroomingTabModel.Signature(v, count);
            unchecked
            {
                sig = sig * 31 + _groomInfoVersion;
                sig = sig * 31 + pins.GetHashCode();
                sig = sig * 31 + Hud.GroomingDismissed.Count;
                sig = sig * 31 + (Hud.GroomingLastNight ? 1 : 0);
                sig = sig * 31 + second / 3600;
            }
            if (sig != _groomTabSig || _groomTabModel == null)
            {
                _groomTabSig = sig;
                _groomTabModel = GroomingTabModel.Build(v, count, _groomInfos, _groomRowOfK, pins, Hud.GroomingDismissed, Hud.GroomingLastNight, second);
                Hud.SetGrooming(_groomTabModel);
            }
            MorningGroomToast(v);
            long mapSig = unchecked(sig * 31 + Hud.GroomerPicked);   // a picked groomer's path (round 3)
            if (_groomMapOn && _groomMapSig != mapSig) { _groomMapSig = mapSig; FillGroomMap(); }
        }

        /// <summary>The morning's line when last night left work undone (not on the first look: a load isn't a morning).</summary>
        void MorningGroomToast(GroomView v)
        {
            if (v == null) return;
            if (v.LastNightDay == _groomToastNight) return;
            bool first = _groomToastNight == int.MinValue;
            _groomToastNight = v.LastNightDay;
            // the runs no groomer could drive to, by name (more groomers wouldn't help: a cat track would)
            string noWay = null;
            int noWayCount = 0;
            for (int k = 0; k < v.LastState.Length && k < _groomRowOfK.Count; k++)
            {
                if (v.LastState[k] != GroomRowState.NoRoute) continue;
                int r = _groomRowOfK[k];
                if (r < 0 || r >= _groomInfos.Count) continue;
                if (noWayCount++ == 0) noWay = _groomInfos[r].Name;
            }
            if (first || v.LastNightDay < 0 || (v.LastNotReached <= 0 && v.LastShortMinutes <= 0 && noWayCount == 0) || v.GroomerCount == 0) return;
            var parts = new List<string>();
            if (v.LastNotReached > 0) parts.Add($"{v.LastNotReached} trail{(v.LastNotReached == 1 ? "" : "s")} not groomed");
            else if (v.LastShortMinutes > 0) parts.Add("Grooming cut short");
            if (v.LastShortMinutes > 0) parts.Add($"{Math.Max(1, (int)Math.Round(v.LastShortMinutes / 60.0))} groomer-hours short");
            if (noWayCount == 1) parts.Add($"{noWay}: no way there");
            else if (noWayCount > 1) parts.Add($"{noWayCount} trails with no way there");
            Hud.GroomMorningToast(string.Join(" · ", parts));
        }

        /// <summary>The player's built trails (name, sign, setting, priority) and each snapshot trail's row.</summary>
        void RefreshGroomInfos(SimSnapshot s, int count)
        {
            var doc = Design?.Document;
            long key = count;
            unchecked { for (int k = 0; k < count; k++) key = key * 1000003 + (long)s.TrailIds[k]; }
            if (ReferenceEquals(doc, _groomInfoDoc) && ReferenceEquals(_sections, _groomInfoSections) && key == _groomInfoKey) return;
            _groomInfoDoc = doc;
            _groomInfoSections = _sections;
            _groomInfoKey = key;
            _groomInfoVersion++;
            _groomInfos.Clear();
            _groomRowOfK.Clear();
            _groomInfoAt.Clear();
            if (doc == null) return;
            if (_sections != null)
                foreach (var t in _sections.Trails)
                {
                    if (!t.Built) continue;
                    var g = GroomingOfRow(doc, t);
                    if (!g.HasValue) continue;
                    _groomInfoAt[t.Id.Value] = _groomInfos.Count;
                    _groomInfos.Add(new GroomTrailInfo
                    {
                        Id = t.Id.Value, Name = t.Name, Symbol = t.Posted.HasValue ? MountainPlanner.UI.Tools.TrailTool.Symbol(t.Posted.Value) : "",
                        Setting = g.Value.Setting, Priority = g.Value.Priority,
                    });
                }
            for (int k = 0; k < count; k++)
            {
                ulong run = s.TrailIds[k];
                ulong row = GroomRowOfRun(doc, run);
                if (!_groomInfoAt.TryGetValue(row, out int at))
                {
                    // A run no player's trail holds: its own row, by its own name.
                    var t = doc.FindTrail(new TrailId(run));
                    if (t == null) { _groomRowOfK.Add(-1); continue; }
                    var g = t.Grooming;
                    at = _groomInfos.Count;
                    _groomInfoAt[run] = at;
                    _groomInfos.Add(new GroomTrailInfo
                    {
                        Id = run, Name = t.Name, Symbol = t.Posted.HasValue ? MountainPlanner.UI.Tools.TrailTool.Symbol(t.Posted.Value) : "",
                        Setting = g.Resolve(t.Settings.Kind), Priority = g.Priority,
                    });
                }
                _groomRowOfK.Add(at);
            }
        }

        /// <summary>The player's trail whose grooming a run takes: the one on the run's middle (as the worker is sent it).</summary>
        ulong GroomRowOfRun(DesignDocument doc, ulong run)
        {
            if (_sections == null) return run;
            var t = doc.FindTrail(new TrailId(run));
            int mid = t != null && _netGeometry != null ? Math.Max(0, _netGeometry.CentrelineLengthMm(t) / 2) : 0;
            var seg = _sections.SegmentAt(new TrailId(run), mid);
            var resolved = seg != null ? _sections.TrailOf(seg) : _sections.TrailOf(new TrailId(run));
            return resolved?.Id.Value ?? run;
        }

        /// <summary>A player's trail's grooming as the design has it now (a shaped trail's own, else its run's), resolved.</summary>
        static (GroomSetting Setting, GroomPriority Priority)? GroomingOfRow(DesignDocument doc, ResolvedTrail t)
        {
            if (doc == null || t == null || t.Segments.Count == 0) return null;
            var run = doc.FindTrail(t.Segments[0].Piece);
            var kind = run?.Settings.Kind ?? TrailKind.Run;
            TrailGrooming g;
            if (t.Line != null && doc.FindLine(t.Line.Id) is TrailLine line) g = line.Grooming;
            else if (run != null) g = run.Grooming;
            else return null;
            return (g.Resolve(kind), g.Priority);
        }

        (GroomSetting Setting, GroomPriority Priority)? TrailGroomingNow(ulong id)
        {
            var doc = Design?.Document;
            var t = TrailRowOf(id);
            if (t != null) return t.Built ? GroomingOfRow(doc, t) : null;
            var run = doc?.FindTrail(new TrailId(id));
            if (run == null || run.Stage != WorkStage.Built) return null;
            return (run.Grooming.Resolve(run.Settings.Kind), run.Grooming.Priority);
        }

        // ---------- the tab's choices ----------

        string ChooseTrailGrooming(ulong id, GroomSetting? setting, GroomPriority? priority)
        {
            var history = Design;
            if (history == null) return "There's no resort open.";
            var trail = TrailRowOf(id);
            DesignCommand cmd = trail != null ? SetTrailGrooming.For(history.Revision, trail, setting, priority)
                : history.Document.FindTrail(new TrailId(id)) != null ? new SetTrailGrooming(history.Revision, new[] { new TrailId(id) }, null, setting, priority)
                : null;
            if (cmd == null) return "That trail isn't on the map any more.";
            _groomForce = true;
            return SubmitGrooming(cmd);
        }

        /// <summary>Tonight's order for a player's trail: to each run it holds (as the worker grooms runs).</summary>
        string ChooseGroomOrder(ulong id, GroomOrder order)
        {
            if (_sim == null) return "The simulation isn't running.";
            var s = _simNow;
            int count = s != null && s.TrailsKnown ? Math.Min(s.TrailCount, _groomRowOfK.Count) : 0;
            if (!_groomInfoAt.TryGetValue(id, out int at)) return "That trail isn't groomed by the simulation yet.";
            int sent = 0;
            for (int k = 0; k < count; k++)
                if (_groomRowOfK[k] == at && SendGroomOrder(s.TrailIds[k], order)) sent++;
            _groomForce = true;
            return sent > 0 ? null : "The order didn't reach the simulation: try again.";
        }

        string ChooseGroomCrews(int shift, int count)
        {
            var history = Design;
            if (history == null) return "There's no resort open.";
            var c = history.Document.Grooming?.Crews ?? GroomCrews.AllAuto;
            // shift -1: every shift back to Auto (the Equipment table's Back to Auto, one undo step)
            var next = shift < 0 ? GroomCrews.AllAuto : new GroomCrews(shift == 0 ? count : c.Swing, shift == 1 ? count : c.Night, shift == 2 ? count : c.Day);
            if (shift < 0 && c.Equals(next)) return null;
            int owned = history.Document.Grooming?.Groomers.Count ?? 0;
            if (count > owned) return DevGroomers.Count > 0 ? "Dev groomers can't be pinned: they aren't in the design." : "A shift can't crew more groomers than the resort owns.";
            return GroomEdit(d => new SetGroomCrews(d, next));
        }

        /// <summary>Show me: the camera frames the trail's middle, about 1,500 ft out (as the Check tool's Show me does).</summary>
        void ShowGroomTrail(ulong id)
        {
            if (Camera == null || _resort == null) return;
            if (!_trailRowLine.TryGetValue(id, out var line) || line.Length == 0)
            {
                if (_trailLayer == null || !_trailLayer.TryGetLine(new TrailId(id), out line) || line.Length == 0) return;
            }
            var mid = line[line.Length / 2];
            float y = _resort.Surface.HeightAt(mid.x, mid.y);
            Camera.Frame(new Vector3(mid.x, float.IsNaN(y) ? 0 : y, mid.y), 450f);
            if (_groomMapOn) _groomMap?.SetHover(-1, 0, 0, id);
            else FocusTrailRow(id);
        }

        // ---------- the map's colours while the tab is open ----------

        void ShowGroomMap()
        {
            if (_groomMapOn && _groomMap == null && _resort != null) _groomMap = new NetworkView(_resort.Root.transform, "Grooming view");
            if (_groomMap == null) return;
            _groomMap.Visible = _groomMapOn;
            if (_groomMapOn) FillGroomMap();
            else _groomMap.SetHover(-1, 0, 0, 0);
        }

        readonly Dictionary<ulong, (Vector2[] Line, Vector3[] Points)> _groomDraped = new Dictionary<ulong, (Vector2[], Vector3[])>();

        /// <summary>Each listed trail in its colour for tonight (or last night): built again only when the model moves.</summary>
        void FillGroomMap()
        {
            // made again after a new mountain disposed it, while the tab stays open (Gate B finding 1)
            if (_groomMap == null && _groomMapOn && _resort != null) { _groomMap = new NetworkView(_resort.Root.transform, "Grooming view"); _groomMap.Visible = true; }
            if (_groomMap == null || _resort == null) return;
            var m = _groomTabModel;
            var segs = new List<NetworkView.Segment>();
            // round 3 (owner 2026-11-21): a picked groomer (its card open) shows its path tonight: its runs bright, its
            // drives between them over the trails, the rest dimmed
            var path = Hud.GroomingLastNight ? null : GroomerPathRows(Hud.GroomerPicked);
            if (m != null)
                foreach (var r in m.Rows)
                {
                    if (!_trailRowLine.TryGetValue(r.Id, out var line) || line.Length < 2) continue;
                    if (!_groomDraped.TryGetValue(r.Id, out var d) || !ReferenceEquals(d.Line, line))
                        _groomDraped[r.Id] = d = (line, DrapeLine(line));
                    var colour = path == null ? MountainHud.GroomMapColour(r) : path.Contains(r.Id) ? MountainHud.GroomMapPath : MountainHud.GroomMapNever;
                    segs.Add(new NetworkView.Segment { Edge = segs.Count, Trail = r.Id, Points = d.Points, Own = colour, Served = true });
                }
            if (path != null && path.Count > 0)
            {
                // the drives: from the depot, run to run in order, and home, as the dots drive them
                var stops = new List<Vector2[]> { new[] { DepotPoint(0) } };
                foreach (var id in path) if (_trailRowLine.TryGetValue(id, out var line) && line.Length >= 2) stops.Add(line);
                stops.Add(new[] { DepotPoint(0) });
                for (int i = 0; i + 1 < stops.Count; i++)
                {
                    NearestEnds(stops[i], stops[i + 1], out var a, out var b);
                    var route = RouteBetween(a, b);
                    var pts = route != null ? route.At : new[] { a, b };
                    if (pts.Length >= 2) segs.Add(new NetworkView.Segment { Edge = segs.Count, Trail = 0, Points = DrapeLine(pts), Own = MountainHud.GroomMapDrive, Served = true });
                }
            }
            _groomMap.SetCatchment(segs.ToArray(), null);
        }

        Vector3[] DrapeLine(Vector2[] line)
        {
            var surface = _resort.Surface;
            var pts = new Vector3[line.Length];
            for (int i = 0; i < line.Length; i++)
            {
                float y = surface.SurfaceAt(line[i].x, line[i].y);
                pts[i] = new Vector3(line[i].x, float.IsNaN(y) ? 0 : y + 2, line[i].y);
            }
            return pts;
        }

        static void NearestEnds(Vector2[] p, Vector2[] q, out Vector2 a, out Vector2 b)
        {
            a = p[0]; b = q[0];
            float best = float.MaxValue;
            foreach (var x in new[] { p[0], p[p.Length - 1] })
                foreach (var y in new[] { q[0], q[q.Length - 1] })
                {
                    float d = (x - y).sqrMagnitude;
                    if (d < best) { best = d; a = x; b = y; }
                }
        }

        /// <summary>The player's trails groomer <paramref name="number"/> works tonight, in the order of its pieces (null: none picked).</summary>
        List<ulong> GroomerPathRows(int number)
        {
            var v = _groomCaptureView != null ? _groomCaptureNight : _simNow?.Grooming;
            if (number == 0 || v == null) return null;
            int g = -1;
            for (int i = 0; i < v.GroomerCount && i < v.Number.Length; i++) if (v.Number[i] == number) g = i;
            if (g < 0) return null;
            var pieces = new List<(int From, int Row)>();
            for (int i = 0; i < v.TransitCount && i < GroomView.MaxTransit; i++)
            {
                int k = v.TransitTrail[i];
                if (v.TransitLane[i] != g || k < 0 || k >= _groomRowOfK.Count) continue;
                int r = _groomRowOfK[k];
                if (r >= 0 && r < _groomInfos.Count) pieces.Add((v.TransitFrom[i], r));
            }
            pieces.Sort((x, y) => x.From.CompareTo(y.From));
            var rows = new List<ulong>();
            foreach (var p in pieces)
            {
                ulong id = _groomInfos[p.Row].Id;
                if (rows.Count == 0 || rows[rows.Count - 1] != id) rows.Add(id);
            }
            return rows;
        }

        /// <summary>Per frame, after the camera: the grooming colours follow it.</summary>
        void TickGroomMap(UnityEngine.Camera cam)
        {
            if (_groomMap == null || !_groomMapOn || cam == null) return;
            _groomMap.Follow(cam);
        }

        // ---------- the ui-parity states (-uicapture): a fixed night over the resort's own trails ----------

        int? _groomCaptureView;
        GroomView _groomCaptureNight;

        /// <summary>-uicapture: a fixed night with that many groomers (null: the sim's own again).</summary>
        internal void SetGroomingForCapture(int? groomers)
        {
            _groomCaptureView = groomers;
            _groomCaptureNight = null;
            _groomToastNight = int.MinValue;   // a capture's night is not a morning
            _groomForce = true;
            UpdateGroomingTab();
        }

        /// <summary>The mockup's night: groomers 1-3 (800 W, 800, 400), each trail in turn planned on one of them from 5 PM, every
        /// fifth not reached, every seventh short, every ninth Never; one powder warning.</summary>
        GroomView CaptureNight(int groomers, int count)
        {
            if (_groomCaptureNight != null && _groomCaptureNight.State.Length == Math.Max(1, count)) return _groomCaptureNight;
            const int day = 40, zero = day * 1440 + 17 * 60;
            var v = new GroomView(Math.Max(1, count))
            {
                Live = true, NightDay = groomers > 0 ? day : -1, SwingStartAbsMin = zero, SwingEndAbsMin = zero + 300, NightEndAbsMin = zero + 870,
                CrewsSwing = groomers, CrewsNight = groomers, GroomerCount = groomers,
            };
            var models = new[] { GroomerModel.S800W, GroomerModel.S800, GroomerModel.S400 };
            for (int g = 0; g < groomers && g < GroomFleet.MaxGroomers; g++)
            {
                v.Number[g] = g + 1; v.Model[g] = models[g % 3]; v.Drive[g] = GroomerDrive.Diesel; v.Shifts[g] = 3; v.SeasonMinutes[g] = 1860 - 300 * g;
                v.Tiller[g] = g == 0;
            }
            int[] next = new int[Math.Max(1, groomers)];
            int want = 0, shortBy = 0;
            for (int k = 0; k < count; k++)
            {
                v.Lane[k] = GroomView.PackLane(-1, 0, 0, -1);
                v.StartMin[k] = -1; v.EndMin[k] = -1;
                int need = 60 + (k * 37) % 90;
                if (groomers == 0) { v.State[k] = GroomRowState.NoGroomer; v.Job[k] = GroomJob.Till; v.NeedMin[k] = (short)need; continue; }
                if (k % 9 == 8) { v.State[k] = GroomRowState.Never; continue; }
                want += need;
                int g = k % groomers;
                v.Job[k] = k % 4 == 3 ? GroomJob.Push : GroomJob.Till;
                v.NeedMin[k] = (short)need;
                v.Groomer[k] = (byte)(g + 1);
                if (k % 5 == 4 || next[g] + need > 870) { v.State[k] = GroomRowState.NotReached; shortBy += need; continue; }
                v.StartMin[k] = (short)next[g];
                v.EndMin[k] = (short)(next[g] + need);
                next[g] += need + 10;
                v.State[k] = k % 7 == 6 ? GroomRowState.Partial : GroomRowState.Planned;
                if (v.State[k] == GroomRowState.Partial) v.EndMin[k] = -1;
            }
            v.WantMinutes = want; v.ShortMinutes = shortBy; v.CrewMinutes = groomers * 870; v.CostUsd = 1093 * groomers;
            if (groomers > 0 && count > 2) { v.WarningCount = 1; v.WarningKind[0] = GroomWarningKind.PowderHeld; v.WarningTrail[0] = 2; v.State[2] = GroomRowState.PowderHeld; }
            return _groomCaptureNight = v;
        }

        void DisposeGroomMap()
        {
            _groomMap?.Dispose();
            _groomMap = null;
            _groomDraped.Clear();
            _groomMapSig = long.MinValue;
            _groomTabSig = long.MinValue;
            _groomInfoKey = long.MinValue;
            _groomToastNight = int.MinValue;
        }
    }
}
