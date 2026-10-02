using System;

namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// Snow depth over the whole map, in metres (0.3 §4.6, T8): the seam a future snow model writes. Iteration 1
    /// fills it with a flat 12 in everywhere. Writers change a rectangle and the renderer uploads only what
    /// changed (<see cref="TakeDirty"/>). Cells run west to east, then south to north.
    /// </summary>
    public sealed class SnowDepthField
    {
        /// <summary>Iteration 1's snow: 12 in everywhere.</summary>
        public const float IterationOneMetres = 0.3048f;

        public readonly int Width, Height;
        public readonly float CellMetres;
        readonly float[] _depth;
        int _dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1;   // exclusive max; empty when x1 <= x0

        public SnowDepthField(int width, int height, float cellMetres, float depthMetres = IterationOneMetres)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "The field needs at least one cell.");
            if (!(cellMetres > 0)) throw new ArgumentOutOfRangeException(nameof(cellMetres));
            Width = width;
            Height = height;
            CellMetres = cellMetres;
            _depth = new float[width * height];
            Fill(depthMetres);
        }

        /// <summary>The depth at a cell, metres.</summary>
        public float this[int x, int y] => _depth[y * Width + x];

        /// <summary>The raw cells (row-major, south row first), for uploading. Read-only by convention.</summary>
        public float[] Cells => _depth;

        public bool IsDirty => _dirtyX1 > _dirtyX0;

        /// <summary>Sets every cell.</summary>
        public void Fill(float depthMetres) => SetRect(0, 0, Width, Height, depthMetres);

        /// <summary>Sets a rectangle of cells (clipped to the field) and marks it changed.</summary>
        public void SetRect(int x, int y, int width, int height, float depthMetres)
        {
            if (!(depthMetres >= 0) || float.IsInfinity(depthMetres)) throw new ArgumentOutOfRangeException(nameof(depthMetres), "Depth must be finite and not negative.");
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + width), y1 = Math.Min(Height, y + height);
            if (x1 <= x0 || y1 <= y0) return;
            for (int j = y0; j < y1; j++)
            {
                int row = j * Width;
                for (int i = x0; i < x1; i++) _depth[row + i] = depthMetres;
            }
            if (IsDirty)
            {
                _dirtyX0 = Math.Min(_dirtyX0, x0); _dirtyY0 = Math.Min(_dirtyY0, y0);
                _dirtyX1 = Math.Max(_dirtyX1, x1); _dirtyY1 = Math.Max(_dirtyY1, y1);
            }
            else
            {
                _dirtyX0 = x0; _dirtyY0 = y0; _dirtyX1 = x1; _dirtyY1 = y1;
            }
        }

        /// <summary>The changed rectangle since the last call (x, y, width, height), then clears it. Width 0 when nothing changed.</summary>
        public (int X, int Y, int Width, int Height) TakeDirty()
        {
            if (!IsDirty) return (0, 0, 0, 0);
            var r = (_dirtyX0, _dirtyY0, _dirtyX1 - _dirtyX0, _dirtyY1 - _dirtyY0);
            _dirtyX0 = _dirtyY0 = _dirtyX1 = _dirtyY1 = 0;
            return r;
        }
    }
}
