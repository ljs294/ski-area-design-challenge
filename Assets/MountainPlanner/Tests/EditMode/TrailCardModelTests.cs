using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI;
using MountainPlanner.UI.Tools;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 B2: what the trail card and Analysis › Trails say (<see cref="TrailCardModel"/>), no scene: a line adds up
    /// the runs it holds by length, and its Status says why it's closed.
    /// </summary>
    public sealed class TrailCardModelTests
    {
        const byte Open = SimSnapshot.TrailSnowOpen;
        static byte Snow(SnowType main, bool open = true, bool moguls = false, bool ice = false) =>
            (byte)((int)main | (open ? Open : 0) | (moguls ? 16 : 0) | (ice ? 32 : 0));

        [Test]
        public void TC01_BeforeTheSnowEveryRowReadsADash()
        {
            var m = new TrailCardModel();
            m.Add(TrailSnowLabel.None, 0, 0, 0, 0, OperatingStatus.Open, 1000);
            Assert.That(m.HasSnow, Is.False);
            Assert.That(m.Conditions(), Is.EqualTo("—"));
            Assert.That(m.ListConditions(), Is.EqualTo("—"));
            Assert.That(m.CoverText(), Is.EqualTo("—"));
            Assert.That(m.RatingText(), Is.EqualTo("—"));
            Assert.That(m.Surface(), Is.EqualTo("—"));
            Assert.That(m.ConditionsBin(), Is.EqualTo("None"));
            Assert.That(m.Status(true, TrailSwitch.Auto, true).Word, Is.EqualTo("Open"), "without trail snow the lifts' word stands");
        }

        [Test]
        public void TC02_ALineIsItsWorstRunWithTheCoverByLength()
        {
            var m = new TrailCardModel();
            m.Add(TrailSnowLabel.Covered, 0, 100, 3, Snow(SnowType.PP), OperatingStatus.Open, 3000);   // the upper 3 km
            m.Add(TrailSnowLabel.Marginal, 0, 60, 1, Snow(SnowType.HP), OperatingStatus.Open, 1000);   // the lower 1 km
            Assert.That(m.Label, Is.EqualTo(TrailSnowLabel.Marginal), "the worst run's label");
            Assert.That(m.CoverPct, Is.EqualTo(90), "(100 × 3 + 60 × 1) ÷ 4");
            Assert.That(m.Rating, Is.EqualTo(1), "the lowest rating");
            Assert.That(m.MainType, Is.EqualTo(SnowType.PP), "the longest part's surface");
            Assert.That(m.ListConditions(), Is.EqualTo("Marginal · Fair"));
            Assert.That(m.Conditions(), Is.EqualTo("Marginal"));
            Assert.That(m.Surface(), Is.EqualTo("Packed powder"));
        }

        [Test]
        public void TC03_AWalkAndTheSurfaceLine()
        {
            var m = new TrailCardModel();
            m.Add(TrailSnowLabel.Marginal, 40, 81, 1, Snow(SnowType.HP, moguls: true, ice: true), OperatingStatus.Open, 500);
            Assert.That(m.Conditions(), Is.EqualTo("Marginal · walk 40 ft"));
            Assert.That(m.ListConditions(), Is.EqualTo("Marginal · walk 40 ft"));
            Assert.That(m.Surface(), Is.EqualTo("Hard pack · bare patches · moguls · some ice"));
            Assert.That(m.CoverText(), Is.EqualTo("81%"));
        }

        [Test]
        public void TC04_StatusSaysWhyItsClosed()
        {
            var bare = new TrailCardModel();
            bare.Add(TrailSnowLabel.Bare, 0, 10, 0, Snow(SnowType.WG, open: false), OperatingStatus.Closed, 1000);
            Assert.That(bare.Status(true, TrailSwitch.Open, true), Is.EqualTo((OperatingStatus.Closed, "Closed · no snow")), "Bare closes it whatever the switch");
            Assert.That(bare.RatingText(), Is.EqualTo("—"), "a Bare trail has no rating to show");
            Assert.That(bare.Conditions(), Is.EqualTo("Bare"));

            var gap = new TrailCardModel();   // the demo's West Cat Track: one long bare stretch on a 93% covered trail
            gap.Add(TrailSnowLabel.Bare, 226, 93, 3, Snow(SnowType.P, open: false), OperatingStatus.Closed, 1000);
            Assert.That(gap.Conditions(), Is.EqualTo("Bare · walk 226 ft"), "the walk says why 93% covered is Bare");
            Assert.That(gap.ListConditions(), Is.EqualTo("Bare · walk 226 ft"));

            var covered = new TrailCardModel();
            covered.Add(TrailSnowLabel.Covered, 0, 98, 2, Snow(SnowType.MG), OperatingStatus.Open, 1000);
            Assert.That(covered.Status(true, TrailSwitch.Closed, true), Is.EqualTo((OperatingStatus.Closed, "Closed by you")));
            Assert.That(covered.Status(true, TrailSwitch.Auto, true), Is.EqualTo((OperatingStatus.Open, "Open")));
            Assert.That(covered.Status(false, TrailSwitch.Auto, true), Is.EqualTo((OperatingStatus.Planned, "Planned")));

            var waiting = new TrailCardModel();
            waiting.Add(TrailSnowLabel.Marginal, 0, 70, 1, Snow(SnowType.PP, open: false), OperatingStatus.Closed, 1000);
            Assert.That(waiting.Status(true, TrailSwitch.Auto, true).Word, Is.EqualTo("Closed · not ready"), "Auto on a Marginal trail it never opened");
            Assert.That(waiting.SwitchHint(TrailSwitch.Auto), Is.EqualTo("Auto: opens when Covered. Open opens it now."));

            var noLift = new TrailCardModel();
            noLift.Add(TrailSnowLabel.Covered, 0, 100, 3, Snow(SnowType.P), OperatingStatus.Closed, 1000);
            Assert.That(noLift.Status(true, TrailSwitch.Auto, true).Word, Is.EqualTo("Closed · no lift"), "lifts run, none reaches it");
            Assert.That(noLift.Status(true, TrailSwitch.Auto, false).Word, Is.EqualTo("Closed"), "after hours it's simply closed");
        }

        [Test]
        public void TC05_ALineIsAsOpenAsItsLeastOpenRun()
        {
            var m = new TrailCardModel();
            m.Add(TrailSnowLabel.Covered, 0, 100, 3, Snow(SnowType.PP), OperatingStatus.Open, 1000);
            m.Add(TrailSnowLabel.Covered, 0, 100, 3, Snow(SnowType.PP), OperatingStatus.Scheduled, 1000);
            Assert.That(m.LiftStatus, Is.EqualTo(OperatingStatus.Scheduled));
            long sig = m.Signature;
            m.Clear();
            Assert.That(m.HasSnow, Is.False);
            m.Add(TrailSnowLabel.Covered, 0, 100, 3, Snow(SnowType.PP), OperatingStatus.Open, 1000);
            m.Add(TrailSnowLabel.Covered, 0, 100, 3, Snow(SnowType.PP), OperatingStatus.Scheduled, 1000);
            Assert.That(m.Signature, Is.EqualTo(sig), "the same snow signs the same: no redraw");
            m.Add(TrailSnowLabel.Covered, 0, 100, 2, Snow(SnowType.PP), OperatingStatus.Open, 1000);
            Assert.That(m.Signature, Is.Not.EqualTo(sig), "a rating change redraws");
        }
    }
}
