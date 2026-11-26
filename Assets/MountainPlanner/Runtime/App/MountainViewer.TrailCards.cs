using System;
using System.Collections;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.07 B2's review pictures: <c>-simto &lt;MM-DD&gt; -trailcardshots &lt;folder&gt;</c>. At the date, Analysis › Trails
    /// with its Conditions and Status, then the cards of a Covered trail, a Bare one, a Marginal one, and the Covered one
    /// switched Closed by the card's own button (then back to Auto); then Analysis › Weather and no panel, where the clouds
    /// stay (Analysis on any other tab clears them away). Each card's rows go to the log, so a picture is
    /// described by what it shows. Quits when done. Only with <c>-simto</c>, a capture run that never saves.
    /// </summary>
    public sealed partial class MountainViewer
    {
        string _cardShotsDir;

        void NoteTrailCardShots(string[] args)
        {
            int i = Array.IndexOf(args, "-trailcardshots");
            if (i >= 0 && i + 1 < args.Length) _cardShotsDir = Path.GetFullPath(args[i + 1]);
        }

        IEnumerator TrailCardShots(string dir)
        {
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 600 && (_lastTrailSnapshot == null || _sections == null || Hud == null); f++) yield return null;
            for (int f = 0; f < 60; f++) yield return null;
            if (_lastTrailSnapshot == null || _sections == null) { Debug.LogWarning("[TrailCards] no trails to show"); Application.Quit(); yield break; }
            Hud.SetAnalysisTab("trails");
            Hud.SetAnalysis(true);
            yield return CardShot(dir, "1-trails-list", 0);
            ulong covered = Pick(TrailSnowLabel.Covered), bare = Pick(TrailSnowLabel.Bare), marginal = Pick(TrailSnowLabel.Marginal);
            // Owner 2026-11-23: the card opens short; the first picture shows it so, the rest with More details unfolded (the
            // grade, the snow and the grass).
            Hud.SetTrailCardMoreForTest(false);
            if (covered != 0) yield return CardShot(dir, "2-card-covered-short", covered);
            if (covered != 0) { Hud.SetTrailCardMoreForTest(true); yield return CardShot(dir, "2-card-covered", covered); }
            Hud.SetTrailCardMoreForTest(true);
            if (bare != 0) yield return CardShot(dir, "3-card-bare", bare);
            if (marginal != 0) yield return CardShot(dir, "4-card-marginal", marginal);
            if (covered != 0)
            {
                Hud.OpenTrailCardFor(covered);
                Hud.ChooseTrailSwitchForTest("Closed");   // the card's own button: one SetTrailSwitch
                for (int f = 0; f < 90; f++) yield return null;
                yield return CardShot(dir, "5-card-closed-by-you", covered);
                Hud.ChooseTrailSwitchForTest("Auto");
                for (int f = 0; f < 30; f++) yield return null;
            }
            // The owner, 2026-11-20: Analysis clears the clouds away, but its Weather tab keeps them (fading back takes half a second).
            Hud.CloseTrailCardForCapture();
            Hud.SetAnalysisTab("weather");
            for (int f = 0; f < 120; f++) yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "6-weather-tab-keeps-clouds.png"));
            yield return null;
            yield return null;
            Hud.SetAnalysis(false);
            for (int f = 0; f < 120; f++) yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "7-no-panel-clouds.png"));
            yield return null;
            yield return null;
            Debug.Log($"[TrailCards] done: covered {Name(covered)}, bare {Name(bare)}, marginal {Name(marginal)}");
            Application.Quit();
        }

        /// <summary>A built trail with that label (the longest, so its card is worth a look), or 0.</summary>
        ulong Pick(TrailSnowLabel label)
        {
            ulong best = 0;
            long bestLen = -1;
            foreach (var t in _sections.Trails)
            {
                if (!t.Built || !_lineSnow.TryGetValue(t.Id.Value, out var m) || !m.HasSnow || m.Label != label) continue;
                long len = 0;
                foreach (var s in t.Segments) len += s.EndMm - s.StartMm;
                if (len > bestLen) { bestLen = len; best = t.Id.Value; }
            }
            return best;
        }

        string Name(ulong id) => TrailRowOf(id)?.Name ?? "none";

        /// <summary>The player's trail is graded (its first run's), as the card's row says.</summary>
        bool GradedRow(ResolvedTrail t) =>
            t != null && Design?.Document.FindTrail(t.Segments[0].Piece) is Trail run && run.Settings.Grading == TrailGrading.Graded;

        IEnumerator CardShot(string dir, string name, ulong id)
        {
            if (id != 0)
            {
                Hud.OpenTrailCardFor(id);
                FocusTrailRow(id);
            }
            else Hud.CloseTrailCardForCapture();
            for (int f = 0; f < 45; f++) yield return null;
            string path = Path.Combine(dir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            if (id != 0 && _lineSnow.TryGetValue(id, out var m))
            {
                var t = TrailRowOf(id);
                var sw = SwitchNow(t) ?? TrailSwitch.Auto;
                Debug.Log($"[TrailCards] {name}: {t?.Name} · switch {sw} · status '{m.Status(true, sw, true).Word}' · conditions '{m.Conditions()}' · " +
                          $"cover {m.CoverText()} · rating {m.RatingText()} · snow '{m.Surface()}' · hint '{m.SwitchHint(sw)}' · " +
                          $"grass '{m.GrassText(GradedRow(t))}'");
            }
            else Debug.Log($"[TrailCards] {name}: the list");
        }
    }
}
