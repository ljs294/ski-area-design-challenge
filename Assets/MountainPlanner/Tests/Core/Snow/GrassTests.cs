using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.29 PR A (SN-35, SN-36, SN-37): a trail's grass. A new trail starts at its meadow's height (natural) or seeded
    /// (graded); summers grow it, a packed winter kills it, a closed one only flattens it; tall grass needs more snow to
    /// cover; Every autumn mows it once from September 15 until the trail is first covered; the save keeps it.
    /// </summary>
    public sealed class GrassTests
    {
        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        static readonly Pacing P = Pacing.Default;

        /// <summary>One double's green as a layout run (natural unless <paramref name="graded"/>) under an even seed (mm).</summary>
        static SimWorld Net(int seedMm, bool graded = false)
        {
            var w = TrailSnowWorldTests.World(seedMm, null, design: GuestFixture.OneDouble());
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, TrailSnowWorldTests.NetLayout(w, graded)));
            w.JoinSnow();
            return w;
        }

        /// <summary><see cref="Net"/> with every trail cell bared and rated.</summary>
        static SimWorld Bare(bool graded = false)
        {
            var w = Net(600, graded);
            TrailSnowWorldTests.Strip(w, (along, lat) => true);
            w.RateTrailsNow();
            return w;
        }

        static void Mow(SimWorld w, bool on)
        {
            var id = w.FineSnow.Layout.TrailId[0];
            var runs = new List<KeyValuePair<ulong, TrailMowing>> { new KeyValuePair<ulong, TrailMowing>(id, on ? TrailMowing.EveryAutumn : TrailMowing.Off) };
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailMowing, 0, SimMowing.From(runs)));
        }

        static int MowFaceDay(SimWorld w)
        {
            int j = 0;
            while (w.Calendar.CalendarOffset(j) < P.MowDay) j++;
            return j;
        }

        /// <summary>A label year of face days, October 1 to September 30, on the trail's cover as it stands.</summary>
        static void Year(SimWorld w)
        {
            for (int j = 0; j < w.Calendar.YearFaceDays; j++) w.GrassFaceDay(j);
        }

        /// <summary>
        /// The v15 sidecar fixture's world: <see cref="TrailSnowWorldTests.GroomSaveWorld"/>'s groomed run, mown every
        /// autumn from the start, saved part way through its third night (its grass, packed days and flags in the bytes).
        /// </summary>
        internal static SimWorld GrassSaveWorld()
        {
            var w = TrailSnowWorldTests.World(600, null, design: GuestFixture.OneDouble());
            var layout = TrailSnowWorldTests.GroomedLayout(w);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            TrailSnowWorldTests.Groomers(w, layout);
            Mow(w, true);
            long end = w.FaceMs + 3 * SimWorld.DayMs;   // three days, as GroomSaveWorld runs them
            while (w.FaceMs < end) w.Step(P.IdleTickFactor);
            w.JoinSnow();
            while (w.HourIndex < 0 || w.HourIndex % 24 != 2) w.Step(1);
            long at = w.FaceMs + SimWorld.HourMs / 3;
            while (w.FaceMs < at) w.Step(1);
            w.JoinSnow();
            return w;
        }

        [Test]
        public void SN35_ANewTrailStartsAtItsMeadowOrSeeded()
        {
            var natural = Net(600);
            var graded = Net(600, graded: true);
            natural.BoardTrailsNow();   // the first hour's board maps the layout's trails onto the network's
            var s = new SimSnapshot(natural.Pacing);
            natural.Write(s);
            Say($"[new] natural {natural.GrassPermilleOf(0)} ‰ (snapshot {s.TrailGrass[0]} %), graded {graded.GrassPermilleOf(0)} ‰");
            Assert.That(natural.GrassPermilleOf(0), Is.EqualTo(500), "the meadow it was cut through");
            Assert.That(graded.GrassPermilleOf(0), Is.EqualTo(0), "the earthwork stripped it and it was seeded");
            Assert.That(s.TrailGrass[0], Is.EqualTo(50), "the snapshot's %");
            // SN-36: day one needs what it did before task 4.29.
            Assert.That(SimWorld.CoverNeededMm(P, graded: false, 500), Is.EqualTo(305), "natural: 12 in");
            Assert.That(SimWorld.CoverNeededMm(P, graded: true, 0), Is.EqualTo(152), "graded: 6 in");
        }

        [Test]
        public void SN35_SummersGrowItToBrushWhenNobodyMowsOrSkis()
        {
            var w = Bare();
            var seen = new List<int> { w.GrassPermilleOf(0) };
            for (int y = 0; y < 2; y++) { Year(w); seen.Add(w.GrassPermilleOf(0)); }
            var m = Bare();
            m.SetGrass(0, P.GrassMownPermille, 0);
            Year(m);
            Say($"[grow] new natural {string.Join(" → ", seen)} ‰ by each September 30; mown {P.GrassMownPermille} → {m.GrassPermilleOf(0)} ‰");
            Assert.That(seen[1], Is.InRange(800, 900), "one closed winter (×0.8), then a summer's growth");
            Assert.That(seen[2], Is.EqualTo(1000), "a second year: brush");
            Assert.That(m.GrassPermilleOf(0), Is.InRange(500, 560), "mown grass is long again after one summer");
        }

        [Test]
        public void SN35_APackedWinterKillsItAClosedOneOnlyFlattensIt()
        {
            int AfterSpring(int packed)
            {
                var w = Bare();
                w.SetGrass(0, 800, packed);
                for (int j = 0; j <= w.Calendar.April1FaceDay; j++) w.GrassFaceDay(j);
                Assert.That(w.GrassPackedDaysOf(0), Is.Zero, "the count starts again");
                return w.GrassPermilleOf(0);
            }
            int killed = AfterSpring(P.GrassKillPackedDays), kept = AfterSpring(P.GrassKillPackedDays - 1);
            Say($"[winter] 800 ‰ after {P.GrassKillPackedDays} packed days: {killed} ‰; after {P.GrassKillPackedDays - 1}: {kept} ‰ (April 1, one day's growth)");
            Assert.That(killed, Is.EqualTo(P.GrassKilledPermille + P.GrassGrowPermilleDay), "packed snow killed it");
            Assert.That(kept, Is.EqualTo(800 * P.GrassKeptPermille / 1000 + P.GrassGrowPermilleDay), "loose snow only flattened it");
        }

        [Test]
        public void SN35_ACoveredTrailOpenToSkiersIsPackedEachDay()
        {
            var open = Net(600);
            var closed = Net(600);
            open.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            foreach (var w in new[] { open, closed })
                for (int d = 0; d < 3; d++)
                {
                    w.BoardTrailsNow();   // the board opens the run
                    w.BoardTrailsNow();   // the next hour's ratings see it open
                    w.GrassFaceDay(5 + d);
                }
            Say($"[packed] open mountain {open.GrassPackedDaysOf(0)} days, closed {closed.GrassPackedDaysOf(0)}");
            Assert.That(open.GrassPackedDaysOf(0), Is.EqualTo(3));
            Assert.That(closed.GrassPackedDaysOf(0), Is.Zero, "nobody skied it");
        }

        [Test]
        public void SN36_TallGrassNeedsMoreSnowToCover()
        {
            var w = Net(280);
            var labels = new List<string>();
            foreach (int g in new[] { 100, 500, 1000 })
            {
                w.SetGrass(0, g, 0);
                w.RateTrailsNow();
                labels.Add($"{g} ‰ needs {SimWorld.CoverNeededMm(P, false, g)} mm: {w.TrailLabelOf(0)} ({w.TrailCoverPermilleOf(0)} ‰ covered)");
            }
            Say("[cover] 280 mm on a natural trail: " + string.Join("; ", labels));
            w.SetGrass(0, 100, 0); w.RateTrailsNow();
            Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Covered), "kept mown, 11 in covers it");
            w.SetGrass(0, 1000, 0); w.RateTrailsNow();
            Assert.That(w.TrailLabelOf(0), Is.Not.EqualTo(TrailSnowLabel.Covered), "brush needs 15 in");
        }

        [Test]
        public void SN37_EveryAutumnMowsOnceFromSeptember15()
        {
            var w = Bare();
            Mow(w, true);
            Assert.That(w.GrassMowsOf(0), Is.True);
            int mowJ = MowFaceDay(w);
            w.SetGrass(0, 600, 0);
            w.GrassFaceDay(w.Calendar.April1FaceDay);       // spring: this year's winter acts
            w.GrassFaceDay(w.Calendar.April1FaceDay + 20);  // June: no mowing
            int june = w.GrassPermilleOf(0);
            w.GrassFaceDay(mowJ - 1);
            int before = w.GrassPermilleOf(0);
            w.GrassFaceDay(mowJ);
            int mown = w.GrassPermilleOf(0);
            w.GrassFaceDay(mowJ + 1);
            int after = w.GrassPermilleOf(0);
            w.GrassFaceDay(0); w.GrassFaceDay(1);                // October: already mown this autumn
            Say($"[mow] June {june} ‰, Sept 14 {before} ‰, mown at face day {mowJ}: {mown} ‰, next day {after} ‰, October {w.GrassPermilleOf(0)} ‰");
            Assert.That(june, Is.GreaterThan(P.GrassMownPermille), "June isn't mown");
            Assert.That(mown, Is.EqualTo(P.GrassMownPermille), "the day's growth, then mown");
            Assert.That(w.GrassMownOf(0), Is.True);
            Assert.That(after, Is.EqualTo(mown + P.GrassGrowPermilleDay), "mown once: it grows on");
            Assert.That(w.GrassPermilleOf(0), Is.EqualTo(after), "no second mow in October, and no growth before spring");
        }

        [Test]
        public void SN37_SwitchedOnInABareOctoberItMowsTheNextDay_ButATrailUnderSnowWaits()
        {
            var bare = Bare();
            Mow(bare, true);
            bare.GrassFaceDay(3);
            var snowy = Net(600);
            Mow(snowy, true);
            snowy.RateTrailsNow();
            snowy.GrassFaceDay(MowFaceDay(snowy));
            snowy.GrassFaceDay(3);
            Say($"[mow] bare October: {bare.GrassPermilleOf(0)} ‰; under snow: {snowy.GrassPermilleOf(0)} ‰");
            Assert.That(bare.GrassPermilleOf(0), Is.EqualTo(P.GrassMownPermille));
            Assert.That(snowy.GrassPermilleOf(0), Is.EqualTo(500), "covered: it waits for next year");
            Mow(bare, false);
            Assert.That(bare.GrassMowsOf(0), Is.False, "Off again");
        }

        [Test]
        public void SN35_ASaveKeepsEachTrailsGrassAndHashesTheSame()
        {
            var w = TrailSnowWorldTests.World(600, null, design: GuestFixture.OneDouble());
            var layout = TrailSnowWorldTests.GroomedLayout(w);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            Mow(w, true);
            long end = w.FaceMs + SimWorld.DayMs + SimWorld.HourMs / 2;   // a day and a half hour on, as the other save tests are
            while (w.FaceMs < end) w.Step(P.IdleTickFactor);
            w.JoinSnow();
            w.SetGrass(0, 734, 3);
            byte[] bytes = w.SaveState();
            var back = TrailSnowWorldTests.Reopen(bytes);
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()), "the same state before its layout arrives");
            back.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            Say($"[save] back: {back.GrassPermilleOf(0)} ‰, {back.GrassPackedDaysOf(0)} packed days, mows {back.GrassMowsOf(0)}");
            Assert.That(back.GrassPermilleOf(0), Is.EqualTo(734));
            Assert.That(back.GrassPackedDaysOf(0), Is.EqualTo(3));
            Assert.That(back.GrassMowsOf(0), Is.True, "the setting as saved until the design's is resent");
        }

        /// <summary>
        /// Demo 155: two label years on the small mountain with the golden (Jackson Hole) weather, the mountain open both
        /// winters. The test mountain gets too little natural snow to cover a trail, so the snow is set each face day: bare
        /// to November 1, then 8 mm deeper a face day to 500 mm, bare again from April 1. Two natural twins side by side; the first mown every autumn, the second never. Each
        /// one's grass on every October 1 and its first Covered day each winter; the mown one should open first in year 2.
        /// </summary>
        [Test, Explicit("Two label years (a few minutes): demo 155"), Category("Report")]
        public void SN37_TwoYearsMownBesideUnmown()
        {
            var w = TrailSnowWorldTests.World(0, null, design: GroomingTests.TwoRunDesign(false));
            var layout = TrailSnowWorldTests.TwoRunLayout(w);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            var runs = new List<KeyValuePair<ulong, TrailMowing>>
            {
                new KeyValuePair<ulong, TrailMowing>(layout.TrailId[0], TrailMowing.EveryAutumn),
                new KeyValuePair<ulong, TrailMowing>(layout.TrailId[1], TrailMowing.Off),
            };
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailMowing, 0, SimMowing.From(runs)));
            int yearDays = w.Calendar.YearFaceDays, nov1 = w.Calendar.FaceDayHolding(31);
            var firstCovered = new int[2, 2];
            for (int y = 0; y < 2; y++) for (int t = 0; t < 2; t++) firstCovered[y, t] = -1;
            var lines = new List<string>();
            var csv = new System.Text.StringBuilder("day,year,month,dom,snow_mm,mown_grass,unmown_grass,mown_need_mm,unmown_need_mm,mown_label,unmown_label,mown_depth_mm,unmown_depth_mm\n");
            for (int day = 0; day < 2 * yearDays; day++)
            {
                int y = day / yearDays, j = day % yearDays;
                if (j == 0)
                {
                    lines.Add($"Year {y + 1}, October 1: mown {w.GrassPermilleOf(0) / 10} %, unmown {w.GrassPermilleOf(1) / 10} % " +
                              $"(needs {SimWorld.CoverNeededMm(P, false, w.GrassPermilleOf(0)) / 25.4:0.0} in and {SimWorld.CoverNeededMm(P, false, w.GrassPermilleOf(1)) / 25.4:0.0} in)");
                }
                int depth = j >= nov1 && j < w.Calendar.April1FaceDay ? Math.Min(500, 8 * (j - nov1 + 1)) : 0;
                if (j >= nov1 && j <= w.Calendar.April1FaceDay) SetDepth(w, depth);
                long end = (long)(day + 1) * SimWorld.DayMs;
                while (w.FaceMs < end) w.Step(P.IdleTickFactor);
                w.JoinSnow();
                if (!w.Open) w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));   // October 1 closes it (TM-11 v2)
                for (int t = 0; t < 2; t++)
                    if (firstCovered[y, t] < 0 && j < w.Calendar.April1FaceDay && w.TrailLabelOf(t) == TrailSnowLabel.Covered) firstCovered[y, t] = j;
                csv.Append($"{day},{y + 1},{w.Calendar.Month(j)},{w.Calendar.Day(j)},{depth},{w.GrassPermilleOf(0)},{w.GrassPermilleOf(1)}," +
                           $"{SimWorld.CoverNeededMm(P, false, w.GrassPermilleOf(0))},{SimWorld.CoverNeededMm(P, false, w.GrassPermilleOf(1))},{w.TrailLabelOf(0)},{w.TrailLabelOf(1)},{MeanDepthMm(w, 0)},{MeanDepthMm(w, 1)}\n");
                if (j == w.Calendar.April1FaceDay + 1)
                    lines.Add($"Year {y + 1}, April: mown {w.GrassPermilleOf(0) / 10} %, unmown {w.GrassPermilleOf(1) / 10} % after the winter");
            }
            string Day(int j) => j < 0 ? "never" : $"face day {j} ({w.Calendar.Month(j)}/{w.Calendar.Day(j)}, {Math.Min(500, 8 * (j - nov1 + 1)) / 25.4:0.0} in of snow)";
            for (int y = 0; y < 2; y++)
                lines.Add($"Year {y + 1} first Covered: mown {Day(firstCovered[y, 0])}, unmown {Day(firstCovered[y, 1])}");
            lines.Add($"Year 3, October 1: mown {w.GrassPermilleOf(0) / 10} %, unmown {w.GrassPermilleOf(1) / 10} %");
            foreach (var l in lines) Say("[two years] " + l);
            string file = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "grass-two-years.csv");
            System.IO.File.WriteAllText(file, csv.ToString());
            Say("[two years] every face day: " + file);
            Assert.That(w.GrassPermilleOf(0), Is.LessThan(w.GrassPermilleOf(1)), "the mown trail is shorter");
            Assert.That(firstCovered[1, 0], Is.GreaterThanOrEqualTo(0), "the mown trail opens in year 2");
            Assert.That(firstCovered[1, 1] < 0 || firstCovered[1, 0] < firstCovered[1, 1], Is.True, "the mown trail opens first");
        }

        /// <summary>The mean snow depth (mm) over layout trail <paramref name="t"/>'s fine cells.</summary>
        static long MeanDepthMm(SimWorld w, int t)
        {
            var fine = w.FineSnow;
            if (fine == null) return 0;
            long sum = 0; int n = 0;
            for (int f = 0; f < fine.Cells; f++) if (fine.Layout.Owner[f] == t) { sum += fine.Swe[f] > 0 ? fine.DepthUm(f) : 0; n++; }
            return n == 0 ? 0 : sum / n / 1000;
        }

        static void SetDepth(SimWorld w, int mm)
        {
            int width = w.Snow.Width, height = w.Snow.Height;
            var a = new ushort[width * height];
            for (int c = 0; c < a.Length; c++) a[c] = (ushort)mm;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(width, height, 0, 0, width, height, a)));
        }

        [Test]
        public void SN37_TheMowingPayloadIsInIdOrderAndBadOnesAreRefused()
        {
            var m = SimMowing.From(new List<KeyValuePair<ulong, TrailMowing>>
            {
                new KeyValuePair<ulong, TrailMowing>(9, TrailMowing.Off), new KeyValuePair<ulong, TrailMowing>(3, TrailMowing.EveryAutumn),
            });
            Assert.That(m.TrailIds, Is.EqualTo(new ulong[] { 3, 9 }));
            Assert.That(m.EveryAutumn, Is.EqualTo(new[] { true, false }));
            Assert.That(m.Problem(), Is.Null);
            var w = Net(600);
            long before = w.Events.Count;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailMowing, 0, new SimMowing(new ulong[] { 9, 3 }, new[] { true, true })));
            bool refused = false;
            for (long q = before; q < w.Events.Count; q++)
                if (w.Events.TryGet(q, out var e) && e.Type == SimEventType.CommandRejected) refused = true;
            Assert.That(refused, Is.True, "out of id order");
        }
    }
}
