using MountainPlanner.Domain.Measure;
using MountainPlanner.Domain.Water;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.09 PR B: the screen's side of the lakes (LakeSurfaces) and the WaterBodies seam's resize and override.</summary>
    public sealed class LakeSurfacesTests
    {
        // A 9 × 3 grid, row 1: the streams at x 1, lake A (index 1) at x 3–4 (and x 3–4 of row 2), lake B (index 2) at x 7.
        //   row 2: . . . A A . . . .
        //   row 1: . S . A A . . B .
        //   row 0: . . . . . . . . .
        static WaterBodyTable Table(int bCells = 1)
        {
            const int w = 9, d = 3;
            var mask = new byte[w * d];
            mask[1 * w + 1] = 1;
            mask[1 * w + 3] = mask[1 * w + 4] = mask[2 * w + 3] = mask[2 * w + 4] = 2;
            mask[1 * w + 7] = 3;
            var bodies = new[]
            {
                new WaterBody(WaterBodyKind.Streams, 0, 1, -1, 0, 0, 1000, 0, 10, false),
                new WaterBody(WaterBodyKind.Lake, 0xA, 4, -1, 0, 0, 1000, 9000, 50, false),
                new WaterBody(WaterBodyKind.Lake, 0xB, bCells, -1, 0, 0, 1000, 4500, 30, false),
            };
            return WaterBodyTable.Of(w, d, 0, 0, mask, bodies, 4);
        }

        [Test]
        public void TheSimulationsUnitsBecomeMetres()
        {
            var s = LakeSurfaces.Surface(WaterSurfaceState.SnowCoveredIce, 1200, 60);
            Assert.That(s.IceMetres, Is.EqualTo(0.3048f).Within(1e-5f), "12 in of ice");
            Assert.That(s.SnowMetres, Is.EqualTo(0.1524f).Within(1e-5f), "6 in of snow");
            Assert.That(LakeSurfaces.Surface(WaterSurfaceState.OpenWater, 50, 10).IceMetres, Is.Zero, "open water carries no ice");
        }

        [Test]
        public void TheMapGrowsOneCellAndALakeBeatsTheStreams()
        {
            var t = Table();
            var m = LakeSurfaces.Dilated(t);
            const int w = 9;
            Assert.That(m[1 * w + 2], Is.EqualTo(2), "between the streams and lake A: the lake");
            Assert.That(m[1 * w + 0], Is.EqualTo(1), "beside the streams alone: the streams");
            Assert.That(m[1 * w + 5], Is.EqualTo(2), "beside lake A only");
            Assert.That(m[1 * w + 6], Is.EqualTo(3), "beside lake B only");
            Assert.That(m[0 * w + 6], Is.EqualTo(3), "diagonal counts");
            Assert.That(m[1 * w + 3], Is.EqualTo(2), "a body cell keeps its body");
            Assert.That(t.Mask[1 * w + 2], Is.Zero, "the table itself is untouched");
        }

        [Test]
        public void BetweenTwoLakesTheLargerWinsThenTheLowerIndex()
        {
            // Lake B moved next to A's column 4 through x 6: the cell at x 5 touches both.
            const int w = 9;
            var bigger = Table(bCells: 9);
            var mask = (byte[])bigger.Mask.Clone();
            mask[1 * w + 7] = 0;
            mask[1 * w + 6] = 3;
            var bodies = new[] { bigger[0], bigger[1], bigger[2] };
            var t = WaterBodyTable.Of(w, 3, 0, 0, mask, bodies, 4);
            Assert.That(LakeSurfaces.Dilated(t)[1 * w + 5], Is.EqualTo(3), "B (9 cells) beats A (4)");
            var same = WaterBodyTable.Of(w, 3, 0, 0, mask, new[] { bigger[0], bigger[1], new WaterBody(WaterBodyKind.Lake, 0xB, 4, -1, 0, 0, 1000, 4500, 30, false) }, 4);
            Assert.That(LakeSurfaces.Dilated(same)[1 * w + 5], Is.EqualTo(2), "a tie goes to the lower index");
        }

        [Test]
        public void APondAddedLaterIsInTheMap()
        {
            const int w = 9;
            var t = Table().WithPond(0xC, new[] { 0 * w + 8 }, 40, 1000);
            var m = LakeSurfaces.Dilated(t);
            int pond = t.IndexOf(0xC);
            Assert.That(pond, Is.GreaterThan(2));
            Assert.That(m[0 * w + 8], Is.EqualTo(pond + 1), "the pond's own cell");
        }

        [Test]
        public void TheReadoutSaysTheBodysIceSnowAndDates()
        {
            Assert.That(LakeSurfaces.Readout(WaterBodyKind.Lake, WaterSurfaceState.SnowCoveredIce, 1400, 60, 12, 3, 0, 0, UnitSystem.Imperial),
                        Is.EqualTo("Lake · 14 in ice · 6 in snow · froze Dec 3"));
            Assert.That(LakeSurfaces.Readout(WaterBodyKind.Lake, WaterSurfaceState.OpenWater, 0, 0, 12, 3, 4, 12, UnitSystem.Imperial),
                        Is.EqualTo("Lake · open water · opened Apr 12"));
            Assert.That(LakeSurfaces.Readout(WaterBodyKind.Pond, WaterSurfaceState.OpenWater, 0, 0, 0, 0, 0, 0, UnitSystem.Imperial),
                        Is.EqualTo("Pond · open water · not frozen yet"));
            Assert.That(LakeSurfaces.Readout(WaterBodyKind.Streams, WaterSurfaceState.Ice, 400, 0, 10, 20, 0, 0, UnitSystem.Metric),
                        Is.EqualTo("Stream · 10 cm ice · froze Oct 20"));
        }

        [Test]
        public void AResizeKeepsTheBodiesAndTheOverride()
        {
            var w = new WaterBodies();
            var open = new WaterSurface(WaterSurfaceState.OpenWater, 0, 0);
            w.Set(0, open);
            int v = w.Version;
            w.Resize(3);
            Assert.That(w.Version, Is.GreaterThan(v));
            Assert.That(w[0], Is.EqualTo(open), "kept");
            Assert.That(w[2], Is.EqualTo(WaterSurface.IterationOne), "new bodies: iteration 1's look");

            var ice = new WaterSurface(WaterSurfaceState.Ice, 0.4f, 0);
            w.SetOverride(ice);
            Assert.That(w[0], Is.EqualTo(ice));
            w.Resize(5);
            Assert.That(w[4], Is.EqualTo(ice), "a body added under an override takes it");
            w.SetOverride(null);
            Assert.That(w.Override, Is.Null);
            Assert.That(w[4], Is.EqualTo(ice), "clearing the override leaves the surfaces until the simulation sets them");
        }
    }
}
