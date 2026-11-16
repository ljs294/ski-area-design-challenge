using System;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Persistence;

namespace MountainPlanner.Presentation.Edits
{
    /// <summary>
    /// SN-01 v2 (task 4.07 PR A2): a tile's samples as <see cref="SurfaceSampler"/> reads them, shared by the opener and the edit
    /// pipeline so both build the same bytes. Heights are the cache's quantised steps (the tile holds them as step ÷ 32766),
    /// turned into whole mm in one rounding: the step is recovered exactly from either the decoded tile or an edited post.
    /// </summary>
    public static class SnowSamples
    {
        /// <summary>A normalised height (step ÷ <see cref="TerrainCache.MaxValue"/>) as whole mm.</summary>
        public static int Millimetres(float normalised, double heightMin, double heightRange)
        {
            int q = (int)Math.Round(normalised * TerrainCache.MaxValue);
            return (int)Math.Round((heightMin + q * heightRange / TerrainCache.MaxValue) * 1000.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>A tile's south-west corner from the ring's south-west corner (mm, either side) and its side (mm): local
        /// metres as the frame gives them, rounded once.</summary>
        public static (long West, long South, long Side) TileMm(double tileWest, double tileSouth, double ringWest, double ringSouth, double tileMetres) =>
            ((long)Math.Round((tileWest - ringWest) * 1000.0), (long)Math.Round((tileSouth - ringSouth) * 1000.0), (long)Math.Round(tileMetres * 1000.0));

        /// <summary>A decoded tile's posts, <c>h[row, column]</c> with row 0 at the south edge (Unity's order).</summary>
        public readonly struct Posts : ITileSamples
        {
            readonly float[,] _h;
            readonly double _min, _range;
            public Posts(float[,] h, double heightMin, double heightRange) { _h = h; _min = heightMin; _range = heightRange; }
            public int Resolution => _h.GetLength(0);
            public int Value(int column, int row) => Millimetres(_h[row, column], _min, _range);
        }

        /// <summary>A tile's forest-floor weights: <paramref name="stride"/> bytes a texel, the weight at <paramref name="offset"/>,
        /// row 0 at the south edge (the splat's order).</summary>
        public readonly struct Canopy : ITileSamples
        {
            readonly byte[] _texels;
            readonly int _res, _stride, _offset;
            public Canopy(byte[] texels, int resolution, int stride, int offset) { _texels = texels; _res = resolution; _stride = stride; _offset = offset; }
            public int Resolution => _res;
            public int Value(int column, int row) => _texels[(row * _res + column) * _stride + _offset];
        }
    }
}
