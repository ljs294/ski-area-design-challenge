using System.Text.Json;
using MountainPlanner.DataSpike.Geo;
using MountainPlanner.DataSpike.IO;
using MountainPlanner.DataSpike.Providers;
using MountainPlanner.DataSpike.Tiff;

namespace MountainPlanner.DataSpike.Tests;

/// <summary>Offline tests: pure maths plus one recorded S1M tile (Jackson Hole). No network.</summary>
public class GeoTests
{
    [Test]
    public void AlbersRoundTripIsSubMillimetre()
    {
        foreach (var (lat, lon) in new[] { (46.935, -121.474), (43.593, -110.848), (25.8, -80.2), (48.9, -67.5) })
        {
            Albers6350.Forward(lat, lon, out double x, out double y);
            Albers6350.Inverse(x, y, out double lat2, out double lon2);
            Albers6350.Forward(lat2, lon2, out double x2, out double y2);
            Assert.That(Math.Abs(x2 - x) + Math.Abs(y2 - y), Is.LessThan(0.001), $"{lat},{lon}");
        }
    }

    [Test]
    public void AlbersMatchesTheBigmapServersOwnProjection()
    {
        // The BIGMAP service (ESRI:102039, same projection) reported this location for the
        // WGS 84 point below; the ~1–2 m gap is the WGS 84 / NAD83 datum difference.
        Albers6350.Forward(46.935, -121.474, out double x, out double y);
        Assert.That(x, Is.EqualTo(-1925082.73).Within(3.0));
        Assert.That(y, Is.EqualTo(2920065.18).Within(3.0));
    }

    [Test]
    public void ScaleIsExactOnTheStandardParallels()
    {
        Assert.That(Albers6350.ParallelScale(29.5), Is.EqualTo(1.0).Within(1e-9));
        Assert.That(Albers6350.ParallelScale(45.5), Is.EqualTo(1.0).Within(1e-9));
        Assert.That(Albers6350.ParallelScale(37.5), Is.InRange(0.98, 1.0));
    }

    [TestCase(46.935, -121.474, "021230211")]
    [TestCase(43.593, -110.848, "021322030")]
    public void CanopyQuadKeys(double lat, double lon, string expected)
    {
        WebMercator.Tile(lat, lon, 9, out int tx, out int ty);
        Assert.That(WebMercator.QuadKey(tx, ty, 9), Is.EqualTo(expected));
    }

    [TestCase(1490500, 465000, "n0470e1490", "n04e14")]
    [TestCase(-1925084, 2920066, "n2930w1930", "n29w19")]
    [TestCase(-1188828, 2381976, "n2390w1190", "n23w11")]
    public void S1mTileNames(double x, double y, string tile, string folder)
    {
        Assert.That(S1m.TileName(x, y, out string f, out _, out _), Is.EqualTo(tile));
        Assert.That(f, Is.EqualTo(folder));
    }

    [Test]
    public void WorldCoverTileName()
    {
        Assert.That(WorldCover.TileUrl(46.935, -121.474), Does.EndWith("ESA_WorldCover_10m_2021_v200_N45W123_Map.tif"));
    }
}

public class CodecTests
{
    [Test]
    public void FloatingPointPredictorRoundTrips()
    {
        const int width = 7, rows = 3;
        var values = new float[width * rows];
        for (int i = 0; i < values.Length; i++) values[i] = 2000f + i * 0.37f - (i % 3) * 11.5f;

        // Encode per Adobe TIFF Tech Note 3: big-endian byte planes, then byte-wise differencing.
        var encoded = new byte[values.Length * 4];
        for (int r = 0; r < rows; r++)
        {
            var planar = new byte[width * 4];
            for (int i = 0; i < width; i++)
            {
                byte[] b = BitConverter.GetBytes(values[r * width + i]);
                if (BitConverter.IsLittleEndian) Array.Reverse(b);
                for (int k = 0; k < 4; k++) planar[k * width + i] = b[k];
            }
            for (int i = planar.Length - 1; i > 0; i--) planar[i] = (byte)(planar[i] - planar[i - 1]);
            Buffer.BlockCopy(planar, 0, encoded, r * width * 4, width * 4);
        }

        Assert.That(Predictors.UndoFloatingPoint32(encoded, width, rows), Is.EqualTo(values));
    }

    [Test]
    public void HorizontalPredictorUndoesDifferences()
    {
        var data = new byte[] { 10, 1, 1, 250, 5, 0, 3 };
        Predictors.UndoHorizontal8(data, 7, 1);
        Assert.That(data, Is.EqualTo(new byte[] { 10, 11, 12, 6, 11, 11, 14 }));
    }
}

/// <summary>
/// Acceptance for task 01 on recorded bytes of S1M tile n2390w1190 (Jackson Hole): the file's
/// directory, one 1 m block, and the 2 m overview block covering it.
/// </summary>
public class S1mFixtureTests
{
    static readonly string Dir = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures");
    FixtureInfo _info = null!;
    TiffImage _tiff = null!;

    sealed class FixtureInfo { public long l0Offset { get; set; } public long l1Offset { get; set; } public int l0Col { get; set; } public int l0Row { get; set; } public int width { get; set; } public int height { get; set; } }

    [OneTimeSetUp]
    public async Task Open()
    {
        _info = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(Path.Combine(Dir, "s1m-fixture.json")))!;
        var source = new SparseByteSource("s1m-fixture",
            (0, File.ReadAllBytes(Path.Combine(Dir, "s1m-header.bin"))),
            (_info.l0Offset, File.ReadAllBytes(Path.Combine(Dir, "s1m-l0-block.bin"))),
            (_info.l1Offset, File.ReadAllBytes(Path.Combine(Dir, "s1m-l1-block.bin"))));
        _tiff = await TiffImage.OpenAsync(source);
    }

    [Test]
    public void DirectoryDescribesAnS1mTile()
    {
        var d = _tiff.Directories[0];
        Assert.Multiple(() =>
        {
            Assert.That(d.Width, Is.EqualTo(10000));
            Assert.That(d.Height, Is.EqualTo(10000));
            Assert.That(d.Compression, Is.EqualTo(5), "LZW");
            Assert.That(d.Predictor, Is.EqualTo(3), "floating-point predictor");
            Assert.That(d.Epsg, Is.EqualTo(6350));
            Assert.That(d.PixelScale![0], Is.EqualTo(1.0));
            Assert.That(d.TiePoint![3], Is.EqualTo(-1190000.0), "west edge");
            Assert.That(d.TiePoint![4], Is.EqualTo(2390000.0), "north edge");
            Assert.That(_tiff.Directories[1].Width, Is.EqualTo(5000), "2 m overview");
        });
    }

    [Test]
    public async Task OneMetreBlockDecodesToPlausibleHeights()
    {
        float[] block = await _tiff.ReadWindowAsync(0, _info.l0Col, _info.l0Row, _info.width, _info.height);
        Assert.That(block.Count(float.IsNaN), Is.Zero);
        Assert.That(block.Min(), Is.GreaterThan(1800f), "Jackson Hole is above 1,800 m");
        Assert.That(block.Max(), Is.LessThan(3300f));
    }

    [Test]
    public async Task OneMetreBlockAveragesToTheTwoMetreOverview()
    {
        int w = _info.width, h = _info.height;
        float[] full = await _tiff.ReadWindowAsync(0, _info.l0Col, _info.l0Row, w, h);
        float[] half = await _tiff.ReadWindowAsync(1, _info.l0Col / 2, _info.l0Row / 2, w / 2, h / 2);
        double sum = 0; int n = 0;
        for (int r = 0; r < h / 2; r++)
            for (int c = 0; c < w / 2; c++)
            {
                double avg = (full[2 * r * w + 2 * c] + full[2 * r * w + 2 * c + 1] + full[(2 * r + 1) * w + 2 * c] + full[(2 * r + 1) * w + 2 * c + 1]) / 4.0;
                sum += Math.Abs(avg - half[r * (w / 2) + c]); n++;
            }
        Assert.That(sum / n, Is.LessThanOrEqualTo(0.05), "mean |average of 1 m cells − 2 m overview| (task 01 acceptance)");
    }
}
