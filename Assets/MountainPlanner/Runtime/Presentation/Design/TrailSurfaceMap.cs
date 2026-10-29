using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using Unity.Collections;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// The look of built trails (task 07, decision SK3): a 1 m world-space map the terrain shader reads, over the
    /// built trails' bounds. Per lattice point:
    /// R the footprint (1 inside, fading over the last metre);
    /// G the height change, edited minus natural, in metres (cut negative, fill positive);
    /// B 1 on a graded trail, 0 on a natural one (the terrain shader smooths a graded surface's shading; the offset
    /// across the trail, which corduroy and groomer passes run along, is the full-precision _TrailOffset map);
    /// A the edited ground's slope across the trail (0 on a level bench, about 1 on a 1:1 face).
    /// It is display data derived from the design and the edited heights, never stored and never read by a rule.
    /// Each trail's painted footprint and automatic centreline come from task 08's TrailFootprint and TrailCentreline;
    /// the numbers are <see cref="TrailSurface"/>'s, which works out again only what a change can reach (task P3-11),
    /// and only those blocks are packed into the textures.
    /// </summary>
    public sealed class TrailSurfaceMap : IDisposable
    {
        public const int MaxTexels = TrailSurface.MaxPoints;

        static readonly int MapId = Shader.PropertyToID("_TrailMap"), RectId = Shader.PropertyToID("_TrailRect"),
                            ParamsId = Shader.PropertyToID("_TrailParams"), OffsetId = Shader.PropertyToID("_TrailOffset");

        Texture2D _map;
        readonly Dictionary<TrailId, (Trail trail, TrailFootprint fp, double[] line)> _shapes = new Dictionary<TrailId, (Trail, TrailFootprint, double[])>();
        readonly TrailSurface _surface = new TrailSurface();
        readonly List<TrailSurface.Shape> _drawn = new List<TrailSurface.Shape>();
        EditLattice _lattice;
        LocalFrame _frame;
        /// <summary>The offset across the trail at full float precision (finding #11: 1 in corduroy needs it).</summary>
        Texture2D _offset;

        public TrailSurfaceMap() => Shader.SetGlobalVector(ParamsId, Vector4.zero);

        /// <summary>The last rebuild's stages, ms (the [Built] log, task P3-11).</summary>
        public string LastSplit { get; private set; } = "";

        /// <summary>The CPU fields (tests).</summary>
        public TrailSurface Surface => _surface;

        /// <summary>Whether a local point (metres east and north of the frame's origin) is under the map.</summary>
        public bool Covers(float x, float z)
        {
            if (_surface.W == 0) return false;
            var p = _frame.ToAlbers(x, z);
            return _surface.Covers(_lattice, p.X, p.Y);
        }

        /// <summary>The trails the map shows: built, with a stroke. Returns how many it drew.</summary>
        public int Rebuild(DesignDocument doc, EditedHeights edited, LocalFrame frame, Func<Trail, TrailBuild> built = null)
        {
            Prepare(doc, edited, frame, built);
            return Upload();
        }

        bool _show;
        double _shapesMs, _fieldsMs;

        /// <summary>
        /// Works out the numbers for <paramref name="doc"/> over <paramref name="edited"/> (task P3-11: on any thread, while
        /// the ground lands; nothing here touches Unity). <see cref="Upload"/> then packs them on the main thread.
        /// </summary>
        public void Prepare(DesignDocument doc, EditedHeights edited, LocalFrame frame, Func<Trail, TrailBuild> built = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _lattice = edited.Lattice;
            _frame = frame;
            _drawn.Clear();
            var seen = new HashSet<TrailId>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || !t.HasEnds) continue;
                seen.Add(t.Id);
                // Task 08: each trail's painted footprint and automatic centreline (TR-02, TR-03), kept while the trail is the same.
                if (!_shapes.TryGetValue(t.Id, out var shape) || !Same(shape.trail, t))
                {
                    // The earthwork's own footprint and centreline for this trail (task 08): no second computation. A split
                    // part's line is its pair's, cut (NT2), so it works out its own, as the network does.
                    var b = built?.Invoke(t);
                    var own = b?.OwnLineFor(t);
                    if (own != null && own.Found) shape = (t, b.Footprint, own.Points);
                    else
                    {
                        var fp = b != null && b.IsFor(t) ? b.Footprint : TrailFootprint.Build(t.Shape, edited.Lattice, frame.Origin.X, frame.Origin.Y);
                        var head = frame.ToAlbers(t.Head.Value.XMetres, t.Head.Value.YMetres);
                        var tail = frame.ToAlbers(t.Tail.Value.XMetres, t.Tail.Value.YMetres);
                        var c = TrailCentreline.Find(fp, head.X, head.Y, tail.X, tail.Y);
                        shape = (t, fp, c.Found ? c.Points : null);
                    }
                    _shapes[t.Id] = shape;
                }
                if (shape.line == null) continue;
                _drawn.Add(new TrailSurface.Shape(t, shape.line, shape.fp, t.Settings.Grading == TrailGrading.Graded));
            }
            var stale = new List<TrailId>();
            foreach (var id in _shapes.Keys) if (!seen.Contains(id)) stale.Add(id);
            foreach (var id in stale) _shapes.Remove(id);
            _shapesMs = watch.Elapsed.TotalMilliseconds;
            _show = _surface.Update(_drawn, edited);
            _fieldsMs = watch.Elapsed.TotalMilliseconds - _shapesMs;
        }

        /// <summary>Packs what <see cref="Prepare"/> worked out into the textures (main thread). Returns how many trails it drew.</summary>
        public int Upload()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double shapesMs = _shapesMs, fieldsMs = _fieldsMs;
            var frame = _frame;
            if (!_show)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                LastSplit = $"shapes {shapesMs:F1}, nothing to show";
                return 0;
            }
            int w = _surface.W, h = _surface.H;
            bool fresh = _map == null || _map.width != w || _map.height != h;
            if (fresh)
            {
                Release();
                _map = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true)
                {
                    name = "Trail surface", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                _offset = new Texture2D(w, h, TextureFormat.RFloat, false, true)
                {
                    name = "Trail offset", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                };
            }
            // Texture rows run north from the south edge; lattice rows run south. Only the blocks worked out again are
            // packed (a new texture, all of it); one Apply each uploads the CPU copy as it then stands.
            NativeArray<ushort> pixels = _map.GetPixelData<ushort>(0);
            NativeArray<float> offsets = _offset.GetPixelData<float>(0);
            var foot = _surface.Foot;
            var change = _surface.Change;
            var graded = _surface.Graded;
            var across = _surface.Across;
            var offset = _surface.Offset;
            void Pack(int i0, int j0, int i1, int j1)
            {
                for (int j = j0; j <= j1; j++)
                {
                    int row = h - 1 - j;
                    for (int i = i0; i <= i1; i++)
                    {
                        int at = j * w + i, p = (row * w + i) * 4;
                        pixels[p] = Mathf.FloatToHalf(foot[at]);
                        pixels[p + 1] = Mathf.FloatToHalf(change[at]);
                        pixels[p + 2] = Mathf.FloatToHalf(graded[at]);
                        pixels[p + 3] = Mathf.FloatToHalf(across[at]);
                        offsets[row * w + i] = offset[at];
                    }
                }
            }
            if (fresh) Pack(0, 0, w - 1, h - 1);
            else foreach (var b in _surface.Redrawn) Pack(b.I0, b.J0, b.I1, b.J1);
            double packMs = watch.Elapsed.TotalMilliseconds;
            if (fresh || _surface.Redrawn.Count > 0)
            {
                _map.Apply(false);
                _offset.Apply(false);
            }
            Shader.SetGlobalTexture(OffsetId, _offset);
            var lattice = _lattice;
            var (west, north) = frame.ToLocal(new AlbersPoint(lattice.X(_surface.C0), lattice.Y(_surface.R0)));
            double south = north - (h - 1);
            // Texel centres sit on lattice points: uv = (p − corner + 0.5) / size.
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalVector(RectId, new Vector4((float)(west - 0.5), (float)(south - 0.5), 1f / w, 1f / h));
            Shader.SetGlobalVector(ParamsId, new Vector4(1, 0, 0, 0));
            LastSplit = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "shapes {0:F1}, fields {1:F1} over {2:N0} points in {3} blocks{4}, pack {5:F1}, upload {6:F1}",
                shapesMs, fieldsMs, _surface.PointsRedrawn, _surface.Redrawn.Count, _surface.WindowChanged ? " (window moved)" : "",
                packMs, watch.Elapsed.TotalMilliseconds - packMs);
            return _drawn.Count;
        }

        // The same trail by value: the history applies a command again, so an edited trail is never the object graded.
        static bool Same(Trail a, Trail b) =>
            ReferenceEquals(a, b) || a.Id == b.Id && a.Head == b.Head && a.Tail == b.Tail && a.Settings.Equals(b.Settings) && a.Shape.Equals(b.Shape);

        void Release()
        {
            foreach (var t in new[] { _map, _offset })
            {
                if (t == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(t);
                else UnityEngine.Object.DestroyImmediate(t);
            }
            _map = null;
            _offset = null;
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Release();
        }
    }
}
