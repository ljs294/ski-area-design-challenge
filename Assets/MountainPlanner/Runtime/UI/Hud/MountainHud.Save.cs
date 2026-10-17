using System;
using MountainPlanner.Presentation;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Save in the HUD (task P3-04; the mockup's bar save symbol and the menu's Save game): white while there are unsaved
    /// changes, grey when everything is saved. Both stay inert until the app flow calls <see cref="SetSave"/>, so a capture
    /// or benchmark run without the flow has nothing to save.
    /// </summary>
    public sealed partial class MountainHud
    {
        /// <summary>The bar's save symbol or the menu's Save game was clicked.</summary>
        public event Action SaveChosen;

        Button _barSave, _menuSave;
        bool _saveLive, _saveDue;

        /// <summary>Makes Save live (or inert again) and shows whether anything is unsaved. Cheap when nothing changed.</summary>
        public void SetSave(bool available, bool due)
        {
            if (_root == null) return;
            if (_barSave == null)
            {
                _barSave = _root.Q<Button>("bar-save");
                _menuSave = _root.Q<Button>("menu-save");
                if (_barSave == null || _menuSave == null) return;
                KeyBindings.Changed += ShowSaveCaptions;
            }
            if (available != _saveLive)
            {
                _saveLive = available;
                _barSave.clickable = available ? new Clickable(() => SaveChosen?.Invoke()) : null;
                _menuSave.clickable = available ? new Clickable(() => { SetDrop(Drop.None); SaveChosen?.Invoke(); }) : null;
                _barSave.focusable = _menuSave.focusable = available;
                _menuSave.EnableInClassList("row--dis", !available);
                _saveDue = !due;   // force the redraw below
            }
            if (due == _saveDue && available) return;
            _saveDue = due;
            _barSave.EnableInClassList("save--due", available && due);
            ShowSaveCaptions();
        }

        void ReleaseSave()
        {
            KeyBindings.Changed -= ShowSaveCaptions;
            _barSave = _menuSave = null;   // re-found (and re-subscribed) if the HUD is enabled again
            _saveLive = false;
        }

        void ShowSaveCaptions()
        {
            if (_barSave == null) return;
            string key = KeyBindings.Caption(GameAction.Save);
            var label = _menuSave.Q<Label>(className: "row__key");
            if (label != null) label.text = key;
            _menuSave.tooltip = _saveLive ? "" : "Saving needs the game's title flow";
            _barSave.tooltip = !_saveLive ? "Saving needs the game's title flow"
                : _saveDue ? (key.Length > 0 ? $"Unsaved changes: save ({key})" : "Unsaved changes: save") : "Everything is saved";
        }
    }
}
