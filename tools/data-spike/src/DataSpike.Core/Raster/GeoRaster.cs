using System;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.DataSpike.Tiff;

namespace MountainPlanner.DataSpike.Raster
{
    /// <summary>
    /// A north-up raster window in its source CRS: pixel (col, row) covers
    /// [OriginX + col·PixelX, OriginX + (col+1)·PixelX] × [OriginY − (row+1)·PixelY, OriginY − row·PixelY].
    /// </summary>
    public sealed class GeoRaster
    {
        public readonly float[] Data;
        public readonly int Width, Height;
        public readonly double OriginX, OriginY, PixelX, PixelY;

        public GeoRaster(float[] data, int width, int height, double originX, double originY, double pixelX, double pixelY)
        {
            Data = data; Width = width; Height = height; OriginX = originX; OriginY = originY; PixelX = pixelX; PixelY = pixelY;
        }

        /// <summary>Nearest-neighbour value at a source-CRS coordinate, NaN outside or on no-data.</summary>
        public float SampleNearest(double x, double y)
        {
            int col = (int)Math.Floor((x - OriginX) / PixelX), row = (int)Math.Floor((OriginY - y) / PixelY);
            if (col < 0 || row < 0 || col >= Width || row >= Height) return float.NaN;
            return Data[row * Width + col];
        }

        public int CountNaN()
        {
            int n = 0;
            foreach (float v in Data) if (float.IsNaN(v)) n++;
            return n;
        }

        /// <summary>
        /// Read the window of a GeoTIFF directory that covers a source-CRS box, expanded to whole pixels.
        /// Returns null when the box does not overlap the image.
        /// </summary>
        public static async Task<GeoRaster?> ReadBoxAsync(TiffImage tiff, int dirIndex, double west, double south, double east, double north, CancellationToken ct)
        {
            var dir = tiff.Directories[dirIndex];
            if (dir.PixelScale == null || dir.TiePoint == null) throw new InvalidOperationException("Directory has no georeferencing");
            double sx = dir.PixelScale[0], sy = dir.PixelScale[1];
            double x0 = dir.TiePoint[3] - dir.TiePoint[0] * sx, y0 = dir.TiePoint[4] + dir.TiePoint[1] * sy;
            int col0 = (int)Math.Floor((west - x0) / sx + 1e-9), col1 = (int)Math.Ceiling((east - x0) / sx - 1e-9);
            int row0 = (int)Math.Floor((y0 - north) / sy + 1e-9), row1 = (int)Math.Ceiling((y0 - south) / sy - 1e-9);
            int c0 = Math.Max(0, col0), r0 = Math.Max(0, row0), c1 = Math.Min(dir.Width, col1), r1 = Math.Min(dir.Height, row1);
            if (c1 <= c0 || r1 <= r0) return null;
            float[] data = await tiff.ReadWindowAsync(dirIndex, c0, r0, c1 - c0, r1 - r0, ct).ConfigureAwait(false);
            return new GeoRaster(data, c1 - c0, r1 - r0, x0 + c0 * sx, y0 - r0 * sy, sx, sy);
        }
    }
}
