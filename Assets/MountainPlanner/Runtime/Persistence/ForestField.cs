#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Flora;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Persistence
{
    /// <summary>Grows a <see cref="ForestPlan"/>: the four passes of <see cref="PoissonForest"/>.</summary>
    public interface IForestPlanter
    {
        void Plant(ForestPlan plan);
    }

    /// <summary>
    /// A site's forest, ready to grow: the prepared 10 m cells, canopy mask, species tables and one slot
    /// per 64 m tile. The planter fills <see cref="Points"/> and <see cref="TileCount"/>.
    /// </summary>
    public sealed class ForestPlan
    {
        public ulong Seed;
        public int TilesX, TilesY;
        public ForestCell[] Cells = Array.Empty<ForestCell>();
        public int CellsX, CellsY, CellOriginX, CellOriginY, MaxSpacing256;
        public ulong[]? CanopyMask;
        public int MaskWest, MaskNorth, MaskWidth, MaskHeight;
        public byte[]? SpeciesIds, SpeciesWeights;
        public int SpeciesColumns, SpeciesRows, SpeciesWest, SpeciesNorth, SpeciesCell;
        public int[] ModelOfIndex = new int[256];
        public int[] SiteCumulative = Array.Empty<int>();
        public int Models, Variants, KrummholzModel = -1, DownwindRotation;
        public int[] TileOffset = Array.Empty<int>();
        public int[] TileCount = Array.Empty<int>();
        public ForestPoint[] Points = Array.Empty<ForestPoint>();

        public int TileCountTotal => TilesX * TilesY;

        /// <summary>Tiles of one pass (0–3), in a stable order.</summary>
        public int[] PhaseTiles(int phase)
        {
            var list = new List<int>(TileCountTotal / 4 + 1);
            for (int ty = phase >> 1; ty < TilesY; ty += 2)
                for (int tx = phase & 1; tx < TilesX; tx += 2)
                    list.Add(ty * TilesX + tx);
            return list.ToArray();
        }

        /// <summary>Pins every array for the duration of a grow and exposes it as <see cref="ForestInputs"/>.</summary>
        public sealed unsafe class Pinned : IDisposable
        {
            readonly List<GCHandle> _handles = new List<GCHandle>();
            public ForestInputs Inputs;

            public Pinned(ForestPlan p)
            {
                Inputs = new ForestInputs
                {
                    Seed = p.Seed, TilesX = p.TilesX, TilesY = p.TilesY,
                    Cells = (ForestCell*)Pin(p.Cells), CellsX = p.CellsX, CellsY = p.CellsY,
                    CellOriginX = p.CellOriginX, CellOriginY = p.CellOriginY, MaxSpacing256 = p.MaxSpacing256,
                    CanopyMask = (ulong*)Pin(p.CanopyMask), MaskWest = p.MaskWest, MaskNorth = p.MaskNorth, MaskWidth = p.MaskWidth, MaskHeight = p.MaskHeight,
                    SpeciesIds = (byte*)Pin(p.SpeciesIds), SpeciesWeights = (byte*)Pin(p.SpeciesWeights),
                    SpeciesColumns = p.SpeciesColumns, SpeciesRows = p.SpeciesRows,
                    SpeciesWest = p.SpeciesWest, SpeciesNorth = p.SpeciesNorth, SpeciesCell = p.SpeciesCell,
                    ModelOfIndex = (int*)Pin(p.ModelOfIndex), SiteCumulative = (int*)Pin(p.SiteCumulative),
                    Models = p.Models, Variants = p.Variants, KrummholzModel = p.KrummholzModel, DownwindRotation = p.DownwindRotation,
                    TileOffset = (int*)Pin(p.TileOffset), TileCount = (int*)Pin(p.TileCount), Points = (ForestPoint*)Pin(p.Points),
                };
            }

            void* Pin(Array? a)
            {
                if (a == null || a.Length == 0) return null;
                var h = GCHandle.Alloc(a, GCHandleType.Pinned);
                _handles.Add(h);
                return (void*)h.AddrOfPinnedObject();
            }

            public void Dispose()
            {
                foreach (var h in _handles) h.Free();
                _handles.Clear();
            }
        }

        /// <summary>Scratch sizes for one thread: bins over a tile and its reach into neighbours, and room for their trees.</summary>
        public int ScratchBins => ((PoissonForest.TileFixed + 2 * MaxSpacing256) >> ForestScratch.BinShift) + 1;
        public const int ScratchTrees = 1 << 15;
    }

    /// <summary>Grows one tile (see <see cref="PoissonForest.PlaceTile"/>).</summary>
    public unsafe delegate int TilePlacer(ForestInputs* inputs, int tile, ForestScratch* scratch);

    /// <summary>
    /// Runs the four passes over a plan, the tiles of each pass in parallel: they never touch, so the result
    /// doesn't depend on the order. Each worker thread has its own scratch.
    /// </summary>
    public static class ForestPasses
    {
        public static unsafe void Run(ForestPlan plan, TilePlacer place)
        {
            using var pinned = new ForestPlan.Pinned(plan);
            var inputs = pinned.Inputs;
            int bins = plan.ScratchBins;
            for (int phase = 0; phase < 4; phase++)
            {
                int[] tiles = plan.PhaseTiles(phase);
                Parallel.For(0, tiles.Length, () => new ScratchBuffer(bins * bins, ForestPlan.ScratchTrees), (n, _, scratch) =>
                {
                    var f = inputs;
                    var s = scratch.Value;
                    place(&f, tiles[n], &s);
                    return scratch;
                }, scratch => scratch.Dispose());
            }
        }

        sealed unsafe class ScratchBuffer : IDisposable
        {
            readonly int[] _heads, _next;
            readonly ForestPoint[] _local;
            GCHandle _h, _n, _l;
            public ForestScratch Value;

            public ScratchBuffer(int heads, int trees)
            {
                _heads = new int[heads];
                _next = new int[trees];
                _local = new ForestPoint[trees];
                _h = GCHandle.Alloc(_heads, GCHandleType.Pinned);
                _n = GCHandle.Alloc(_next, GCHandleType.Pinned);
                _l = GCHandle.Alloc(_local, GCHandleType.Pinned);
                Value = new ForestScratch
                {
                    Heads = (int*)_h.AddrOfPinnedObject(), HeadCapacity = heads,
                    Next = (int*)_n.AddrOfPinnedObject(), Local = (ForestPoint*)_l.AddrOfPinnedObject(), LocalCapacity = trees,
                };
            }

            public void Dispose()
            {
                _h.Free();
                _n.Free();
                _l.Free();
            }
        }
    }

    /// <summary>
    /// Grows a plan as plain C#: the downloader, CI and the game's fallback. The game's Burst planter must
    /// give the same bytes (EditMode test).
    /// </summary>
    public sealed class ManagedForestPlanter : IForestPlanter
    {
        public unsafe void Plant(ForestPlan plan) => ForestPasses.Run(plan, (f, tile, s) => PoissonForest.PlaceTile(ref *f, tile, ref *s));
    }

    /// <summary>
    /// A site's forest from the package's canopy, land cover and species layers (0.3 §4.5, T7). Each 10 m
    /// cell is prepared once with the forest rules (<see cref="ForestPlacement"/>, D4 calibration); the
    /// trees are then grown by <see cref="PoissonForest"/> per 64 m tile. Deterministic: the same package
    /// always grows the same forest, and a tree belongs to exactly one tile.
    /// </summary>
    public sealed class ForestField
    {
        /// <summary>Bytes per stored tree: x and z (16-bit fractions of the tile), height, prototype, rotation, width.</summary>
        public const int BytesPerTree = 8;
        public const double HeightStep = 0.25, WidthStep = 1.0 / 32;

        readonly PackageManifest _package;
        readonly byte[]? _canopy;
        readonly GridHeader _canopyHeader;
        readonly byte[]? _cover;
        readonly GridHeader _coverHeader;
        readonly byte[]? _speciesIds, _speciesWeights;
        readonly GridHeader _speciesHeader;
        readonly int[] _modelOfIndex;          // species table index (1-based) → model
        readonly double[] _siteShares;         // model → share of the site's biomass
        readonly AlbersBox _core;
        /// <summary>Tree share given to a ring WorldCover forest cell, measured in the core (see constructor).</summary>
        public readonly double RingTreeShare;
        readonly ulong _seed;
        readonly ForestCalibration _calibration = ForestCalibration.Default;

        readonly TerrainCache.HeightField? _heights;

        /// <summary>How long the last <see cref="BuildAll"/> spent preparing cells and growing trees (seconds).</summary>
        public double PrepareSeconds, PlantSeconds;
        /// <summary>The core's median treeline from the last <see cref="Prepare"/> (NaN: none, or no heights given).</summary>
        public double TreelineMetres = double.NaN;

        /// <param name="heights">The package's heights, for krummholz at the treeline (without them, none grows).</param>
        public ForestField(PackageManifest package, string folder, TerrainCache.HeightField? heights = null)
        {
            _package = package;
            _heights = heights;
            _seed = CoverNoise.SeedFor(package.Site.CentreX, package.Site.CentreY) ^ 0x7EE5UL;
            var site = SiteSquare.Create(new AlbersPoint(package.Site.CentreX, package.Site.CentreY), package.Site.SizeMetres / 1000.0);
            _core = site.Core;
            if (package.Layers.Any(l => l.Id == "canopy-core")) _canopy = ResortPackage.ReadByteLayer(folder, package, "canopy-core", out _canopyHeader);
            if (package.Layers.Any(l => l.Id == "cover")) _cover = ResortPackage.ReadByteLayer(folder, package, "cover", out _coverHeader);
            if (package.Layers.Any(l => l.Id == "species-ids") && package.Layers.Any(l => l.Id == "species-weights"))
            {
                _speciesIds = ResortPackage.ReadByteLayer(folder, package, "species-ids", out _speciesHeader);
                _speciesWeights = ResortPackage.ReadByteLayer(folder, package, "species-weights", out _);
            }
            _modelOfIndex = new int[256];
            _siteShares = new double[SpeciesMap.Models.Length];
            foreach (var s in package.Species)
            {
                if (s.Index <= 0 || s.Index > 255) continue;
                int model = SpeciesMap.ModelFor(s.Spcd);
                _modelOfIndex[s.Index] = model;
                _siteShares[model] += s.ShareOfBiomass;
            }
            if (_siteShares.Sum() <= 0) _siteShares[SpeciesMap.IndexOf("douglas_fir")] = 1;
            RingTreeShare = MeasureRingShare();
        }

        /// <summary>
        /// Calibrates the ring against the core: the canopy's total tree share over the core divided by
        /// the number of core cells WorldCover calls forest. Ring forest cells then carry, on average, as
        /// many trees as the core really has per WorldCover forest cell, so density doesn't jump at the seam.
        /// </summary>
        double MeasureRingShare()
        {
            if (_canopy == null || _cover == null) return ForestPlacement.WorldCoverTreeShare;
            var h = _canopyHeader;
            const int m = ForestPlacement.CellMetres;
            double shares = 0;
            long forestCells = 0;
            for (int br = 0; br + m <= h.Height; br += m)
                for (int bc = 0; bc + m <= h.Width; bc += m)
                {
                    int trees = 0;
                    for (int j = 0; j < m; j++)
                    {
                        long row = (long)(br + j) * h.Width + bc;
                        for (int i = 0; i < m; i++) if (_canopy[row + i] >= ForestRule.TreeCode) trees++;
                    }
                    shares += trees / 100.0;
                    if (CoverAt(h.West + bc + m / 2.0, h.North - br - m / 2.0) == 10) forestCells++;
                }
            if (forestCells == 0) return ForestPlacement.WorldCoverTreeShare;
            return Clamp(shares / forestCells, 0.05, 0.8);
        }

        /// <summary>
        /// Prepares the plan for the whole tile grid: the forest frame starts at the grid's south-west corner
        /// and holds 16 × 16 forest tiles per terrain tile.
        /// </summary>
        public ForestPlan Prepare(TileGrid tiles)
        {
            double ox = tiles.West, oy = tiles.North - tiles.Rows * TileGrid.TileMetres;
            int perTerrainTile = (int)(TileGrid.TileMetres / PoissonForest.TileMetres);
            var plan = new ForestPlan
            {
                Seed = _seed, TilesX = tiles.Columns * perTerrainTile, TilesY = tiles.Rows * perTerrainTile,
                Models = SpeciesMap.Models.Length, Variants = SpeciesMap.VariantsPerModel, KrummholzModel = SpeciesMap.IndexOf("krummholz"),
                DownwindRotation = Treeline.DownwindRotation,
            };
            const int m = ForestPlacement.CellMetres;

            // Cells on the global 10 m grid, covering the frame.
            long cellX0 = (long)Math.Floor(ox / m), cellY0 = (long)Math.Floor(oy / m);
            plan.CellsX = (int)((long)Math.Floor((ox + tiles.Columns * TileGrid.TileMetres - 1e-9) / m) - cellX0 + 1);
            plan.CellsY = (int)((long)Math.Floor((oy + tiles.Rows * TileGrid.TileMetres - 1e-9) / m) - cellY0 + 1);
            plan.CellOriginX = Fixed(cellX0 * m - ox);
            plan.CellOriginY = Fixed(cellY0 * m - oy);
            plan.Cells = new ForestCell[plan.CellsX * plan.CellsY];
            var cells = plan.Cells;
            int cellsX = plan.CellsX;
            Parallel.For(0, plan.CellsY, j =>
            {
                for (int i = 0; i < cellsX; i++) cells[j * cellsX + i] = PrepareCell(cellX0 + i, cellY0 + j);
            });
            plan.MaxSpacing256 = cells.Length == 0 ? 0 : cells.Max(c => c.Spacing256);
            AddKrummholz(plan, cellX0, cellY0);

            if (_canopy != null)
            {
                var h = _canopyHeader;
                plan.MaskWest = Whole(h.West - ox);
                plan.MaskNorth = Whole(h.North - oy);
                plan.MaskWidth = h.Width;
                plan.MaskHeight = h.Height;
                var mask = new ulong[((long)h.Width * h.Height + 63) / 64];
                for (int r = 0; r < h.Height; r++)
                {
                    long row = (long)r * h.Width;
                    for (int c = 0; c < h.Width; c++)
                        if (_canopy[row + c] >= ForestRule.TreeCode)
                        {
                            long bit = row + c;
                            mask[bit >> 6] |= 1UL << (int)(bit & 63);
                        }
                }
                plan.CanopyMask = mask;
            }

            if (_speciesIds != null && _speciesWeights != null)
            {
                var h = _speciesHeader;
                plan.SpeciesIds = _speciesIds;
                plan.SpeciesWeights = _speciesWeights;
                plan.SpeciesColumns = h.Width / 4;
                plan.SpeciesRows = h.Height;
                plan.SpeciesWest = Fixed(h.West - ox);
                plan.SpeciesNorth = Fixed(h.North - oy);
                plan.SpeciesCell = Fixed(h.CellSize);
            }
            Array.Copy(_modelOfIndex, plan.ModelOfIndex, 256);
            double total = _siteShares.Sum();
            plan.SiteCumulative = new int[_siteShares.Length];
            double running = 0;
            for (int k = 0; k < _siteShares.Length; k++)
            {
                running += _siteShares[k];
                plan.SiteCumulative[k] = (int)Math.Round(running / total * 65536);
            }

            // Each tile's quota sets its slot; the planter fills the slots.
            plan.TileOffset = new int[plan.TileCountTotal + 1];
            plan.TileCount = new int[plan.TileCountTotal];
            var quotas = new int[plan.TileCountTotal];
            unsafe
            {
                using var pinned = new ForestPlan.Pinned(plan);
                var inputs = pinned.Inputs;
                Parallel.For(0, quotas.Length, t =>
                {
                    var f = inputs;
                    quotas[t] = PoissonForest.TileQuota(ref f, t);
                });
            }
            for (int t = 0; t < quotas.Length; t++) plan.TileOffset[t + 1] = plan.TileOffset[t] + quotas[t];
            plan.Points = new ForestPoint[plan.TileOffset[quotas.Length]];
            return plan;
        }

        /// <summary>
        /// Marks the core's cells just below the treeline (<see cref="Treeline"/>) with their chance of growing
        /// krummholz, from each cell's elevation and whether it holds forest.
        /// </summary>
        void AddKrummholz(ForestPlan plan, long cellX0, long cellY0)
        {
            if (_heights == null || _canopy == null) return;
            const int m = ForestPlacement.CellMetres;
            var h = _canopyHeader;
            // The cells whose centres lie on the canopy map (the core).
            int i0 = (int)(Math.Ceiling((h.West - m / 2.0) / m) - cellX0), i1 = (int)(Math.Floor((h.West + h.Width * h.CellSize - m / 2.0 - 1e-9) / m) - cellX0);
            int j0 = (int)(Math.Ceiling((h.North - h.Height * h.CellSize - m / 2.0 + 1e-9) / m) - cellY0), j1 = (int)(Math.Floor((h.North - m / 2.0) / m) - cellY0);
            i0 = Math.Max(0, i0); j0 = Math.Max(0, j0); i1 = Math.Min(plan.CellsX - 1, i1); j1 = Math.Min(plan.CellsY - 1, j1);
            int cols = i1 - i0 + 1, rows = j1 - j0 + 1;
            if (cols <= 0 || rows <= 0) return;
            var elevation = new float[cols * rows];
            var forest = new bool[cols * rows];
            var cells = plan.Cells;
            var heights = _heights;
            Parallel.For(0, rows, y =>
            {
                for (int x = 0; x < cols; x++)
                {
                    long cx = cellX0 + i0 + x, cy = cellY0 + j0 + y;
                    elevation[y * cols + x] = (float)heights.At(cx * m + m / 2.0, cy * m + m / 2.0);
                    forest[y * cols + x] = cells[(j0 + y) * plan.CellsX + i0 + x].Kind == ForestCell.Core;
                }
            });
            byte[] chance = Treeline.Krummholz(elevation, forest, cols, rows, m, out TreelineMetres);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    cells[(j0 + y) * plan.CellsX + i0 + x].Krummholz = chance[y * cols + x];
        }

        /// <summary>One 10 m cell's forest (the rules of <see cref="ForestPlacement"/>), or an empty cell.</summary>
        ForestCell PrepareCell(long cx, long cy)
        {
            const int m = ForestPlacement.CellMetres;
            double x0 = cx * m, y0 = cy * m;
            bool core = _canopy != null && Inside(_canopyHeader, x0 + m / 2.0, y0 + m / 2.0);
            double dominant, expected, width = 1;
            byte kind;
            if (core)
            {
                int trees10 = 0, tallest = 0;
                for (int j = 0; j < m; j++)
                    for (int i = 0; i < m; i++)
                    {
                        int v = CanopyAt(x0 + i + 0.5, y0 + j + 0.5);
                        if (v >= ForestRule.TreeCode) trees10++;
                        if (v > tallest) tallest = v;
                    }
                if (trees10 == 0) return default;
                dominant = Clamp(_calibration.DominantHeight((byte)tallest), ForestPlacement.MinHeight, ForestPlacement.MaxHeight);
                expected = ForestPlacement.TreesPerCell(trees10 / 100.0, dominant, _calibration);
                kind = ForestCell.Core;
            }
            else
            {
                if (_cover == null || CoverAt(x0 + m / 2.0, y0 + m / 2.0) != 10) return default;
                dominant = ForestPlacement.RingDominantHeight * (0.85 + 0.3 * ForestPlacement.Hash01(_seed, cx, cy, 0, 7));
                double density = ForestPlacement.RingDensity(OutsideCore(x0 + m / 2.0, y0 + m / 2.0));
                expected = ForestPlacement.TreesPerCell(RingTreeShare, dominant, _calibration) * density;
                width = Math.Min(3, 1 / Math.Sqrt(density));   // fewer, wider crowns in the distance
                kind = ForestCell.Ring;
            }
            double spacing = 0.9 * ForestPlacement.CrownRadius(dominant) * width;
            return new ForestCell
            {
                Kind = kind,
                Expected256 = (ushort)Math.Min(65535, Math.Round(expected * PoissonForest.Fixed)),
                Spacing256 = (ushort)Math.Min(65535, Math.Round(spacing * PoissonForest.Fixed)),
                HeightCode = (byte)Math.Min(255, Math.Round(dominant / HeightStep)),
                Width32 = (byte)Math.Min(255, Math.Round(width / WidthStep)),
            };
        }

        /// <summary>
        /// Grows the whole site and returns each terrain tile's trees, in <see cref="TileGrid.All"/> order
        /// (positions relative to the tile's south-west corner).
        /// </summary>
        public List<PlacedTree>[] BuildAll(TileGrid tiles, IForestPlanter? planter = null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var plan = Prepare(tiles);
            PrepareSeconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            (planter ?? new ManagedForestPlanter()).Plant(plan);
            PlantSeconds = clock.Elapsed.TotalSeconds;
            return Split(plan, tiles);
        }

        /// <summary>Groups a grown plan's trees by terrain tile (forest tiles south to north, west to east).</summary>
        public static List<PlacedTree>[] Split(ForestPlan plan, TileGrid tiles)
        {
            int per = (int)(TileGrid.TileMetres / PoissonForest.TileMetres);
            var keys = tiles.All().ToList();
            var result = new List<PlacedTree>[keys.Count];
            Parallel.For(0, keys.Count, n =>
            {
                var key = keys[n];
                int tx0 = key.Column * per, ty0 = (tiles.Rows - 1 - key.Row) * per;
                int fx = tx0 * PoissonForest.TileFixed, fy = ty0 * PoissonForest.TileFixed;
                var list = new List<PlacedTree>();
                for (int ty = ty0; ty < ty0 + per; ty++)
                    for (int tx = tx0; tx < tx0 + per; tx++)
                    {
                        int t = ty * plan.TilesX + tx, start = plan.TileOffset[t];
                        for (int k = 0; k < plan.TileCount[t]; k++)
                        {
                            var p = plan.Points[start + k];
                            list.Add(new PlacedTree
                            {
                                X = (p.X - fx) / (float)PoissonForest.Fixed, Z = (p.Y - fy) / (float)PoissonForest.Fixed,
                                Height = p.HeightCode * (float)HeightStep, Prototype = p.Prototype, Rotation = p.Rotation,
                                WidthScale = p.Width32 * (float)WidthStep,
                            });
                        }
                    }
                result[n] = list;
            });
            return result;
        }

        int CanopyAt(double x, double y)
        {
            var h = _canopyHeader;
            int c = (int)Math.Floor((x - h.West) / h.CellSize), r = (int)Math.Floor((h.North - y) / h.CellSize);
            if (c < 0 || r < 0 || c >= h.Width || r >= h.Height) return 0;
            return _canopy![(long)r * h.Width + c];
        }

        int CoverAt(double x, double y)
        {
            var h = _coverHeader;
            int c = (int)Math.Floor((x - h.West) / h.CellSize), r = (int)Math.Floor((h.North - y) / h.CellSize);
            if (c < 0 || r < 0 || c >= h.Width || r >= h.Height) return 0;
            return _cover![(long)r * h.Width + c];
        }

        static bool Inside(GridHeader h, double x, double y) =>
            x >= h.West && x < h.West + h.Width * h.CellSize && y <= h.North && y > h.North - h.Height * h.CellSize;

        double OutsideCore(double x, double y)
        {
            double dx = Math.Max(0, Math.Max(_core.West - x, x - _core.East));
            double dy = Math.Max(0, Math.Max(_core.South - y, y - _core.North));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        static int Fixed(double metres) => (int)Math.Round(metres * PoissonForest.Fixed);

        /// <summary>A whole number of metres (the grids sit on whole metres), or an error.</summary>
        static int Whole(double metres)
        {
            double r = Math.Round(metres);
            if (Math.Abs(metres - r) > 1e-6) throw new InvalidOperationException($"The canopy grid is {metres} m from the forest frame, not whole metres.");
            return (int)r;
        }

        /// <summary>Packs trees for the cache: 8 bytes each; positions are 16-bit fractions of the tile (1.6 cm steps).</summary>
        public static byte[] Encode(List<PlacedTree> trees, double tileMetres)
        {
            var bytes = new byte[trees.Count * BytesPerTree];
            for (int i = 0; i < trees.Count; i++)
            {
                var t = trees[i];
                int o = i * BytesPerTree;
                ushort x = (ushort)Math.Min(65535, Math.Round(t.X / tileMetres * 65535)), z = (ushort)Math.Min(65535, Math.Round(t.Z / tileMetres * 65535));
                bytes[o] = (byte)x;
                bytes[o + 1] = (byte)(x >> 8);
                bytes[o + 2] = (byte)z;
                bytes[o + 3] = (byte)(z >> 8);
                bytes[o + 4] = (byte)Math.Min(255, Math.Round(t.Height / HeightStep));
                bytes[o + 5] = t.Prototype;
                bytes[o + 6] = t.Rotation;
                bytes[o + 7] = (byte)Math.Min(255, Math.Round(t.WidthScale / WidthStep));
            }
            return bytes;
        }

        /// <summary>Unpacks one tree: position as a 0–1 fraction of the tile (x east, z north).</summary>
        public static void Decode(byte[] bytes, int i, out float fx, out float fz, out float height, out int prototype, out float rotationRadians, out float width)
        {
            int o = i * BytesPerTree;
            fx = (bytes[o] | bytes[o + 1] << 8) / 65535f;
            fz = (bytes[o + 2] | bytes[o + 3] << 8) / 65535f;
            height = bytes[o + 4] * (float)HeightStep;
            prototype = bytes[o + 5];
            rotationRadians = bytes[o + 6] / 256f * 2 * (float)Math.PI;
            width = bytes[o + 7] * (float)WidthStep;
        }
    }
}
