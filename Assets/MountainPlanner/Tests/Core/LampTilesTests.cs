using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 4.50 slice 3 part 2: which lamps can light each tile of ground.
    public sealed class LampTilesTests
    {
        [Test]
        public void ALampIsInEveryTileItReachesAndNoOther()
        {
            // A lamp near a tile corner reaching 6 m, one well inside a tile, one far off: brute force over every tile.
            var x = new[] { 31f, 50f, -400f };
            var z = new[] { 30.5f, 80f, 1000f };
            var range = new[] { 6f, 4f, 14f };
            var tiles = new LampTiles();
            tiles.Build(x, z, range, 3);
            float t = LampTiles.TileMetres;
            for (int row = tiles.Row0 - 1; row <= tiles.Row0 + tiles.Height; row++)
                for (int col = tiles.Column0 - 1; col <= tiles.Column0 + tiles.Width; col++)
                {
                    var expect = new List<int>();
                    for (int i = 0; i < 3; i++)
                    {
                        double nx = Math.Max(col * t, Math.Min((col + 1) * t, x[i])) - x[i];
                        double nz = Math.Max(row * t, Math.Min((row + 1) * t, z[i])) - z[i];
                        if (nx * nx + nz * nz <= range[i] * range[i]) expect.Add(i);
                    }
                    Assert.That(tiles.LampsIn(col, row).ToList(), Is.EqualTo(expect), $"tile {col},{row}");
                }
            // the corner lamp reaches all four tiles round (32, 32); the inside one only its own
            Assert.That(new[] { (0, 0), (1, 0), (0, 1), (1, 1) }.All(c => tiles.LampsIn(c.Item1, c.Item2).Contains(0)), Is.True);
            Assert.That(tiles.LampsIn(1, 2).ToList(), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void AFullTileKeepsTheLampsNearestItsCentre()
        {
            // Twenty lamps along a line through one tile: it keeps the sixteen nearest its centre, in lamp order, every time.
            int n = 20;
            var x = Enumerable.Range(0, n).Select(i => 2f + i * 1.5f).ToArray();
            var z = Enumerable.Repeat(16f, n).ToArray();
            var range = Enumerable.Repeat(1f, n).ToArray();
            var a = new LampTiles();
            a.Build(x, z, range, n);
            var kept = a.LampsIn(0, 0).ToList();
            Assert.That(kept.Count, Is.EqualTo(LampTiles.MaxPerTile));
            var nearest = Enumerable.Range(0, n).Where(i => x[i] < 32).OrderBy(i => Math.Abs(x[i] - 16)).ThenBy(i => i).Take(16).OrderBy(i => i);
            Assert.That(kept, Is.EqualTo(nearest.ToList()));
            var b = new LampTiles();
            b.Build(x, z, range, n);
            Assert.That(b.Index, Is.EqualTo(a.Index));
            Assert.That(b.Cells, Is.EqualTo(a.Cells));
        }

        [Test]
        public void NoLampsNoTiles()
        {
            var tiles = new LampTiles();
            tiles.Build(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), 0);
            Assert.That(tiles.TileCount, Is.EqualTo(0));
            Assert.That(tiles.LampsIn(0, 0), Is.Empty);
        }
    }
}
