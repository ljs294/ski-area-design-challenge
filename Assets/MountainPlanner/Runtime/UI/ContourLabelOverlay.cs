using System;
using MountainPlanner.Domain.Measure;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Contour elevation labels on screen (task 12b.2): a fixed pool of HUD labels placed over the labelled lines,
    /// turned to run along them and kept upright. Ten times a second it chooses which labels to show: on screen, not
    /// behind a ridge, not crowding each other, and only the heavy lines' labels where the labelled lines would
    /// pack closer than a few dozen pixels. Every frame it moves the chosen ones with the camera. Nothing is
    /// allocated after setup: the label texts were made when the labels were placed.
    /// </summary>
    public sealed class ContourLabelOverlay
    {
        public const int MaxLabels = 40;
        const float LabelWidth = 64, LabelHeight = 18;
        const float MinSpacingPixels = 28, ClearancePixels = 96, Margin = 24;
        const float SelectEvery = 0.1f;

        readonly VisualElement _layer;
        readonly Label[] _pool = new Label[MaxLabels];
        readonly int[] _chosen = new int[MaxLabels];
        readonly Vector2[] _accepted = new Vector2[MaxLabels];
        int _chosenCount;
        ContourLabel[] _labels = Array.Empty<ContourLabel>();
        float[] _priority = Array.Empty<float>();
        int[] _order = Array.Empty<int>();
        float _nextSelect, _labelledInterval = 60.96f;
        bool _shown;

        public ContourLabelOverlay(VisualElement layer)
        {
            _layer = layer;
            for (int k = 0; k < MaxLabels; k++)
            {
                var label = new Label { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
                label.AddToClassList("contour-label");
                label.style.display = DisplayStyle.None;
                _layer.Add(label);
                _pool[k] = label;
            }
        }

        /// <summary>The labels to choose from (a new mountain, or new units).</summary>
        public void SetLabels(ContourLabel[] labels, UnitSystem units)
        {
            _labels = labels ?? Array.Empty<ContourLabel>();
            _labelledInterval = (float)(UnitFormat.ContourIntervalMetres(units) * UnitFormat.IndexEvery);
            if (_priority.Length < _labels.Length)
            {
                _priority = new float[_labels.Length];
                _order = new int[_labels.Length];
            }
            _chosenCount = 0;
            _nextSelect = 0;
        }

        /// <summary>Call once a frame. Hidden when <paramref name="visible"/> is false (contours off, or the HUD hidden).</summary>
        public void Update(Camera camera, ITerrainSurface surface, bool visible)
        {
            if (!visible || camera == null || _labels.Length == 0)
            {
                if (_shown) Hide();
                return;
            }
            if (!_shown)
            {
                _layer.style.display = DisplayStyle.Flex;
                _shown = true;
            }
            if (Time.unscaledTime >= _nextSelect)
            {
                _nextSelect = Time.unscaledTime + SelectEvery;
                Select(camera, surface);
            }
            for (int k = 0; k < MaxLabels; k++)
            {
                var label = _pool[k];
                if (k >= _chosenCount)
                {
                    if (label.style.display != DisplayStyle.None) label.style.display = DisplayStyle.None;
                    continue;
                }
                var l = _labels[_chosen[k]];
                if (!Project(camera, l, out var at, out float degrees)) { label.style.display = DisplayStyle.None; continue; }
                var local = _layer.WorldToLocal(RuntimePanelUtils.ScreenToPanel(_layer.panel, new Vector2(at.x, Screen.height - at.y)));
                label.style.translate = new Translate(local.x - LabelWidth / 2, local.y - LabelHeight / 2);
                label.style.rotate = new Rotate(-degrees);
                if (label.style.display != DisplayStyle.Flex) label.style.display = DisplayStyle.Flex;
            }
        }

        void Hide()
        {
            _layer.style.display = DisplayStyle.None;
            _shown = false;
        }

        /// <summary>A label's screen point and its line's screen angle in degrees, turned to read left to right.</summary>
        static bool Project(Camera camera, in ContourLabel l, out Vector3 at, out float degrees)
        {
            var p = new Vector3(l.X, l.Elevation, l.Z);
            at = camera.WorldToScreenPoint(p);
            degrees = 0;
            if (at.z <= 0) return false;
            var ahead = camera.WorldToScreenPoint(p + new Vector3(Mathf.Cos(l.Angle), 0, Mathf.Sin(l.Angle)) * 20);
            degrees = Mathf.Atan2(ahead.y - at.y, ahead.x - at.x) * Mathf.Rad2Deg;
            if (degrees > 90) degrees -= 180;
            else if (degrees < -90) degrees += 180;
            return true;
        }

        void Select(Camera camera, ITerrainSurface surface)
        {
            float pixelsPerRadian = Screen.height / (2 * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            int candidates = 0;
            for (int i = 0; i < _labels.Length; i++)
            {
                var l = _labels[i];
                var at = camera.WorldToScreenPoint(new Vector3(l.X, l.Elevation, l.Z));
                if (at.z <= 1 || at.x < Margin || at.y < Margin || at.x > Screen.width - Margin || at.y > Screen.height - Margin) continue;
                // How far apart the labelled lines sit on screen here; where they pack tight only the heavy lines' labels stay.
                float metresBetween = _labelledInterval / Mathf.Max(l.Rise, 0.02f);
                float pixelsBetween = metresBetween / at.z * pixelsPerRadian;
                if (!l.Major && pixelsBetween < MinSpacingPixels) continue;
                if (l.Major && pixelsBetween * 5 < MinSpacingPixels) continue;
                _priority[candidates] = (l.Major ? 0 : 1e6f) + at.z;   // heavy lines first, then the nearest
                _order[candidates] = i;
                candidates++;
            }
            Array.Sort(_priority, _order, 0, candidates);
            _chosenCount = 0;
            for (int c = 0; c < candidates && _chosenCount < MaxLabels; c++)
            {
                var l = _labels[_order[c]];
                Vector2 at = camera.WorldToScreenPoint(new Vector3(l.X, l.Elevation, l.Z));
                bool crowded = false;
                for (int a = 0; a < _chosenCount && !crowded; a++) crowded = (_accepted[a] - at).sqrMagnitude < ClearancePixels * ClearancePixels;
                if (crowded || Hidden(camera.transform.position, l, surface)) continue;
                _accepted[_chosenCount] = at;
                _chosen[_chosenCount++] = _order[c];
            }
        }

        /// <summary>True when the ground rises between the camera and the label (checked at 16 points along the way).</summary>
        static bool Hidden(Vector3 eye, in ContourLabel l, ITerrainSurface surface)
        {
            if (surface == null) return false;
            var target = new Vector3(l.X, l.Elevation, l.Z);
            for (int k = 1; k < 16; k++)
            {
                var p = Vector3.Lerp(eye, target, k / 16f);
                float ground = surface.HeightAt(p.x, p.z);
                if (!float.IsNaN(ground) && ground > p.y + 2) return true;
            }
            return false;
        }
    }
}
