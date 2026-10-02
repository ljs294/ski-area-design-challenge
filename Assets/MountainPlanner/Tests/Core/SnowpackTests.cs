using System;
using System.Linq;
using MountainPlanner.Domain.Snow;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // The natural snowpack v0 (task 12b): each factor on a synthetic grid, plus determinism.
    public sealed class SnowpackTests
    {
        const double Cell = 8;

        static float[] Depths(int w, int h, Func<int, int, double> elevation, Func<int, int, double> canopy = null, double convergence = 0)
        {
            var e = new float[w * h];
            var c = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    e[y * w + x] = (float)elevation(x, y);
                    c[y * w + x] = canopy == null ? 0 : (float)canopy(x, y);
                }
            var d = new float[w * h];
            Snowpack.Compute(w, h, Cell, e, c, convergence, d);
            return d;
        }

        /// <summary>An east-west ridge with 25° flanks: y below 20 faces south, above faces north.</summary>
        static double Ridge(int x, int y) => 2000 - Math.Abs(y - 20) * Cell * Math.Tan(25 * Math.PI / 180);

        [Test]
        public void HigherGroundHoldsMoreSnow()
        {
            // A gentle ramp (under 2°, so it faces nowhere): 12 in at the bottom, the summit depth at the top.
            var d = Depths(60, 5, (x, y) => 1000 + x * 0.25);
            Assert.That(d[2 * 60 + 1], Is.EqualTo(Snowpack.ValleyMetres).Within(0.02));
            Assert.That(d[2 * 60 + 58], Is.EqualTo(Snowpack.SummitMetres).Within(0.05));
            for (int x = 1; x < 60; x++) Assert.That(d[2 * 60 + x], Is.GreaterThanOrEqualTo(d[2 * 60 + x - 1]), $"never less snow uphill ({x})");
        }

        [Test]
        public void SouthFacesHoldLessThanNorthFaces()
        {
            var d = Depths(10, 41, Ridge);
            // Cells eight below and above the crest: the same height, away from the ridge's wind scouring.
            float south = d[12 * 10 + 5], north = d[28 * 10 + 5];
            Assert.That(north, Is.GreaterThan(south * 1.2f), $"north {north:F2} m, south {south:F2} m");
        }

        [Test]
        public void AspectIsMeasuredFromTrueNorth()
        {
            // With true north 90° clockwise from grid north (convergence 90°), the ridge's grid-north face looks
            // true west and its grid-south face true east: no longer north against south, so the two match.
            var d = Depths(10, 41, Ridge, convergence: 90);
            Assert.That(d[28 * 10 + 5], Is.EqualTo(d[12 * 10 + 5]).Within(0.001));
        }

        [Test]
        public void SteepFacesShedTheirSnow()
        {
            double Face(int x, int y) => 3000 - y * Cell * Math.Tan(65 * Math.PI / 180);   // a 65° face
            var d = Depths(5, 20, Face);
            Assert.That(d[10 * 5 + 2], Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void ForestHoldsSomeSnowInItsBranches()
        {
            var open = Depths(9, 9, (x, y) => 1500);
            var forest = Depths(9, 9, (x, y) => 1500, (x, y) => 1);
            Assert.That(forest[40] / open[40], Is.EqualTo(1 - Snowpack.CanopyLoss).Within(1e-4));
        }

        [Test]
        public void WindScoursRidgesAndLoadsHollows()
        {
            var d = Depths(21, 21, (x, y) => 1500 + (x == 5 && y == 10 ? 8 : 0) - (x == 15 && y == 10 ? 8 : 0));
            float flat = d[3 * 21 + 10], knoll = d[10 * 21 + 5], pit = d[10 * 21 + 15];
            Assert.That(knoll, Is.LessThan(flat), "a knoll is scoured");
            Assert.That(pit, Is.GreaterThan(flat), "a hollow is loaded");
        }

        [Test]
        public void MissingDataGetsTheValleyDepthAndNoNaNs()
        {
            var d = Depths(12, 12, (x, y) => x < 3 ? double.NaN : 1800 + y * 2);
            Assert.That(d.All(v => !float.IsNaN(v) && v >= 0));
            Assert.That(d[5 * 12 + 1], Is.EqualTo((float)Snowpack.ValleyMetres));
        }

        [Test]
        public void TheSameTerrainAlwaysGetsTheSameSnow()
        {
            double Hills(int x, int y) => 1800 + 120 * Math.Sin(x * 0.11) * Math.Cos(y * 0.07) + 3 * x;
            var a = Depths(80, 70, Hills, (x, y) => (x * 7 + y * 3) % 10 / 10.0, 8.95);
            var b = Depths(80, 70, Hills, (x, y) => (x * 7 + y * 3) % 10 / 10.0, 8.95);
            Assert.That(b, Is.EqualTo(a));
        }

        [Test]
        public void TheFieldTakesAWholeModelAndMarksItAllChanged()
        {
            var field = new SnowDepthField(4, 3, 8);
            field.TakeDirty();
            var depths = Enumerable.Range(0, 12).Select(i => i * 0.1f).ToArray();
            field.CopyFrom(depths);
            Assert.That(field[3, 2], Is.EqualTo(1.1f).Within(1e-6));
            Assert.That(field.TakeDirty(), Is.EqualTo((0, 0, 4, 3)));
            depths[0] = -1;
            Assert.Throws<ArgumentOutOfRangeException>(() => field.CopyFrom(depths));
            Assert.Throws<ArgumentException>(() => field.CopyFrom(new float[5]));
        }
    }
}
