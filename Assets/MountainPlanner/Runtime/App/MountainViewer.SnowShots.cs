using System;
using System.Collections;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// -snowshots &lt;folder&gt; (task 4.15 PR B review): the Snowmaking network's tools driven by script on whatever area
    /// opens, a picture at each step the owner reviews. A pond on gentle ground with 900 m of climb above it; a pump house
    /// on its shore; a water line from the house up the fall line (drawn, then reviewed); guns on every hydrant; the
    /// pressure layer; Check's problems; a booster and the pressure after it; the lasso. Every figure in the log line
    /// beside its picture comes from the game's own solve, so captions say only what the solve said.
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartSnowShots(string[] args)
        {
            int i = Array.IndexOf(args, "-snowshots");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(SnowShots(args[i + 1]));
        }

        IEnumerator WaitSnowSolve()
        {
            // The worker's solve lands a few ticks after the network is sent: wait for it to stop pending.
            for (int i = 0; i < 1200; i++)
            {
                _tools?.Tick(default);
                bool pending = _snowCompile != null || !_simSnowNetSent || (_simNow?.SnowSolvePending ?? true);
                if (!pending && _simNow?.Snowmaking != null) break;
                yield return null;
            }
            for (int i = 0; i < 10; i++) yield return null;
        }

        void LogSolve(string shot)
        {
            var s = _snowCheckOn ? _simNow?.SnowCheck : _simNow?.Snowmaking;
            if (s == null) { Debug.Log($"[SnowShots] {shot}: no solve yet"); return; }
            int run = 0, shortP = 0;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < s.GunStates.Length; i++)
            {
                if (s.GunStates[i] == GunState.Running || s.GunStates[i] == GunState.RunningWarm) run++;
                if (s.GunStates[i] == GunState.ShortOfPressure) shortP++;
                if (!float.IsNaN(s.GunPsi[i])) { lo = Mathf.Min(lo, s.GunPsi[i]); hi = Mathf.Max(hi, s.GunPsi[i]); }
            }
            Debug.Log($"[SnowShots] {shot}: solved {s.Solved}, what-if {s.WhatIf}, {s.GunStates.Length} guns, {run} running, {shortP} short, " +
                      $"gun psi {(lo <= hi ? $"{lo:0}–{hi:0}" : "-")}, {s.DemandGpm:0} gpm, {s.Diagnostics.Length} problems: " +
                      string.Join("; ", s.Diagnostics.Take(6).Select(d => d.ToString())));
        }

        IEnumerator SnowShots(string dir)
        {
            Directory.CreateDirectory(dir);
            StartCoroutine(QuitAfter(1800));   // -simto first can take minutes
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null || _resort.WaterBodies == null) yield return null;
            Ground.Start();
            float give = Time.unscaledTime + 300;
            while (!Ground.Ready && Time.unscaledTime < give) yield return null;
            if (!Ground.Ready) { Debug.LogError("[SnowShots] the ground lattice never came ready"); Application.Quit(1); yield break; }
            // With -simto the season skips to a cold morning first, so the guns run at their stages instead of too warm.
            float simToStart = Time.unscaledTime;
            while (_simToRunning) yield return null;
            if (Time.unscaledTime - simToStart > 1) Debug.Log($"[SnowShots] -simto took {Time.unscaledTime - simToStart:0} s");
            // Paused, the light is the player's (question 7): late morning, so the pictures aren't dawn-dark. The clock stays.
            Lighting.SetTime(Lighting.Clock.Now.WithSecondOfDay(11 * 3600 + 30 * 60));
            if (Camera != null) Camera.InputEnabled = false;
            var core = _resort.Core;
            for (int i = 0; i < 60; i++) yield return null;

            // ---- The site: a gentle dry 60 m circle with a dry line 900 m up the fall line from its edge, inside the core.
            const float r = 30, lineLen = 900;
            Vector2 pondAt = default, up = default;
            float best = float.MaxValue;
            for (float x = core.xMin + 120; x <= core.xMax - 120; x += 40)
            for (float z = core.yMin + 120; z <= core.yMax - 120; z += 40)
            {
                var c = new Vector2(x, z);
                var g = new Vector2(H(c + Vector2.right * 25) - H(c - Vector2.right * 25), H(c + Vector2.up * 25) - H(c - Vector2.up * 25));
                if (float.IsNaN(g.x) || float.IsNaN(g.y) || g.sqrMagnitude < 1e-3f) continue;
                var dirUp = g.normalized;
                var end = c + dirUp * (r + 40 + lineLen);
                if (!core.Contains(end)) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                bool dry = true;
                for (int k = 0; k < 16 && dry; k++)
                {
                    float a = k * Mathf.PI / 8;
                    for (float rr = 0; rr <= r + 20 && dry; rr += 12.5f)
                    {
                        var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
                        float h = H(p);
                        if (float.IsNaN(h) || WaterUnder(PointMm.FromMetres(p.x, p.y)) != null) dry = false;
                        else if (rr <= r) { lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h); }
                    }
                }
                for (float u = r + 20; u <= r + 40 + lineLen && dry; u += 25)
                    if (float.IsNaN(H(c + dirUp * u)) || WaterUnder(PointMm.FromMetres((c + dirUp * u).x, (c + dirUp * u).y)) != null) dry = false;
                if (!dry) continue;
                float climb = H(end) - H(c);
                // About 3 m of fall across the pond, and as near 280 m of climb as the area has (the top guns go short).
                float score = Mathf.Abs(hi - lo - 3f) * 10 + Mathf.Abs(climb - 280f);
                if (score < best) { best = score; pondAt = c; up = dirUp; }
            }
            if (best == float.MaxValue) { Debug.LogError("[SnowShots] no site fits"); Application.Quit(1); yield break; }
            var side = new Vector2(-up.y, up.x);
            Debug.Log($"[SnowShots] pond at {pondAt}, up {up}, climb {H(pondAt + up * (r + 40 + lineLen)) - H(pondAt):0} m");

            // ---- The pond (4.14's tool), natural and full, so the network has a source.
            Hud.SetToolboxTab("snow");
            Hud.SetToolbox(true);
            _tools.Choose(PondTool.ToolId);
            var pond = (PondTool)_tools.Active;
            _toolScripted = true;
            for (int k = 0; k < 9; k++)
            {
                float a = k * Mathf.PI * 2 / 9;
                ClickAt(pondAt + new Vector2(Mathf.Cos(a) * r * (k % 2 == 0 ? 1f : 0.85f), Mathf.Sin(a) * r));
            }
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return WaitWaterTool(() => pond.Analysing);
            pond.SetWaterOptions(PondFill.Natural, false);
            yield return WaitWaterTool(() => pond.Analysing);
            Debug.Log($"[SnowShots] pond: {pond.Problem ?? "ok"}; {_tools.Confirm(WorkStage.Built)?.Reason ?? "built"}");
            _toolScripted = false;
            _tools.Close();
            for (int i = 0; i < 900 && _resort.WaterBodies.Ponds < 1; i++) yield return null;
            for (int i = 0; i < 30; i++) yield return null;

            var house = pondAt + up * (r + 22);
            var lineTop = pondAt + up * (r + 40 + lineLen);
            var lineMid = (house + lineTop) / 2;

            // 01: the tray: the Water group, then the network's five tools.
            LookAt(house, 260, Yaw(up), 48);
            yield return Shot(dir, "01-snow-tray");

            // 02: the pump house review beside the pond.
            _tools.Choose(PumpHouseTool.ToolId);
            var ph = (PumpHouseTool)_tools.Active;
            _toolScripted = true;
            ClickAt(house);
            ClickAt(house + side * 20);
            yield return WaitWaterTool(() => false);
            LookAt(house, 190, Yaw(up), 50);
            Debug.Log($"[SnowShots] house review: {ph.Reviewing}, source {ph.Source:x}");
            yield return Shot(dir, "02-pump-house-review");
            Debug.Log($"[SnowShots] house: {_tools.Confirm(WorkStage.Built)?.Reason ?? "built"}");
            _toolScripted = false;
            _tools.Close();

            // 03, 04: the water line up the fall line: the live readout while drawing, then its review.
            _tools.Choose(WaterLineTool.ToolId);
            var line = (WaterLineTool)_tools.Active;
            _toolScripted = true;
            ClickAt(house);
            ClickAt(house + up * (lineLen * 0.35f) + side * 25);
            _tools.Tick(GroundInput(lineTop - side * 15));
            for (int i = 0; i < 600 && (line.Last == null || line.Solving); i++) { _tools.Tick(GroundInput(lineTop - side * 15)); yield return null; }
            LookAt(lineMid, lineLen * 1.05f, Yaw(up) + 25, 52);
            Debug.Log($"[SnowShots] drawing: end {line.Last?.EndPsi:0} psi, {line.Last?.Guns} guns, {line.Last?.Short} short, {line.Last?.MaxVelocityFtS:0.0} ft/s");
            yield return Shot(dir, "03-line-drawing-live");
            ClickAt(lineTop - side * 15);
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            for (int i = 0; i < 600 && (line.Last == null || line.Solving || _tools.Context.Running > 0); i++) { _tools.Tick(default); yield return null; }
            Debug.Log($"[SnowShots] review: {line.Reviewing}, {line.Last?.Hydrants} hydrants, end {line.Last?.EndPsi:0} psi, {line.Last?.Short} of {line.Last?.Guns} short");
            yield return Shot(dir, "04-line-review");
            Debug.Log($"[SnowShots] line: {_tools.Confirm(WorkStage.Built)?.Reason ?? "laid"}");
            _toolScripted = false;
            _tools.Close();
            var pipe = Design.Document.Snow.Pipes.LastOrDefault();
            if (pipe == null) { Debug.LogError("[SnowShots] no line was laid"); Application.Quit(1); yield break; }

            // 05: Fill hydrants: a gun on every hydrant of the line, set On.
            _tools.Choose(SnowGunTool.ToolId);
            var gun = (SnowGunTool)_tools.Active;
            _toolScripted = true;
            gun.Choose(GunModel.Stick20, 1);
            ClickAt(Xz(SnowGeometry.At(pipe.Route, SnowGeometry.Length(pipe.Route) / 2)));
            yield return WaitSnowSolve();
            // Close enough to see each gun beside its hydrant, a quarter of the way up.
            LookAt(Xz(SnowGeometry.At(pipe.Route, SnowGeometry.Length(pipe.Route) * 0.25)), 150, Yaw(up) + 30, 50);
            LogSolve("05-guns-filled");
            yield return Shot(dir, "05-guns-filled");
            _toolScripted = false;
            _tools.Close();

            // 06: the Pipe pressure info layer.
            Hud.SetToolbox(false);
            SetLayerForCapture(MapLayers.Pressure, true);
            yield return WaitSnowSolve();
            LookAt(lineMid, lineLen * 0.95f, Yaw(up) + 25, 55);   // the whole line: the colour falls with the climb
            LogSolve("06-pressure-layer");
            yield return Shot(dir, "06-pressure-layer");
            SetLayerForCapture(MapLayers.Pressure, false);

            // 07: Check: the problems by number, Show me and the fixes.
            Hud.SetToolbox(true);
            _tools.Choose(CheckNetworkTool.ToolId);
            yield return WaitSnowSolve();
            for (int i = 0; i < 10; i++) { _tools.Tick(default); yield return null; }
            LogSolve("07-check-problems");
            yield return Shot(dir, "07-check-problems");
            // 08: every gun running (the what-if).
            ((CheckNetworkTool)_tools.Active).Read(true);
            for (int i = 0; i < 600 && _simNow?.SnowCheck == null; i++) { _tools.Tick(default); yield return null; }
            yield return WaitSnowSolve();
            for (int i = 0; i < 10; i++) { _tools.Tick(default); yield return null; }
            LogSolve("08-check-every-gun");
            yield return Shot(dir, "08-check-every-gun");
            _tools.Close();

            // 09, 10: a booster a third of the way up: the arrow, then the pressure after it.
            _tools.Choose(InlinePumpTool.ToolId);
            _toolScripted = true;
            // Halfway between the two hydrants either side of 42% up the line (a booster keeps 10 ft from any tap).
            var stations = SnowRoutes.Stations(Design.Document.Snow, pipe);
            double want = SnowGeometry.Length(pipe.Route) * 0.42, mid = want;
            for (int k = 0; k + 1 < stations.Count; k++)
                if (stations[k].C <= want && want <= stations[k + 1].C) { mid = (stations[k].C + stations[k + 1].C) / 2; break; }
            var boostAt = Xz(SnowGeometry.At(pipe.Route, mid));
            for (int i = 0; i < 5; i++) { _tools.Tick(GroundInput(boostAt)); yield return null; }
            LookAt(boostAt, 220, Yaw(up) + 25, 55);
            yield return Shot(dir, "09-inline-pump-arrow");
            ClickAt(boostAt);
            _toolScripted = false;
            _tools.Close();
            Hud.SetToolbox(false);
            SetLayerForCapture(MapLayers.Pressure, true);
            yield return WaitSnowSolve();
            LookAt(lineMid, lineLen * 0.95f, Yaw(up) + 25, 55);
            LogSolve("10-pressure-after-booster");
            yield return Shot(dir, "10-pressure-after-booster");
            SetLayerForCapture(MapLayers.Pressure, false);

            // 11: the lasso: the top half's guns selected, Auto / On / Off offered.
            Hud.SetToolbox(true);
            _tools.Choose(SnowGunTool.ToolId);
            gun = (SnowGunTool)_tools.Active;
            _toolScripted = true;
            gun.Choose(GunModel.Stick20, 2);
            float half = (float)SnowGeometry.Length(pipe.Route) / 2000f;
            gun.Pick(Design.Document.Snow.Guns.Where(g => Vector2.Dot(Xz(g.At) - house, up) > half).Select(g => g.Id).ToList());
            for (int i = 0; i < 10; i++) { _tools.Tick(default); yield return null; }
            yield return Shot(dir, "11-lasso-selected");
            _toolScripted = false;
            _tools.Close();

            // 12: a booster facing downhill three quarters of the way up (MapLibre's most common mistake, made on purpose):
            // Check names it (7, with Flip pump) and the guns past it that it cuts off, grouped.
            var st2 = SnowRoutes.Stations(Design.Document.Snow, pipe);
            double want2 = SnowGeometry.Length(pipe.Route) * 0.75, mid2 = want2;
            for (int k = 0; k + 1 < st2.Count; k++)
                if (st2[k].C <= want2 && want2 <= st2[k + 1].C) { mid2 = (st2[k].C + st2[k + 1].C) / 2; break; }
            var wrong = new AddSnowTap(Design.Revision, SnowNodeKind.Pump, pipe.Id, SnowGeometry.At(pipe.Route, mid2), forward: false, PumpSpec.Booster);
            Debug.Log($"[SnowShots] wrong-way booster: {Design.Submit(wrong, null).Reason ?? "placed"}");
            _tools.Choose(CheckNetworkTool.ToolId);
            yield return WaitSnowSolve();
            for (int i = 0; i < 10; i++) { _tools.Tick(default); yield return null; }
            LookAt(lineMid, lineLen * 0.95f, Yaw(up) + 25, 55);
            LogSolve("12-check-wrong-way-pump");
            yield return Shot(dir, "12-check-wrong-way-pump");
            _tools.Close();
            Hud.SetToolbox(false);

            // 13–17: the cards, opened as a click with no tool armed opens them; each card's text goes to the log.
            var net = Design.Document.Snow;
            var cut = net.Guns.Where(g => _simNow?.Snowmaking is SnowSolution sol && sol.GunIndex(g.Id) is int gi && gi >= 0 && sol.GunStates[gi] == GunState.NoWater)
                         .OrderBy(g => Vector2.Dot(Xz(g.At) - house, up)).FirstOrDefault();
            if (cut != null)
            {
                ShowWaterCardFor(SnowCards.Key(cut.Id));
                LookAt(Xz(cut.At), 160, Yaw(up) + 30, 50);
                yield return SettleCard();
                LogCard("13-gun-card-cut-off");
                yield return Shot(dir, "13-gun-card-cut-off");
            }
            // The line's card where it was clicked a quarter of the way up, over the pressure layer.
            SetLayerForCapture(MapLayers.Pressure, true);
            _snowCardAtMm = SnowGeometry.Length(pipe.Route) * 0.25;
            ShowWaterCardFor(SnowCards.Key(pipe.Id));
            LookAt(Xz(SnowGeometry.At(pipe.Route, _snowCardAtMm)), 260, Yaw(up) + 25, 55);
            yield return SettleCard();
            LogCard("14-pipe-card");
            yield return Shot(dir, "14-pipe-card");
            SetLayerForCapture(MapLayers.Pressure, false);
            // The wrong-way booster's card, then its Flip button and the card after.
            var bad = net.Nodes.FirstOrDefault(n => n.Kind == SnowNodeKind.Pump && !n.Forward);
            if (bad != null)
            {
                ShowWaterCardFor(SnowCards.Key(bad.Id));
                LookAt(Xz(bad.At), 200, Yaw(up) + 25, 55);
                yield return SettleCard();
                LogCard("15-pump-card-wrong-way");
                yield return Shot(dir, "15-pump-card-wrong-way");
                Hud.PressWaterCardForTest("flip");
                yield return WaitSnowSolve();
                yield return SettleCard();
                LogSolve("16-pump-card-flipped");
                LogCard("16-pump-card-flipped");
                yield return Shot(dir, "16-pump-card-flipped");
            }
            // The pump house's card, and the network's names on the map.
            var built = Design.Document.Snow.Houses.FirstOrDefault();
            if (built != null)
            {
                ShowWaterCardFor(SnowCards.Key(built.Id));
                LookAt(Xz(built.Centre) + up * 90, 260, Yaw(up) + 25, 55);
                yield return SettleCard();
                LogCard("17-pump-house-card-labels");
                yield return Shot(dir, "17-pump-house-card-labels");
            }
            Hud.CloseWaterCardIfOpen();

            Debug.Log("[SnowShots] done");
            Application.Quit(0);
        }

        IEnumerator SettleCard()
        {
            for (int i = 0; i < 20; i++) yield return null;   // the card follows the solve; the labels place over a few frames
        }

        void LogCard(string shot) => Debug.Log($"[SnowShots] {shot}: {Hud.WaterCardTextForTest() ?? "no card"}");

        float H(Vector2 p) => _resort.Surface.HeightAt(p.x, p.y);
        static Vector2 Xz(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);
        /// <summary>A camera yaw looking up the line (the camera stands below it).</summary>
        static float Yaw(Vector2 dir) => Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
    }
}
