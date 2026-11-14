using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Water
{
    /// <summary>What a water body is (LK-01 v2): the one streams body, a lake from the map, or a pond the player built (4.14).</summary>
    public enum WaterBodyKind : byte
    {
        Streams = 0,
        Lake = 1,
        Pond = 2,
    }

    /// <summary>The <c>Pacing</c> numbers the body map reads (SI-01), so Domain needs no reference to Simulation.</summary>
    public readonly struct WaterBodyTuning
    {
        /// <summary>A cell is water at this share or more (0–255 of the cell).</summary>
        public readonly int ShareMin;
        /// <summary>A lake's least area (m²): LK-01's 1 acre.</summary>
        public readonly int MinAreaSquareMetres;
        /// <summary>A core whose area ÷ (widest inscribed width)² is above this (×10) is a river, not a lake.</summary>
        public readonly int RiverElongationTenths;
        /// <summary>How far (cells) a lake's core grows back through its water.</summary>
        public readonly int GrowCells;
        /// <summary>At most this many lakes (the smallest beyond it join the streams body), and this many ponds after them.</summary>
        public readonly int MaxLakes, MaxPonds;
        /// <summary>LK-02's clamp and LK-07's streams depth (0.1 ft).</summary>
        public readonly int DepthMinTenthsFt, DepthMaxTenthsFt, StreamsDepthTenthsFt;
        /// <summary>The SI-06 tag a lake's id is keyed with (the simulation's register).</summary>
        public readonly ulong Tag;

        public WaterBodyTuning(int shareMin, int minAreaSquareMetres, int riverElongationTenths, int growCells, int maxLakes, int maxPonds,
                               int depthMinTenthsFt, int depthMaxTenthsFt, int streamsDepthTenthsFt, ulong tag)
        {
            if (shareMin < 1 || shareMin > 255) throw new ArgumentOutOfRangeException(nameof(shareMin));
            if (maxLakes < 0 || maxPonds < 0 || 1 + maxLakes + maxPonds > 255) throw new ArgumentOutOfRangeException(nameof(maxLakes), "The mask holds 255 bodies.");
            ShareMin = shareMin; MinAreaSquareMetres = Math.Max(1, minAreaSquareMetres); RiverElongationTenths = Math.Max(1, riverElongationTenths);
            GrowCells = Math.Max(1, growCells); MaxLakes = maxLakes; MaxPonds = maxPonds;
            DepthMinTenthsFt = Math.Max(1, depthMinTenthsFt); DepthMaxTenthsFt = Math.Max(DepthMinTenthsFt, depthMaxTenthsFt);
            StreamsDepthTenthsFt = Math.Max(1, streamsDepthTenthsFt); Tag = tag;
        }
    }

    /// <summary>One water body's fixed facts (LK-01, LK-02). Integers only.</summary>
    public readonly struct WaterBody
    {
        public readonly WaterBodyKind Kind;
        /// <summary>The keyed id (SI-06): from the centroid's position for a lake, the design's id for a pond, 0 for the streams.</summary>
        public readonly ulong Id;
        /// <summary>Its 8 m cells, and the one cell whose air it reads (the body's cell nearest its centroid).</summary>
        public readonly int Cells, AirCell;
        /// <summary>Its centroid in the design's frame (mm from the site's centre).</summary>
        public readonly int CentroidXMm, CentroidYMm;
        /// <summary>The water's surface (ft): the median of its cells' heights.</summary>
        public readonly int SurfaceFeet;
        /// <summary>The equivalent radius √(area ÷ π) (mm) and LK-02's mean depth (0.1 ft).</summary>
        public readonly int RadiusMm, DepthTenthsFt;
        /// <summary>The ring (or missing heights) cuts it: its area and depth are low estimates.</summary>
        public readonly bool Clipped;

        public WaterBody(WaterBodyKind kind, ulong id, int cells, int airCell, int centroidXMm, int centroidYMm, int surfaceFeet, int radiusMm, int depthTenthsFt, bool clipped)
        {
            Kind = kind; Id = id; Cells = cells; AirCell = airCell; CentroidXMm = centroidXMm; CentroidYMm = centroidYMm;
            SurfaceFeet = surfaceFeet; RadiusMm = radiusMm; DepthTenthsFt = depthTenthsFt; Clipped = clipped;
        }

        public long AreaSquareMetres => (long)Cells * WaterBodyTable.CellMetres * WaterBodyTable.CellMetres;

        public override string ToString() => $"{Kind} {Id:X16}: {AreaSquareMetres:N0} m², {DepthTenthsFt / 10.0:0.#} ft deep{(Clipped ? " (clipped)" : "")}";
    }

    /// <summary>
    /// LK-01 v2's water-body map (task 4.09): every lake and the streams body, on the 8 m surface grid over the ring, built
    /// once when an area opens from the ground cover's water share and the downloaded heights. Immutable once built (SI-21):
    /// the worker gets it by reference, and a pond (4.14) comes as a new table from <see cref="WithPond"/>.
    /// - Body 0 is the streams body (every stream, river and water too small to be a lake); lakes follow in id order.
    /// - <see cref="Mask"/> holds each cell's body index + 1, or 0 for land, so the renderer can read it as a byte texture.
    /// The ingest burns lakes and streams into one water band, so the map tells them apart by shape: eroding the water by one
    /// cell removes streams (2.5–14 m wide), a long thin core is a river, and each lake core grows back through its water.
    /// </summary>
    public sealed class WaterBodyTable
    {
        public const int CellMetres = Snow.SurfaceGrid.CellMetres;
        /// <summary>The streams body's index.</summary>
        public const int Streams = 0;

        public readonly int Width, Depth;
        /// <summary>The south-west corner of cell (0, 0) in the design's frame, mm from the site's centre.</summary>
        public readonly int OriginXMm, OriginYMm;
        /// <summary>Per cell (west to east, then south to north): body index + 1, 0 for land.</summary>
        public readonly byte[] Mask;
        readonly WaterBody[] _bodies;
        /// <summary>How many of the bodies are ponds, and how many more the table may take.</summary>
        public readonly int Ponds, MaxPonds;
        /// <summary>A content hash over every field.</summary>
        public readonly ulong Hash;

        public int Count => _bodies.Length;
        public WaterBody this[int body] => _bodies[body];
        public int Cells => Width * Depth;

        /// <summary>The body index at a cell, or −1 for land or outside the grid.</summary>
        public int BodyAt(int cell) => cell < 0 || cell >= Mask.Length ? -1 : Mask[cell] - 1;

        /// <summary>The body index holding an id, or −1.</summary>
        public int IndexOf(ulong id)
        {
            for (int b = 0; b < _bodies.Length; b++)
                if (_bodies[b].Id == id) return b;
            return -1;
        }

        WaterBodyTable(int width, int depth, int originX, int originY, byte[] mask, WaterBody[] bodies, int ponds, int maxPonds)
        {
            Width = width; Depth = depth; OriginXMm = originX; OriginYMm = originY; Mask = mask; _bodies = bodies; Ponds = ponds; MaxPonds = maxPonds;
            ulong h = 14695981039346656037UL;
            void Mix(long v) => h = (h ^ (ulong)v) * 1099511628211UL;
            Mix(width); Mix(depth); Mix(originX); Mix(originY); Mix(bodies.Length); Mix(ponds); Mix(maxPonds);
            foreach (byte v in mask) Mix(v);
            foreach (var b in bodies)
            {
                Mix((long)b.Kind); Mix((long)b.Id); Mix(b.Cells); Mix(b.AirCell); Mix(b.CentroidXMm); Mix(b.CentroidYMm);
                Mix(b.SurfaceFeet); Mix(b.RadiusMm); Mix(b.DepthTenthsFt); Mix(b.Clipped ? 1 : 0);
            }
            Hash = h;
        }

        /// <summary>A table with only the streams body and no water: a map without water, or a test's start.</summary>
        public static WaterBodyTable Empty(int width, int depth, int originXMm, int originYMm, int streamsDepthTenthsFt, int maxPonds = 0) =>
            new WaterBodyTable(width, depth, originXMm, originYMm, new byte[width * depth],
                new[] { new WaterBody(WaterBodyKind.Streams, 0, 0, -1, 0, 0, 0, 0, streamsDepthTenthsFt, false) }, 0, maxPonds);

        /// <summary>
        /// A table from its parts (fixtures, and a table a later format may store): body 0 must be the streams body, and the
        /// mask may name only the bodies given.
        /// </summary>
        public static WaterBodyTable Of(int width, int depth, int originXMm, int originYMm, byte[] mask, WaterBody[] bodies, int maxPonds = 0)
        {
            if (width <= 0 || depth <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (mask == null || mask.Length != width * depth) throw new ArgumentException("One mask byte per cell.", nameof(mask));
            if (bodies == null || bodies.Length < 1 || bodies.Length > 255 || bodies[0].Kind != WaterBodyKind.Streams)
                throw new ArgumentException("Body 0 is the streams body; at most 255 bodies.", nameof(bodies));
            int ponds = 0;
            for (int b = 1; b < bodies.Length; b++)
            {
                if (bodies[b].Kind == WaterBodyKind.Streams) throw new ArgumentException("Only body 0 is the streams body.", nameof(bodies));
                if (bodies[b].Kind == WaterBodyKind.Pond) ponds++;
            }
            foreach (byte m in mask)
                if (m > bodies.Length) throw new ArgumentException("The mask names a body the table doesn't have.", nameof(mask));
            return new WaterBodyTable(width, depth, originXMm, originYMm, (byte[])mask.Clone(), (WaterBody[])bodies.Clone(), ponds, Math.Max(maxPonds, ponds));
        }

        /// <summary>
        /// Builds the map. <paramref name="share"/> is each cell's water share (0–255), <paramref name="feet"/> its height
        /// (whole ft, <see cref="int.MinValue"/> where there's none).
        /// </summary>
        public static WaterBodyTable Build(int width, int depth, byte[] share, int[] feet, int originXMm, int originYMm, WaterBodyTuning t)
        {
            if (width <= 0 || depth <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            int n = width * depth;
            if (share == null || share.Length != n) throw new ArgumentException("One share per cell.", nameof(share));
            if (feet == null || feet.Length != n) throw new ArgumentException("One height per cell.", nameof(feet));

            var water = new bool[n];
            for (int k = 0; k < n; k++) water[k] = share[k] >= t.ShareMin;

            // The chessboard distance (cells) from each water cell to the nearest land, the grid's edge counting as water so a
            // lake the ring cuts keeps its core. Two passes; 0 on land.
            var dist = new int[n];
            const int Far = int.MaxValue / 4;
            for (int k = 0; k < n; k++) dist[k] = water[k] ? Far : 0;
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    int k = j * width + i;
                    if (dist[k] == 0) continue;
                    int d = dist[k];
                    if (i > 0) d = Math.Min(d, dist[k - 1] + 1);
                    if (j > 0)
                    {
                        d = Math.Min(d, dist[k - width] + 1);
                        if (i > 0) d = Math.Min(d, dist[k - width - 1] + 1);
                        if (i < width - 1) d = Math.Min(d, dist[k - width + 1] + 1);
                    }
                    dist[k] = d;
                }
            for (int j = depth - 1; j >= 0; j--)
                for (int i = width - 1; i >= 0; i--)
                {
                    int k = j * width + i;
                    if (dist[k] == 0) continue;
                    int d = dist[k];
                    if (i < width - 1) d = Math.Min(d, dist[k + 1] + 1);
                    if (j < depth - 1)
                    {
                        d = Math.Min(d, dist[k + width] + 1);
                        if (i < width - 1) d = Math.Min(d, dist[k + width + 1] + 1);
                        if (i > 0) d = Math.Min(d, dist[k + width - 1] + 1);
                    }
                    dist[k] = d;
                }
            // A cell with no land touching it (8-connected) survives the erosion. Water that is all water to the grid's edge
            // keeps Far: treat it as a wide body.

            // The cores: 4-connected components of the eroded water, in scan order.
            var label = new int[n];   // core number + 1 during labelling; 0 = none
            var queue = new int[n];
            var coreCells = new List<int>();
            var coreMaxDist = new List<int>();
            for (int k0 = 0; k0 < n; k0++)
            {
                if (dist[k0] < 2 || label[k0] != 0) continue;
                int id = coreCells.Count + 1, head = 0, tail = 0, count = 0, maxD = 0;
                label[k0] = id; queue[tail++] = k0;
                while (head < tail)
                {
                    int k = queue[head++];
                    count++;
                    if (dist[k] > maxD) maxD = dist[k];
                    int i = k % width, j = k / width;
                    if (i > 0) Visit(k - 1);
                    if (i < width - 1) Visit(k + 1);
                    if (j > 0) Visit(k - width);
                    if (j < depth - 1) Visit(k + width);
                }
                coreCells.Add(count);
                coreMaxDist.Add(maxD);

                void Visit(int m)
                {
                    if (dist[m] >= 2 && label[m] == 0) { label[m] = id; queue[tail++] = m; }
                }
            }

            // The shape test: a long thin core is a river. Its widest inscribed width (cells) is 2 × the deepest distance − 1.
            int cores = coreCells.Count;
            var lakeCore = new bool[cores];
            for (int c = 0; c < cores; c++)
            {
                long w = Math.Min(2L * coreMaxDist[c] - 1, 2L * Math.Max(width, depth));
                lakeCore[c] = (long)coreCells[c] * 10 <= (long)t.RiverElongationTenths * w * w;
            }

            // Each lake core grows back through its water, layer by layer; a cell two cores reach in the same layer goes to the lower.
            var grown = new int[n];   // core number + 1, or 0
            int frontier = 0;
            for (int k = 0; k < n; k++)
                if (label[k] != 0 && lakeCore[label[k] - 1]) { grown[k] = label[k]; queue[frontier++] = k; }
            var next = new List<int>();
            for (int layer = 0; layer < t.GrowCells && frontier > 0; layer++)
            {
                next.Clear();
                for (int f = 0; f < frontier; f++)
                {
                    int k = queue[f], i = k % width, j = k / width, g = grown[k];
                    if (i > 0) Reach(k - 1, g);
                    if (i < width - 1) Reach(k + 1, g);
                    if (j > 0) Reach(k - width, g);
                    if (j < depth - 1) Reach(k + width, g);
                }
                // Cells reached this layer carry a negative mark until the layer ends, so a layer never feeds itself.
                frontier = 0;
                foreach (int k in next) { grown[k] = -grown[k]; queue[frontier++] = k; }

                void Reach(int m, int g)
                {
                    if (!water[m]) return;
                    if (grown[m] == 0) { grown[m] = -g; next.Add(m); }
                    else if (grown[m] < 0 && -grown[m] > g) grown[m] = -g;
                }
            }

            // Grown bodies: area, sums, edges.
            var area = new int[cores];
            var sumI = new long[cores];
            var sumJ = new long[cores];
            var clipped = new bool[cores];
            for (int k = 0; k < n; k++)
            {
                int g = grown[k];
                if (g <= 0) continue;
                int c = g - 1, i = k % width, j = k / width;
                area[c]++; sumI[c] += i; sumJ[c] += j;
                if (i == 0 || j == 0 || i == width - 1 || j == depth - 1 || feet[k] == int.MinValue) clipped[c] = true;
            }
            int minCells = (t.MinAreaSquareMetres + CellMetres * CellMetres - 1) / (CellMetres * CellMetres);
            var lakes = new List<int>();
            for (int c = 0; c < cores; c++)
                if (lakeCore[c] && area[c] >= minCells) lakes.Add(c);
            // Keep the largest (ties to the lower core), then order by id.
            lakes.Sort((a, b) => area[a] != area[b] ? area[b].CompareTo(area[a]) : a.CompareTo(b));
            if (lakes.Count > t.MaxLakes) lakes.RemoveRange(t.MaxLakes, lakes.Count - t.MaxLakes);

            var bodies = new List<(WaterBody Body, int Core)>();
            foreach (int c in lakes)
            {
                int ci = (int)((2 * sumI[c] + area[c]) / (2L * area[c])), cj = (int)((2 * sumJ[c] + area[c]) / (2L * area[c]));
                int cx = originXMm + ci * CellMetres * 1000 + CellMetres * 500, cy = originYMm + cj * CellMetres * 1000 + CellMetres * 500;
                ulong id = Design.DesignKey.Hash(0, t.Tag, (ulong)(long)FloorDiv(cx, CellMetres * 1000), (ulong)(long)FloorDiv(cy, CellMetres * 1000));
                bodies.Add((new WaterBody(WaterBodyKind.Lake, id, area[c], -1, cx, cy, 0, 0, 0, clipped[c]), c));
            }
            bodies.Sort((a, b) => a.Body.Id.CompareTo(b.Body.Id));

            // The mask: lakes by index, every other water cell the streams body.
            var coreToBody = new int[cores];
            for (int c = 0; c < cores; c++) coreToBody[c] = -1;
            for (int b = 0; b < bodies.Count; b++) coreToBody[bodies[b].Core] = b + 1;
            var mask = new byte[n];
            int streamsCells = 0;
            for (int k = 0; k < n; k++)
            {
                if (!water[k]) continue;
                int g = grown[k], b = g > 0 ? coreToBody[g - 1] : -1;
                if (b > 0) mask[k] = (byte)(b + 1);
                else { mask[k] = 1; streamsCells++; }
            }

            // Each body's air cell, surface, radius and LK-02 depth.
            var all = new WaterBody[bodies.Count + 1];
            var heights = new List<int>[all.Length];
            var slopes = new List<int>[all.Length];
            for (int b = 0; b < all.Length; b++) { heights[b] = new List<int>(); slopes[b] = new List<int>(); }
            for (int k = 0; k < n; k++)
                if (mask[k] != 0 && feet[k] != int.MinValue) heights[mask[k] - 1].Add(feet[k]);
            var surface = new int[all.Length];
            for (int b = 0; b < all.Length; b++) surface[b] = Median(heights[b]);
            // Shoreline slope (‰): from each shore cell (a lake cell beside land), the steepest of the 8 directions where the next
            // two cells are land, rise over run from the waterline (the shore cell's outer edge) to the second land cell's
            // centre, 1.5 cells; a round shore's radial slope is a diagonal as often as not.
            const int RunFeet2 = 3 * CellMetres * 1000 * 10 / 2 / 3048;      // 1.5 cells in ft (39)
            const int RunFeet2Diagonal = 3 * CellMetres * 14142 / 2 / 3048;  // 1.5 diagonal cells in ft (55)
            int[] di = { 1, -1, 0, 0, 1, 1, -1, -1 }, dj = { 0, 0, 1, -1, 1, -1, 1, -1 };
            for (int k = 0; k < n; k++)
            {
                int m = mask[k];
                if (m <= 1) continue;   // lakes only: the streams body has a fixed depth
                int i = k % width, j = k / width, steepest = -1;
                for (int d = 0; d < 8; d++)
                {
                    int i1 = i + di[d], j1 = j + dj[d], i2 = i + 2 * di[d], j2 = j + 2 * dj[d];
                    if (i2 < 0 || j2 < 0 || i2 >= width || j2 >= depth) continue;
                    int k1 = j1 * width + i1, k2 = j2 * width + i2;
                    if (mask[k1] != 0 || mask[k2] != 0 || feet[k2] == int.MinValue) continue;
                    int slope = Math.Max(0, (feet[k2] - surface[m - 1]) * 1000 / (d < 4 ? RunFeet2 : RunFeet2Diagonal));
                    if (slope > steepest) steepest = slope;
                }
                if (steepest >= 0) slopes[m - 1].Add(steepest);
            }
            all[Streams] = new WaterBody(WaterBodyKind.Streams, 0, streamsCells, NearestCellAtHeight(mask, feet, 1, surface[0]), 0, 0, surface[0], 0, t.StreamsDepthTenthsFt, false);
            // Each lake's cell nearest its centroid, in one pass (ties to the lower cell).
            var airCell = new int[all.Length];
            var airDist = new long[all.Length];
            for (int b = 0; b < all.Length; b++) { airCell[b] = -1; airDist[b] = long.MaxValue; }
            for (int k = 0; k < n; k++)
            {
                int m = mask[k];
                if (m <= 1) continue;
                var w = bodies[m - 2].Body;
                long dx = originXMm + (k % width) * (long)CellMetres * 1000 + CellMetres * 500 - w.CentroidXMm;
                long dy = originYMm + (k / width) * (long)CellMetres * 1000 + CellMetres * 500 - w.CentroidYMm;
                long d = dx * dx + dy * dy;
                if (d < airDist[m - 1]) { airDist[m - 1] = d; airCell[m - 1] = k; }
            }
            for (int b = 0; b < bodies.Count; b++)
            {
                var w = bodies[b].Body;
                int index = b + 1;
                long areaMm2 = w.AreaSquareMetres * 1_000_000L;
                int radius = (int)ISqrt(areaMm2 * 1_000_000L / 3_141_593L);
                long radiusTenthsFt = (long)radius * 10 / 305;   // mm → 0.1 ft (304.8 mm)
                int slope = Median(slopes[index]);
                long depthTenths = (long)slope * radiusTenthsFt / 3000;
                long max = Math.Min(t.DepthMaxTenthsFt, Math.Max(t.DepthMinTenthsFt, radiusTenthsFt / 3));
                depthTenths = Math.Max(t.DepthMinTenthsFt, Math.Min(max, depthTenths));
                int air = airCell[index];
                all[index] = new WaterBody(WaterBodyKind.Lake, w.Id, w.Cells, air, w.CentroidXMm, w.CentroidYMm, surface[index], radius, (int)depthTenths, w.Clipped);
            }
            return new WaterBodyTable(width, depth, originXMm, originYMm, mask, all, 0, t.MaxPonds);
        }

        /// <summary>
        /// A new table with a pond (4.14) added after the other bodies: its id (the design's), its cells, depth (0.1 ft) and
        /// surface (ft). The pond's cells leave whatever body held them.
        /// </summary>
        public WaterBodyTable WithPond(ulong id, int[] cells, int depthTenthsFt, int surfaceFeet)
        {
            if (Ponds >= MaxPonds) throw new InvalidOperationException("The table has no room for another pond.");
            if (cells == null || cells.Length == 0) throw new ArgumentException("A pond has cells.", nameof(cells));
            if (IndexOf(id) >= 0) throw new ArgumentException("That id is taken.", nameof(id));
            var mask = (byte[])Mask.Clone();
            var counts = new int[_bodies.Length];
            int index = _bodies.Length;
            long si = 0, sj = 0;
            foreach (int k in cells)
            {
                if (k < 0 || k >= mask.Length) throw new ArgumentOutOfRangeException(nameof(cells));
                if (mask[k] != 0) counts[mask[k] - 1]++;
                mask[k] = (byte)(index + 1);
                si += k % Width; sj += k / Width;
            }
            var all = new WaterBody[_bodies.Length + 1];
            for (int b = 0; b < _bodies.Length; b++)
            {
                var o = _bodies[b];
                all[b] = counts[b] == 0 ? o : new WaterBody(o.Kind, o.Id, o.Cells - counts[b], o.AirCell >= 0 && mask[o.AirCell] == b + 1 ? o.AirCell : -1,
                                                            o.CentroidXMm, o.CentroidYMm, o.SurfaceFeet, o.RadiusMm, o.DepthTenthsFt, o.Clipped);
            }
            int ci = (int)(si / cells.Length), cj = (int)(sj / cells.Length);
            int cx = OriginXMm + ci * CellMetres * 1000 + CellMetres * 500, cy = OriginYMm + cj * CellMetres * 1000 + CellMetres * 500;
            long areaMm2 = (long)cells.Length * CellMetres * CellMetres * 1_000_000L;
            all[index] = new WaterBody(WaterBodyKind.Pond, id, cells.Length, NearestCell(mask, index + 1, Width, cx, cy, OriginXMm, OriginYMm), cx, cy, surfaceFeet,
                                       (int)ISqrt(areaMm2 * 1_000_000L / 3_141_593L), depthTenthsFt, false);
            return new WaterBodyTable(Width, Depth, OriginXMm, OriginYMm, mask, all, Ponds + 1, MaxPonds);
        }

        static int Median(List<int> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>The body's cell nearest a point (ties to the lower cell), or −1 if it has none.</summary>
        static int NearestCell(byte[] mask, int value, int width, int xMm, int yMm, int originX, int originY)
        {
            long best = long.MaxValue;
            int at = -1;
            for (int k = 0; k < mask.Length; k++)
            {
                if (mask[k] != value) continue;
                long dx = originX + (k % width) * (long)CellMetres * 1000 + CellMetres * 500 - xMm, dy = originY + (k / width) * (long)CellMetres * 1000 + CellMetres * 500 - yMm;
                long d = dx * dx + dy * dy;
                if (d < best) { best = d; at = k; }
            }
            return at;
        }

        /// <summary>The body's first cell (scan order) whose height is closest to <paramref name="feet"/>, or −1.</summary>
        static int NearestCellAtHeight(byte[] mask, int[] heights, int value, int feet)
        {
            int best = int.MaxValue, at = -1;
            for (int k = 0; k < mask.Length; k++)
            {
                if (mask[k] != value || heights[k] == int.MinValue) continue;
                int d = Math.Abs(heights[k] - feet);
                if (d < best) { best = d; at = k; }
            }
            return at;
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>The integer square root (floor).</summary>
        public static long ISqrt(long v)
        {
            if (v <= 0) return 0;
            long r = (long)Math.Sqrt(v);
            while (r * r > v) r--;
            while ((r + 1) * (r + 1) <= v) r++;
            return r;
        }
    }
}
