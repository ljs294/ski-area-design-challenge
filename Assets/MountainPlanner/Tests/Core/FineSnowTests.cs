using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 4.07 PR B1: SN-03 v3's fine field and how it shares the 8 m cell's hour (SN-28, SN-30 v2). Engine-free.
    public sealed class FineSnowTests
    {
        const int Grid = 128;
        const long Origin = -512_000;

        static TrailSnowLayout Layout(int widthMm = 30_000, int cap = 2_000_000)
        {
            var stroke = new Stroke(widthMm, false, new[] { new PointMm(0, -200_000), new PointMm(0, 200_000) });
            int row0 = int.MaxValue, rowEnd = int.MinValue;
            TrailMask.Builder.RowsOf(stroke, ref row0, ref rowEnd);
            var b = new TrailMask.Builder(row0, rowEnd);
            b.Stamp(stroke);
            var t = new TrailSnowInput
            {
                Mask = b.ToMask(), LineXMm = new[] { 0, 0 }, LineYMm = new[] { -200_000, 200_000 }, WidthMm = widthMm,
                SegmentStartMm = new[] { 0 }, SegmentEdge = new[] { 0 },
            };
            return TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { t }, cap);
        }

        // A field with water rising to the east and north, 250 kg/m³, packed powder.
        static SnowField Field()
        {
            var f = new SnowField(Grid, Grid);
            for (int j = 0; j < Grid; j++)
                for (int i = 0; i < Grid; i++)
                {
                    int c = j * Grid + i;
                    f.Pack[c] = new SnowPack { Swe = 200_000 + 1_037 * i + 553 * j, Density = 250_000 };
                    f.Type[c] = (byte)SnowType.PP; f.Age[c] = 30;
                }
            return f;
        }

        static int[] Weights(int bc, int value) { var w = new int[bc]; for (int k = 0; k < bc; k++) w[k] = value; return w; }

        [Test]
        public void SeedingKeepsEachBlocksWaterAndBlendsAcrossIt()
        {
            var L = Layout();
            var field = Field();
            var fine = new FineSnow(L);
            fine.Seed(field);
            for (int b = 0; b < L.Blocks; b++)
            {
                int cell = L.BlockCell[b];
                Assert.That(fine.BlockSum(b), Is.EqualTo((long)L.BlockCells * field.Pack[cell].Swe), $"block {b}");
                int f0 = b * L.BlockCells;
                // The east column holds more than the west (the field rises to the east).
                Assert.That(fine.Swe[f0 + L.PerSide - 1], Is.GreaterThan(fine.Swe[f0]));
                Assert.That(fine.Density[f0] / FineSnow.DensityOne, Is.EqualTo(250));
                Assert.That(fine.Type[f0], Is.EqualTo((byte)SnowType.PP));
            }
        }

        [Test]
        public void AnHourIsSharedExactlyAndPackedSnowLosesLess()
        {
            var L = Layout();
            var field = Field();
            var fine = new FineSnow(L);
            fine.Seed(field);
            int bc = L.BlockCells;
            var w = new int[bc];
            for (int k = 0; k < bc; k++) w[k] = k % 2 == 0 ? 750 : FineSnow.WeightOne;
            for (int b = 0; b < L.Blocks; b++)
            {
                int swe8 = field.Pack[L.BlockCell[b]].Swe;
                int f0 = b * bc;
                int even = fine.Swe[f0], odd = fine.Swe[f0 + 1];
                Assert.That(fine.ShareHour(b, 0, 0, 3_000, w), Is.True);
                Assert.That(fine.BlockSum(b), Is.EqualTo((long)bc * (swe8 - 3_000)), "the loss is exact");
                Assert.That(fine.BlockSum(b) / bc, Is.EqualTo(swe8 - 3_000), "the roll-up is the 8 m cell's water");
                Assert.That(even - fine.Swe[f0], Is.LessThan(odd - fine.Swe[f0 + 1]), "the packed cell lost less");
                Assert.That(fine.ShareHour(b, 1_200, 100_000, 0, w), Is.True);
                Assert.That(fine.BlockSum(b), Is.EqualTo((long)bc * (swe8 - 3_000 + 1_200)), "the gain is exact");
            }
        }

        [Test]
        public void ACellShortOfItsSharePassesTheRestOn()
        {
            var L = Layout();
            var fine = new FineSnow(L);
            int bc = L.BlockCells;
            // One block: half its cells hold 100 µm, half 10,000 µm. A loss of 2,000 µm a cell is more than the thin ones hold.
            for (int k = 0; k < bc; k++) { fine.Swe[k] = k < bc / 2 ? 100 : 10_000; fine.Density[k] = 300 * FineSnow.DensityOne; }
            long before = fine.BlockSum(0);
            Assert.That(fine.ShareHour(0, 0, 0, 2_000, Weights(bc, FineSnow.WeightOne)), Is.True);
            Assert.That(fine.BlockSum(0), Is.EqualTo(before - 2_000L * bc));
            for (int k = 0; k < bc; k++) Assert.That(fine.Swe[k], Is.GreaterThanOrEqualTo(0));
            for (int k = 0; k < bc / 2; k++) Assert.That((fine.Swe[k], fine.Density[k]), Is.EqualTo((0, (ushort)0)), "melted out");
            // More than the whole block holds is refused, and nothing changes.
            long now = fine.BlockSum(0);
            Assert.That(fine.ShareHour(0, 0, 0, (int)(now / bc) + 1, Weights(bc, FineSnow.WeightOne)), Is.False);
            Assert.That(fine.BlockSum(0), Is.EqualTo(now));
        }

        [Test]
        public void NewSnowMixesByVolumeAndTheRollUpGivesTheMeanDepth()
        {
            var L = Layout();
            var fine = new FineSnow(L);
            int bc = L.BlockCells;
            for (int k = 0; k < bc; k++) { fine.Swe[k] = 40_000 + 1_000 * k; fine.Density[k] = (ushort)((200 + 5 * k) * FineSnow.DensityOne); }
            // 10 mm of water at 100 kg/m³ (100 mm of snow) on 40 mm of water at 200 kg/m³ (200 mm): 50 mm in 300 mm, 166 kg/m³.
            fine.ShareHour(0, 10_000, 100_000, 0, Weights(bc, FineSnow.WeightOne));
            Assert.That(fine.Density[0] / (double)FineSnow.DensityOne, Is.EqualTo(166.7).Within(0.2));
            fine.RollUp(0, out int swe, out int density, out byte _);
            long depth = 0, sum = 0;
            for (int k = 0; k < bc; k++) { depth += fine.DepthUm(k); sum += fine.Swe[k]; }
            Assert.That(swe, Is.EqualTo(sum / bc));
            // The 8 m cell's depth (its water over its density) is the fine cells' mean depth, within 10 µm (the roll-up floors the water).
            Assert.That((double)swe * 1_000_000 / density, Is.EqualTo((double)depth / bc).Within(10));
        }

        [Test]
        public void FoldingKeepsTheRollUpAndReturnsTheRemainder()
        {
            var L = Layout();
            var field = Field();
            var fine = new FineSnow(L);
            fine.Seed(field);
            int cell = L.BlockCell[0], swe8 = field.Pack[cell].Swe;
            fine.Swe[3] += 5;   // a remainder of 5 fine µm
            Assert.That(fine.Fold(0, field), Is.EqualTo(5));
            Assert.That(field.Pack[cell].Swe, Is.EqualTo(swe8));
        }

        [Test]
        public void PackedSnowsWeightFallsWithDensity()
        {
            var fine = new FineSnow(Layout());
            int Weight(int rho, bool packed)
            {
                fine.Density[0] = (ushort)(rho * FineSnow.DensityOne);
                fine.Flags[0] = (byte)(packed ? FineSnow.Packed : 0);
                return fine.Weight(0, 750);
            }
            Assert.That(Weight(150, true), Is.EqualTo(1000));
            Assert.That(Weight(300, true), Is.EqualTo(875));
            Assert.That(Weight(450, true), Is.EqualTo(750));
            Assert.That(Weight(450, false), Is.EqualTo(1000), "naturally settled snow melts as A1 calibrated it");
        }

        [Test]
        public void SettlingSparesPackedSnow()
        {
            var fine = new FineSnow(Layout());
            for (int k = 0; k < 2; k++) { fine.Swe[k] = 50_000; fine.Density[k] = 300 * FineSnow.DensityOne; }
            fine.Flags[1] = FineSnow.Packed;
            fine.Settle(0, 1_100);
            Assert.That((fine.Density[0] / FineSnow.DensityOne, fine.Density[1] / FineSnow.DensityOne), Is.EqualTo((330, 330)));
            fine.Settle(0, 900);
            Assert.That((fine.Density[0] / FineSnow.DensityOne, fine.Density[1] / FineSnow.DensityOne), Is.EqualTo((297, 330)));
        }

        [Test]
        public void TheRollUpsTypeIsTheMostCommon()
        {
            var L = Layout();
            var fine = new FineSnow(L);
            int bc = L.BlockCells;
            for (int k = 0; k < bc; k++)
            {
                fine.Swe[k] = 1_000; fine.Density[k] = 300 * FineSnow.DensityOne;
                fine.Type[k] = (byte)(k < bc / 2 ? SnowType.MG : SnowType.P);
            }
            fine.RollUp(0, out _, out _, out byte type);
            Assert.That(type, Is.EqualTo((byte)SnowType.P), "a tie goes to the lower type");
            fine.Type[0] = (byte)SnowType.P;
            fine.RollUp(0, out _, out _, out type);
            Assert.That(type, Is.EqualTo((byte)SnowType.P));
            fine.Type[bc - 1] = (byte)SnowType.MG; fine.Type[bc - 2] = (byte)SnowType.MG;
            fine.RollUp(0, out _, out _, out type);
            Assert.That(type, Is.EqualTo((byte)SnowType.MG));
        }
    }
}
