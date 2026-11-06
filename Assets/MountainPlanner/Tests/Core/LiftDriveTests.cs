using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Drive at either end (owner, 2026-11-02, LD9; save v10): every fixed-grip terminal carries a head for each end, so the
    // player's drive end swaps which model stands where and which head it shows, and the top's row (4, 6 or 8 sheaves) follows
    // the summit as tower heads follow the rope (TW-04). The Sessellift Stern drives at the bottom only.
    public sealed class LiftDriveTests
    {
        const double West = 500_000.25, North = 4_200_000.75;

        sealed class Flat : IHeightSource
        {
            public double At(double x, double y) => 1500;
        }

        static void AssertTops(LiftType t, string model)
        {
            Assert.That(t.TopTrains.Select(e => e.Model), Is.All.EqualTo(model));
            Assert.That(t.TopTrains.Select(e => e.Head), Is.EqualTo(new[] { "top", "top6", "top8" }));
            Assert.That(t.TopTrains.Select(e => e.Sheaves), Is.EqualTo(new[] { 4, 6, 8 }));
            Assert.That(t.TopTrains.Select(e => e.Row), Is.All.EqualTo(TerminalRow.Support));
            Assert.That(t.Top, Is.SameAs(t.TopTrains[0]), "the 4-sheave row until a rope picks another");
        }

        [Test]
        public void LD9_EachDriveEndStandsItsOwnModelsAndHeads()
        {
            var top = LiftType.Of(LiftCatalogIds.SesselliftFgq4, LiftDrive.Top);
            Assert.That((top.Bottom.Model, top.Bottom.Head, top.Bottom.Row), Is.EqualTo(("sessellift_fgq4_return", "bottom", TerminalRow.HoldDown)));
            AssertTops(top, "sessellift_fgq4_drive");
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).Drive, Is.EqualTo(LiftDrive.Top), "the Sessellift drives at the top unless chosen");

            var bottom = LiftType.Of(LiftCatalogIds.SesselliftFgd2, LiftDrive.Bottom);
            Assert.That((bottom.Bottom.Model, bottom.Bottom.Head, bottom.Bottom.Row), Is.EqualTo(("sessellift_fgd2_drive", "bottom", TerminalRow.HoldDown)));
            AssertTops(bottom, "sessellift_fgd2_return");

            var monta = LiftType.Of(LiftCatalogIds.MontaFg4);
            Assert.That(monta.Drive, Is.EqualTo(LiftDrive.Bottom), "the Monta drives at the bottom unless chosen");
            Assert.That((monta.Bottom.Model, monta.Bottom.OwnStation, monta.Top.Model), Is.EqualTo(("monta_fg4_drive", true, "monta_fg4_return")));
            Assert.That(monta.TopTrains, Is.Null, "its one-sheave top station has no heads");

            var montaTop = LiftType.Of(LiftCatalogIds.MontaFg3, LiftDrive.Top);
            Assert.That((montaTop.Bottom.Model, montaTop.Bottom.Head, montaTop.Bottom.Row, montaTop.Bottom.OwnStation),
                        Is.EqualTo(("monta_fg3_return_bottom", (string)null, TerminalRow.HoldDown, false)));
            AssertTops(montaTop, "monta_fg3_drive");
            Assert.That((montaTop.Seats, montaTop.GaugeMetres), Is.EqualTo((3, 3.658)), "the drive end changes the ends, not the numbers");
        }

        [Test]
        public void LD9_TheSternDrivesAtTheBottomOnly()
        {
            foreach (var (id, basis, prefix) in new[]
            {
                (LiftCatalogIds.SesselliftStern4, LiftCatalogIds.SesselliftFgq4, "sessellift_stern4_"),
                (LiftCatalogIds.SesselliftStern3, LiftCatalogIds.SesselliftFgt3, "sessellift_stern3_"),
                (LiftCatalogIds.SesselliftStern2, LiftCatalogIds.SesselliftFgd2, "sessellift_stern2_"),
            })
            {
                Assert.That(LiftCatalogIds.Known(id) && LiftCatalogIds.Buildable(id), Is.True, id);
                Assert.That(LiftCatalogIds.DriveChoice(id), Is.False, id);
                var t = LiftType.Of(id, LiftDrive.Top);
                var b = LiftType.Of(basis);
                Assert.That((t.CatalogId, t.Drive), Is.EqualTo((id, LiftDrive.Bottom)), id);
                Assert.That((t.Bottom.Model, t.Bottom.Head, t.Bottom.Row), Is.EqualTo((prefix + "drive", "bottom", TerminalRow.HoldDown)), id);
                AssertTops(t, prefix + "return");
                Assert.That((t.Seats, t.GaugeMetres, t.RopeDiameterMetres, t.ChairHalfWidthMetres, t.TowerKit, t.Capacity),
                            Is.EqualTo((b.Seats, b.GaugeMetres, b.RopeDiameterMetres, b.ChairHalfWidthMetres, b.TowerKit, b.Capacity)),
                            id + ": the FGQ-4 family's chairs, towers and numbers");
                var lift = new Lift(new LiftId(1), "Stern", WorkStage.Built, id, new PointMm(0, 0), new PointMm(0, 500_000), TowerMode.Automatic,
                                    TowerStance.Plumb, null, null, LiftDrive.Top);
                Assert.That(lift.Drive, Is.EqualTo(LiftDrive.Bottom), id + ": a lift can't ask for the other end");
            }
        }

        [Test]
        public void LD9_TypesWithoutTheChoiceKeepTheirOwnEnd()
        {
            foreach (string id in new[] { LiftCatalogIds.SesselliftD6, LiftCatalogIds.SesselliftD4, "detach-eight:monta" })
            {
                var lift = new Lift(new LiftId(1), "Six", WorkStage.Built, id, new PointMm(0, 0), new PointMm(0, 500_000), TowerMode.Automatic,
                                    TowerStance.Plumb, null, null, LiftDrive.Bottom);
                Assert.That(lift.Drive, Is.EqualTo(LiftCatalogIds.DefaultDrive(id)), id);
                Assert.That(LiftType.Of(id, LiftDrive.Bottom).Bottom.Model, Is.EqualTo(LiftType.Of(id).Bottom.Model), id);
            }
        }

        // A short line with no towers between the terminals' exits, its chord swept from level to steep: the rope reaches the
        // top steeper than its chord (it sags), and the top takes the row with the fewest sheaves that carries it, 4.5° a sheave,
        // so a steeper summit takes a longer row (a sheave's load can call for a longer row before the angle does); past what the 8
        // carries it stays on the 8, red.
        [TestCase(LiftCatalogIds.SesselliftFgq4, LiftDrive.Top)]
        [TestCase(LiftCatalogIds.SesselliftFgt3, LiftDrive.Bottom)]
        [TestCase(LiftCatalogIds.MontaFg2, LiftDrive.Top)]
        [TestCase(LiftCatalogIds.SesselliftStern4, LiftDrive.Bottom)]
        public void LD9_TheTopRowFollowsTheSummit(string id, LiftDrive drive)
        {
            var type = LiftType.Of(id, drive);
            double length = 120, bx = West + 100, by = North - 500;
            double span = length - type.Bottom.ExitAlong - type.Top.ExitAlong, bottomY = 1520;
            var seen = new System.Collections.Generic.SortedSet<int>();
            int last = 4;
            for (double chord = 0; chord <= 30; chord += 0.5)
            {
                double topY = bottomY + Math.Tan(chord * Math.PI / 180) * span;
                var rope = LiftRope.Solve(type, new Flat(), bx, by, bx, by + length, Array.Empty<Tower>(), TowerStance.Plumb,
                                          type.Bottom.ExitAlong, bottomY, type.Top.ExitAlong, topY);
                double entry = Math.Max(Math.Abs(rope.TopEntryLoaded), Math.Abs(rope.TopEntryEmpty)) * 180 / Math.PI;
                int picked = rope.Type.Top.Sheaves;
                Assert.That(picked, Is.GreaterThanOrEqualTo(last), $"{id} at {chord}°: a steeper summit never takes a shorter row");
                last = picked;
                Assert.That(rope.Type.Top.Head, Is.EqualTo(picked == 4 ? "top" : "top" + picked));
                Assert.That(rope.Type.Bottom, Is.SameAs(type.Bottom), "the bottom end is the drive end's, whatever the top picks");
                // The fewest that carries: the picked row carries the rope over its own exit, and the next shorter row, solved
                // alone on its own exit (the terminal's level unchanged), doesn't. Past what the 8 carries it stays on the 8, red.
                if (!rope.TopCarried)
                {
                    Assert.That(picked, Is.EqualTo(8), $"{id} at {chord}°: past every row, the longest, red");
                    continue;
                }
                int at = type.TopTrains.ToList().FindIndex(t => t.Sheaves == picked);
                if (at > 0)
                {
                    var shorter = type.TopTrains[at - 1];
                    var alone = LiftRope.Solve(type.With(type.Bottom, shorter, type.ChairHalfWidthMetres, new[] { shorter }), new Flat(), bx, by, bx,
                                               by + length, Array.Empty<Tower>(), TowerStance.Plumb, type.Bottom.ExitAlong, bottomY,
                                               shorter.ExitAlong, topY - type.Top.ExitRise + shorter.ExitRise);
                    Assert.That(alone.TopCarried, Is.False, $"{id} at {chord}°: the {shorter.Sheaves}-sheave row would have carried it");
                }
                seen.Add(picked);
            }
            Assert.That(seen.Count, Is.GreaterThanOrEqualTo(2), $"{id}: rows picked {string.Join(", ", seen)}");
        }

        [Test]
        public void LD9_TheTopPadIsTheStationsOwn()
        {
            // The station's own pad: a longer row's extra sheaves reach out over the line in the air.
            var type = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            var pad = type.TopFootprint;
            Assert.That(pad.Ahead, Is.EqualTo(type.TopTrains[0].Ahead));
            Assert.That(type.TopTrains.Max(t => t.Ahead), Is.GreaterThan(pad.Ahead), "the 8 reaches further out than the 4");
            Assert.That(pad.Behind, Is.EqualTo(type.TopTrains.Max(t => t.Behind)));
            Assert.That(pad.HalfWidth, Is.EqualTo(type.TopTrains.Max(t => t.HalfWidth)));
            var monta = LiftType.Of(LiftCatalogIds.MontaFg4);
            Assert.That(monta.TopFootprint, Is.SameAs(monta.Top), "one row, its own footprint");
        }
    }
}
