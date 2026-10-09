using MountainPlanner.Presentation;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The HUD's key captions follow the player's keys (Settings › Controls; task P2-05): the bar's tooltips and the
    /// Layers panel's key column. Redrawn only when a binding changes.
    /// </summary>
    public sealed partial class MountainHud
    {
        static readonly (string Row, GameAction Action)[] LayerKeys =
        {
            ("layer-snow", GameAction.LayerSnow), ("layer-trees", GameAction.LayerTrees), ("layer-slope", GameAction.InfoSlope),
            ("layer-exposure", GameAction.InfoExposure), ("layer-depth", GameAction.InfoDepth), ("layer-conditions", GameAction.InfoConditions),
            ("layer-contours", GameAction.InfoContours),
        };

        string _pauseKey = "Space";

        void WireKeyCaptions()
        {
            KeyBindings.Changed -= ShowKeyCaptions;
            KeyBindings.Changed += ShowKeyCaptions;
            ShowKeyCaptions();
        }

        void ReleaseKeyCaptions() => KeyBindings.Changed -= ShowKeyCaptions;

        void ShowKeyCaptions()
        {
            if (_root == null) return;
            SetTip("bar-toolbox", "Toolbox", GameAction.Toolbox);
            SetTip("bar-analysis", "Analysis", GameAction.Analysis);
            for (int i = 0; i < 4; i++) SetTip("bar-speed-" + (i + 1), "Speed " + (i + 1), GameAction.Speed1 + i);
            _pauseKey = KeyBindings.Caption(GameAction.Pause);
            _shownPaused = -1;   // the pause tooltip redraws with the next clock update
            foreach (var (row, action) in LayerKeys)
            {
                var key = _root.Q(row)?.Q<Label>(className: "row__key");
                if (key != null) key.text = KeyBindings.Caption(action);
            }
        }

        void SetTip(string name, string what, GameAction action)
        {
            var e = _root.Q(name);
            if (e == null) return;
            string key = KeyBindings.Caption(action);
            e.tooltip = key.Length > 0 ? $"{what} ({key})" : what;
        }

        /// <summary>"Play (Space)" or "Pause (Space)", with the player's key.</summary>
        string PauseTip(bool paused) =>
            _pauseKey.Length > 0 ? (paused ? "Play (" : "Pause (") + _pauseKey + ")" : paused ? "Play" : "Pause";
    }
}
