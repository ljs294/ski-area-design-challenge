using System.Collections.Generic;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Landmark names on the map (Corbet's Couloir at Jackson Hole), drawn in the HUD's UI Toolkit panel rather than
    /// IMGUI: an active OnGUI allocates every frame, and the game must allocate nothing per frame (0.3 §8; task 15).
    /// One label per landmark, made once; each frame only moves them. A label the ground hides from the camera is hidden
    /// (owner, 2026-10-20: "labels are visible through terrain"). A label's sight is checked only while it's on screen and
    /// only once the eye has moved since its last check (owner, 2026-10-23, finding D1: the checks every frame made the
    /// game lag); in motion a few labels are checked each frame, in turn.
    /// </summary>
    public sealed class LandmarkLabelOverlay
    {
        const float LabelWidth = 240, LabelHeight = 26;
        const int SightEvery = 4;   // frames between one label's sight checks: a quarter of the labels each frame
        const float Stand = 2;      // metres the sight line aims above the label's point, so its own ground doesn't hide it

        readonly VisualElement _layer;
        readonly List<Label> _labels = new List<Label>();
        readonly List<Vector3> _anchors = new List<Vector3>();
        readonly List<bool> _hidden = new List<bool>();
        readonly List<Vector3> _checkedFrom = new List<Vector3>();   // the eye each label's sight was last checked from
        int _frame;
        bool _shown = true;

        /// <summary>Adds its own full-screen layer just above <paramref name="contourLayer"/>, so the HUD's panels stay on top.</summary>
        public LandmarkLabelOverlay(VisualElement contourLayer)
        {
            _layer = new VisualElement { name = "landmark-labels", pickingMode = PickingMode.Ignore };
            _layer.AddToClassList("contour-labels");   // the same full-screen, pass-through layer the contour labels use
            var parent = contourLayer.parent;
            parent.Insert(parent.IndexOf(contourLayer) + 1, _layer);
        }

        /// <summary>The landmarks to label: each name and the world point its label sits on.</summary>
        public void SetLandmarks(IReadOnlyList<(string Name, Vector3 At)> landmarks)
        {
            _layer.Clear();
            _labels.Clear();
            _anchors.Clear();
            _hidden.Clear();
            _checkedFrom.Clear();
            foreach (var (name, at) in landmarks)
            {
                var label = new Label(name) { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
                label.AddToClassList("landmark-label");
                _layer.Add(label);
                _labels.Add(label);
                _anchors.Add(at);
                _hidden.Add(false);
                _checkedFrom.Add(new Vector3(float.NaN, float.NaN, float.NaN));
            }
        }

        /// <summary>
        /// Call once a frame, after the camera has moved. Hidden when <paramref name="visible"/> is false; a label behind
        /// <paramref name="surface"/>'s ground is hidden (null: never).
        /// </summary>
        public void Update(Camera camera, ITerrainSurface surface, bool visible)
        {
            if (!visible || camera == null || _labels.Count == 0)
            {
                if (_shown)
                {
                    _layer.style.display = DisplayStyle.None;
                    _shown = false;
                }
                return;
            }
            if (!_shown)
            {
                _layer.style.display = DisplayStyle.Flex;
                _shown = true;
            }
            _frame++;
            var eye = camera.transform.position;
            for (int i = 0; i < _labels.Count; i++)
            {
                var label = _labels[i];
                Vector3 screen = camera.WorldToScreenPoint(_anchors[i]);
                bool onScreen = screen.z > 0 && screen.x > -LabelWidth && screen.x < camera.pixelWidth + LabelWidth
                                && screen.y > -LabelHeight && screen.y < camera.pixelHeight + LabelHeight;
                if (onScreen && (i + _frame) % SightEvery == 0 && Moved(eye, _checkedFrom[i], screen.z))
                {
                    _hidden[i] = LabelSight.Hidden(eye, _anchors[i] + Vector3.up * Stand, surface);
                    _checkedFrom[i] = eye;
                    SightChecks++;
                }
                if (!onScreen || _hidden[i])
                {
                    if (label.style.display != DisplayStyle.None) label.style.display = DisplayStyle.None;
                    continue;
                }
                if (_layer.panel == null) continue;   // not in a panel yet (tests)
                var local = _layer.WorldToLocal(RuntimePanelUtils.ScreenToPanel(_layer.panel, new Vector2(screen.x, Screen.height - screen.y)));
                label.style.translate = new Translate(local.x - LabelWidth / 2, local.y - LabelHeight);   // centred, standing on the point
                if (label.style.display != DisplayStyle.Flex) label.style.display = DisplayStyle.Flex;
            }
        }

        /// <summary>Whether the eye has moved enough since a label's last sight check to check again: a metre, or a thousandth
        /// of the distance to the label when that's more (never checked: NaN, always).</summary>
        static bool Moved(Vector3 eye, Vector3 from, float distance) =>
            float.IsNaN(from.x) || (eye - from).sqrMagnitude > Mathf.Max(1f, distance * 0.001f) * Mathf.Max(1f, distance * 0.001f);

        /// <summary>How many sight checks have run (tests: a still camera adds none).</summary>
        public int SightChecks { get; private set; }
    }
}
