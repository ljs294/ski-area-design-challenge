using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Measure;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// The Trails map layer (task 08, owner TL6): every trail drawn as a trail map draws it, one line along its automatic
    /// centreline (NW-09) in its rating's colour (TD-03), plans orange (CF-06), the trail in focus with a white casing,
    /// and its name with the rating's symbol centred on the line. A map layer, so it shows physical things only; the
    /// graded ground stays when it's off. Redrawn when the design or the ground changes, never per frame; the rating
    /// comes from each trail's last grade (TD-01 v2) or, without one, the painted cells' estimate (TD-06).
    /// </summary>
    public sealed class TrailMapView : IDisposable
    {
        readonly PendingPreview _lines;
        readonly Dictionary<TrailId, Shape> _shapes = new Dictionary<TrailId, Shape>();
        readonly List<(TrailId id, int slot)> _slots = new List<(TrailId, int)>();
        TrailId _focus;

        sealed class Shape
        {
            public Trail Trail;
            public TrailFootprint Footprint;
            public Vector2[] Line;   // local x/z metres
            public SlopeBands.Band Band;
            public double Rating = double.NaN, Mean = double.NaN, Steepest = double.NaN;
            public double LengthMetres, DropMetres;
        }

        public TrailMapView(Transform parent, World.ITerrainSurface surface, Material lineMaterial)
        {
            _lines = new PendingPreview(parent, "Trails layer", surface, lineMaterial);
        }

        public bool Visible
        {
            get => _lines.Root.gameObject.activeSelf;
            set => _lines.Root.gameObject.SetActive(value);
        }

        /// <summary>A trail's figures as drawn (Analysis › Trails and its card read these).</summary>
        public readonly struct Facts
        {
            public readonly double Rating, Mean, Steepest, LengthMetres, DropMetres;
            public Facts(double rating, double mean, double steepest, double length, double drop)
            {
                Rating = rating; Mean = mean; Steepest = steepest; LengthMetres = length; DropMetres = drop;
            }
        }

        public bool TryGetFacts(TrailId id, out Facts facts)
        {
            facts = default;
            if (!_shapes.TryGetValue(id, out var s)) return false;
            facts = new Facts(s.Rating, s.Mean, s.Steepest, s.LengthMetres, s.DropMetres);
            return true;
        }

        /// <summary>
        /// Redraws every trail in <paramref name="doc"/>; <paramref name="labels"/> gets each one's name and the world
        /// point its label sits on (the middle of its line).
        /// </summary>
        public void Rebuild(DesignDocument doc, EditedHeights heights, LocalFrame frame, DesignEarthwork earthwork, List<(string Name, Vector3 At)> labels,
                            World.ITerrainSurface surface)
        {
            labels?.Clear();
            var seen = new HashSet<TrailId>();
            int slot = 0;
            _slots.Clear();
            foreach (var t in doc.Trails)
            {
                if (!t.HasEnds) continue;
                seen.Add(t.Id);
                if (!_shapes.TryGetValue(t.Id, out var s) || !ReferenceEquals(s.Trail, t)) _shapes[t.Id] = s = Build(t, heights, frame);
                Rate(s, heights, earthwork);
                if (s.Line == null) continue;
                _slots.Add((t.Id, slot));
                _lines.Line(slot++, s.Line, s.Line.Length, InkOf(s, t.Id == _focus));
                if (labels != null)
                {
                    var mid = s.Line[s.Line.Length / 2];
                    float y = surface != null ? surface.HeightAt(mid.x, mid.y) : 0;
                    string symbol = t.Stage == WorkStage.Plan ? "" : Symbol(s.Band) + " ";
                    labels.Add((symbol + t.Name, new Vector3(mid.x, float.IsNaN(y) ? 0 : y, mid.y)));
                }
            }
            _lines.HideFrom(slot);
            var stale = new List<TrailId>();
            foreach (var id in _shapes.Keys) if (!seen.Contains(id)) stale.Add(id);
            foreach (var id in stale) _shapes.Remove(id);
        }

        /// <summary>A trail in focus (a hovered Analysis row, its focus card) draws white; the rest keep their colour.</summary>
        public void Focus(TrailId id)
        {
            if (id == _focus) return;
            _focus = id;
            foreach (var (tid, slot) in _slots)
                if (_shapes.TryGetValue(tid, out var s) && s.Line != null) _lines.Line(slot, s.Line, s.Line.Length, InkOf(s, tid == _focus));
        }

        public void Follow(Camera camera) => _lines.Follow(camera);

        public int VisibleParts => _lines.Visible;

        static PreviewInk InkOf(Shape s, bool focus)
        {
            if (focus) return PreviewInk.Casing;
            if (s.Trail.Stage == WorkStage.Plan) return PreviewInk.Plan;
            return s.Band == SlopeBands.Band.Green ? PreviewInk.RatingGreen : s.Band == SlopeBands.Band.Blue ? PreviewInk.RatingBlue : PreviewInk.RatingBlack;
        }

        public static string Symbol(SlopeBands.Band band) =>
            band == SlopeBands.Band.Green ? "●" : band == SlopeBands.Band.Blue ? "■" : band == SlopeBands.Band.Black ? "◆" : "◆◆";

        static Shape Build(Trail t, EditedHeights heights, LocalFrame frame)
        {
            var s = new Shape { Trail = t };
            s.Footprint = TrailFootprint.Build(t.Parts, heights.Lattice, frame.Origin.X, frame.Origin.Y);
            var head = frame.ToAlbers(t.Head.Value.XMetres, t.Head.Value.YMetres);
            var tail = frame.ToAlbers(t.Tail.Value.XMetres, t.Tail.Value.YMetres);
            var c = TrailCentreline.Find(s.Footprint, head.X, head.Y, tail.X, tail.Y);
            if (!c.Found) return s;
            int n = Math.Min(c.Points.Length / 2, 1000);
            s.Line = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                int at = (int)((long)k * (c.Points.Length / 2 - 1) / Math.Max(1, n - 1));
                s.Line[k] = new Vector2((float)(c.Points[2 * at] - frame.Origin.X), (float)(c.Points[2 * at + 1] - frame.Origin.Y));
            }
            s.LengthMetres = c.LengthMetres;
            s.DropMetres = heights.At(head.X, head.Y) - heights.At(tail.X, tail.Y);
            return s;
        }

        static void Rate(Shape s, EditedHeights heights, DesignEarthwork earthwork)
        {
            var build = earthwork?.DetailOf(s.Trail.Id.Value) as TrailBuild;
            if (build?.Grade != null && s.Trail.Stage == WorkStage.Built)
            {
                var r = TrailRating.Rate(heights, build.Footprint, build.Grade);
                s.Rating = r.ValuePercent;
                s.Mean = r.MeanPercent;
                s.Steepest = r.MaxPercent;
                s.LengthMetres = build.Grade.LengthMetres;
                s.DropMetres = r.DropMetres;
            }
            else if (double.IsNaN(s.Rating)) s.Rating = TrailRating.Estimate(heights, s.Footprint);
            s.Band = double.IsNaN(s.Rating) ? SlopeBands.Band.Green : SlopeBands.Of(s.Rating);
        }

        public void Dispose() => _lines.Dispose();
    }
}
