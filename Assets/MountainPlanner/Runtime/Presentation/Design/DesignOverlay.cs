using System;
using MountainPlanner.Domain.Design;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// The committed design, drawn plainly until tasks 08 and 09 draw the real thing (P3-06): each lift a line with a dot
    /// at each end (the trail map's lift), each trail its painted edges with a dot at the head and tail; plans orange
    /// (CF-06). Redrawn when the design changes (snapshots out), never per frame.
    /// </summary>
    public sealed class DesignOverlay : IDisposable
    {
        readonly PendingPreview _lines;
        readonly DesignHistory _history;

        /// <summary>The history it draws.</summary>
        public DesignHistory History => _history;
        readonly Vector2[] _pair = new Vector2[2];
        Vector2[] _left = new Vector2[64], _right = new Vector2[64];

        public DesignOverlay(Transform parent, World.ITerrainSurface surface, Material lineMaterial, DesignHistory history)
        {
            _lines = new PendingPreview(parent, "Design overlay", surface, lineMaterial);
            _history = history;
            _history.Changed += OnChanged;
            Draw(history.Document);
        }

        void OnChanged(DesignSnapshot snapshot) => Draw(snapshot.Document);

        public bool Visible
        {
            get => _lines.Root.gameObject.activeSelf;
            set => _lines.Root.gameObject.SetActive(value);
        }

        /// <summary>
        /// Whether built work is drawn too. Once the mountain draws built trails and lifts as they are (task 07's built
        /// view, decision SK11) the overlay keeps to plans.
        /// </summary>
        public bool ShowBuilt
        {
            get => _showBuilt;
            set
            {
                if (_showBuilt == value) return;
                _showBuilt = value;
                Draw(_history.Document);
            }
        }

        bool _showBuilt = true;

        void Draw(DesignDocument doc)
        {
            int slot = 0;
            foreach (var lift in doc.Lifts)
            {
                if (!_showBuilt && lift.Stage == WorkStage.Built) continue;
                var ink = lift.Stage == WorkStage.Plan ? PreviewInk.Plan : PreviewInk.Lift;
                _pair[0] = DesignFrame.ToLocal(lift.Bottom);
                _pair[1] = DesignFrame.ToLocal(lift.Top);
                _lines.Line(slot++, _pair, 2, ink);
                _lines.Dot(slot++, _pair[0], ink);
                _lines.Dot(slot++, _pair[1], ink);
            }
            foreach (var trail in doc.Trails)
            {
                if (!_showBuilt && trail.Stage == WorkStage.Built) continue;
                var ink = trail.Stage == WorkStage.Plan ? PreviewInk.Plan : PreviewInk.Paint;
                foreach (var part in trail.Parts)
                    foreach (var stroke in part.Strokes)
                    {
                        if (stroke.Erase) continue;   // the outline of what was painted; erased holes come with task 08's footprint
                        int n = stroke.Points.Count;
                        if (_left.Length < n) { _left = new Vector2[n * 2]; _right = new Vector2[n * 2]; }
                        float r = stroke.WidthMm / 2000f;
                        if (n == 1) { _lines.Ring(slot++, DesignFrame.ToLocal(stroke.Points[0]), r, ink); continue; }
                        DesignFrame.Edges(stroke.Points, r, _left, _right);
                        _lines.Line(slot++, _left, n, ink);
                        _lines.Line(slot++, _right, n, ink);
                    }
                if (trail.HasEnds)
                {
                    _lines.Dot(slot++, DesignFrame.ToLocal(trail.Head.Value), ink);
                    _lines.Dot(slot++, DesignFrame.ToLocal(trail.Tail.Value), ink);
                }
            }
            _lines.HideFrom(slot);
        }

        public void Follow(Camera camera) => _lines.Follow(camera);

        /// <summary>For tests: how many lines and dots are showing.</summary>
        public int VisibleParts => _lines.Visible;

        public void Dispose()
        {
            _history.Changed -= OnChanged;
            _lines.Dispose();
        }
    }
}
