using System.Collections.Generic;
using MountainPlanner.Presentation.Design;
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
    /// <para>
    /// With a <see cref="LabelPlacement"/> the names never overlap another set's (NT7): each tries its spots, then its
    /// nudges, until its box is free. A name set along a line (a run, a lift) is centred on it and slanted with it, read
    /// left to right, as the mockup's trail map sets them (owner, 2026-10-26); an upright one stands on its point.
    /// </para>
    /// </summary>
    public sealed class LandmarkLabelOverlay
    {
        const float LabelWidth = 240, LabelHeight = 26;
        const float TextHeight = 20;   // the box a name keeps clear: its letters' height
        const float Margin = 4;        // pixels kept clear either side of a name
        const int SightEvery = 4;   // frames between one label's sight checks: a quarter of the labels each frame
        const float Stand = 2;      // metres the sight line aims above the label's point, so its own ground doesn't hide it
        static readonly Vector2[] NoNudge = { Vector2.zero };

        readonly VisualElement _layer;
        readonly LabelPlacement _placement;
        readonly bool _yields;
        readonly Vector2[] _nudges;
        readonly List<Label> _labels = new List<Label>();
        readonly List<float> _widths = new List<float>();        // each name's measured width (NaN until it's in a panel)
        readonly List<int> _firstSpot = new List<int>();        // each name's spots, from here to the next name's
        readonly List<MapLabelSpot> _spots = new List<MapLabelSpot>();
        readonly List<bool> _hidden = new List<bool>();          // per spot
        readonly List<Vector3> _checkedFrom = new List<Vector3>();   // the eye each spot's sight was last checked from
        Camera _camera;
        ITerrainSurface _surface;
        bool _wanted;
        int _frame;
        bool _shown = true;

        /// <summary>
        /// Adds its own full-screen layer just above <paramref name="contourLayer"/>, so the HUD's panels stay on top. With
        /// <paramref name="placement"/> its names are placed with the other sets', at <paramref name="rank"/> (see
        /// <see cref="LabelPlacement"/>); <paramref name="yields"/> false: they always show. <paramref name="nudges"/>:
        /// screen offsets (pixels, y down) a name tries at each spot before the next.
        /// </summary>
        public LandmarkLabelOverlay(VisualElement contourLayer, LabelPlacement placement = null, int rank = LabelPlacement.Landmarks,
                                    bool yields = true, Vector2[] nudges = null)
        {
            _layer = new VisualElement { name = "landmark-labels", pickingMode = PickingMode.Ignore };
            _layer.AddToClassList("contour-labels");   // the same full-screen, pass-through layer the contour labels use
            var parent = contourLayer.parent;
            parent.Insert(parent.IndexOf(contourLayer) + 1, _layer);
            Rank = rank;
            _yields = yields;
            _nudges = nudges ?? NoNudge;
            _placement = placement;
            placement?.Add(this);
        }

        /// <summary>The set's place in the order names are placed (<see cref="LabelPlacement"/>).</summary>
        public int Rank { get; }

        /// <summary>The landmarks to label: each name and the world point its label stands on.</summary>
        public void SetLandmarks(IReadOnlyList<(string Name, Vector3 At)> landmarks)
        {
            Clear();
            foreach (var (name, at) in landmarks) Add(name, MapLabelSpot.Upright(at));
        }

        /// <summary>The names to place, in priority order: each with its spots, best first.</summary>
        public void SetLabels(IReadOnlyList<(string Name, MapLabelSpot[] Spots)> labels)
        {
            Clear();
            foreach (var (name, spots) in labels)
            {
                if (spots == null || spots.Length == 0) continue;
                Add(name, spots);
            }
        }

        void Clear()
        {
            _layer.Clear();
            _labels.Clear();
            _widths.Clear();
            _firstSpot.Clear();
            _spots.Clear();
            _hidden.Clear();
            _checkedFrom.Clear();
        }

        void Add(string name, params MapLabelSpot[] spots)
        {
            var label = new Label(name) { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
            label.AddToClassList("landmark-label");
            if (spots[0].Along) label.AddToClassList("landmark-label--along");
            _layer.Add(label);
            _labels.Add(label);
            _widths.Add(float.NaN);
            _firstSpot.Add(_spots.Count);
            foreach (var s in spots)
            {
                _spots.Add(s);
                _hidden.Add(false);
                _checkedFrom.Add(new Vector3(float.NaN, float.NaN, float.NaN));
            }
        }

        /// <summary>
        /// Call once a frame, after the camera has moved. Hidden when <paramref name="visible"/> is false; a label behind
        /// <paramref name="surface"/>'s ground is hidden (null: never). With a <see cref="LabelPlacement"/> the names move
        /// when it places them; without one, now.
        /// </summary>
        public void Update(Camera camera, ITerrainSurface surface, bool visible)
        {
            _camera = camera;
            _surface = surface;
            _wanted = visible;
            if (_placement == null) Place(null);
        }

        /// <summary>Places this set's names clear of <paramref name="taken"/>, adding their boxes (null: anywhere).</summary>
        internal void Place(List<LabelBox> taken)
        {
            var camera = _camera;
            if (!_wanted || camera == null || _labels.Count == 0)
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
                int end = i + 1 < _firstSpot.Count ? _firstSpot[i + 1] : _spots.Count;
                bool placed = false;
                LabelBox box = default;
                Vector2 centre = default;
                for (int s = _firstSpot[i]; s < end && !placed; s++)
                {
                    var spot = _spots[s];
                    Vector3 screen = camera.WorldToScreenPoint(spot.At);
                    bool onScreen = screen.z > 0 && screen.x > -LabelWidth && screen.x < camera.pixelWidth + LabelWidth
                                    && screen.y > -LabelHeight && screen.y < camera.pixelHeight + LabelHeight;
                    if (!onScreen) continue;
                    if ((s + _frame) % SightEvery == 0 && Moved(eye, _checkedFrom[s], screen.z))
                    {
                        _hidden[s] = LabelSight.Hidden(eye, spot.At + Vector3.up * Stand, _surface);
                        _checkedFrom[s] = eye;
                        SightChecks++;
                    }
                    if (_hidden[s] || _layer.panel == null) continue;   // behind the ground, or not in a panel yet (tests)
                    var local = ToLocal(screen);
                    float degrees = 0;
                    if (spot.Along)
                    {
                        Vector3 a = camera.WorldToScreenPoint(spot.From), b = camera.WorldToScreenPoint(spot.To);
                        if (a.z > 0 && b.z > 0) degrees = Slant(ToLocal(a), ToLocal(b));
                    }
                    else local.y -= LabelHeight / 2;   // an upright name stands on its point
                    var half = new Vector2(Width(i) / 2 + Margin, TextHeight / 2);
                    for (int n = 0; n < _nudges.Length; n++)
                    {
                        box = new LabelBox(local + _nudges[n], half, degrees);
                        if (taken == null || !_yields || LabelPlacement.Free(taken, box))
                        {
                            placed = true;
                            centre = box.Centre;
                            break;
                        }
                    }
                }
                if (!placed)
                {
                    if (label.style.display != DisplayStyle.None) label.style.display = DisplayStyle.None;
                    continue;
                }
                taken?.Add(box);
                label.style.translate = new Translate(centre.x - LabelWidth / 2, centre.y - LabelHeight / 2);
                label.style.rotate = new Rotate(new Angle(Mathf.Atan2(box.Sin, box.Cos) * Mathf.Rad2Deg, AngleUnit.Degree));
                if (label.style.display != DisplayStyle.Flex) label.style.display = DisplayStyle.Flex;
            }
        }

        Vector2 ToLocal(Vector3 screen) =>
            _layer.WorldToLocal(RuntimePanelUtils.ScreenToPanel(_layer.panel, new Vector2(screen.x, Screen.height - screen.y)));

        /// <summary>The slant (degrees, clockwise on screen) of the line from <paramref name="a"/> to <paramref name="b"/>,
        /// turned so the name reads left to right.</summary>
        public static float Slant(Vector2 a, Vector2 b)
        {
            float degrees = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            if (degrees > 90) degrees -= 180;
            else if (degrees < -90) degrees += 180;
            return degrees;
        }

        // A name's width in pixels, measured once it's in a panel (until then, a guess from its letters).
        float Width(int i)
        {
            if (!float.IsNaN(_widths[i])) return _widths[i];
            var label = _labels[i];
            float w = label.MeasureTextSize(label.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            if (w > 0 && !float.IsNaN(w))
            {
                _widths[i] = w;
                return w;
            }
            return label.text.Length * 9f;
        }

        /// <summary>Whether the eye has moved enough since a label's last sight check to check again: a metre, or a thousandth
        /// of the distance to the label when that's more (never checked: NaN, always).</summary>
        static bool Moved(Vector3 eye, Vector3 from, float distance) =>
            float.IsNaN(from.x) || (eye - from).sqrMagnitude > Mathf.Max(1f, distance * 0.001f) * Mathf.Max(1f, distance * 0.001f);

        /// <summary>How many sight checks have run (tests: a still camera adds none).</summary>
        public int SightChecks { get; private set; }
    }
}
