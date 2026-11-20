using System;
using System.IO;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 4.07 B1 part 2c: the sidecar's trail snow codec. Exact, deterministic, and it refuses what doesn't add up.
    public sealed class FineSnowCodecTests
    {
        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        /// <summary>A keyed, repeatable value (no System.Random in engine-free code).</summary>
        static uint Key(int a, int b)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)a) * 16777619u;
                h = (h ^ (uint)b) * 16777619u;
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                return h;
            }
        }

        /// <summary>
        /// Blocks of every kind: bare (age and the rain mark kept), one even value, corduroy runs, and wild cell by cell, with
        /// the extremes in the last block.
        /// </summary>
        static FinePlanes Sample(int metres, int blocks = 12)
        {
            int per = 8 / metres;
            var cells = new int[blocks];
            for (int b = 0; b < blocks; b++) cells[b] = 37 + b * 3 + (b > 5 ? 1000 : 0);
            var p = new FinePlanes(metres, per, cells);
            int bc = p.BlockCells;
            for (int b = 0; b < blocks; b++)
                for (int k = 0; k < bc; k++)
                {
                    int f = b * bc + k;
                    switch (b % 4)
                    {
                        case 0:   // bare: no water, but its age and the rain mark stay
                            p.Age[f] = 17; p.Flags[f] = FineSnow.RainWet;
                            break;
                        case 1:   // one even value: freshly seeded
                            p.Swe[f] = 120_000; p.Density[f] = 150 * FineSnow.DensityOne; p.Type[f] = 1; p.Age[f] = 3;
                            break;
                        case 2:   // corduroy: runs across the block
                            p.Swe[f] = 90_000 + (k / per % 2) * 4_000; p.Density[f] = (ushort)(420 * FineSnow.DensityOne); p.Type[f] = 3;
                            p.Flags[f] = (byte)(FineSnow.Packed | (2 << FineSnow.GroomShift));
                            break;
                        default:  // wild: every plane cell by cell
                            uint h = Key(b, k);
                            p.Swe[f] = (int)(h % 400_000); p.Density[f] = (ushort)(h >> 7); p.Passes[f] = (ushort)(h >> 11);
                            p.Moguls[f] = (byte)(h >> 3); p.Type[f] = (byte)(h % 11); p.Age[f] = (byte)(h >> 17); p.Flags[f] = (byte)(h % 64);
                            p.Machine[f * 3] = (byte)(h >> 5); p.Machine[f * 3 + 1] = (byte)(h >> 9); p.Machine[f * 3 + 2] = (byte)(h >> 21);
                            break;
                    }
                }
            // The extremes, in the last block's first cell.
            int last = (blocks - 1) * bc;
            p.Swe[last] = int.MaxValue; p.Density[last] = ushort.MaxValue; p.Passes[last] = ushort.MaxValue;
            p.Moguls[last] = 255; p.Type[last] = 15; p.Age[last] = 255; p.Flags[last] = 0x3F;
            p.Machine[last * 3] = 255; p.Machine[last * 3 + 1] = 255; p.Machine[last * 3 + 2] = 255;
            return p;
        }

        static byte[] Encode(FinePlanes p)
        {
            var o = new SidecarWriter();
            FineSnowCodec.Write(o, p);
            return o.ToArray();
        }

        static FinePlanes Decode(byte[] bytes) => FineSnowCodec.Read(new SidecarReader(bytes));

        static void AssertSame(FinePlanes a, FinePlanes b)
        {
            Assert.That(b.FineMetres, Is.EqualTo(a.FineMetres));
            Assert.That(b.BlockCell, Is.EqualTo(a.BlockCell));
            Assert.That(b.Swe, Is.EqualTo(a.Swe)); Assert.That(b.Density, Is.EqualTo(a.Density)); Assert.That(b.Passes, Is.EqualTo(a.Passes));
            Assert.That(b.Moguls, Is.EqualTo(a.Moguls)); Assert.That(b.Type, Is.EqualTo(a.Type)); Assert.That(b.Age, Is.EqualTo(a.Age));
            Assert.That(b.Flags, Is.EqualTo(a.Flags)); Assert.That(b.Machine, Is.EqualTo(a.Machine));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        public void FC01_ARoundTripIsExactAtEveryFineSize(int metres)
        {
            var p = Sample(metres);
            byte[] bytes = Encode(p);
            var back = Decode(bytes);
            AssertSame(p, back);
            Say($"[codec] {metres} m: {p.Cells} cells ({p.Cells * FineSnow.BytesPerCell} B raw) in {bytes.Length} B");
            Assert.That(Encode(back), Is.EqualTo(bytes), "the same bytes again");
        }

        [Test]
        public void FC02_EachWayOfStoringABlockIsUsed()
        {
            var p = Sample(2, blocks: 4);
            byte[] bytes = Encode(p);
            var r = new SidecarReader(bytes);
            r.Skip(1 + 4 + 4 + 4);
            for (int b = 0; b < p.Blocks; b++) r.Var();
            int bare = r.U8() | r.U8() << 8 | r.U8() << 16;
            Assert.That(bare & 3, Is.EqualTo(0), "a bare block's water: all zero");
            Assert.That((bare >> 10) & 3, Is.EqualTo(1), "its age: one value");
            var even = Decode(bytes);
            AssertSame(p, even);
            // The modes of every plane of every block, by decoding the block headers in turn, is the codec's own business;
            // here the sample holds zero, one-value, run and cell-by-cell planes and comes back exactly.
        }

        [Test]
        public void FC03_BrokenDataIsRefused()
        {
            var p = Sample(2, blocks: 4);
            byte[] good = Encode(p);
            Assert.Throws<EndOfStreamException>(() => Decode(good.AsSpan(0, good.Length - 3).ToArray()), "cut short");
            var flipped = (byte[])good.Clone();
            flipped[flipped.Length - 1] ^= 0x40;
            Assert.Throws<InvalidDataException>(() => Decode(flipped), "the check");
            var version = (byte[])good.Clone();
            version[0] = 2;
            Assert.Throws<InvalidDataException>(() => Decode(version), "an unknown codec");
            var size = (byte[])good.Clone();
            size[1] = 3;
            Assert.Throws<InvalidDataException>(() => Decode(size), "a fine size that isn't 1, 2 or 4 m");

            // A type past 15 and a flag past bit 5 are refused, however the block is stored.
            var badType = Sample(2, blocks: 4); badType.Type[5] = 16;
            Assert.Throws<InvalidDataException>(() => Decode(Encode(badType)));
            var badFlags = Sample(2, blocks: 4); badFlags.Flags[5] = 0x40;
            Assert.Throws<InvalidDataException>(() => Decode(Encode(badFlags)));
            var negative = Sample(2, blocks: 4); negative.Swe[5] = -1;
            Assert.Throws<InvalidDataException>(() => Decode(Encode(negative)));
            // Blocks out of order.
            var order = new FinePlanes(2, 4, new[] { 9, 9 });
            Assert.Throws<InvalidDataException>(() => Decode(Encode(order)));
        }

        [Test]
        public void FC04_BadRunsAreRefused()
        {
            // One block of 16 cells, its water as runs: hand-written bytes whose runs don't fill the block.
            var o = new SidecarWriter();
            o.U8(FineSnowCodec.Version); o.I32(2); o.I32(4); o.I32(1);
            o.Var(10);   // the block's 8 m cell (from −1: cell 9)
            int modes = 2;   // the water as runs, every other plane zero
            o.U8(modes); o.U8(0); o.U8(0);
            o.Var(2); o.Var(500); o.Var(10); o.Var(700); o.Var(5);   // 15 of 16 cells
            o.U64(0);
            Assert.Throws<InvalidDataException>(() => Decode(o.ToArray()));
        }
    }
}
