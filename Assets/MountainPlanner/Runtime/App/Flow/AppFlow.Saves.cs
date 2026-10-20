using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Saves;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Flow;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Saves in the game (task P3-04; decisions SV1, SV2): which save an open loads, Save (Ctrl+S, the bar's save symbol,
    /// the menu's Save game) with a name the first time, the Autosave slot every few minutes when something changed, and
    /// "Save before leaving?" on Exit to title and Quit. The rules themselves are engine-free (<see cref="OpenDesign"/>).
    /// -crashsave kills the game part way into its next save, for the demo of a crash leaving the last good save.
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>What the next open loads: null for the area's newest save, "" for a new design, else a save folder.</summary>
        string _openSave;
        float _savedAt;
        bool _quitConfirmed, _quitHooked;
        string _recoveredNote;
        UI.MountainHud _saveHud;

        static string Now => DownloadService.UtcStamp(DateTime.UtcNow);
        static string GameVersion => BuildInfo.Current.version ?? "";

        /// <summary>Opens a save from Load Area (the folder), or the area empty ("" — New design).</summary>
        public void OpenSave(string packageFolder, string saveFolder)
        {
            _openSave = saveFolder ?? "";
            Controller.Open(packageFolder);
        }

        /// <summary>
        /// Picks and reads the design for an area about to open (from <see cref="OpenMountain"/>). False, with a dialog,
        /// when the save can't be opened: the area then stays closed and the player chooses again.
        /// </summary>
        bool PrepareDesign(string packageFolder)
        {
            OpenDesign.Current = null;
            string pick = _openSave;
            _openSave = null;
            PackageManifest m;
            try { m = ResortPackage.ReadManifest(packageFolder); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { return true; }   // the viewer reports it
            string heights = m.Layers.FirstOrDefault(l => l.Id == "heights-core")?.Sha256 ?? "";
            // A capture or benchmark run never opens the player's own design unless it's asked to: it builds and undoes work
            // on whatever it opens (task 08: a UI capture opened the owner's autosave).
            string folder = pick ?? (CaptureRun ? null : SaveStore.Newest(DataRoot, m.PackageId)?.Folder);
            if (string.IsNullOrEmpty(folder))
            {
                OpenDesign.Current = OpenDesign.New(DataRoot, m.PackageId, heights);
                return true;
            }
            try
            {
                var open = OpenDesign.Load(DataRoot, folder, m.PackageId, heights);
                OpenDesign.Current = open;
                _recoveredNote = open.Recovered.Length > 0 ? $"The save was damaged, so its previous version opened ({open.Recovered})" : null;
                Debug.Log($"[Saves] Opened {folder}");
                return true;
            }
            catch (FormatTooNewException e)
            {
                Screens.Alert("This save can't be opened", e.Message, "New design", () => OpenSave(packageFolder, ""), "Close", error: true);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Saves] {folder} can't be opened: {e.Message}");
                Screens.Alert("This save can't be opened", e.Message, "New design", () => OpenSave(packageFolder, ""), "Close", error: true);
            }
            return false;
        }

        /// <summary>Load Area's save rows (FlowScreens.Saves.cs).</summary>
        void WireSaveList()
        {
            Screens.SaveOpenChosen += (area, save) => { if (area?.Entry != null) OpenSave(area.Entry.Folder, save?.Folder ?? ""); };
            Screens.SaveRenameConfirmed += (save, name) =>
            {
                if (!SaveStore.Rename(save.Folder, name)) Screens.Toast("That save can't be renamed: it's damaged, in use, or from a newer version.", 5);
                RefreshLibrary();
            };
            Screens.SaveDeleteConfirmed += save =>
            {
                if (SaveStore.Delete(save.Folder)) Screens.Toast($"Deleted the save {save.Name}.");
                else Screens.Toast($"{save.Name} is in use; close the game's other window and try again.", 5);
                RefreshLibrary();
            };
        }

        /// <summary>The title's Continue line when the area has a save: "Jackson Hole · Teton plan, saved 12 min ago".</summary>
        string ContinueLine(LibraryEntry target)
        {
            var newest = SaveStore.Newest(DataRoot, target.PackageId);
            return newest == null ? null : $"{target.Name} · {newest.Name}, saved {LibraryViewModel.SavedWhen(newest.SavedUtc, DateTime.UtcNow)}";
        }

        /// <summary>Back on the title: no design is open.</summary>
        void CloseDesign() => OpenDesign.Current = null;

        /// <summary>The scene's HUD: Save, and leaving through Exit to title asks first.</summary>
        void WireSaves(UI.MountainHud hud)
        {
            _saveHud = hud;
            MountainViewer.SampleDesignChosen = AddSampleDesign;
            hud.SaveChosen += () => SaveNow(null);
            _savedAt = Time.realtimeSinceStartup;
            if (_recoveredNote != null && !MountainViewer.TitleMode) { hud.Toast(_recoveredNote, new Color32(0xf0, 0xb4, 0x3c, 255)); _recoveredNote = null; }
            if (!_quitHooked)
            {
                _quitHooked = true;
                Application.wantsToQuit += WantsToQuit;   // the menu's Quit, Alt+F4 and the window's close box
            }
            var crash = Environment.GetCommandLineArgs();
            if (Array.IndexOf(crash, "-crashsave") >= 0 && OpenDesign.Current != null)
                OpenDesign.Current.Step = s =>
                {
                    if (s != SaveStep.TempWritten) return;
                    Debug.Log("[Saves] -crashsave: killing the game after save.json.tmp, before the commit");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                };
        }

        /// <summary>A capture, benchmark or test run: it never opens or autosaves the player's saves on its own.</summary>
        static bool CaptureRun => _captureRun ??= Array.Exists(Environment.GetCommandLineArgs(), a => Array.IndexOf(CaptureSwitches, a) >= 0);
        static bool? _captureRun;
        static readonly string[] CaptureSwitches =
        {
            "-uicapture", "-flowcapture", "-trailshots", "-trailbench", "-paintbench", "-paintmovie", "-screenshot", "-benchmark", "-benchmark-views", "-pathmovie", "-lineup",
            "-lodtransitions", "-clip", "-titleshots", "-titlemovie", "-photoshot", "-styletile", "-testbench", "-editbench", "-smoketest",
        };

        void UpdateSaves(Keyboard keys)
        {
            var open = OpenDesign.Current;
            bool live = open != null && !MountainViewer.TitleMode && _viewer != null && _viewer.Hud == _saveHud && _saveHud != null;
            if (_saveHud != null) _saveHud.SetSave(live, live && open.Dirty);
            if (!live) return;
            if (!FlowHasKeyboard() && KeyBindings.Pressed(keys, GameAction.Save)) SaveNow(null);
            int minutes = UI.Flow.GameplayPreferences.AutosaveMinutes;
            if (minutes > 0 && !CaptureRun && open.Dirty && Time.realtimeSinceStartup - _savedAt >= minutes * 60f)
            {
                _savedAt = Time.realtimeSinceStartup;
                if (TryWrite(() => open.Autosave(Now, GameVersion), "Autosave")) _saveHud.Toast("Autosaved");
            }
        }

        /// <summary>
        /// Save: to the design's own save, or, the first time, asks for a name (the area's name filled in). Runs
        /// <paramref name="then"/> once saved (Save and leave).
        /// </summary>
        void SaveNow(Action then)
        {
            var open = OpenDesign.Current;
            if (open == null) return;
            if (open.HasName)
            {
                if (TryWrite(() => open.Save(Now, GameVersion), open.SaveName)) { Saved(open.SaveName); then?.Invoke(); }
                return;
            }
            string area = _saveHud != null ? _saveHud.SiteName : "";
            Screens.Prompt("Save", area, $"Saved under {area} in Load Area. {KeyBindings.Caption(GameAction.Save)} saves to it from now on; autosave keeps its own slot.",
                "Save", 40, n => ResortLibrary.NormalizeName(n).Length > 0, n =>
                {
                    string name = ResortLibrary.NormalizeName(n);
                    if (TryWrite(() => open.SaveAs(name, Now, GameVersion), name)) { Saved(name); then?.Invoke(); }
                });
        }

        void Saved(string name)
        {
            _savedAt = Time.realtimeSinceStartup;
            _saveHud?.Toast($"Saved {name}", new Color32(0x8f, 0xcf, 0x8a, 255));
        }

        /// <summary>Runs a write; a failure (a newer save in the way, a full or locked disk) says why in a dialog.</summary>
        bool TryWrite(Func<bool> write, string what)
        {
            try
            {
                if (write()) return true;
                Screens.Alert("Couldn't save", $"\"{what}\" was saved by a newer version of Mountain Planner, so this version leaves it alone. Save under another name, or update the game.", null, null);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Saves] Couldn't save {what}: {e}");
                Screens.Alert("Couldn't save", $"{e.Message} The last good save is unchanged.", null, null);
            }
            return false;
        }

        /// <summary>Exit to title or Quit: straight away when everything is saved, else "Save before leaving?" (SV2).</summary>
        void AskBeforeLeaving(Action leave)
        {
            var open = OpenDesign.Current;
            if (open == null || !open.Dirty || MountainViewer.TitleMode) { leave(); return; }
            string area = _saveHud != null ? _saveHud.SiteName : "This area";
            Screens.Alert("Save before leaving?", $"{area} has changes that aren't saved.", "Save and leave", () => SaveNow(leave),
                "Keep playing", "Leave without saving", leave, error: false);
        }

        bool WantsToQuit()
        {
            if (_quitConfirmed || Application.isBatchMode) return true;
            var open = OpenDesign.Current;
            if (open == null || !open.Dirty || MountainViewer.TitleMode) return true;
            AskBeforeLeaving(() => { _quitConfirmed = true; Application.Quit(); });
            return false;
        }

        // ---------- the developer panel's sample design (demo.bat 62: there are no drawing tools yet) ----------

        /// <summary>
        /// Adds a lift and a trail near the area's centre by command, so there is something to save. Design points are
        /// whole millimetres from the site's centre on the package grid's axes (TE5), never absolute EPSG:6350.
        /// </summary>
        void AddSampleDesign()
        {
            var open = OpenDesign.Current;
            if (open == null) return;
            var h = open.History;
            int n = h.Document.Lifts.Count + 1;
            double cx = 120 * (n - 1), cy = 0;
            var bottom = PointMm.FromMetres(cx - 150, cy - 400);
            var top = PointMm.FromMetres(cx + 150, cy + 400);
            var lift = h.Submit(new AddLift(h.Revision, $"Sample Chair {n}", LiftCatalogIds.SesselliftFgq4, bottom, top, TowerMode.Automatic, TowerStance.Plumb,
                new[] { new TowerSpec(DesignUnits.FeetToMm(900), DesignUnits.FeetToMm(35)), new TowerSpec(DesignUnits.FeetToMm(1800), DesignUnits.FeetToMm(40)) }, WorkStage.Built));
            var head = PointMm.FromMetres(cx + 170, cy + 400);
            var tail = PointMm.FromMetres(cx - 130, cy - 400);
            var part = new FootprintPart(new[] { new Stroke(DesignUnits.FeetToMm(100), false, new[] { head, PointMm.FromMetres(cx + 260, cy), tail }) });
            var trail = h.Submit(new AddTrail(h.Revision, $"Sample Run {n}", TrailSettings.GradedRun, new[] { part }, head, tail, WorkStage.Plan));
            Debug.Log($"[Saves] Sample design: lift {lift}, trail {trail}");
            _saveHud?.Toast(lift.Accepted && trail.Accepted ? $"Added Sample Chair {n} and Sample Run {n} (a plan)" : $"The sample design was refused: {lift} / {trail}");
        }
    }
}
