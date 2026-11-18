using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Water;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.14's water records (LK-10, WS-03): lake and stream names, a lake's depth and source switch, a stream's width
    /// that every dam on it reads, and renaming ponds and dams without a regrade.
    /// </summary>
    public sealed class WaterRecordTests
    {
        const ulong Lake = 0x0fed_cba9_8765_4321UL;
        const ulong Way = 0x1234_5678_9abc_def0UL;

        static readonly PointMm[] Square =
            { new PointMm(100_000, 50_000), new PointMm(140_000, 50_000), new PointMm(140_000, 90_000), new PointMm(100_000, 90_000) };

        static DesignDocument Apply(DesignDocument doc, DesignCommand c)
        {
            var r = c.Apply(doc);
            Assert.That(r.Accepted, Is.True, r.Reason);
            return r.Document;
        }

        static string Refused(DesignDocument doc, DesignCommand c)
        {
            var r = c.Apply(doc);
            Assert.That(r.Accepted, Is.False, "refused");
            Assert.That(r.Document, Is.SameAs(doc), "a refusal changes nothing");
            return r.Reason;
        }

        [Test]
        public void LK10_ALakeKeepsItsNameDepthAndSourceApart()
        {
            var doc = Apply(DesignDocument.Empty(1), new SetLakeSource(0, Lake, true, 43));
            doc = Apply(doc, new SetLake(0, Lake, "Moose Lake", 43));
            Assert.That(doc.FindLake(Lake), Is.EqualTo(new LakeRecord(Lake, "Moose Lake", 43, true)));
            doc = Apply(doc, new SetLakeSource(0, Lake, false, 43));
            Assert.That(doc.FindLake(Lake).Name, Is.EqualTo("Moose Lake"), "switching the source off keeps the name");
            Assert.That(doc.IsLakeSource(Lake), Is.False);
            Assert.That(Refused(doc, new SetLake(0, Lake, "Moose Lake", 43)), Is.EqualTo("Nothing to change."));
            Assert.That(Refused(doc, new SetLake(0, Lake, " Moose", 43)), Does.Contain("space"));
            Assert.That(Refused(doc, new SetLake(0, Lake, "Moose Lake", 3001)), Does.Contain("depth"));
            doc = Apply(doc, new SetLake(0, Lake, "", 0));
            Assert.That(doc.Lakes, Is.Empty, "no name, no depth, not a source: OpenStreetMap's again, no record");
            Assert.That(doc.FindLake(Lake).IsEmpty, Is.True);
        }

        [Test]
        public void LK10_AStreamKeepsANameAndAWidth()
        {
            var doc = Apply(DesignDocument.Empty(1), new SetStream(0, Way, "Cody Creek", 0));
            Assert.That(doc.FindStream(Way), Is.EqualTo(new StreamRecord(Way, "Cody Creek", 0)), "a name alone keeps OpenStreetMap's width");
            doc = Apply(doc, new SetStream(0, Way, "Cody Creek", 460));
            Assert.That(Refused(doc, new SetStream(0, Way, "Cody Creek", 460)), Is.EqualTo("Nothing to change."));
            Assert.That(Refused(doc, new SetStream(0, Way, "Cody Creek", -1)), Does.Contain("wide"));
            Assert.That(Refused(doc, new SetStream(0, 0, "Cody Creek", 460)), Does.Contain("no stream"));
            doc = Apply(doc, new SetStream(0, Way, "", 0));
            Assert.That(doc.Streams, Is.Empty, "back to OpenStreetMap's: no record");
        }

        [Test]
        public void WS03_EveryDamOnAStreamReadsItsOneWidth()
        {
            var doc = Apply(DesignDocument.Empty(1),
                new AddDam(0, "Twin Dam", new PointMm(-200_000, 10_000), new PointMm(-170_000, 12_000), 2_050_250, true, SpillBank.First, Way, false, 457));
            Assert.That(doc.FindStream(Way).WidthCm, Is.EqualTo(457), "the dam's review set the stream's width");
            doc = Apply(doc, new AddDam(0, "Lower Dam", new PointMm(-200_000, -90_000), new PointMm(-170_000, -88_000), 2_000_000, true, SpillBank.First, Way));
            Assert.That(doc.FindStream(Way).WidthCm, Is.EqualTo(457), "−1 leaves the width alone");
            var lower = doc.Dams.Single(d => d.Name == "Lower Dam");
            doc = Apply(doc, new EditDam(0, lower, 300));
            Assert.That(doc.FindStream(Way).WidthCm, Is.EqualTo(300), "an edit sets it for both dams");
            Assert.That(Refused(doc, new EditDam(0, lower.WithName("Low Dam"), 200_000)), Does.Contain("wide"), "a bad width refuses the whole edit");
            doc = Apply(doc, new AddDam(0, "No Stream", new PointMm(300_000, 10_000), new PointMm(330_000, 12_000), 2_000_000, true, SpillBank.First, 0, false, 600));
            Assert.That(doc.Streams.Select(s => s.Way), Is.EqualTo(new[] { Way }), "a dam without a stream line sets no record");
        }

        [Test]
        public void LK10_AnOutlineGoesRoundTheWaterAndItsIslands()
        {
            // A 4 × 3 lake with an island in its middle row, and a lone cell apart: three loops.
            string[] map = { "####..#", "#.##...", "####..." };
            var loops = WaterOutline.Trace(0, 0, 6, 2, (i, j) => map[j][i] == '#');
            Assert.That(loops.Count, Is.EqualTo(3));
            Assert.That(loops[0], Is.EqualTo(new[] { 0, 0, 4, 0, 4, 3, 0, 3 }), "the shore: four corners, straight runs collapsed");
            Assert.That(loops.Any(l => l.SequenceEqual(new[] { 6, 0, 7, 0, 7, 1, 6, 1 })), Is.True, "the lone cell");
            Assert.That(loops.Any(l => l.Length == 8 && l.Contains(1) && l.Contains(2)), Is.True, "the island's hole");
            Assert.That(WaterOutline.Trace(0, 0, 2, 2, (i, j) => false), Is.Empty);
        }

        [Test]
        public void LK10_RenamingAPondOrADamMovesNoGround()
        {
            var doc = Apply(DesignDocument.Empty(1), new AddPond(0, "Base Pond", Square, 2_075_000, 3_048));
            doc = Apply(doc, new AddDam(0, "Twin Dam", new PointMm(-200_000, 10_000), new PointMm(-170_000, 12_000), 2_050_250, true, SpillBank.First, Way));
            var pond = doc.Ponds.Single();
            var dam = doc.Dams.Single();
            doc = Apply(doc, new RenameWater(0, pond.Id.Value, "Snowmaking Pond"));
            doc = Apply(doc, new RenameWater(0, dam.Id.Value, "Upper Dam"));
            Assert.That(doc.Ponds.Single().Name, Is.EqualTo("Snowmaking Pond"));
            Assert.That(doc.Ponds.Single().Ring, Is.EqualTo(pond.Ring));
            Assert.That(doc.Ponds.Single().FullPoolMm, Is.EqualTo(pond.FullPoolMm));
            Assert.That(doc.Dams.Single().Name, Is.EqualTo("Upper Dam"));
            Assert.That(doc.Dams.Single().SameWork(dam), Is.True, "the same work: no regrade");
            Assert.That(Refused(doc, new RenameWater(0, dam.Id.Value, "Upper Dam")), Is.EqualTo("Nothing to change."));
            Assert.That(Refused(doc, new RenameWater(0, dam.Id.Value, "")), Does.Contain("empty"));
            Assert.That(Refused(doc, new RenameWater(0, 0xdead_beefUL, "X")), Is.Not.Empty);
        }
    }
}
