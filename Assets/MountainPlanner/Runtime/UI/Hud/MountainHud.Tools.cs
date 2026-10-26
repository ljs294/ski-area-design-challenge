using System;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Hud;
using MountainPlanner.UI.Tools;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The Toolbox's live tools (task P3-06): a tile whose tool is in <see cref="ToolCatalog"/> takes clicks and shows
    /// its key; the armed tool's tile is lit; the tray tells the app when it closes or changes tab, so the armed tool
    /// goes with it. The other tiles stay greyed until their tasks (08–10, Phase 4).
    /// </summary>
    public sealed partial class MountainHud
    {
        /// <summary>A live tool's tile was clicked (its id).</summary>
        public event Action<string> ToolChosen;

        /// <summary>The tray closed, or changed tab: the armed tool goes too (the mockup).</summary>
        public event Action ToolboxLeft;

        // The mockup's tool hints (its TOOLS table), for the live tools.
        static string ToolHint(string id) => id switch
        {
            "new-lift" => "anchor a terminal, then click the far end",
            "new-trail" => "paint it, then set its head and tail",
            "edit-lift" => "change a lift",
            "remove-lift" => "take a lift out",
            "edit-trail" => "repaint it, move its ends, regrade it",
            "remove-trail" => "put its ground and trees back",
            TerrainIds.Raise => "left raises, right lowers",
            TerrainIds.Flatten => "right-click a height, paint to it",
            TerrainIds.Smooth => "take out the bumps",
            TerrainIds.Slope => "right-click a height, drag a ramp to it",
            _ => "",
        };

        /// <summary>The tray letter's action for a tool id: New, Edit or Remove (the Lifts and Trails tabs).</summary>
        public static GameAction? ToolAction(string id) =>
            id == TerrainIds.Raise ? GameAction.TerrainRaise
            : id == TerrainIds.Flatten ? GameAction.TerrainFlatten
            : id == TerrainIds.Smooth ? GameAction.TerrainSmooth
            : id == TerrainIds.Slope ? GameAction.TerrainSlope
            : id.StartsWith("new-", StringComparison.Ordinal) ? GameAction.ToolNew
            : id.StartsWith("edit-", StringComparison.Ordinal) ? GameAction.ToolEdit
            : id.StartsWith("remove-", StringComparison.Ordinal) ? GameAction.ToolRemove
            : (GameAction?)null;

        /// <summary>The tool a tray letter means in the open tab ("new-lift"), or null.</summary>
        public string ToolFor(GameAction action)
        {
            if (_toolboxTab == "terrain")   // P3-15: the Terrain tab's own letters
                return action switch
                {
                    GameAction.TerrainRaise => TerrainIds.Raise,
                    GameAction.TerrainFlatten => TerrainIds.Flatten,
                    GameAction.TerrainSmooth => TerrainIds.Smooth,
                    GameAction.TerrainSlope => TerrainIds.Slope,
                    _ => null,
                };
            string noun = _toolboxTab == "lifts" ? "lift" : _toolboxTab == "trails" ? "trail" : null;
            if (noun == null) return null;
            return action switch
            {
                GameAction.ToolNew => "new-" + noun,
                GameAction.ToolEdit => "edit-" + noun,
                GameAction.ToolRemove => "remove-" + noun,
                _ => null,
            };
        }

        public string ToolboxTab => _toolboxTab;

        string _activeTool;

        static string KeyCaption(string id, string fallback)
        {
            var action = ToolAction(id);
            return action.HasValue ? KeyBindings.Caption(action.Value) : fallback;
        }

        /// <summary>Makes a tray tile live when its tool works (from SetToolboxTab).</summary>
        bool MakeLive(Button tile, string id, string name)
        {
            if (!ToolCatalog.IsLive(id)) return false;
            tile.RemoveFromClassList("tool--dis");
            tile.clickable = new Clickable(() => ToolChosen?.Invoke(id));
            tile.EnableInClassList("tool--on", id == _activeTool);
            tile.RegisterCallback<PointerEnterEvent>(_ => ShowTool(name, KeyCaption(id, ""), ToolHint(id)));
            tile.RegisterCallback<PointerLeaveEvent>(_ => ShowActiveTool());
            return true;
        }

        /// <summary>Lights the armed tool's tile and names it in the tray's label (null: none).</summary>
        public void SetActiveTool(string id)
        {
            _activeTool = id;
            foreach (var tile in _tools.Children()) tile.EnableInClassList("tool--on", tile.name == "tool-" + id);
            ShowActiveTool();
            RetintSoon();
        }

        public string ActiveTool => _activeTool;

        void ShowActiveTool()
        {
            if (_activeTool == null) { ShowTool("Select tool", "", ""); return; }
            foreach (var t in Tools)
                if (t.Id == _activeTool) { ShowTool(t.Name, KeyCaption(t.Id, t.Key), ""); return; }
        }

        /// <summary>The tool window takes the keyboard (Tab, P3-06 Q7): the arrows walk its controls, Enter or Space presses one.</summary>
        public bool TakeToolKeyboard(Focusable first)
        {
            if (first == null || HudWindow.Current == null) return false;
            UiFocus.UseKeyboard(_root);
            first.Focus();
            return true;
        }

        void LeftToolbox() => ToolboxLeft?.Invoke();
    }
}
