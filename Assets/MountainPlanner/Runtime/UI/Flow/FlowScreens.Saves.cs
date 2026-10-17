using System;
using System.Collections.Generic;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Saves;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// Load Area's saves (task P3-04; the mockup's #demo=load): each area shows how many saves it has, and → (or a click
    /// on the count) lists them under it, newest first, with New design last. Enter opens a save, F2 renames it, Delete
    /// removes it after asking; the Autosave slot can't be renamed, and a save from a newer version is greyed.
    /// </summary>
    public sealed partial class FlowScreens
    {
        /// <summary>A save (or, with null, New design) was chosen under its area.</summary>
        public event Action<LibraryRow, SaveSummary> SaveOpenChosen;
        public event Action<SaveSummary, string> SaveRenameConfirmed;
        public event Action<SaveSummary> SaveDeleteConfirmed;

        /// <summary>The areas whose saves are shown, by folder; kept across redraws and while the game runs.</summary>
        readonly HashSet<string> _savesShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The count beside an area ("3 saves"): a click shows or hides them.</summary>
        void AddSaveCount(VisualElement el, LibraryRow r)
        {
            var count = Text(LibraryViewModel.SaveCount(r.Saves.Count), "lib-saves");
            count.tooltip = r.Saves.Count > 0 ? "Show or hide the saves (→ ←)" : "Nothing saved yet: Open starts a new design";
            if (r.Saves.Count > 0)
                count.RegisterCallback<ClickEvent>(e => { ToggleSaves(r, !_savesShown.Contains(r.Entry.Folder)); e.StopPropagation(); });
            el.Add(count);
        }

        /// <summary>The rows under an area whose saves are shown: each save, then New design.</summary>
        void AddSaveRows(LibraryRow area)
        {
            if (area.Saves.Count == 0 || !area.CanOpen || !_savesShown.Contains(area.Entry.Folder)) return;
            foreach (var s in area.Saves) AddSubRow(new LibraryRow { Entry = area.Entry, Area = area, Save = s, Name = s.Name });
            AddSubRow(new LibraryRow { Entry = area.Entry, Area = area, IsNewDesign = true, Name = "New design" });
        }

        void AddSubRow(LibraryRow r)
        {
            var el = new VisualElement();
            el.AddToClassList("lib-row");
            el.AddToClassList("lib-row--save");
            var name = Text(r.Name, "lib-save-name");
            name.tooltip = r.Name;
            el.Add(name);
            var s = r.Save;
            if (s == null)
            {
                el.AddToClassList("lib-row--newdesign");
                el.Add(Text("", "lib-save-tag-space"));
                el.Add(Text("", "lib-cell", "lib-save-when"));
                el.Add(Text("the area with nothing built", "lib-cell", "lib-save-counts", "mono"));
            }
            else if (s.Refusal.Length > 0)
            {
                el.AddToClassList("lib-row--newer");
                el.tooltip = s.Refusal;
                el.Add(Text("", "lib-save-tag-space"));
                el.Add(Text("", "lib-cell", "lib-save-when"));
                el.Add(Text("saved by a newer version", "lib-cell", "lib-save-counts"));
            }
            else
            {
                if (s.Autosave) el.Add(Text("Autosave", "lib-save-tag"));
                else el.Add(Text("", "lib-save-tag-space"));
                el.Add(Text(LibraryViewModel.SavedWhen(s.SavedUtc, DateTime.UtcNow), "lib-cell", "lib-save-when", "mono"));
                el.Add(Text(LibraryViewModel.Counts(s), "lib-cell", "lib-save-counts", "mono"));
                if (s.FromBackup) el.tooltip = "save.json was damaged: this is its previous version.";
            }
            var spacer = new VisualElement();
            spacer.AddToClassList("spacer");
            el.Add(spacer);
            var actions = new VisualElement();
            actions.AddToClassList("lib-actions");
            var open = Btn(s != null && s.Refusal.Length > 0 ? "Update to open" : "Open", "btn--go", () => OpenSaveRow(r));
            open.SetEnabled(r.CanOpen);
            actions.Add(open);
            el.Add(actions);
            el.RegisterCallback<ClickEvent>(e =>
            {
                Select(_rowData.IndexOf(r));
                if (e.clickCount == 2) OpenSaveRow(r);
            });
            el.focusable = true;
            el.RegisterCallback<NavigationSubmitEvent>(e => { if (e.target == el) OpenSaveRow(r); });
            el.RegisterCallback<KeyDownEvent>(e => { if (e.target == el) SaveKeys(e, r); });
            el.RegisterCallback<FocusInEvent>(_ => Select(_rowData.IndexOf(r)));
            _rows.Add(el);
            _rowElements.Add(el);
            _rowData.Add(r);
        }

        void OpenSaveRow(LibraryRow r)
        {
            if (r.CanOpen) SaveOpenChosen?.Invoke(r.Area, r.Save);
        }

        /// <summary>
        /// Load Area's keys on an area or a save row: → shows an area's saves (and steps into them), ← goes back to the area
        /// and hides them, F2 renames a save, Delete removes one. True when the key was used.
        /// </summary>
        bool SaveKeys(KeyDownEvent e, LibraryRow r)
        {
            var area = r.Area ?? r;
            if (area.Entry == null || area.Saves.Count == 0) return false;
            switch (e.keyCode)
            {
                case KeyCode.RightArrow when !r.IsSaveRow && area.CanOpen:
                    ToggleSaves(area, true, step: true);
                    break;
                case KeyCode.LeftArrow when r.IsSaveRow || _savesShown.Contains(area.Entry.Folder):
                    ToggleSaves(area, false);
                    break;
                case KeyCode.F2 when r.Save != null:
                    OpenSaveRename(r);
                    break;
                case KeyCode.Delete when r.Save != null:
                    ConfirmSaveDelete(r);
                    break;
                default:
                    return false;
            }
            e.StopPropagation();
            return true;
        }

        /// <summary>Lists an area's saves (the captures, task P3-04); false when it has none.</summary>
        public bool ShowSaves(LibraryRow area)
        {
            if (area?.Entry == null || area.Saves.Count == 0) return false;
            ToggleSaves(area, true, step: true);
            return true;
        }

        void ToggleSaves(LibraryRow area, bool show, bool step = false)
        {
            if (show) _savesShown.Add(area.Entry.Folder);
            else _savesShown.Remove(area.Entry.Folder);
            if (_libraryVm == null) return;
            RenderLibrary(_libraryVm, Mode);
            int at = _rowData.FindIndex(x => x.Area == null && x.Entry?.Folder == area.Entry.Folder);
            if (at >= 0) Select(step && show && at + 1 < _rowData.Count ? at + 1 : at);
            FocusSelectedRow();
        }

        /// <summary>F2 on a save: the Rename dialog. The Autosave slot keeps its name; a newer save can't be changed.</summary>
        public void OpenSaveRename(LibraryRow r)
        {
            var s = r?.Save;
            if (s == null || s.Refusal.Length > 0) return;
            if (s.Autosave) { Toast("Autosave keeps its name. Save under a name in the game (Ctrl+S) to keep a copy.", 4); return; }
            Prompt("Rename save", s.Name, "The save's name in Load Area.", "Rename", 40,
                   text => ResortLibrary.NormalizeName(text).Length > 0, name => SaveRenameConfirmed?.Invoke(s, ResortLibrary.NormalizeName(name)));
        }

        /// <summary>Delete on a save: asks first; the area and its other saves stay.</summary>
        public void ConfirmSaveDelete(LibraryRow r)
        {
            var s = r?.Save;
            if (s == null) return;
            Confirm($"Delete the save {s.Name} from {r.Area?.Name ?? r.Entry?.Name}? The area and its other saves stay. This can't be undone.",
                    "Delete", () => SaveDeleteConfirmed?.Invoke(s));
        }

        /// <summary>Manage Areas' Delete names the saves that go with the area.</summary>
        static string SavesGoing(LibraryRow r) =>
            r.Saves.Count == 0 ? "" : r.Saves.Count == 1 ? " Its save goes with it." : $" Its {r.Saves.Count} saves go with it.";
    }
}
