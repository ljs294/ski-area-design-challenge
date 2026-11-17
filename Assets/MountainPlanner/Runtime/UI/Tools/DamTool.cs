using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Water;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.14's Dam tool (PD-05…07; the mockup's Snowmaking tab, Dam, B): the first click sets the bank and full pool
    /// (its ground height); the far bank snaps to the same contour within 250 ft of the pointer and at least 16 ft away;
    /// the line must cross exactly one stream, which says where upstream is. Then the review: the embankment, the
    /// reservoir, the stream's inflow and its fill time, the spillway bank (PD-07) and the channel width. PgUp/PgDn move
    /// full pool a foot and re-snap both banks. Edit (<see cref="EditWaterTool"/>) opens the review on a built dam.
    /// </summary>
    public sealed class DamTool : IDesignTool, IKeepsWork
    {
        public const string ToolId = "dam";
        const int BankASlot = 0, BankBSlot = 1, LineSlot = 2, CrossSlot = 3;
        const int FootMm = 305, Rays = 32;
        static readonly string[] Spills = { "First bank", "Far bank" };
        static readonly string[] Starts = { "Full", "Empty" };

        enum Step { First, Far, Review }

        ToolContext _ctx;
        Step _step;
        string _name;
        PointMm _a, _b;
        bool _hasB, _snapFree;
        int _poolMm, _widthCm;
        SpillBank _spill;
        bool _startsEmpty;
        Action<int> _setStart;
        StreamCrossing? _stream;
        bool _upstreamLeft;
        Dam _editing;
        Analysis _analysis;
        bool _analysing;
        Action<int> _setSpill;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _xz = new Vector2[2];
        readonly WaterPreview _water = new WaterPreview();
        int _tipFt = int.MinValue;

        sealed class Analysis
        {
            public string Problem;
            public DesignEarthwork.Result Rebuild;
            public DamBuild Build;
            public List<double> Contours;
        }

        public DamTool() { }

        /// <summary>Edit (task 4.14): the review of a built dam: its pool, spillway bank and channel width.</summary>
        public DamTool(Dam editing) { _editing = editing; }

        public string Id => ToolId;
        public string Name => _editing != null ? "Edit dam" : "New dam";
        public bool HasWork => _editing != null ? Changed : _step != Step.First;
        bool Changed => _editing != null && (!Current().SameWork(_editing) || _name != _editing.Name || _startsEmpty != _editing.StartsEmpty
                                              || _widthCm != RecordWidthCm());

        // WS-03, LK-10: the width lives on the stream's record, so every dam on the stream reads the same one.
        ulong Way => _stream?.Way ?? _editing?.StreamWay ?? 0;
        int RecordWidthCm() => Way != 0 ? _ctx.History.Document.FindStream(Way).WidthCm : 0;
        int StreamWidthArg() => Way != 0 && _widthCm != RecordWidthCm() ? _widthCm : -1;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();
            _water.ShowStreams(_ctx, true);   // F3: where a dam can start
            var w = context.Window;
            w.SetSteps(new[] { "First bank", "Far bank", "Review" });
            w.SetPlanShown(false);
            w.RemoveOption("spill");
            _setSpill = w.AddSwitch("spill", "Spillway", Spills, 0, i => { _spill = (SpillBank)i; if (_step == Step.Review) Analyse(); },
                "PD-07: a 20 ft notch down to full pool at this bank, so a full reservoir spills there and never overtops");
            w.ShowOption("spill", false);
            w.RemoveOption("start");
            _setStart = w.AddSwitch("start", "Starts", Starts, 0, i => { _startsEmpty = i == 1; if (_step == Step.Review) Review(); },
                "WS-10: a new reservoir starts full, or empty and fills from the stream");
            w.ShowOption("start", false);
            if (_editing != null)
            {
                _name = _editing.Name;
                _a = _editing.BankA;
                _b = _editing.BankB;
                _hasB = true;
                _poolMm = _editing.FullPoolMm;
                _spill = _editing.Spill;
                _startsEmpty = _editing.StartsEmpty;
                _upstreamLeft = _editing.UpstreamLeft;
                _stream = Crossing(_a, _b, out _);
                _widthCm = RecordWidthCm();
                w.SetGoLabel("Regrade");
                EnterReview();
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
            _step = Step.First;
            _hasB = false;
            _stream = null;
            _spill = SpillBank.First;
            _widthCm = 0;
            _startsEmpty = false;
            _name = NextName();
            var w = _ctx.Window;
            w.SetName(_name);
            w.SetRenamable(Rename);
            w.SetStep(0);
            w.SetHint(FirstHint);
            _tipFt = int.MinValue;
            w.SetMessage(null);
            w.SetKeys();
            w.SetNext(false, false);
            w.SetCommit(false, false);
            w.ShowOption("spill", false);
            w.ShowOption("start", false);
            w.ShowReview(null);
            Draw(default, false);
        }

        string NextName()
        {
            var doc = _ctx.History.Document;
            for (int n = doc.Dams.Count + 1; ; n++)
            {
                string name = "Reservoir " + n;
                bool taken = false;
                foreach (var d in doc.Dams) taken |= d.Name == name;
                if (!taken) return name;
            }
        }

        string Rename(string name)
        {
            if (DesignRules.NameProblem(name) is string p) return p;
            _name = name;
            _ctx.Window.SetName(name);
            if (_step == Step.Review) Review();
            return null;
        }

        public void Tick(in ToolInput input)
        {
            if (_editing != null && _ctx.History.Document.FindDam(_editing.Id) == null) { _ctx.Window.SetMessage("That dam is gone (undone or removed)."); return; }
            switch (_step)
            {
                case Step.First: FirstTick(input); break;
                case Step.Far: FarTick(input); break;
                default: ReviewTick(input); break;
            }
        }

        // PD-12: a dam starts beside a stream (no hook in tests without a map).
        bool NearStream(PointMm p) => _ctx.StreamDistance == null || _ctx.StreamDistance(p) * 1000 <= PondRules.DamStartReachMm;

        void FirstTick(in ToolInput input)
        {
            bool near = input.HasGround && !input.OverPanel && NearStream(input.Point);
            Draw(input.Point, input.HasGround && !input.OverPanel, near ? PreviewInk.Line : PreviewInk.Bad);
            Tip(input.HasGround && !input.OverPanel ? GroundAt(input.Point) : double.NaN, near);
            if (!input.Click || input.OverPanel || !input.HasGround) return;
            if (!input.InCore) { _ctx.Window.SetMessage("Keep the dam inside the core.", true); return; }
            if (!near) { _ctx.Window.SetMessage("Click beside a stream: a dam starts within 250 ft of one.", true); return; }
            double g = GroundAt(input.Point);
            if (double.IsNaN(g)) { _ctx.Window.SetMessage("Reading the ground…"); return; }
            _a = input.Point;
            _poolMm = (int)Math.Round(g * 1000);
            _step = Step.Far;
            var w = _ctx.Window;
            w.SetStep(1);
            w.SetMessage(null);
            w.SetHint($"Full pool {ToolFormat.Elevation(_poolMm / 1000.0)}. Click the far bank: it snaps to the same height within 250 ft (the yellow line).");
            _tipFt = int.MinValue;
            Draw(input.Point, true);
            PoolContour();
        }

        const string FirstHint = "Click one bank of a stream (the blue lines), at the height the water should reach: that is full pool.";

        // F3: the first bank's hint carries the pointer's full pool, changed only when it moves a foot.
        void Tip(double ground, bool near)
        {
            int ft = double.IsNaN(ground) ? int.MinValue : (int)Math.Round(ground / 0.3048) * (near ? 1 : -1);
            if (ft == _tipFt) return;
            _tipFt = ft;
            _ctx.Window.SetHint(double.IsNaN(ground) ? FirstHint
                : near ? FirstHint + " Full pool here: " + ToolFormat.Elevation(ground) + "."
                : FirstHint + " Too far from a stream here.");
        }

        // F3: the contour at full pool round the first bank, where the far bank can land (yellow, as the review's contours).
        void PoolContour()
        {
            var host = _ctx.Earthwork;
            if (host == null || !host.Ready) return;
            var heights = host.Heights;
            host.Earthwork.Site.PointOf(_a, out double x, out double y);
            double level = _poolMm / 1000.0;
            _ctx.Analyse(() =>
            {
                var segments = new List<double>();
                PendingContours.AtLevel(heights, x, y, PoolContourRadiusM, 2, level, segments);
                return segments;
            }, segments => { if (_step == Step.Far) host.ShowPending(segments); });
        }

        const double PoolContourRadiusM = 300;

        void FarTick(in ToolInput input)
        {
            if (!input.HasGround || input.OverPanel) { Draw(default, false); return; }
            bool snapped = SnapToPool(input.Point, out var at);
            Draw(snapped ? at : input.Point, true, snapped ? PreviewInk.Snap : PreviewInk.Bad);
            if (!input.Click) return;
            if (!snapped) { _ctx.Window.SetMessage($"No ground at {ToolFormat.Elevation(_poolMm / 1000.0)} within 250 ft of here.", true); return; }
            if (!input.InCore) { _ctx.Window.SetMessage("Keep the dam inside the core.", true); return; }
            if (LineProblem(_a, at) is string p) { _ctx.Window.SetMessage(p, true); return; }
            _b = at;
            _hasB = true;
            EnterReview();
        }

        // PD-05: one stream, exactly; it says where upstream is.
        string LineProblem(PointMm a, PointMm b)
        {
            long len = (long)Math.Sqrt(a.DistanceSquared(b));
            if (len < PondRules.DamMinLengthMm) return "The banks are less than 16 ft apart.";
            if (len > PondRules.DamMaxLengthMm) return "A dam is at most 3,280 ft long.";
            var s = Crossing(a, b, out int count);
            if (count == 0) return "A dam crosses a stream: this line crosses none. Dig a pond instead (Pond, O).";
            if (count > 1) return "This line crosses more than one stream: dam one at a time.";
            _stream = s;
            if (_step != Step.Review) _widthCm = RecordWidthCm();   // a pool move keeps the width set in review
            // No stream line, or its sides tie (the host sends Upstream == At): the higher side is upstream.
            _upstreamLeft = s.HasValue && !s.Value.Upstream.Equals(s.Value.At) ? DamEarthwork.UpstreamLeftOf(a, b, s.Value.Upstream) : HigherSideLeft(a, b);
            return null;
        }

        StreamCrossing? Crossing(PointMm a, PointMm b, out int count)
        {
            var find = _ctx.StreamsCrossing;
            if (find == null) { count = 1; return null; }   // tests without a map: the ground decides
            var all = find(a, b);
            count = all?.Count ?? 0;
            return count == 1 ? all[0] : (StreamCrossing?)null;
        }

        // The fallback (no stream lines): the side whose ground 165 ft out is higher is upstream.
        bool HigherSideLeft(PointMm a, PointMm b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return true;
            double nx = -dy / len * 50_000, ny = dx / len * 50_000;
            var mid = new PointMm((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            double left = GroundAt(new PointMm(mid.X + (int)nx, mid.Y + (int)ny));
            double right = GroundAt(new PointMm(mid.X - (int)nx, mid.Y - (int)ny));
            return !(right > left);
        }

        /// <summary>PD-05: the point within 250 ft of <paramref name="near"/> where the ground is at full pool, nearest the pointer.</summary>
        bool SnapToPool(PointMm near, out PointMm at)
        {
            at = near;
            double pool = _poolMm / 1000.0;
            double g0 = GroundAt(near);
            if (double.IsNaN(g0)) return false;
            if (Math.Abs(g0 - pool) * 1000 <= PondRules.ContourToleranceMm && FarEnough(near)) return true;
            double best = double.MaxValue;
            bool found = false;
            int steps = PondRules.SnapMm / 1000;
            for (int k = 0; k < Rays; k++)
            {
                double ang = k * (2 * Math.PI / Rays), cx = Math.Cos(ang), cy = Math.Sin(ang);
                double prev = g0 - pool;
                for (int s = 1; s <= steps; s++)
                {
                    var q = new PointMm(near.X + (int)(cx * s * 1000), near.Y + (int)(cy * s * 1000));
                    double d = GroundAt(q) - pool;
                    if (double.IsNaN(d)) break;
                    if (Math.Sign(d) != Math.Sign(prev) || Math.Abs(d) * 1000 <= PondRules.ContourToleranceMm)
                    {
                        // Between s-1 and s: the straight-line crossing.
                        double t = prev == d ? 1 : prev / (prev - d);
                        double r = (s - 1 + t) * 1000;
                        var p = new PointMm(near.X + (int)(cx * r), near.Y + (int)(cy * r));
                        if (r < best && FarEnough(p)) { best = r; at = p; found = true; }
                        break;
                    }
                    prev = d;
                }
            }
            return found;
        }

        bool FarEnough(PointMm p) => _snapFree || !p.Within(_a, PondRules.DamMinLengthMm);

        double GroundAt(PointMm p)
        {
            var host = _ctx.Earthwork;
            if (host == null || !host.Ready) return double.NaN;
            host.Earthwork.Site.PointOf(p, out double x, out double y);
            return host.Heights.At(x, y);
        }

        void EnterReview()
        {
            _step = Step.Review;
            var w = _ctx.Window;
            w.SetStep(2);
            w.SetName(_name);
            w.SetRenamable(Rename);
            w.SetHint(_editing != null
                ? "PgUp and PgDn move full pool a foot (both banks re-snap); [ and ] the stream's channel width."
                : "PgUp and PgDn move full pool a foot (both banks re-snap); [ and ] the stream's channel width.");
            w.SetKeys((GameAction.TargetUp, "pool up"), (GameAction.TargetDown, "pool down"), (GameAction.BrushWider, "wider"), (GameAction.BrushNarrower, "narrower"));
            w.ShowOption("spill", true);
            _setSpill?.Invoke((int)_spill);
            w.ShowOption("start", true);
            _setStart?.Invoke(_startsEmpty ? 1 : 0);
            Draw(default, false);
            Analyse();
        }

        void ReviewTick(in ToolInput input)
        {
            bool changed = false;
            if (input.Pressed(ToolKeys.TargetUp)) changed |= MovePool(FootMm);
            if (input.Pressed(ToolKeys.TargetDown)) changed |= MovePool(-FootMm);
            if ((input.Pressed(ToolKeys.BrushWider) || input.Pressed(ToolKeys.BrushNarrower)) && Way == 0)
                _ctx.Window.SetMessage("This area has no stream lines yet, so the width stays the 8 ft default.");
            else if (input.Pressed(ToolKeys.BrushWider) || input.Pressed(ToolKeys.BrushNarrower))
            {
                int now = _widthCm > 0 ? _widthCm : (int)Math.Round(StreamWidthMetres() * 100);
                int next = now + (input.Pressed(ToolKeys.BrushWider) ? 30 : -30);
                _widthCm = Math.Max(30, Math.Min(PondRules.WidthMaxCm, next));
                Review();   // the width moves the inflow only, not the ground
            }
            if (changed) Analyse();
        }

        // Moving the pool slides each bank along the slope to the new contour (PD-05: within 250 ft).
        bool MovePool(int byMm)
        {
            int old = _poolMm;
            var a0 = _a;
            _poolMm += byMm;
            var keepB = _b;
            _snapFree = true;   // FarEnough measures from bank A: no limit while bank A itself re-snaps
            bool okA = SnapToPool(a0, out var a);
            _snapFree = false;
            _a = okA ? a : a0;
            PointMm b = keepB;
            bool okB = okA && SnapToPool(keepB, out b);
            if (!okA || !okB || LineProblem(_a, b) != null)
            {
                _poolMm = old;
                _a = a0;
                _b = keepB;
                _ctx.Window.SetMessage("The banks can't move to that pool: no ground at that height nearby.", true);
                return false;
            }
            _b = b;
            return true;
        }

        double StreamWidthMetres() => _stream?.WidthMetres ?? StreamFlow.DefaultWidthMetres("stream");

        Dam Current() => new Dam(_editing?.Id ?? default, _name, _a, _b, _poolMm, _upstreamLeft, _spill, Way, _startsEmpty);

        DesignCommand Command(long revision) => _editing != null
            ? new EditDam(revision, Current(), StreamWidthArg())
            : new AddDam(revision, _name, _a, _b, _poolMm, _upstreamLeft, _spill, _stream?.Way ?? 0, _startsEmpty, StreamWidthArg());

        void Analyse()
        {
            DropAnalysis(true);
            var host = _ctx.Earthwork;
            if (host == null || !host.Ready) { Review(); return; }
            _analysing = true;
            var cmd = Command(_ctx.History.Revision);
            ulong key = _editing?.Id.Value ?? 0;
            var doc = _ctx.History.Document;
            var committed = _ctx.History.Terrain;
            var earthwork = host.Earthwork;
            var heights = host.Heights;
            double interval = host.ContourInterval;
            Review();
            _ctx.Analyse(() =>
            {
                var a = new Analysis();
                var after = cmd.Apply(doc);
                if (!after.Accepted) { a.Problem = after.Reason; return a; }
                a.Rebuild = earthwork.Rebuild(after.Document, committed);
                a.Build = (a.Rebuild.DetailOf(key != 0 ? key : after.Created[0]) ?? earthwork.DetailOf(key)) as DamBuild;
                var pending = new EditedHeights(heights.Source, heights.Lattice, a.Rebuild.Edits);
                a.Contours = TrailTool.Contours(heights, pending, a.Rebuild.Changed, interval);
                a.Problem = a.Build?.Problem ?? (a.Build == null ? "This dam moves no ground." : null);
                return a;
            }, a =>
            {
                _analysing = false;
                _analysis = a;
                _ctx.PendingTerrain = a.Problem == null ? a.Rebuild?.Edits : null;
                if (a.Contours != null) host.ShowPending(a.Contours);
                ShowWater(host);
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

        // F3: the graded ground, the reservoir at full pool, its shore, the spillway and where a refusal happens.
        void ShowWater(IEarthworkHost host)
        {
            var b = _analysis?.Build;
            if (_analysis?.Rebuild == null || host?.Earthwork == null) { _water.Clear(_ctx); return; }
            Vector2? spill = b != null ? new Vector2((float)b.SpillX, (float)b.SpillY) : (Vector2?)null;
            Vector2? problem = b != null && !double.IsNaN(b.ProblemX) ? new Vector2((float)b.ProblemX, (float)b.ProblemY) : (Vector2?)null;
            _water.Show(_ctx, _analysis.Rebuild.Edits, b, host.Earthwork.Site, spill, problem, _editing?.Id.Value ?? 0);
        }

        // The review (the mockup's damreview): the embankment, the reservoir, the stream and its fill time.
        void Review()
        {
            var w = _ctx.Window;
            var b = _analysis?.Build;
            _review.Clear();
            _review.Add("Full pool", ToolFormat.Elevation(_poolMm / 1000.0), "PD-05: the first bank's height");
            if (b != null)
            {
                _review.Add("Dam", $"{ToolFormat.Length(b.LengthMetres)} long · up to {ToolFormat.Depth(b.HeightMaxMetres)} high (mean {ToolFormat.Depth(b.HeightMeanMetres)})", "PD-06")
                    .Add("Crest", ToolFormat.Elevation(b.CrestMetres), "PD-06: 2 ft over full pool");
                _review.Add("Reservoir", $"{ToolFormat.Area(b.AreaSquareMetres)} · {ToolFormat.Water(b.VolumeCubicMetres)} · {ToolFormat.Gallons(b.VolumeCubicMetres)}", "PD-09")
                    .Add("Depth", $"mean {ToolFormat.Depth(b.MeanDepthMetres)} · deepest {ToolFormat.Depth(b.MaxDepthMetres)}")
                    .Add("Fill", ToolFormat.Volume(b.FillCubicMetres), "PD-06: embankment fill only")
                    .Add("Over the stream", $"full pool {ToolFormat.Depth(b.PoolOverBedMetres)} over the bed", "PD-13: at least 5 ft")
                    .Add("Embankment", $"{ToolFormat.Length(b.EmbankmentMetres)} of the {ToolFormat.Length(b.LengthMetres)} line stands", "PD-14")
                    .Add("Water per fill", b.FillCubicMetres > 0 ? WaterPerFill(b) : "no fill", "PD-11, PD-14: under 1:1 is poor, under 1:10 refused");
            }
            double widthM = _widthCm > 0 ? _widthCm / 100.0 : StreamWidthMetres();
            string widthFrom = _widthCm > 0 ? "yours (the stream's)" : _stream?.WidthFromOsm == true ? "from the map" : "default";
            _review.Add("Stream", _stream.HasValue ? _stream.Value.Label : "streams body (no stream lines in this area)", "WS-08");
            _review.Add("Width", $"{ToolFormat.Depth(widthM)} · {widthFrom}", "WS-03: [ and ] change it");
            double cfs = StreamFlow.BaseCfs(widthM / 0.3048);
            _review.Add("Inflow", $"{ToolFormat.Flow(cfs)} (Dec–Feb {ToolFormat.Flow(cfs * StreamFlow.SeasonFactor(1))})", "WS-03");
            if (b != null && cfs > 0)
            {
                double days = b.VolumeCubicMetres / (cfs * StreamFlow.SeasonFactor(1) * 0.028316846592 * 86400);
                _review.Add("Fills in", days < 1 ? "under a day" : $"{Math.Ceiling(days):0} days in winter", "WS-03");
            }
            _review.Add("Spillway", _spill == SpillBank.First ? "at the first bank" : "at the far bank", "PD-07");
            _review.Add("Starts", _startsEmpty ? "empty: fills from the stream" : "full", "WS-10");
            if (b?.Warning != null && _analysis?.Problem == null) _review.Warn(b.Warning, "PD-11");
            if (_analysis?.Problem != null) _review.Warn(_analysis.Problem, "PD-06", true);
            bool ok = !_analysing && _analysis?.Problem == null && _analysis?.Rebuild != null && (_editing == null || Changed);
            _review.Ready(ok, false);
            w.ShowReview(_review);
            w.SetMessage(_analysing ? "Grading…" : null);
            w.SetCommit(ok, false);
            Draw(default, false);
        }

        // PD-14: water held per cubic yard of fill, in gallons, and the ratio by volume.
        static string WaterPerFill(DamBuild b)
        {
            double ratio = b.VolumeCubicMetres / b.FillCubicMetres;
            double galPerYd3 = ratio * 0.764554857984 * 264.172052;
            return $"{galPerYd3.ToString(galPerYd3 >= 100 ? "N0" : "0.#", System.Globalization.CultureInfo.InvariantCulture)} gal per yd³ (1 : {(1 / Math.Max(ratio, 1e-9)).ToString(ratio >= 1 ? "0.##" : "0.#", System.Globalization.CultureInfo.InvariantCulture)})";
        }

        void Draw(PointMm hover, bool showHover, PreviewInk hoverInk = PreviewInk.Line)
        {
            var p = _ctx.Preview;
            if (_step == Step.First)
            {
                p.Hide(BankASlot); p.Hide(BankBSlot); p.Hide(LineSlot); p.Hide(CrossSlot);
                if (showHover) p.Dot(BankASlot, Xz(hover), hoverInk);
                return;
            }
            p.Dot(BankASlot, Xz(_a), PreviewInk.Snap);
            var end = _hasB && _step == Step.Review ? _b : hover;
            bool shown = _step == Step.Review || showHover;
            if (!shown) { p.Hide(BankBSlot); p.Hide(LineSlot); }
            else
            {
                var ink = _step == Step.Review ? (_analysis?.Problem != null ? PreviewInk.Bad : PreviewInk.Ok) : hoverInk;
                p.Dot(BankBSlot, Xz(end), ink);
                _xz[0] = Xz(_a);
                _xz[1] = Xz(end);
                p.Line(LineSlot, _xz, 2, ink);
            }
            if (_stream.HasValue && _step == Step.Review) p.Dot(CrossSlot, Xz(_stream.Value.At), PreviewInk.Snap); else p.Hide(CrossSlot);
        }

        static Vector2 Xz(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);

        public ToolBackOut Escape()
        {
            if (_editing != null) return ToolBackOut.Close;
            if (_step == Step.Review) { DropAnalysis(); _hasB = false; _step = Step.Far; _ctx.Window.SetStep(1); _ctx.Window.ShowReview(null); _ctx.Window.ShowOption("spill", false); _ctx.Window.ShowOption("start", false); _ctx.Window.SetCommit(false, false); _ctx.Window.SetKeys(); PoolContour(); return ToolBackOut.Stepped; }
            if (_step == Step.Far) { Restart(); return ToolBackOut.Stepped; }
            return ToolBackOut.Close;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (stage != WorkStage.Built || _step != Step.Review || _analysis?.Problem != null || _analysis?.Rebuild == null) return null;
            return Command(baseRevision);
        }

        public void Committed(CommandResult result)
        {
            if (_analysis?.Rebuild != null && _ctx.History.Terrain == _analysis.Rebuild.Edits) _ctx.Earthwork?.Earthwork?.Accept(_analysis.Rebuild);
            _ctx.Earthwork?.ClearPending();
            _water.Clear(_ctx);   // the built water and ground take over
            _ctx.Notice?.Invoke(_editing != null ? _name + " regraded" : _name + " built");
            if (_editing != null) { _editing = _ctx.History.Document.FindDam(_editing.Id); _analysis = null; Review(); return; }
            Restart();
        }

        public void AskBeforeLeaving(Action then) => _ctx.Window.SetAskRow("Leave without building this dam?", GameAction.ToolNext,
            ("leave", "Leave", () => { _step = Step.First; then(); }), ("stay", "Stay", () => _ctx.Window.SetAskRow(null, GameAction.ToolNext)));

        public void Disarm()
        {
            if (_ctx == null) return;
            _ctx.Earthwork?.ClearPending();
            _water.Clear(_ctx);
            _water.ShowStreams(_ctx, false);
            _ctx.Preview.Clear();
            _ctx = null;
        }

        // For tests.
        public int PoolMm => _poolMm;
        public PointMm BankA => _a;
        public PointMm BankB => _b;
        public bool UpstreamLeft => _upstreamLeft;
        public SpillBank Spill => _spill;
        public string Problem => _analysis?.Problem;
        public bool InReview => _step == Step.Review;
        public bool Analysing => _analysing;
    }
}
