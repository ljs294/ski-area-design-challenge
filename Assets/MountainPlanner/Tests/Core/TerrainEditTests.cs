using System;
using System.Linq;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // The terrain edit tiles and their canonical bytes (task P3-05 PR A; rules GR-09, GR-06, CL-05, CL-07, CF-05, SH-04).
    public sealed class TerrainEditTests
    {
        /// <summary>
        /// The golden: the scripted edit set's canonical bytes. A change here is a change to the edit tile shape, which
        /// task 04's save reads, and needs the owner's approval (AGENTS.md determinism rules).
        /// </summary>
        const string GoldenSha = "bec0602105a8c3c4fc041aa0df47a4a5022ab556604a375194b0fb9429ff81d7";

        [Test]
        public void GR09_EditTilesFillCacheTilesSixteenBySixteen()
        {
            Assert.That(new EditTileKey(0, 0).CacheTile, Is.EqualTo(new TileKey(0, 0)));
            Assert.That(new EditTileKey(15, 15).CacheTile, Is.EqualTo(new TileKey(0, 0)));
            Assert.That(new EditTileKey(16, 31).CacheTile, Is.EqualTo(new TileKey(1, 1)));
            Assert.That(EditTileKey.Of(63, 64), Is.EqualTo(new EditTileKey(0, 1)));
            Assert.That(EditTileKey.Of(-1, 0), Is.EqualTo(new EditTileKey(-1, 0)), "floor division west of the corner");
            Assert.That(EditTileKey.Metres * EditTileKey.PerCacheTile, Is.EqualTo((int)TileGrid.TileMetres));
            Assert.That(new EditTileKey(9, 0).CompareTo(new EditTileKey(0, 1)), Is.LessThan(0), "row first, like TileKey");
        }

        [Test]
        public void GR09_AnAllZeroPartIsDroppedSoEqualContentGivesEqualTiles()
        {
            var key = new EditTileKey(2, 3);
            var b = new TerrainEditTile.Builder(key).SetDeltaMm(5, 6, 120).SetDeltaMm(5, 6, 0).SetCleared(1, 1, true).SetCleared(1, 1, false);
            var tile = b.Build();
            Assert.That(tile.IsEmpty, Is.True);
            Assert.That(tile, Is.EqualTo(TerrainEditTile.Empty(key)));

            var a = new TerrainEditTile.Builder(key).SetDeltaMm(63, 63, -4000).SetCleared(0, 63, true).Build();
            var c = a.Edit().Build();
            Assert.That(c, Is.EqualTo(a));
            Assert.That(c.HasHeights && c.HasClearing, Is.True);
            Assert.That(c.DeltaMm(63, 63), Is.EqualTo(-4000));
            Assert.That(c.IsCleared(0, 63), Is.True);
            Assert.That(c.IsCleared(1, 63), Is.False);
        }

        [Test]
        public void GR09_ChangesPastTheLimitAndPointsOutsideTheTileAreRefused()
        {
            var b = new TerrainEditTile.Builder(new EditTileKey(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => b.SetDeltaMm(0, 0, TerrainEditTile.DeltaLimitMm + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => b.SetDeltaMm(64, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => b.SetCleared(0, -1, true));
            Assert.DoesNotThrow(() => b.SetDeltaMm(0, 0, -TerrainEditTile.DeltaLimitMm));
        }

        [Test]
        public void GR09_SetsHoldTilesInKeyOrderWithoutEmptiesOrDuplicates()
        {
            var t1 = Heights(new EditTileKey(5, 2), 10);
            var t2 = Heights(new EditTileKey(1, 2), 20);
            var t0 = Heights(new EditTileKey(9, 1), 30);
            var set = TerrainEdits.From(new[] { t1, t2, TerrainEditTile.Empty(new EditTileKey(0, 0)), t0 });
            Assert.That(set.Tiles.Select(t => t.Key), Is.EqualTo(new[] { t0.Key, t2.Key, t1.Key }));
            Assert.Throws<ArgumentException>(() => TerrainEdits.From(new[] { t1, Heights(t1.Key, 99) }));
            Assert.That(TerrainEdits.From(new TerrainEditTile[0]), Is.SameAs(TerrainEdits.Empty));
        }

        [Test]
        public void GR09_AChangeSharesEveryTileItDidNotTouch()
        {
            var a = Heights(new EditTileKey(0, 0), 1);
            var b = Heights(new EditTileKey(1, 0), 2);
            var set = TerrainEdits.From(new[] { a, b });
            var b2 = Heights(b.Key, 3);
            var next = set.With(new[] { b2 });
            Assert.That(next.Get(a.Key), Is.SameAs(a), "untouched tiles are shared");
            Assert.That(next.Get(b.Key), Is.SameAs(b2));
            Assert.That(set.Get(b.Key), Is.SameAs(b), "the earlier set is unchanged");

            var removed = next.With(new[] { TerrainEditTile.Empty(a.Key) });
            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(removed.TryGet(a.Key, out _), Is.False);
            Assert.That(removed.Get(a.Key).IsEmpty, Is.True);
        }

        [Test]
        public void CF05_ChangedListsExactlyTheTilesToRebuild()
        {
            var a = Heights(new EditTileKey(0, 0), 1);
            var b = Heights(new EditTileKey(1, 0), 2);
            var c = Heights(new EditTileKey(2, 0), 3);
            var from = TerrainEdits.From(new[] { a, b });
            var to = from.With(new[] { TerrainEditTile.Empty(a.Key), Heights(b.Key, 2), c });
            // a removed, b rebuilt with equal content (not a change), c added.
            Assert.That(TerrainEdits.Changed(from, to), Is.EqualTo(new[] { a.Key, c.Key }));
            Assert.That(TerrainEdits.Changed(to, from), Is.EqualTo(new[] { a.Key, c.Key }));
            Assert.That(TerrainEdits.Changed(from, from), Is.Empty);
            Assert.That(TerrainEdits.Changed(TerrainEdits.Empty, from), Is.EqualTo(new[] { a.Key, b.Key }));
        }

        [Test]
        public void GR09_GlobalLookupsCrossTileEdges()
        {
            var west = new TerrainEditTile.Builder(new EditTileKey(3, 4)).SetDeltaMm(63, 0, 7).SetCleared(63, 63, true).Build();
            var east = new TerrainEditTile.Builder(new EditTileKey(4, 4)).SetDeltaMm(0, 0, 9).Build();
            var set = TerrainEdits.From(new[] { west, east });
            int row = 4 * 64;
            Assert.That(set.DeltaMmAt(4 * 64 - 1, row), Is.EqualTo(7));
            Assert.That(set.DeltaMmAt(4 * 64, row), Is.EqualTo(9), "the shared edge point belongs to the east tile");
            Assert.That(set.DeltaMmAt(4 * 64 + 1, row), Is.EqualTo(0));
            Assert.That(set.IsClearedAt(4 * 64 - 1, 5 * 64 - 1), Is.True);
            Assert.That(set.IsClearedAt(4 * 64 - 1, 5 * 64), Is.False);
            Assert.That(set.DeltaMmAt(-5, -5), Is.EqualTo(0), "no tile, no change");
        }

        [Test]
        public void CL07_UndoingAnEditRestoresByteIdenticalEdits()
        {
            var before = Scripted();
            var bytesBefore = TerrainEditBytes.Encode(before);
            var edit = before.Get(new EditTileKey(3, 5)).Edit().SetDeltaMm(10, 10, -2500).SetCleared(40, 40, false).Build();
            var after = before.With(new[] { edit, Heights(new EditTileKey(8, 8), 77) });
            Assert.That(DesignBytesSame(TerrainEditBytes.Encode(after), bytesBefore), Is.False);
            // Undo switches back to the earlier set; its bytes never changed.
            Assert.That(DesignBytesSame(TerrainEditBytes.Encode(before), bytesBefore), Is.True);
            // Rebuilding the earlier tiles from the later set gives the same bytes too.
            var rebuilt = after.With(TerrainEdits.Changed(after, before).Select(before.Get));
            Assert.That(DesignBytesSame(TerrainEditBytes.Encode(rebuilt), bytesBefore), Is.True);
        }

        [Test]
        public void SH04_CanonicalBytesRoundTripAndMatchTheirGolden()
        {
            var a = TerrainEditBytes.Encode(Scripted());
            var b = TerrainEditBytes.Encode(Scripted());
            Assert.That(DesignBytesSame(a, b), Is.True);
            var back = TerrainEditBytes.Decode(a);
            Assert.That(DesignBytesSame(TerrainEditBytes.Encode(back), a), Is.True);
            Assert.That(back.Tiles.Zip(Scripted().Tiles, (x, y) => x.Equals(y)).All(e => e), Is.True);
            Assert.That(TerrainEditBytes.Encode(TerrainEdits.Empty).Length, Is.EqualTo(10), "magic, shape and count");
            Assert.That(Sha(a), Is.EqualTo(GoldenSha), "golden terrain edit bytes");
        }

        [Test]
        public void SH04_MalformedOrNonCanonicalBytesAreRefused()
        {
            var good = TerrainEditBytes.Encode(Scripted());
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(null));
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(good.Take(good.Length - 1).ToArray()), "short");
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(good.Concat(new byte[] { 0 }).ToArray()), "trailing");
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(Patch(good, 0, (byte)'X')), "magic");
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(Patch(good, 4, 2)), "a future shape");

            // Tiles out of order: swap the first two tiles' keys.
            var swapped = (byte[])good.Clone();
            int first = 10, second = first + 9 + 4 * EditTileKey.Count + 8 * TerrainEditTile.ClearingWords;
            for (int k = 0; k < 8; k++) (swapped[first + k], swapped[second + k]) = (swapped[second + k], swapped[first + k]);
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(swapped), "out of order");

            // An all-zero heights part is not canonical.
            var zero = new byte[10 + 9 + 4 * EditTileKey.Count];
            Array.Copy(TerrainEditBytes.Encode(TerrainEdits.Empty), zero, 10);
            zero[6] = 1;
            zero[10 + 8] = 1;
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(zero), "empty heights part");

            // A change past the limit.
            var big = (byte[])good.Clone();
            int d0 = first + 9;
            big[d0] = 0xFF; big[d0 + 1] = 0xFF; big[d0 + 2] = 0xFF; big[d0 + 3] = 0x7F;
            Assert.Throws<FormatException>(() => TerrainEditBytes.Decode(big), "past the limit");
        }

        /// <summary>
        /// A small fixed edit set: a bench-like cut and fill across two tiles with a cleared strip, and a clearing-only
        /// tile beside them.
        /// </summary>
        static TerrainEdits Scripted()
        {
            var a = new TerrainEditTile.Builder(new EditTileKey(3, 5));
            var b = new TerrainEditTile.Builder(new EditTileKey(4, 5));
            for (int j = 20; j < 50; j++)
                for (int i = 0; i < 64; i++)
                {
                    int across = j - 35;                   // metres from the centreline
                    a.SetDeltaMm(i, j, across * 450 - i * 13);
                    b.SetDeltaMm(i, j, across * 450 - (i + 64) * 13);
                    a.SetCleared(i, j, true);
                    b.SetCleared(i, j, (i * 7 + j * 3) % 5 != 0);   // a ragged edge
                }
            var c = new TerrainEditTile.Builder(new EditTileKey(4, 6));
            for (int i = 0; i < 64; i += 3) c.SetCleared(i, 0, true);
            return TerrainEdits.From(new[] { c.Build(), b.Build(), a.Build() });
        }

        static TerrainEditTile Heights(EditTileKey key, int mm) => new TerrainEditTile.Builder(key).SetDeltaMm(1, 1, mm).Build();

        static bool DesignBytesSame(byte[] a, byte[] b) => Domain.Design.DesignBytes.Same(a, b);

        static byte[] Patch(byte[] b, int at, byte v)
        {
            var c = (byte[])b.Clone();
            c[at] = v;
            return c;
        }
    }
}
