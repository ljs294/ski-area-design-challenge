using System;
using System.Collections;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Water;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// -watershots &lt;folder&gt; (task 4.14 review): the Snowmaking tools driven by script on whatever area opens, a picture
    /// per step: the review set for demo 117 (F1, F3, F4, F5). It finds its own spots, so it works on any area: a dam across
    /// the first stream cell that takes one (with a refused low dam and a refused bank away from a stream), a pumped pond
    /// on the gentlest ground clear of water, and the cards for the pond, the dam, its stream and the first lake in the core. Not a test: nothing is asserted.
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartWaterShots(string[] args)
        {
            int i = Array.IndexOf(args, "-watershots");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(WaterShots(args[i + 1]));
        }

        static IEnumerator QuitAfter(float seconds)
        {
            float end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end) yield return null;
            Debug.LogError("[WaterShots] gave up after " + seconds + " s");
            Application.Quit(1);
        }

        IEnumerator WaitWaterTool(Func<bool> busy)
        {
            for (int i = 0; i < 3000 && (busy() || _tools.Context.Running > 0); i++) { _tools.Tick(default); yield return null; }
            for (int i = 0; i < 3; i++) { _tools.Tick(default); yield return null; }
        }

        void LookAt(Vector2 xz, float distance, float yaw, float pitch)
        {
            Camera.Frame(new Vector3(xz.x, _resort.Surface.HeightAt(xz.x, xz.y), xz.y), distance);
            Camera.SetAngles(yaw, pitch);
        }

        void ClickAt(Vector2 xz) { _tools.Tick(GroundInput(xz, click: true)); _tools.Tick(GroundInput(xz)); }

        IEnumerator WaterShots(string dir)
        {
            Directory.CreateDirectory(dir);
            StartCoroutine(QuitAfter(600));   // a capture that throws never holds the GPU past ten minutes
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null || _resort.WaterBodies == null) yield return null;
            Debug.Log("[WaterShots] the area is open; starting the ground lattice");
            Ground.Start();
            float give = Time.unscaledTime + 300;
            while (!Ground.Ready && Time.unscaledTime < give) yield return null;
            if (!Ground.Ready) { Debug.LogError("[WaterShots] the ground lattice never came ready"); Application.Quit(1); yield break; }
            if (Camera != null) Camera.InputEnabled = false;
            var core = _resort.Core;
            for (int i = 0; i < 60; i++) yield return null;

            // ---- The pond: the gentlest 60 m circle (about 3 m of fall across it) with no water under or near it.
            const float r = 30;
            Vector2 pondAt = default;
            float bestScore = float.MaxValue;
            for (float x = core.xMin + 150; x <= core.xMax - 150; x += 40)
            for (float z = core.yMin + 150; z <= core.yMax - 150; z += 40)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                bool dry = true;
                for (int k = 0; k < 16 && dry; k++)
                {
                    float a = k * Mathf.PI / 8;
                    for (float rr = 0; rr <= r + 20 && dry; rr += 12.5f)
                    {
                        var p = new Vector2(x + Mathf.Cos(a) * rr, z + Mathf.Sin(a) * rr);
                        float h = _resort.Surface.HeightAt(p.x, p.y);
                        if (float.IsNaN(h) || WaterUnder(PointMm.FromMetres(p.x, p.y)) != null) dry = false;
                        else if (rr <= r) { lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h); }
                    }
                }
                if (!dry) continue;
                float score = Mathf.Abs(hi - lo - 3f);
                if (score < bestScore) { bestScore = score; pondAt = new Vector2(x, z); }
            }
            Debug.Log($"[WaterShots] pond at {pondAt} (fall off by {bestScore:F1} m)");

            // ---- The dam site: stream cells in the core, well away from the pond; bank A climbs 6 m out of the bed across
            // the fall line, the far bank the same the other way (the tool snaps it to the pool). Found by trying the review.
            Hud.SetToolboxTab("snow");
            Hud.SetToolbox(true);
            var t = _resort.WaterBodies;
            const float cell = WaterBodyTable.CellMetres;
            Vector2 damAt = default, bankA = default, bankB = default, across = default;
            float bed = 0;
            bool dammed = false;
            int tries = 0;
            for (int c = 0; c < t.Cells && !dammed && tries < 40; c += 37)
            {
                if (t.BodyAt(c) != WaterBodyTable.Streams) continue;
                var at = new Vector2(t.OriginXMm / 1000f + (c % t.Width + 0.5f) * cell, t.OriginYMm / 1000f + (c / t.Width + 0.5f) * cell);
                if (!core.Contains(at) || Vector2.Distance(at, pondAt) < 250 || at.x < core.xMin + 120 || at.x > core.xMax - 120 || at.y < core.yMin + 120 || at.y > core.yMax - 120) continue;
                // The stream's cell centre can sit well up a bank: start from the true bed, the lowest ground within 20 m.
                var cellAt = at;
                float h0 = _resort.Surface.HeightAt(at.x, at.y);
                for (int i = -20; i <= 20; i += 2)
                for (int j = -20; j <= 20; j += 2)
                {
                    float h = _resort.Surface.HeightAt(cellAt.x + i, cellAt.y + j);
                    if (h < h0) { h0 = h; at = new Vector2(cellAt.x + i, cellAt.y + j); }
                }
                var up = new Vector2(_resort.Surface.HeightAt(at.x + 25, at.y) - _resort.Surface.HeightAt(at.x - 25, at.y),
                                     _resort.Surface.HeightAt(at.x, at.y + 25) - _resort.Surface.HeightAt(at.x, at.y - 25));
                if (up.sqrMagnitude < 1e-4f) continue;
                if (up.magnitude / 50 > 0.15f) continue;   // a gentle reach only: the guardrails would refuse a cliff anyway
                var downDir = -up.normalized;
                if (h0 - _resort.Surface.HeightAt(at.x + downDir.x * 40, at.y + downDir.y * 40) > 20) continue;
                var side = new Vector2(-up.y, up.x).normalized;
                float out1 = Out(at, side, h0 + 6), out2 = Out(at, -side, h0 + 6);
                if (out1 == 0 || out2 == 0) continue;
                tries++;
                _tools.Choose(DamTool.ToolId);
                var probe = (DamTool)_tools.Active;
                _toolScripted = true;
                ClickAt(at + side * out1);
                ClickAt(at - side * out2);
                yield return WaitWaterTool(() => probe.Analysing);
                if (probe.InReview && probe.Problem == null)
                {
                    dammed = true; damAt = at; bed = h0; across = side;
                    bankA = at + side * out1; bankB = at - side * out2;
                }
                else Debug.Log($"[WaterShots] dam try {tries} at {at}: {(probe.InReview ? probe.Problem : _tools.Context?.Window?.Message)}");
                _tools.Close();
                _toolScripted = false;
            }

            ulong damId = 0;
            if (dammed)
            {
                // 01: the Dam tool armed: the streams in blue, the hint giving full pool under the pointer.
                LookAt((bankA + bankB) / 2, 190, 200, 50);
                _tools.Choose(DamTool.ToolId);
                var dam = (DamTool)_tools.Active;
                _toolScripted = true;
                _tools.Tick(GroundInput(bankA));
                yield return Shot(dir, "01-dam-first-bank");
                // 02: the first bank set: the yellow line at full pool, where the far bank can land.
                ClickAt(bankA);
                yield return WaitWaterTool(() => false);
                _tools.Tick(GroundInput(bankB));
                yield return Shot(dir, "02-dam-far-bank");
                // 03: the review: the graded embankment, the water at full pool, its shore, the spillway.
                ClickAt(bankB);
                yield return WaitWaterTool(() => dam.Analysing);
                LookAt(damAt, 320, 200, 45);
                yield return Shot(dir, "03-dam-review");
                _tools.Close();
                _toolScripted = false;

                // 04: PD-13: on the good dam's own line (so it crosses the stream), banks a metre over its lowest point: full
                // pool stands under 5 ft over the stream.
                var lineDir = (bankB - bankA).normalized;
                float lineLen = Vector2.Distance(bankA, bankB), lowAt = 0, lowH = float.MaxValue;
                for (float u = 0; u <= lineLen; u += 0.5f)
                {
                    float h = _resort.Surface.HeightAt(bankA.x + lineDir.x * u, bankA.y + lineDir.y * u);
                    if (h < lowH) { lowH = h; lowAt = u; }
                }
                var lowPoint = bankA + lineDir * lowAt;
                float low1 = Out(lowPoint, -lineDir, lowH + 1), low2 = Out(lowPoint, lineDir, lowH + 1);
                _tools.Choose(DamTool.ToolId);
                dam = (DamTool)_tools.Active;
                _toolScripted = true;
                ClickAt(lowPoint - lineDir * Mathf.Max(low1, 2));
                ClickAt(lowPoint + lineDir * Mathf.Max(low2, 2));
                yield return WaitWaterTool(() => dam.Analysing);
                Debug.Log($"[WaterShots] low dam: {(dam.InReview ? dam.Problem ?? "accepted" : _tools.Context?.Window?.Message)}");
                LookAt(lowPoint, 200, 200, 50);
                yield return Shot(dir, "04-dam-refused-too-low");
                _tools.Close();
                _toolScripted = false;

                // 05: PD-12: a first bank far from any stream (the pond's dry ground).
                _tools.Choose(DamTool.ToolId);
                _toolScripted = true;
                ClickAt(pondAt);
                LookAt(pondAt, 300, 210, 45);
                yield return Shot(dir, "05-dam-refused-no-stream");
                _tools.Close();
                _toolScripted = false;

                // 06: the good dam built: its water, its name on the map.
                _tools.Choose(DamTool.ToolId);
                dam = (DamTool)_tools.Active;
                _toolScripted = true;
                ClickAt(bankA);
                ClickAt(bankB);
                yield return WaitWaterTool(() => dam.Analysing);
                Debug.Log($"[WaterShots] dam at {damAt}: {_tools.Confirm(WorkStage.Built)}");
                yield return WaitWaterTool(() => false);
                _toolScripted = false;
                _tools.Close();
                for (int i = 0; i < 600 && _resort.WaterBodies.Ponds < 1; i++) yield return null;
                var dams = Design.Document.Dams;
                if (dams.Count > 0) damId = dams[dams.Count - 1].Id.Value;
                Hud.SetToolbox(false);
                LookAt(damAt, 300, 200, 38);
                yield return Shot(dir, "06-dam-built");
                Hud.SetToolboxTab("snow");
                Hud.SetToolbox(true);
            }
            else Debug.Log("[WaterShots] no stream here took a dam");

            // ---- 07, 08: the pond, pumped only and starting empty (F5).
            _tools.Choose(PondTool.ToolId);
            var pond = (PondTool)_tools.Active;
            _toolScripted = true;
            LookAt(pondAt, 260, 210, 42);
            for (int k = 0; k < 9; k++)
            {
                float a = k * Mathf.PI * 2 / 9;
                ClickAt(pondAt + new Vector2(Mathf.Cos(a) * r * (k % 2 == 0 ? 1f : 0.85f), Mathf.Sin(a) * r));
            }
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return WaitWaterTool(() => pond.Analysing);
            pond.SetWaterOptions(PondFill.Pumped, true);
            yield return WaitWaterTool(() => pond.Analysing);
            yield return Shot(dir, "07-pond-review-pumped-empty");
            Debug.Log($"[WaterShots] pond problem: {pond.Problem ?? "none"}; build: {_tools.Confirm(WorkStage.Built)}");
            yield return WaitWaterTool(() => false);
            _toolScripted = false;
            _tools.Close();
            Hud.SetToolbox(false);
            for (int i = 0; i < 600 && _resort.WaterBodies.Ponds < (dammed ? 2 : 1); i++) yield return null;
            var ponds = Design.Document.Ponds;
            ulong pondId = ponds.Count > 0 ? ponds[ponds.Count - 1].Id.Value : 0;
            LookAt(pondAt, 220, 210, 35);
            Debug.Log($"[WaterShots] after the builds: {_resort.WaterBodies.Ponds} pond bodies in the table, {_pondWater?.Count ?? 0} pond meshes drawn");
            yield return Shot(dir, "08-pond-built");

            // ---- 09-12: the cards (F1), opened as a click would.
            if (pondId != 0)
            {
                ShowWaterCardFor("pond:" + Hex(pondId));
                yield return Shot(dir, "09-pond-card");
            }
            if (damId != 0)
            {
                LookAt(damAt, 300, 200, 38);
                ShowWaterCardFor("dam:" + Hex(damId));
                yield return Shot(dir, "10-dam-card");
                ulong way = Design.Document.FindDam(new DamId(damId))?.StreamWay ?? 0;
                if (way != 0)
                {
                    ShowWaterCardFor("stream:" + Hex(way));
                    yield return Shot(dir, "11-stream-card");
                }
            }
            t = LakesOnly;
            for (int c = 0; c < t.Cells; c++)
            {
                int b = t.BodyAt(c);
                if (b <= 0 || t[b].Kind != WaterBodyKind.Lake) continue;
                var at = new Vector2(t.OriginXMm / 1000f + (c % t.Width + 0.5f) * cell, t.OriginYMm / 1000f + (c / t.Width + 0.5f) * cell);
                if (!core.Contains(at)) continue;
                var centre = new Vector2(t[b].CentroidXMm / 1000f, t[b].CentroidYMm / 1000f);
                LookAt(core.Contains(centre) ? centre : at, Mathf.Clamp(Mathf.Sqrt(t[b].AreaSquareMetres) * 3, 250, 1200), 200, 45);
                ShowWaterCardFor("lake:" + Hex(t[b].Id));
                yield return Shot(dir, "12-lake-card");
                break;
            }
            Hud.CloseWaterCardIfOpen();
            Debug.Log("[WaterShots] done");
            Application.Quit();
        }

        // How far along dir from at the ground first reaches height h (0 for not within 120 m).
        float Out(Vector2 at, Vector2 dir, float h)
        {
            for (float s = 1; s <= 120; s += 1) if (_resort.Surface.HeightAt(at.x + dir.x * s, at.y + dir.y * s) >= h) return s;
            return 0;
        }
    }
}
