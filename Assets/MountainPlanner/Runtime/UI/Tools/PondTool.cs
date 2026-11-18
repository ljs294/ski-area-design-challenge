using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.14's Pond tool (PD-01…04; the mockup's Snowmaking tab, Pond, O): click the shoreline point by point (Backspace
    /// takes the last back), Enter or a click on the first point closes it; then full pool (suggested, PD-03; PgUp/PgDn a
    /// foot) and the dig (0–50 ft; [ and ] a foot), with the graded ground, the pending contours and the review live. Break
    /// ground builds it. Edit (<see cref="EditWaterTool"/>) opens the same window on a built pond, its points draggable.
    /// </summary>
    public sealed class PondTool : IDesignTool, IKeepsWork
    {
        public const string ToolId = "pond";
        const int FirstSlot = 0, RingSlot = 1, DotSlots = 2;
        const float ClosePixels = 8, GrabPixels = 9;
        const int FootMm = 305;

        enum Step { Shore, Pool }

        ToolContext _ctx;
        readonly List<PointMm> _ring = new List<PointMm>();
        Step _step;
        string _name;
        int _poolMm, _digMm = PondRules.ExcavationDefaultMm, _springGpm;
        bool _suggested;
        PondFill _fill;
        bool _startsEmpty;
        Action<int> _setFill, _setStart;
        static readonly string[] Fills = { "Naturally", "Pumped only" };
        static readonly string[] Starts = { "Full", "Empty" };
        Pond _editing;
        int _drag = -1;
        Analysis _analysis;
        bool _analysing, _wantAnalysis;
        readonly ReviewModel _review = new ReviewModel();
        readonly WaterPreview _water = new WaterPreview();
        Vector2[] _xz = new Vector2[16];

        sealed class Analysis
        {
            public string Problem;
            public DesignEarthwork.Result Rebuild;
            public PondBuild Build;
            public List<double> Contours;
        }

        public PondTool() { }

        /// <summary>Edit (task 4.14): the window opens on a built pond, at its pool and dig.</summary>
        public PondTool(Pond editing) { _editing = editing; }

        public string Id => ToolId;
        public string Name => _editing != null ? "Edit pond" : "New pond";
        public bool HasWork => _editing != null ? _analysis?.Rebuild != null && Changed : _ring.Count > 0;
        bool Changed => _editing != null && (_name != _editing.Name || _poolMm != _editing.FullPoolMm || _digMm != _editing.ExcavationMm || !SameRing(_editing.Ring)
                                              || _fill != _editing.Fill || _startsEmpty != _editing.StartsEmpty);

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();
            var w = context.Window;
            w.SetSteps(new[] { "Shoreline", "Pool and review" });
            w.SetCommit(false, false);
            w.SetPlanShown(false);
            w.RemoveOption("fill");
            w.RemoveOption("start");
            _setFill = w.AddSwitch("fill", "Fills", Fills, 0, i => { _fill = (PondFill)i; if (_step == Step.Pool) Review(); },
                "WS-09: naturally from runoff (and a spring), or pumped only: lined, filled from another source by a pump house");
            _setStart = w.AddSwitch("start", "Starts", Starts, 0, i => { _startsEmpty = i == 1; if (_step == Step.Pool) Review(); },
                "WS-10: a new pond starts full, or empty and fills");
            w.ShowOption("fill", false);
            w.ShowOption("start", false);
            if (_editing != null)
            {
                _fill = _editing.Fill;
                _startsEmpty = _editing.StartsEmpty;
                _ring.AddRange(_editing.Ring);
                _name = _editing.Name;
                _poolMm = _editing.FullPoolMm;
                _digMm = _editing.ExcavationMm;
                _springGpm = _editing.SpringGpm;
                w.SetGoLabel("Regrade");
                EnterPool(false);
            }
            else
            {
                w.SetGoLabel("Break ground");
                Restart();
            }
        }

        void Restart()
        {
            DropAnalysis();
            _ring.Clear();
            _step = Step.Shore;
            _name = NextName();
            _suggested = false;
            _digMm = PondRules.ExcavationDefaultMm;
            _fill = PondFill.Natural;
            _startsEmpty = false;
            var w = _ctx.Window;
            w.ShowOption("fill", false);
            w.ShowOption("start", false);
            w.SetName(_name);
            w.SetRenamable(Rename);
            w.SetStep(0);
            w.SetHint("Click the shoreline, point by point: it is the water's edge when full. Enter, or a click on the first point, closes it.");
            w.SetMessage(null);
            w.SetKeys((GameAction.ToolNext, "close"), (GameAction.UndoStroke, "last point"));
            w.SetNext(false, false);
            w.SetCommit(false, false);
            w.ShowReview(null);
            DrawRing(-1);
        }

        string NextName()
        {
            var doc = _ctx.History.Document;
            for (int n = doc.Ponds.Count + 1; ; n++)
            {
                string name = "Pond " + n;
                bool taken = false;
                foreach (var p in doc.Ponds) taken |= p.Name == name;
                if (!taken) return name;
            }
        }

        string Rename(string name)
        {
            if (DesignRules.NameProblem(name) is string p) return p;
            _name = name;
            _ctx.Window.SetName(name);
            return null;
        }

        public void Tick(in ToolInput input)
        {
            if (_editing != null && _ctx.History.Document.FindPond(_editing.Id) == null) { _ctx.Window.SetMessage("That pond is gone (undone or removed)."); return; }
            if (_wantAnalysis && _ctx.Earthwork != null && _ctx.Earthwork.Ready) Analyse();
            if (_step == Step.Shore) ShoreTick(input);
            else PoolTick(input);
        }

        void ShoreTick(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.UndoStroke) && _ring.Count > 0) { _ring.RemoveAt(_ring.Count - 1); DrawRing(-1); }
            if (input.Pressed(ToolKeys.Next)) { Close(); return; }
            DrawRing(input.HasGround && !input.OverPanel ? _ring.Count : -1, input.Point);
            if (!input.Click || input.OverPanel || !input.HasGround) return;
            if (!input.InCore) { _ctx.Window.SetMessage("Keep the pond inside the core.", true); return; }
            if (_ring.Count >= 3 && Pixels(_ring[0], input) <= ClosePixels) { Close(); return; }
            if (_ring.Count > 0 && _ring[_ring.Count - 1].Within(input.Point, 1000)) return;   // a metre at least
            if (_ring.Count >= PondRules.MaxRingPoints) return;
            _ring.Add(input.Point);
            _ctx.Window.SetMessage(null);
            DrawRing(-1);
        }

        static float Pixels(PointMm a, in ToolInput input) =>
            (float)(Math.Sqrt(a.DistanceSquared(input.Point)) / 1000 / Math.Max(1e-3f, input.MetresPerPixel));

        void Close()
        {
            if (PondRules.RingProblem(_ring) is string p) { _ctx.Window.SetMessage(p, true); return; }
            if (WaterProblem() is string w) { _ctx.Window.SetMessage(w, true); return; }
            EnterPool(true);
        }

        // The ring may not cross a stream or a lake: a stream is dammed, a lake used as it is.
        string WaterProblem()
        {
            var under = _ctx.WaterUnder;
            if (under == null) return null;
            for (int i = 0; i < _ring.Count; i++)
            {
                var a = _ring[i];
                var b = _ring[(i + 1) % _ring.Count];
                int n = Math.Max(1, (int)(Math.Sqrt(a.DistanceSquared(b)) / 2000));
                for (int k = 0; k < n; k++)
                {
                    var q = new PointMm(a.X + (int)((long)(b.X - a.X) * k / n), a.Y + (int)((long)(b.Y - a.Y) * k / n));
                    if (under(q) is string name) return $"The shoreline crosses {name}: dam a stream instead (Dam, B), or use a lake as it is (Lake, L).";
                }
            }
            return null;
        }

        void EnterPool(bool suggest)
        {
            _step = Step.Pool;
            var w = _ctx.Window;
            w.SetStep(1);
            w.SetName(_name);
            w.SetRenamable(Rename);
            w.SetHint(_editing != null
                ? "Drag a point to move the shoreline. PgUp and PgDn raise and lower full pool, [ and ] the dig."
                : "Full pool starts at the suggested level. PgUp and PgDn raise and lower it a foot, [ and ] the dig.");
            w.SetKeys((GameAction.TargetUp, "pool up"), (GameAction.TargetDown, "pool down"), (GameAction.BrushWider, "dig deeper"), (GameAction.BrushNarrower, "dig less"));
            w.ShowOption("fill", true);
            w.ShowOption("start", true);
            _setFill?.Invoke((int)_fill);
            _setStart?.Invoke(_startsEmpty ? 1 : 0);
            DrawRing(-1);
            if (suggest && !_suggested) Suggest();
            else Analyse();
        }

        // PD-03, off the main thread: the mean ground inside, rounded up to half a metre, plus one.
        void Suggest()
        {
            var host = _ctx.Earthwork;
            if (host == null || !host.Ready) { _poolMm = 0; _wantAnalysis = true; _ctx.Window.SetMessage("Reading the ground…"); return; }
            var ring = _ring.ToArray();
            var heights = host.Heights;
            var site = host.Earthwork.Site;
            _analysing = true;
            _ctx.Analyse(() => PondEarthwork.SuggestPoolMm(ring, heights, site), pool =>
            {
                _analysing = false;
                _poolMm = pool;
                _suggested = true;
                Analyse();
            });
        }

        void PoolTick(in ToolInput input)
        {
            bool changed = false;
            if (input.Pressed(ToolKeys.TargetUp)) { _poolMm += FootMm; _suggested = false; changed = true; }
            if (input.Pressed(ToolKeys.TargetDown)) { _poolMm -= FootMm; _suggested = false; changed = true; }
            if (input.Pressed(ToolKeys.BrushWider) && _digMm < PondRules.ExcavationMaxMm) { _digMm = Math.Min(PondRules.ExcavationMaxMm, _digMm + FootMm); changed = true; }
            if (input.Pressed(ToolKeys.BrushNarrower) && _digMm > 0) { _digMm = Math.Max(0, _digMm - FootMm); changed = true; }
            // Edit: drag a shoreline point (owner: fixed from MapLibre, where nothing was editable).
            if (input.Click && !input.OverPanel && input.HasGround) _drag = Nearest(input);
            if (_drag >= 0 && input.Drag && input.HasGround) { _ring[_drag] = input.Point; DrawRing(-1); }
            if (_drag >= 0 && input.Release)
            {
                _drag = -1;
                if (PondRules.RingProblem(_ring) is string p) _ctx.Window.SetMessage(p, true);
                else if (WaterProblem() is string w) _ctx.Window.SetMessage(w, true);
                else changed = true;
            }
            if (changed) Analyse();
        }

        int Nearest(in ToolInput input)
        {
            int best = -1;
            float bestPx = GrabPixels;
            for (int i = 0; i < _ring.Count; i++)
            {
                float px = Pixels(_ring[i], input);
                if (px < bestPx) { bestPx = px; best = i; }
            }
            return best;
        }

        DesignCommand Command(long revision) => _editing != null
            ? new EditPond(revision, _editing.Id, _name, _ring.ToArray(), _poolMm, _digMm, _springGpm, _fill, _startsEmpty)
            : new AddPond(revision, _name, _ring.ToArray(), _poolMm, _digMm, _fill, _startsEmpty);

        /// <summary>The pond against the design's earthwork, off the main thread (CF-02): what Break ground would build.</summary>
        void Analyse()
        {
            DropAnalysis(true);
            var host = _ctx.Earthwork;
            if (host == null) { Review(); return; }   // no ground session (unit tests)
            if (!host.Ready) { _wantAnalysis = true; return; }
            if (_poolMm == 0) { Suggest(); return; }
            _wantAnalysis = false;
            _analysing = true;
            _ctx.Window.SetMessage("Grading…");
            var cmd = Command(_ctx.History.Revision);
            ulong key = _editing?.Id.Value ?? 0;
            var doc = _ctx.History.Document;
            var committed = _ctx.History.Terrain;
            var earthwork = host.Earthwork;
            var heights = host.Heights;
            double interval = host.ContourInterval;
            _ctx.Analyse(() =>
            {
                var a = new Analysis();
                var after = cmd.Apply(doc);
                if (!after.Accepted) { a.Problem = after.Reason; return a; }
                a.Rebuild = earthwork.Rebuild(after.Document, committed);
                a.Build = (a.Rebuild.DetailOf(key != 0 ? key : after.Created[0]) ?? earthwork.DetailOf(key)) as PondBuild;
                var pending = new EditedHeights(heights.Source, heights.Lattice, a.Rebuild.Edits);
                a.Contours = TrailTool.Contours(heights, pending, a.Rebuild.Changed, interval);
                a.Problem = a.Build?.Problem ?? (a.Build == null ? "This pond moves no ground." : null);
                return a;
            }, a =>
            {
                _analysing = false;
                _analysis = a;
                _ctx.PendingTerrain = a.Problem == null ? a.Rebuild?.Edits : null;
                if (a.Contours != null) host.ShowPending(a.Contours);
                // F3: the berm and the dig on the mountain, and the water at full pool.
                if (a.Rebuild != null && host.Earthwork != null) _water.Show(_ctx, a.Rebuild.Edits, a.Build, host.Earthwork.Site, null, null, _editing?.Id.Value ?? 0);
                else _water.Clear(_ctx);
                Review();
            });
        }

        // keepWater: a new analysis is on its way, so the old water and ground stay until it lands (no flicker).
        void DropAnalysis(bool keepWater = false)
        {
            _analysis = null;
            if (_ctx == null) return;
            _ctx.PendingTerrain = null;
            _ctx.Earthwork?.ClearPending();
            if (!keepWater) _water.Clear(_ctx);
        }

        // The review (the mockup's pondpool): water, depth, berm, the bill and the trees.
        void Review()
        {
            var w = _ctx.Window;
            var b = _analysis?.Build;
            _review.Clear();
            _review.Add("Full pool", ToolFormat.Elevation(_poolMm / 1000.0) + (_suggested ? " · suggested" : ""), "PD-03");
            _review.Add("Excavation", ToolFormat.Depth(_digMm / 1000.0), "PD-02");
            _review.Add("Fills", _fill == PondFill.Pumped ? "pumped only: a pump house fills it from another source (Water lines, coming next)" : "naturally: runoff and any spring", "WS-09");
            _review.Add("Starts", _startsEmpty ? "empty" : "full", "WS-10");
            if (b != null)
            {
                _review.Add("Water", $"{ToolFormat.Area(b.RingSquareMetres)} · {ToolFormat.Water(b.VolumeCubicMetres)} · {ToolFormat.Gallons(b.VolumeCubicMetres)}", "PD-09");
                _review.Add("Depth", $"mean {ToolFormat.Depth(b.MeanDepthMetres)} · deepest {ToolFormat.Depth(b.MaxDepthMetres)}");
                _review.Add("Berm", $"crest {ToolFormat.Elevation(b.CrestMetres)} · up to {ToolFormat.Depth(b.BermMaxMetres)} high", "PD-02");
                _review.Add("Cut · fill", $"{ToolFormat.Volume(b.CutCubicMetres)} · {ToolFormat.Volume(b.FillCubicMetres)}", "PD-04");
                _review.Add("Balance", ToolFormat.Volume(b.CutCubicMetres - b.FillCubicMetres, true));
                _review.Add("Trees cleared", "about " + (b.ClearedCells / 90).ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
                if (b.FillCubicMetres > b.CutCubicMetres * 1.25) _review.Warn("The berm needs more fill than the dig gives: dig deeper ( ] ) to balance it.", "PD-04");
                if (b.Warning != null && _analysis?.Problem == null) _review.Warn(b.Warning, "PD-11");
            }
            if (_analysis?.Problem != null) _review.Warn(_analysis.Problem, null, true);
            bool ok = _analysis?.Problem == null && _analysis?.Rebuild != null && (_editing == null || Changed);
            _review.Ready(ok, false);
            w.ShowReview(_review);
            w.SetMessage(_analysing ? "Grading…" : null);
            w.SetCommit(ok, false);
        }

        void DrawRing(int hover, PointMm at = default)
        {
            int n = _ring.Count + (hover >= 0 ? 1 : 0);
            if (_xz.Length < n + 1) _xz = new Vector2[n * 2];
            for (int i = 0; i < _ring.Count; i++) _xz[i] = Xz(_ring[i]);
            if (hover >= 0) _xz[_ring.Count] = Xz(at);
            var p = _ctx.Preview;
            if (n >= 2) p.Line(RingSlot, _xz, n, _analysis?.Problem != null ? PreviewInk.Bad : PreviewInk.Line, _step == Step.Pool);
            else p.Hide(RingSlot);
            if (_ring.Count > 0) p.Dot(FirstSlot, _xz[0], PreviewInk.Snap); else p.Hide(FirstSlot);
            for (int i = DotSlots; i < DotSlots + PondRules.MaxRingPoints; i++) p.Hide(i);   // not HideFrom: the water preview's slots sit above
            for (int i = 1; i < _ring.Count; i++) p.Dot(DotSlots + i - 1, _xz[i], i == _drag ? PreviewInk.Snap : PreviewInk.Line);
        }

        static Vector2 Xz(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);

        bool SameRing(IReadOnlyList<PointMm> ring)
        {
            if (ring.Count != _ring.Count) return false;
            for (int i = 0; i < ring.Count; i++) if (!ring[i].Equals(_ring[i])) return false;
            return true;
        }

        public ToolBackOut Escape()
        {
            if (_editing != null) return ToolBackOut.Close;
            if (_step == Step.Pool) { _step = Step.Shore; DropAnalysis(); _ctx.Window.SetStep(0); _ctx.Window.ShowReview(null); _ctx.Window.ShowOption("fill", false); _ctx.Window.ShowOption("start", false); _ctx.Window.SetCommit(false, false); DrawRing(-1); return ToolBackOut.Stepped; }
            if (_ring.Count > 0) { Restart(); return ToolBackOut.Stepped; }
            return ToolBackOut.Close;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (stage != WorkStage.Built || _analysis?.Problem != null || _analysis?.Rebuild == null) return null;
            return Command(baseRevision);
        }

        public void Committed(CommandResult result)
        {
            if (_analysis?.Rebuild != null && _ctx.History.Terrain == _analysis.Rebuild.Edits) _ctx.Earthwork?.Earthwork?.Accept(_analysis.Rebuild);
            _ctx.Earthwork?.ClearPending();
            _water.Clear(_ctx);   // the built water and ground take over
            _ctx.Notice?.Invoke(_editing != null ? _name + " regraded" : _name + " dug");
            if (_editing != null) { _editing = _ctx.History.Document.FindPond(_editing.Id); _analysis = null; Review(); return; }
            Restart();
        }

        public void AskBeforeLeaving(Action then) => _ctx.Window.SetAskRow("Leave without building this pond?", GameAction.ToolNext,
            ("leave", "Leave", () => { _ring.Clear(); then(); }), ("stay", "Stay", () => _ctx.Window.SetAskRow(null, GameAction.ToolNext)));

        public void Disarm()
        {
            if (_ctx == null) return;
            _ctx.Earthwork?.ClearPending();
            _water.Clear(_ctx);
            _ctx.Preview.Clear();
            _ctx = null;
        }

        /// <summary>The Fills and Starts switches, as a click on them (tests and -watershots).</summary>
        public void SetWaterOptions(PondFill fill, bool startsEmpty)
        {
            _fill = fill;
            _startsEmpty = startsEmpty;
            _setFill?.Invoke((int)fill);
            _setStart?.Invoke(startsEmpty ? 1 : 0);
            if (_step == Step.Pool) Review();
        }

        // For tests.
        public int Points => _ring.Count;
        public int PoolMm => _poolMm;
        public int DigMm => _digMm;
        public string Problem => _analysis?.Problem;
        public bool Analysing => _analysing;
    }
}
