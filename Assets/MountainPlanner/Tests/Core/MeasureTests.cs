using System.Linq;
using MountainPlanner.Domain.Measure;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Units and slope (task 12b.2): imperial by default, metric on request; steepness always a grade in percent.
    public sealed class MeasureTests
    {
        const UnitSystem Ft = UnitSystem.Imperial, M = UnitSystem.Metric;

        [Test]
        public void ElevationsReadInFeetOrMetres()
        {
            Assert.That(UnitFormat.Elevation(2633.5, Ft), Is.EqualTo("8,640 ft"));
            Assert.That(UnitFormat.Elevation(2633.5, M), Is.EqualTo("2,634 m"));
            Assert.That(UnitFormat.ElevationKey(2633.5, Ft), Is.EqualTo(8640));
        }

        [Test]
        public void SnowDepthsReadInInchesAndFeetOrCentimetres()
        {
            Assert.That(UnitFormat.SnowDepth(0.2032, Ft), Is.EqualTo("8 in"));
            Assert.That(UnitFormat.SnowDepth(0.7112, Ft), Is.EqualTo("2 ft 4 in"));
            Assert.That(UnitFormat.SnowDepth(0.9144, Ft), Is.EqualTo("3 ft"));
            Assert.That(UnitFormat.SnowDepth(0.71, M), Is.EqualTo("71 cm"));
            Assert.That(UnitFormat.SnowDepth(1.4, M), Is.EqualTo("1.4 m"));
            Assert.That(UnitFormat.SnowDepth(-0.1, M), Is.EqualTo("0 cm"));
        }

        [Test]
        public void ContoursFollowTheUsgsMountainIntervalOrTenMetres()
        {
            Assert.That(UnitFormat.ContourIntervalMetres(Ft), Is.EqualTo(12.192).Within(1e-9), "40 ft");
            Assert.That(UnitFormat.ContourIntervalMetres(M), Is.EqualTo(10));
            Assert.That(UnitFormat.ContourLabel(8200 * 0.3048, Ft), Is.EqualTo("8200"));
            Assert.That(UnitFormat.ContourLabel(2500, M), Is.EqualTo("2500"));
        }

        [Test]
        public void TheSnowDepthStopsAreRoundNumbersInEachSystem()
        {
            var ft = UnitFormat.SnowDepthStops(Ft);
            Assert.That(ft.Select(m => UnitFormat.SnowDepth(m, Ft)), Is.EqualTo(new[] { "0 in", "6 in", "1 ft 6 in", "3 ft", "6 ft", "10 ft" }));
            Assert.That(UnitFormat.SnowDepthStops(M).Select(m => UnitFormat.SnowDepth(m, M)), Is.EqualTo(new[] { "0 cm", "15 cm", "50 cm", "1 m", "2 m", "3 m" }));
        }

        [Test]
        public void ScaleBarLengthsRiseAndCarryTheirUnits()
        {
            foreach (var units in new[] { Ft, M })
            {
                var l = UnitFormat.ScaleLengths(units);
                for (int i = 1; i < l.Length; i++) Assert.That(l[i].Metres, Is.GreaterThan(l[i - 1].Metres));
            }
            Assert.That(UnitFormat.ScaleLengths(Ft).Select(l => l.Label), Does.Contain("1,000 ft").And.Contain("½ mi").And.Contain("1 mi"));
            Assert.That(UnitFormat.ScaleLengths(M).Select(l => l.Label), Does.Contain("500 m").And.Contain("2 km"));
            Assert.That(UnitFormat.SiteSize(5000, Ft), Is.EqualTo("3.1 mi"));
            Assert.That(UnitFormat.SiteSize(5000, M), Is.EqualTo("5 km"));
        }

        [Test]
        public void TrailBandsArePercentGrades()
        {
            Assert.That(SlopeBands.Of(24.9), Is.EqualTo(SlopeBands.Band.Green));
            Assert.That(SlopeBands.Of(25), Is.EqualTo(SlopeBands.Band.Blue));
            Assert.That(SlopeBands.Of(40), Is.EqualTo(SlopeBands.Band.Black));
            Assert.That(SlopeBands.Of(60), Is.EqualTo(SlopeBands.Band.DoubleBlack));
            Assert.That(SlopeBands.PercentFromDegrees(45), Is.EqualTo(100).Within(1e-9), "100% is 45°");
            Assert.That(SlopeBands.DegreesFromPercent(60), Is.EqualTo(30.96).Within(0.01));
            Assert.That(SlopeBands.PercentFromRise(-0.4), Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void BearingsNameTheirCompassPoint()
        {
            Assert.That(SlopeBands.CompassPoint(0), Is.EqualTo("N"));
            Assert.That(SlopeBands.CompassPoint(22.4), Is.EqualTo("N"));
            Assert.That(SlopeBands.CompassPoint(22.6), Is.EqualTo("NE"));
            Assert.That(SlopeBands.CompassPoint(135), Is.EqualTo("SE"));
            Assert.That(SlopeBands.CompassPoint(-10), Is.EqualTo("N"));
            Assert.That(SlopeBands.CompassPoint(300), Is.EqualTo("NW"));
        }

        // A cone 1.6 km across rising at 50% (cells of 8 m): its contours are circles.
        static float[] Cone(int n, out double centre)
        {
            double c = centre = n * 8 / 2.0;
            var e = new float[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double x = (i + 0.5) * 8 - c, z = (j + 0.5) * 8 - c;
                    e[j * n + i] = (float)(3000 - 0.5 * System.Math.Sqrt(x * x + z * z));
                }
            return e;
        }

        [Test]
        public void ContourLabelsSitOnTheLabelledLines()
        {
            var e = Cone(200, out double c);
            var labels = ContourLabels.Place(200, 200, 8, 0, 0, e, Ft);
            Assert.That(labels.Length, Is.GreaterThan(20));
            foreach (var l in labels)
            {
                double feet = l.Elevation / 0.3048;
                Assert.That(feet % 200, Is.EqualTo(0).Within(1e-3).Or.EqualTo(200).Within(1e-3), "every 200 ft");
                Assert.That(l.Text, Is.EqualTo(((int)System.Math.Round(feet)).ToString()));
                Assert.That(l.Major, Is.EqualTo(System.Math.Round(feet) % 1000 == 0));
                double r = System.Math.Sqrt((l.X - c) * (l.X - c) + (l.Z - c) * (l.Z - c));
                Assert.That(3000 - 0.5 * r, Is.EqualTo(l.Elevation).Within(3), "on its line");
                Assert.That(l.Rise, Is.EqualTo(0.5).Within(0.05));
                // Along the line: perpendicular to the way uphill (toward the centre).
                double up = System.Math.Atan2(c - l.Z, c - l.X);
                Assert.That(System.Math.Abs(System.Math.Cos(l.Angle - up)), Is.LessThan(0.15));
            }
            Assert.That(ContourLabels.Place(200, 200, 8, 0, 0, e, M).Select(l => l.Elevation % 50).All(r => r < 1e-3 || r > 50 - 1e-3), "every 50 m");
        }

        [Test]
        public void ContourLabelsAreSpacedAndRepeatable()
        {
            var e = Cone(200, out _);
            var a = ContourLabels.Place(200, 200, 8, 0, 0, e, Ft, 600);
            var b = ContourLabels.Place(200, 200, 8, 0, 0, e, Ft, 600);
            Assert.That(b.Select(l => (l.X, l.Z, l.Text)), Is.EqualTo(a.Select(l => (l.X, l.Z, l.Text))));
            foreach (var g in a.GroupBy(l => l.Text))
                foreach (var l in g)
                    Assert.That(g.Where(o => !o.Equals(l)).All(o => System.Math.Sqrt((o.X - l.X) * (o.X - l.X) + (o.Z - l.Z) * (o.Z - l.Z)) > 150),
                        "labels on one line stay apart");
            var flat = new float[50 * 50];
            for (int k = 0; k < flat.Length; k++) flat[k] = 999.9f + k % 50 * 0.01f;   // crosses 1,000 m at under 3%: the line wanders, no label
            Assert.That(ContourLabels.Place(50, 50, 8, 0, 0, flat, M), Is.Empty);
        }
    }
}
