using System;
using System.Collections;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI;
using MountainPlanner.UI.Hud;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// -groomtab &lt;folder&gt; (task 4.25 PR B review), with -drawnresort -simto (and no -groomers: a new resort has none): the
    /// Grooming tab as a new player finds it, then two groomers bought through the same commands the buy card sends (one undo
    /// step each), tonight's plan, Equipment and the cards, the map's colours, the trail card; then on to the morning for last
    /// night and the corduroy near and far on the longest run, with and without the Snow conditions layer.
    /// </summary>
    public sealed partial class MountainViewer
    {
        IEnumerator GroomTab(string dir)
        {
            Directory.CreateDirectory(dir);
            StartCoroutine(QuitAfter(1500));
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            float give = Time.unscaledTime + 120;
            while (!_simToRunning && Time.unscaledTime < give) yield return null;
            while (_simToRunning) yield return null;
            if (_simNow == null || _netGeometry == null || Design == null) { Debug.LogError("[GroomTab] no world"); Application.Quit(1); yield break; }
            if (Camera != null) Camera.InputEnabled = false;
            _scriptedRun = true;
            SendSim(SimCommandKind.Pause);
            var core = _resort.Core;
            float span = Mathf.Max(core.width, core.height);
            LookAt(core.center, span * 0.75f, 0, 55);
            Hud.SetAnalysis(true);
            Hud.SetAnalysisTab("grooming");
            Hud.ShowGroomingForCapture(false, false, GroomFilter.Tonight);
            yield return Wait(60);
            Debug.Log($"[GroomTab] new resort: {_simNow.Grooming.GroomerCount} groomers");
            yield return Shot(dir, "01-new-resort-no-groomers");
            Hud.ShowGroomingForCapture(true, false, GroomFilter.Tonight);
            yield return Wait(30);
            yield return Shot(dir, "01b-equipment-buy-button");

            Hud.OpenGroomerBuyCardFor(GroomerModel.S800);
            yield return Wait(30);
            yield return Shot(dir, "02-buy-card");
            foreach (var m in new[] { GroomerModel.S800, GroomerModel.S400W })
            {
                string why = SubmitGrooming(new BuyGroomer(Design.Revision, m, GroomerDrive.Diesel));
                Debug.Log($"[GroomTab] buy {m}: {why ?? "bought"}");
            }
            Hud.CloseGroomCardIfOpen();
            // the widest run tilled tonight by order (the trail card's Push/Till), so the morning has a wide groomed run
            var longest = Design.Document.Trails.Where(t => t.Stage == WorkStage.Built && t.Settings.Kind == TrailKind.Run)
                                .OrderByDescending(t => (double)(t.Shape?.Mask?.Cells ?? 0) / Math.Max(1, _netGeometry.CentrelineLengthMm(t))).FirstOrDefault();   // the widest
            if (longest != null) { SendGroomOrder(longest.Id.Value, GroomOrder.Till); Debug.Log($"[GroomTab] ordered Till tonight on {longest.Name}"); }
            // both groomers out on both night shifts (pinned: Auto crews one until PR C's crewing), with two more runs to till
            Debug.Log($"[GroomTab] crews pinned 2/2: {SubmitGrooming(new SetGroomCrews(Design.Revision, new GroomCrews(2, 2, GroomCrews.Auto))) ?? "ok"}");
            foreach (var extra in Design.Document.Trails.Where(t => t.Stage == WorkStage.Built && t.Settings.Kind == TrailKind.Run && t != longest)
                                        .OrderByDescending(t => t.Shape?.Mask?.Cells ?? 0).Take(2))
            { SendGroomOrder(extra.Id.Value, GroomOrder.Till); Debug.Log($"[GroomTab] ordered Till tonight on {extra.Name}"); }
            // GO-14 v3 (owner 2026-11-21): by day, before the swing, the tab already shows tonight's plan for what was picked;
            // GO-03 v2's night runs to the 8:30 opening, so first let the clock pass the night's end
            {
                float by = Time.unscaledTime + 300;
                SendSim(SimCommandKind.Resume);
                SendSim(SimCommandKind.SetSpeed, 4);
                while (Time.unscaledTime < by && !(_simNow.Grooming.Preview || _simNow.Grooming.NightDay < 0)) yield return null;
                SendSim(SimCommandKind.Pause);
            }
            yield return Wait(60);
            var pv = _simNow.Grooming;
            Debug.Log($"[GroomTab] day preview at {_simNow.FaceMs % SimWorld.DayMs / 3_600_000:D2}h: preview {pv.Preview}, night {pv.NightDay}, want {pv.WantMinutes} min, crewed {pv.CrewMinutes}, short {pv.ShortMinutes}");
            Hud.ShowGroomingForCapture(false, false, GroomFilter.Tonight);
            yield return Wait(30);
            yield return Shot(dir, "02b-day-preview");
            Hud.ShowGroomingForCapture(true, false, GroomFilter.Tonight);
            yield return Wait(30);
            yield return Shot(dir, "02c-day-preview-equipment");
            // the mountain opens and skis the day at 8x (a new resort is closed, and Autopilot grooms only what skiers,
            // new snow or ice left), on to the swing shift's plan
            if (!_simNow.Open) SendSim(SimCommandKind.SetMountainOpen, 1);
            SendSim(SimCommandKind.Resume);
            SendSim(SimCommandKind.SetSpeed, 4);
            long day0 = _simNow.FaceMs / SimWorld.DayMs;
            float until = Time.unscaledTime + 900;
            GroomView g;
            int planned;
            while (true)
            {
                g = _simNow.Grooming;
                planned = 0;
                for (int k = 0; k < g.State.Length; k++) if (g.State[k] == GroomRowState.Planned || g.State[k] == GroomRowState.Partial) planned++;
                int minute = (int)(_simNow.FaceMs / 60000);
                if (g.NightDay >= day0 && !g.Preview && minute >= g.SwingStartAbsMin + 30 && (planned > 0 || Time.unscaledTime > until - 120)) break;
                if (Time.unscaledTime > until) break;
                yield return null;
            }
            SendSim(SimCommandKind.Pause);
            yield return Wait(90);
            g = _simNow.Grooming;
            planned = 0;
            for (int k = 0; k < g.State.Length; k++) if (g.State[k] == GroomRowState.Planned || g.State[k] == GroomRowState.Partial) planned++;
            Debug.Log($"[GroomTab] after buying: {g.GroomerCount} groomers, night {g.NightDay}, {planned} trails planned, want {g.WantMinutes} min, crewed {g.CrewMinutes}, short {g.ShortMinutes}, ≈ ${g.CostUsd}");
            Hud.ShowGroomingForCapture(false, false, GroomFilter.Tonight);
            yield return Wait(30);
            yield return Shot(dir, "03-trails-tonight");
            Hud.ShowGroomingForCapture(false, false, GroomFilter.Needs);
            yield return Wait(30);
            yield return Shot(dir, "04-needs-you");
            Hud.ShowGroomingForCapture(true, false, GroomFilter.Tonight);
            yield return Wait(30);
            yield return Shot(dir, "05-equipment");
            if (g.GroomerCount > 0) { Hud.OpenGroomerCardFor(g.Number[0]); yield return Wait(30); yield return Shot(dir, "06-groomer-card"); Hud.CloseGroomCardIfOpen(); }

            var runs = Design.Document.Trails.Where(t => t.Stage == WorkStage.Built && t.Settings.Kind == TrailKind.Run)
                             .OrderByDescending(t => _netGeometry.CentrelineLengthMm(t)).Take(1).ToArray();
            Hud.ShowGroomingForCapture(false, false, GroomFilter.Tonight);
            if (runs.Length > 0)
            {
                Hud.OpenTrailCardFor(TrailRowOf(runs[0].Id.Value, 0));
                yield return Wait(30);
                yield return Shot(dir, "07-trail-card");
                Hud.CloseTrailCardIfOpen();
            }

            // on to the morning: last night, and the corduroy the night left
            long before = _simNow.Tick;
            SendSim(SimCommandKind.Resume);
            SendSim(SimCommandKind.SkipToMorning);
            int night = _simNow.Grooming.NightDay;
            while (_simNow.Tick == before || _simNow.Skip != SkipKind.None) yield return null;
            Debug.Log($"[GroomTab] landed: last night {_simNow.Grooming.LastNightDay}, tonight {_simNow.Grooming.NightDay} (planned night {night})");
            // the night's report comes at the first hour after the night's end (GroomHour): run on to it
            SendSim(SimCommandKind.SetSpeed, 4);
            float reportBy = Time.unscaledTime + 120;
            while (_simNow.Grooming.LastNightDay < night && Time.unscaledTime < reportBy) yield return null;
            SendSim(SimCommandKind.Pause);
            Debug.Log($"[GroomTab] report at {_simNow.FaceMs % SimWorld.DayMs / 60000 / 60:D2}:{_simNow.FaceMs % SimWorld.DayMs / 60000 % 60:D2}: last night {_simNow.Grooming.LastNightDay}");
            yield return Wait(150);
            g = _simNow.Grooming;
            int done = 0;
            for (int k = 0; k < g.LastState.Length; k++) if (g.LastState[k] == GroomRowState.Done) done++;
            Debug.Log($"[GroomTab] morning: {done} trails groomed last night, {g.LastNotReached} not reached, worked {g.LastWorkMinutes} min, " +
                      $"{_simNow.TrailBlocksGroomed} of {_simNow.TrailBlocks} trail squares machine groomed, {g.WayPasses} stretches groomed on the way");
            Hud.ShowGroomingForCapture(false, true, GroomFilter.Tonight);
            yield return Wait(30);
            yield return Shot(dir, "08-last-night");
            Hud.SetAnalysis(false);
            // the corduroy on the ordered run when the night groomed it, else on another trail it groomed
            bool longestDone = false;
            for (int k = 0; longest != null && k < g.LastState.Length && k < _simNow.TrailIds.Length; k++)
                if (_simNow.TrailIds[k] == longest.Id.Value) longestDone = g.LastState[k] == GroomRowState.Done || g.LastState[k] == GroomRowState.PitchSkipped || g.LastState[k] == GroomRowState.Partial;
            Debug.Log($"[GroomTab] the ordered run ({longest?.Name}) groomed: {longestDone}");
            if (longestDone) runs = new[] { longest };
            for (int k = 0; !longestDone && k < g.LastState.Length && k < _simNow.TrailIds.Length; k++)
            {
                if (g.LastState[k] != GroomRowState.Done) continue;
                ulong id = _simNow.TrailIds[k];
                var groomed = Design.Document.Trails.FirstOrDefault(t => t.Id.Value == id && t.Stage == WorkStage.Built);
                if (groomed != null) { runs = new[] { groomed }; Debug.Log($"[GroomTab] corduroy on {groomed.Name}"); break; }
            }
            if (runs.Length > 0)
            {
                var at = DesignFrame.ToLocal(_netGeometry.PointAlong(runs[0], _netGeometry.CentrelineLengthMm(runs[0]) / 2));
                foreach (var (dist, pitch, tag) in new[] { (25f, 35f, "near"), (90f, 40f, "mid"), (600f, 50f, "far") })
                {
                    LookAt(at, dist, 20, pitch);
                    yield return Shot(dir, $"09-corduroy-{tag}");
                }
                SetLayerForCapture(MapLayers.SnowConditions, true);
                LookAt(at, 600f, 20, 50);
                yield return Shot(dir, "10-conditions");
                SetLayerForCapture(MapLayers.SnowConditions, false);
            }
            Debug.Log("[GroomTab] done");
            Application.Quit(0);
        }

        static IEnumerator Wait(int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }
    }
}
