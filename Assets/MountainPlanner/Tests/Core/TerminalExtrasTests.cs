using MountainPlanner.Domain.Costs;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 4.50 slice 2 (owner 2026-11-25): every terminal ships with its operator hut (always, no switch), a fixed grip's
    // top (drive or return) with its stop gate, pedestal and, 5 ft up or more, its unload ramp (the Monta drive's wide one);
    // the costs go into the lift's price.
    public sealed class TerminalExtrasTests
    {
        static EndExtras End(string id, bool top, LiftDrive? drive = null) => TerminalExtras.For(LiftType.Of(id, drive), top);

        [Test]
        public void HutSizeFollowsTheDrive()
        {
            // Detachables and the gondola: the drive's large hut, the return's small.
            foreach (string id in new[] { LiftCatalogIds.SesselliftD6, LiftCatalogIds.SesselliftD4, LiftCatalogIds.SesselliftG8 })
            {
                bool topDrives = TerminalExtras.TopDrives(LiftType.Of(id));
                Assert.That(End(id, topDrives).Hut, Is.EqualTo(HutKind.Large), id);
                Assert.That(End(id, !topDrives).Hut, Is.EqualTo(HutKind.Small), id);
                Assert.That(End(id, true).UnloadEnd || End(id, false).UnloadEnd, Is.False, id + ": no ramp at a detachable");
            }
            // Fixed grips: a bottom drive the small hut, a bottom return the mini; every top the mini, in the ramp's bay.
            Assert.That(End(LiftCatalogIds.SesselliftFgq4, true, LiftDrive.Top).Hut, Is.EqualTo(HutKind.Mini));
            Assert.That(End(LiftCatalogIds.SesselliftFgq4, false, LiftDrive.Top).Hut, Is.EqualTo(HutKind.Mini));
            Assert.That(End(LiftCatalogIds.SesselliftFgq4, true, LiftDrive.Bottom).Hut, Is.EqualTo(HutKind.Mini));
            Assert.That(End(LiftCatalogIds.SesselliftFgq4, false, LiftDrive.Bottom).Hut, Is.EqualTo(HutKind.Small));
            Assert.That(End(LiftCatalogIds.MontaFg4, false).Hut, Is.EqualTo(HutKind.Small), "the Monta drives at the bottom");
            Assert.That(End(LiftCatalogIds.SesselliftStern2, true).Hut, Is.EqualTo(HutKind.Mini));
        }

        [Test]
        public void AFixedGripsTopGetsTheGateAndRamp()
        {
            Assert.That(End(LiftCatalogIds.SesselliftFgq4, false, LiftDrive.Bottom).UnloadEnd, Is.False, "a bottom isn't an unload");
            Assert.That(End(LiftCatalogIds.SesselliftFgq4, false, LiftDrive.Top).UnloadEnd, Is.False);
            var sessellift = End(LiftCatalogIds.SesselliftFgq4, true, LiftDrive.Bottom);
            Assert.That(sessellift.UnloadEnd && sessellift.Maker == "sessellift" && sessellift.Width == RampWidth.Quad);
            Assert.That(sessellift.RampModel, Is.EqualTo("bldg_ramp_quad"));
            Assert.That(sessellift.GateModel, Is.EqualTo("bldg_stop_gate_sessellift"));
            Assert.That(sessellift.PedestalModel, Is.EqualTo(TerminalExtras.Pedestal));
            var stern = End(LiftCatalogIds.SesselliftStern3, true);
            Assert.That(stern.Maker == "stern" && stern.Width == RampWidth.Triple, $"{stern.Maker} {stern.Width}");
            var monta = End(LiftCatalogIds.MontaFg2, true);
            Assert.That(monta.Maker == "monta" && monta.Width == RampWidth.Double, $"{monta.Maker} {monta.Width}");
            Assert.That(monta.RampModel, Is.EqualTo("bldg_ramp_double"), "the Monta return: the standard ramp");
            // Owner 2026-11-25: a top drive too. The Sessellift drive on the standard ramps, its own gate (its wheel 3.23 m
            // past the unload); the Monta drive on its wide ramps (its A-frame's feet through the deck, the hut outboard).
            var sdrive = End(LiftCatalogIds.SesselliftFgq4, true, LiftDrive.Top);
            Assert.That(sdrive.UnloadEnd && sdrive.Drive && !sdrive.Wide, $"{sdrive.Style}");
            Assert.That(sdrive.RampModel, Is.EqualTo("bldg_ramp_quad"));
            Assert.That(sdrive.GateModel, Is.EqualTo("bldg_stop_gate_sessellift_drive"));
            Assert.That(TerminalExtras.WheelU(sdrive.Style), Is.EqualTo(3.230));
            var mdrive = End(LiftCatalogIds.MontaFg3, true, LiftDrive.Top);
            Assert.That(mdrive.UnloadEnd && mdrive.Wide, $"{mdrive.Style}");
            Assert.That(mdrive.RampModel, Is.EqualTo("bldg_ramp_triple_wide"));
            Assert.That(mdrive.GateModel, Is.EqualTo("bldg_stop_gate_monta_drive"));
            Assert.That(TerminalExtras.Spec(mdrive).DeckWidth, Is.EqualTo(10.4), "the wide triple: 10.4 m");
        }

        [Test]
        public void SurfaceLiftsGetTheirOwnHutAndCarpetsNone()
        {
            Assert.That(End(LiftCatalogIds.MontaTbar, false).Hut, Is.EqualTo(HutKind.Tow));
            Assert.That(End(LiftCatalogIds.MontaTbar, true).Hut, Is.EqualTo(HutKind.None));
            Assert.That(End(LiftCatalogIds.MontaJbar, false).HutModel, Is.EqualTo("monta_tbar_hut"));
            foreach (bool top in new[] { false, true })
                Assert.That(End(LiftCatalogIds.GlidepathCarpet, top).Hut, Is.EqualTo(HutKind.None), "a carpet keeps its control post only");
        }

        sealed class Plane : IHeightSource
        {
            public double Height;
            public double At(double x, double y) => Height;
        }

        [Test]
        public void TheRampStandsFromFiveFeetOverTheGround()
        {
            var type = LiftType.Of(LiftCatalogIds.SesselliftStern4);
            var ground = new Plane { Height = 1000 };
            Assert.That(TerminalExtras.UnloadRaise(type, ground, 0, 0, 0, -1, 1002.0), Is.EqualTo(2.0).Within(1e-9));
            Assert.That(TerminalExtras.RampShown(TerminalExtras.For(type, true), 1.524), Is.True);
            Assert.That(TerminalExtras.RampShown(TerminalExtras.For(type, true), 1.5), Is.False, "under 5 ft: the pad");
            Assert.That(TerminalExtras.UnloadRaise(type, ground, 0, 0, 0, -1, 999.0), Is.EqualTo(0), "never below the ground");
            Assert.That(TerminalExtras.UnloadRaise(LiftType.Of(LiftCatalogIds.SesselliftD6), ground, 0, 0, 0, -1, 1005), Is.EqualTo(0),
                        "only a fixed grip's top return");
        }

        [Test]
        public void TheCostsGoIntoTheLiftsPrice()
        {
            // The hut sheet: $130k a detachable or gondola; a fixed grip's bottom drive $40k or return $25k, its top the
            // mini $25k plus its gate and pedestal $2k; $20k a tow, nothing for a carpet.
            Assert.That(TerminalExtrasCost.HutsUsd(LiftType.Of(LiftCatalogIds.SesselliftD6)), Is.EqualTo(130_000));
            Assert.That(TerminalExtrasCost.HutsUsd(LiftType.Of(LiftCatalogIds.SesselliftFgq4, LiftDrive.Top)), Is.EqualTo(52_000));
            Assert.That(TerminalExtrasCost.HutsUsd(LiftType.Of(LiftCatalogIds.SesselliftStern4)), Is.EqualTo(67_000));
            Assert.That(TerminalExtrasCost.HutsUsd(LiftType.Of(LiftCatalogIds.MontaTbar)), Is.EqualTo(20_000));
            Assert.That(TerminalExtrasCost.HutsUsd(LiftType.Of(LiftCatalogIds.GlidepathCarpet)), Is.EqualTo(0));
            // unloadramp.json's review sheet: a quad 2 m up on level ground $72.1k, a triple $64.1k, a double $56.1k.
            var stern4 = LiftType.Of(LiftCatalogIds.SesselliftStern4);
            Assert.That(TerminalExtrasCost.RampUsd(stern4, 2.0), Is.EqualTo(72_100).Within(100));
            Assert.That(TerminalExtrasCost.RampUsd(LiftType.Of(LiftCatalogIds.SesselliftStern3), 2.0), Is.EqualTo(64_100).Within(100));
            Assert.That(TerminalExtrasCost.RampUsd(LiftType.Of(LiftCatalogIds.SesselliftStern2), 2.0), Is.EqualTo(56_100).Within(100));
            Assert.That(TerminalExtrasCost.RampUsd(stern4, 1.0), Is.EqualTo(0), "under 5 ft: no ramp");
            // The Monta drive's wide quad, 2 m up: $90.9k (its site report: 148.2 m²).
            Assert.That(TerminalExtrasCost.RampUsd(LiftType.Of(LiftCatalogIds.MontaFg4, LiftDrive.Top), 2.0), Is.EqualTo(90_900).Within(100));
            var lift = LiftCost.Of(stern4, 1000, 300);
            var with = TerminalExtrasCost.With(lift, stern4, 2.0);
            Assert.That(with.Line(TerminalExtrasCost.Huts), Is.EqualTo(67_000));
            Assert.That(with.TotalUsd, Is.EqualTo(lift.TotalUsd + 67_000 + TerminalExtrasCost.RampUsd(stern4, 2.0)));
            Assert.That(TerminalExtrasCost.With(lift, stern4, 0).Line(TerminalExtrasCost.Ramp), Is.EqualTo(0));
        }
    }
}
