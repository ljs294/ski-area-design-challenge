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

        internal sealed class Shape
        {
            public Trail Trail;
            public TrailFootprint Footprint;
            public Vector2[] Line;   // local x/z metres
            public Vector2[] Drawn;  // Line, its ends carried to the hidden nodes they're in (NT42)
            public SlopeBands.Band Band;
            public double Rating = double.NaN, Mean = double.NaN, Steepest = double.NaN;
            public double LengthMetres, DropMetres;
            // Task P3-11: the rating stands while its build is the same and no ground under the footprint moved.
            public bool Rated;
            public TrailBuild RatedBuild;
            public HashSet<EditTileKey> FootprintTiles;   // the edit tiles its painted cells touch

            public Shape Clone() => (Shape)MemberwiseClone();
        }

        /// <summary>The shapes and ratings for one design over one ground, worked out off the main thread (task P3-11).</summary>
        public sealed class Prepared
        {
            internal DesignDocument Doc;
            internal TerrainEdits Over;
            internal Dictionary<TrailId, Shape> Shapes;
            internal int Built, Rated;
            internal double Ms;
        }

        /// <summary>
        /// Works out every trail's shape and rating for <paramref name="doc"/> over <paramref name="heights"/>, on any thread
        /// (the shapes here are copies: nothing drawn changes until <see cref="Rebuild"/> takes the result on the main thread).
        /// </summary>
        public Prepared Prepare(DesignDocument doc, EditedHeights heights, LocalFrame frame, DesignEarthwork earthwork)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var p = new Prepared { Doc = doc, Over = heights.Edits, Shapes = new Dictionary<TrailId, Shape>() };
            var moved = new HashSet<EditTileKey>(_ratedOver == null ? new List<EditTileKey>() : TerrainEdits.Changed(_ratedOver, heights.Edits));
            bool all = _ratedOver == null;
            foreach (var t in doc.Trails)
            {
                if (!t.HasEnds) continue;
                Shape s;
                if (!_shapes.TryGetValue(t.Id, out var was) || !SameTrail(was.Trail, t)) { s = Build(t, heights, frame, earthwork); p.Built++; }
                else { s = was.Clone(); s.Trail = t; }
                if (all || Meets(s, moved)) s.Rated = false;
                if (!s.Rated) p.Rated++;
                Rate(s, heights, earthwork);
                p.Shapes[t.Id] = s;
            }
            p.Ms = watch.Elapsed.TotalMilliseconds;
            return p;
        }

        TerrainEdits _ratedOver;

        /// <summary>The last redraw's stages, ms (the [Built] log, task P3-11).</summary>
        public string LastSplit { get; private set; } = "";

        public TrailMapView(Transform parent, World.ITerrainSurface surface, Material lineMaterial)
        {
            _ground = new EditedSurface(surface);
            _lines = new PendingPreview(parent, "Trails layer", _ground, lineMaterial);
            _lines.SetFade(FadeNear, FadeFar);
        }

        /// <summary>Run lines fade out near the camera: gone within 40 m, fully drawn from 150 m (owner, TL26).</summary>
        public const float FadeNear = 40, FadeFar = 150;

        readonly EditedSurface _ground;

        /// <summary>
        /// The ground the lines drape on: the edited heights the edit pipeline laid (SH-02), exact the moment they're laid,
        /// rather than the terrain's own sampling, which can lag an edit; the terrain outside them.
        /// </summary>
        sealed class EditedSurface : World.ITerrainSurface
        {
            readonly World.ITerrainSurface _terrain;
            public EditedHeights Heights;
            public LocalFrame Frame;
            public EditedSurface(World.ITerrainSurface terrain) { _terrain = terrain; }
            public Rect Footprint => _terrain?.Footprint ?? default;

            public float HeightAt(float x, float z)
            {
                if (Heights == null) return _terrain != null ? _terrain.HeightAt(x, z) : float.NaN;
                var a = Frame.ToAlbers(x, z);
                return (float)Heights.At(a.X, a.Y);
            }
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
        /// Redraws every trail in <paramref name="doc"/>; <paramref name="labels"/> gets each one's name, the spots its
        /// label may sit on along its line (the middle first, NT7) and the line's length.
        /// </summary>
        public void Rebuild(DesignDocument doc, EditedHeights heights, LocalFrame frame, DesignEarthwork earthwork,
                            List<(string Name, MapLabelSpot[] Spots, double Length)> labels, World.ITerrainSurface surface, Prepared prepared = null)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), build = 0, rate = 0, lines = 0, labelled = 0;
            int built = 0, rated = 0;
            // Prepared for this very design and ground (task P3-11): its shapes and ratings are taken as they are.
            if (prepared != null && (!ReferenceEquals(prepared.Doc, doc) || !ReferenceEquals(prepared.Over, heights.Edits))) prepared = null;
            if (prepared != null) { built = prepared.Built; rated = prepared.Rated; }
            labels?.Clear();
            _ground.Heights = heights;
            _ground.Frame = frame;
            var seen = new HashSet<TrailId>();
            var nodes = TrailNodes.Of(doc);
            int slot = CasingSlot + 1;
            _slots.Clear();
            // The ground that moved since the last redraw: a rating whose footprint it meets is worked out again.
            var moved = new HashSet<EditTileKey>(_ratedOver == null ? new List<EditTileKey>() : TerrainEdits.Changed(_ratedOver, heights.Edits));
            bool all = _ratedOver == null;
            _ratedOver = heights.Edits;
            foreach (var t in doc.Trails)
            {
                if (!t.HasEnds) continue;
                seen.Add(t.Id);
                // The same trail by value keeps its shape (the history applies a command again, so an edited trail is never
                // the object graded, task P3-11); its stage too, which colours and rates it.
                long a = System.Diagnostics.Stopwatch.GetTimestamp();
                Shape s;
                long b, c;
                if (prepared != null && prepared.Shapes.TryGetValue(t.Id, out s))
                {
                    _shapes[t.Id] = s;
                    b = c = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                else
                {
                    if (!_shapes.TryGetValue(t.Id, out s) || !SameTrail(s.Trail, t)) { _shapes[t.Id] = s = Build(t, heights, frame, earthwork); built++; }
                    else s.Trail = t;
                    b = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (all || Meets(s, moved)) s.Rated = false;
                    if (!s.Rated) rated++;
                    Rate(s, heights, earthwork);
                    c = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                build += b - a;
                rate += c - b;
                if (s.Line == null) continue;
                s.Drawn = Joined(s.Line, nodes, t);
                _slots.Add((t.Id, slot));
                _lines.Line(slot++, s.Drawn, s.Drawn.Length, InkOf(s));
                long d = System.Diagnostics.Stopwatch.GetTimestamp();
                lines += d - c;
                if (labels != null)
                {
                    string symbol = t.Stage == WorkStage.Plan ? "" : Symbol(s.Band) + " ";
                    labels.Add((symbol + t.Name, LabelSpots(s.Drawn), s.LengthMetres));
                }
                labelled += System.Diagnostics.Stopwatch.GetTimestamp() - d;
            }
            _lines.HideFrom(slot);
            double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastSplit = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "shapes {0:F1} ({1} built), ratings {2:F1} ({3} rated){7}, lines {4:F1}, labels {5:F1}, the rest {6:F1}",
                Ms(build), built, Ms(rate), rated, Ms(lines), Ms(labelled), Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0 - build - rate - lines - labelled),
                prepared != null ? $" (prepared off the main thread in {prepared.Ms:F1})" : "");
            _used = slot;
            DrawCasing();
            var stale = new List<TrailId>();
            foreach (var id in _shapes.Keys) if (!seen.Contains(id)) stale.Add(id);
            foreach (var id in stale) _shapes.Remove(id);
        }

        /// <summary>
        /// Trails made of sections (task P3-10, sections finding 5): one line per trail instead of one per run, so a run
        /// shared by two trails no longer reads as one line. <paramref name="covered"/> holds the runs those lines draw;
        /// any other run keeps its own line. Redrawn when the trails are worked out, never per frame.
        /// </summary>
        public void SetTrailLines(IReadOnlyList<(Vector2[] Line, PreviewInk Ink)> trails, ICollection<TrailId> covered)
        {
            int slot = CasingSlot + 1;
            foreach (var (line, ink) in trails)
            {
                if (line == null || line.Length < 2) continue;
                _lines.Line(slot++, line, line.Length, ink);
            }
            foreach (var (id, _) in _slots)
                if (!covered.Contains(id) && _shapes.TryGetValue(id, out var s) && s.Drawn != null)
                    _lines.Line(slot++, s.Drawn, s.Drawn.Length, InkOf(s));
            _lines.HideFrom(slot);
            _used = slot;
            DrawCasing();
        }

        /// <summary>A trail's ink: orange while planned (CF-06), else its rating's colour (TD-03).</summary>
        public static PreviewInk InkOf(bool built, double rating)
        {
            if (!built) return PreviewInk.Plan;
            var band = double.IsNaN(rating) ? SlopeBands.Band.Green : SlopeBands.Of(rating);
            return band == SlopeBands.Band.Green ? PreviewInk.RatingGreen : band == SlopeBands.Band.Blue ? PreviewInk.RatingBlue : PreviewInk.RatingBlack;
        }

        /// <summary>A trail in focus (a hovered Analysis row, its focus card) gets a wider white casing under its line (owner, #87).</summary>
        public void Focus(TrailId id)
        {
            if (id == _focus && _focusLine == null) return;
            _focus = id;
            _focusLine = null;
            DrawCasing();
        }

        /// <summary>A trail made of sections in focus (task P3-10, NT43): the casing under its whole line.</summary>
        public void FocusLine(Vector2[] line)
        {
            _focus = default;
            _focusLine = line;
            DrawCasing();
        }

        Vector2[] _focusLine;

        /// <summary>A run's centreline as drawn (local x/z metres, head first), or false when it has none.</summary>
        public bool TryGetLine(TrailId id, out Vector2[] line)
        {
            line = _shapes.TryGetValue(id, out var s) ? s.Line : null;
            return line != null && line.Length > 1;
        }

        int _used;
        // The casing first, so the trails from slot 1 draw over it.
        const int CasingSlot = 0;

        void DrawCasing()
        {
            var line = _focusLine;
            if (line == null && !_focus.IsNone && _shapes.TryGetValue(_focus, out var s)) line = s.Drawn;
            if (line == null || line.Length < 2) { _lines.Hide(CasingSlot); return; }
            _lines.Line(CasingSlot, line, line.Length, PreviewInk.Casing);
            _lines.SetWidth(CasingSlot, 2.4f, behind: true);
        }

        public void Follow(Camera camera) => _lines.Follow(camera);

        /// <summary>The ground the lines drape on (the edited heights, read from the package: never sampled per frame).</summary>
        public World.ITerrainSurface Ground => _ground;

        public int VisibleParts => _lines.Visible;

        /// <summary>
        /// NT42 (owner, 2026-10-27): where a section's end shares a hidden node with others, its line is drawn on to the
        /// node's point, so lines that meet look met. Only the drawing: the painted ends, the ground and the save stay as
        /// built, and nothing marks or names the node. The line itself when neither end moves.
        /// </summary>
        static Vector2[] Joined(Vector2[] line, TrailNodes nodes, Trail t)
        {
            ulong head = nodes.HeadNode(t.Id), tail = nodes.TailNode(t.Id);
            bool h = head != 0 && nodes.CountOf(head) > 1, k = tail != 0 && nodes.CountOf(tail) > 1;
            Vector2 Local(ulong node) { var p = nodes.PointOf(node); return new Vector2((float)p.XMetres, (float)p.YMetres); }
            var from = h ? Local(head) : line[0];
            var to = k ? Local(tail) : line[line.Length - 1];
            h &= (from - line[0]).sqrMagnitude > 0.25f;
            k &= (to - line[line.Length - 1]).sqrMagnitude > 0.25f;
            if (!h && !k) return line;
            var drawn = new Vector2[line.Length + (h ? 1 : 0) + (k ? 1 : 0)];
            int at = 0;
            if (h) drawn[at++] = from;
            Array.Copy(line, 0, drawn, at, line.Length);
            if (k) drawn[drawn.Length - 1] = to;
            return drawn;
        }

        static PreviewInk InkOf(Shape s)
        {
            if (s.Trail.Stage == WorkStage.Plan) return PreviewInk.Plan;
            return s.Band == SlopeBands.Band.Green ? PreviewInk.RatingGreen : s.Band == SlopeBands.Band.Blue ? PreviewInk.RatingBlue : PreviewInk.RatingBlack;
        }

        // Where a run's name may sit (NT7): the middle of its line first, then further out either way; each slanted along
        // the line 20 m either side (less on a short run).
        static readonly float[] LabelAlong = { 0.5f, 0.32f, 0.68f, 0.18f, 0.82f };

        public MapLabelSpot[] LabelSpots(Vector2[] line)
        {
            if (line.Length < 2)
            {
                float y = _ground.HeightAt(line[0].x, line[0].y);
                return new[] { MapLabelSpot.Upright(new Vector3(line[0].x, float.IsNaN(y) ? 0 : y, line[0].y)) };
            }
            var run = new float[line.Length];
            for (int k = 1; k < line.Length; k++) run[k] = run[k - 1] + Vector2.Distance(line[k - 1], line[k]);
            float total = run[line.Length - 1], side = Mathf.Min(20, total / 4);
            var spots = new MapLabelSpot[LabelAlong.Length];
            for (int k = 0; k < spots.Length; k++)
            {
                float at = total * LabelAlong[k];
                spots[k] = new MapLabelSpot(OnLine(line, run, at), OnLine(line, run, at - side), OnLine(line, run, at + side));
            }
            return spots;
        }

        // The point <paramref name="at"/> metres along the line, on the ground.
        Vector3 OnLine(Vector2[] line, float[] run, float at)
        {
            at = Mathf.Clamp(at, 0, run[run.Length - 1]);
            int k = 1;
            while (k < line.Length - 1 && run[k] < at) k++;
            float span = run[k] - run[k - 1];
            var p = span > 0 ? Vector2.Lerp(line[k - 1], line[k], (at - run[k - 1]) / span) : line[k];
            float y = _ground.HeightAt(p.x, p.y);
            return new Vector3(p.x, float.IsNaN(y) ? 0 : y, p.y);
        }

        public static string Symbol(SlopeBands.Band band) =>
            band == SlopeBands.Band.Green ? "●" : band == SlopeBands.Band.Blue ? "■" : band == SlopeBands.Band.Black ? "◆" : "◆◆";

        static Shape Build(Trail t, EditedHeights heights, LocalFrame frame, DesignEarthwork earthwork)
        {
            var s = new Shape { Trail = t };
            var head = frame.ToAlbers(t.Head.Value.XMetres, t.Head.Value.YMetres);
            var tail = frame.ToAlbers(t.Tail.Value.XMetres, t.Tail.Value.YMetres);
            // The earthwork's own footprint and centreline for this very trail when it has them (task 08).
            TrailCentreline.Result c;
            // (a split part's line is its pair's, cut (NT2): it works out its own, as the network does).
            var b = earthwork?.DetailOf(t.Id.Value) as TrailBuild;
            var own = b?.OwnLineFor(t);
            if (own != null)
            {
                s.Footprint = b.Footprint;
                c = own;
            }
            else
            {
                s.Footprint = b != null && b.IsFor(t) ? b.Footprint : TrailFootprint.Build(t.Shape, heights.Lattice, frame.Origin.X, frame.Origin.Y);
                c = TrailCentreline.Find(s.Footprint, head.X, head.Y, tail.X, tail.Y);
            }
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

        static bool SameTrail(Trail a, Trail b) =>
            ReferenceEquals(a, b) || a.Id == b.Id && a.Stage == b.Stage && a.Head == b.Head && a.Tail == b.Tail && a.Settings.Equals(b.Settings) && a.Shape.Equals(b.Shape);

        // Whether ground moved on any tile a shape's painted cells touch (worked out once per shape).
        static bool Meets(Shape s, HashSet<EditTileKey> tiles)
        {
            var fp = s.Footprint;
            if (fp == null || tiles.Count == 0) return false;
            if (s.FootprintTiles == null)
            {
                s.FootprintTiles = new HashSet<EditTileKey>();
                for (int r = fp.Row0; r < fp.Row0 + fp.Height; r++)
                    for (int c = fp.Column0; c < fp.Column0 + fp.Width; c++)
                        if (fp.Contains(c, r))
                        {
                            s.FootprintTiles.Add(EditTileKey.Of(c, r));
                            s.FootprintTiles.Add(EditTileKey.Of(c + 1, r + 1));
                        }
            }
            foreach (var k in tiles) if (s.FootprintTiles.Contains(k)) return true;
            return false;
        }

        static void Rate(Shape s, EditedHeights heights, DesignEarthwork earthwork)
        {
            var build = earthwork?.DetailOf(s.Trail.Id.Value) as TrailBuild;
            if (build != null && !build.IsFor(s.Trail)) build = null;   // a build of another version of the trail never rates it
            if (s.Rated && ReferenceEquals(s.RatedBuild, build)) return;
            s.Rated = true;
            s.RatedBuild = build;
            if (build?.Grade != null && s.Trail.Stage == WorkStage.Built)
            {
                var r = TrailRating.Rate(heights, build.Footprint, build.Grade);
                s.Rating = r.ValuePercent;
                s.Mean = r.MeanPercent;
                s.Steepest = r.MaxPercent;
                s.LengthMetres = build.Grade.LengthMetres;
                s.DropMetres = r.DropMetres;
            }
            else s.Rating = TrailRating.Estimate(heights, s.Footprint);   // again whenever the ground under it moves (task P3-11)
            s.Band = double.IsNaN(s.Rating) ? SlopeBands.Band.Green : SlopeBands.Of(s.Rating);
        }

        public void Dispose() => _lines.Dispose();
    }
}
