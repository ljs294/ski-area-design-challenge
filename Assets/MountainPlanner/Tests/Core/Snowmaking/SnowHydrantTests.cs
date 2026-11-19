using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>
    /// Task 4.15's hydrant rule (owner 2026-11-19): a line laid with hydrants spaced along it also gets one at each free end, so a
    /// dead end is never bare; a player may add a hydrant anywhere else on a line; pipe past the last hydrant is ignored.
    /// </summary>
    public sealed class SnowHydrantTests
    {
        static DesignDocument Lay(SnowEnd from, SnowEnd to, int spacingMm, DesignDocument doc = null)
        {
            doc ??= DesignDocument.Empty(1);
            var route = new[] { SnowNet.Ft(0, 0), SnowNet.Ft(1000, 0) };
            var r = new AddSnowPipe(0, "Line", 8, route, from, to, spacingMm).Apply(doc);
            Assert.That(r.Accepted, Is.True, r.Reason);
            return r.Document;
        }

        [Test]
        public void ASpacedLineGetsAHydrantAtEachFreeEnd()
        {
            var doc = Lay(SnowEnd.Free(SnowNet.Ft(0, 0)), SnowEnd.Free(SnowNet.Ft(1000, 0)), 30_480);
            var net = doc.Snow;
            var pipe = net.Pipes.Single();
            var hydrants = net.HydrantsOn(pipe.Id);
            Assert.That(net.FindNode(pipe.From).Kind, Is.EqualTo(SnowNodeKind.Hydrant), "the start is free");
            Assert.That(net.FindNode(pipe.To).Kind, Is.EqualTo(SnowNodeKind.Hydrant), "the end is free");
            Assert.That(hydrants, Has.Member(net.FindNode(pipe.From)).And.Member(net.FindNode(pipe.To)));
            Assert.That(hydrants.Select(h => h.Label).Distinct().Count(), Is.EqualTo(hydrants.Count), "every hydrant has its own label");
            Assert.That(net.FindNode(pipe.To).Label, Is.EqualTo(hydrants.Max(h => h.Label)), "the far end is numbered last");
            Assert.That(net.TapsOn(pipe.Id).Count, Is.EqualTo(hydrants.Count - 2), "the ends are pipe ends, not taps");
        }

        [Test]
        public void AnUnspacedLineLeavesItsEndsAsTheyAre()
        {
            var net = Lay(SnowEnd.Free(SnowNet.Ft(0, 0)), SnowEnd.Free(SnowNet.Ft(1000, 0)), 0).Snow;
            var pipe = net.Pipes.Single();
            Assert.That(net.HydrantsOn(pipe.Id), Is.Empty);
            Assert.That(net.FindNode(pipe.To).Kind, Is.Not.EqualTo(SnowNodeKind.Hydrant));
        }

        [Test]
        public void FillHydrantsCoversTheEndHydrant()
        {
            var doc = Lay(SnowEnd.Free(SnowNet.Ft(0, 0)), SnowEnd.Free(SnowNet.Ft(1000, 0)), 30_480);
            var pipe = doc.Snow.Pipes.Single();
            var points = new System.Collections.Generic.List<PointMm>();
            AddSnowGuns.FillPoints(doc.Snow, pipe.Id, 3_048, points, new System.Collections.Generic.List<int>());
            Assert.That(points.Count, Is.EqualTo(doc.Snow.HydrantsOn(pipe.Id).Count), "one gun per hydrant, the ends included");
        }

        [Test]
        public void AHydrantCanBeAddedAnywhereOnALine()
        {
            var doc = Lay(SnowEnd.Free(SnowNet.Ft(0, 0)), SnowEnd.Free(SnowNet.Ft(1000, 0)), 0);
            var pipe = doc.Snow.Pipes.Single();
            var r = new AddSnowTap(0, SnowNodeKind.Hydrant, pipe.Id, SnowNet.Ft(437, 0)).Apply(doc);
            Assert.That(r.Accepted, Is.True, r.Reason);
            Assert.That(r.Document.Snow.HydrantsOn(pipe.Id).Single().At, Is.EqualTo(SnowNet.Ft(437, 0)));
        }
    }
}
