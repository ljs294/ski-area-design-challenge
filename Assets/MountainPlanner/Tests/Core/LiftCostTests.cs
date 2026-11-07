using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Costs;
using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Lift costs (owner, 2026-11-04): Terminals + a × slope length + b × vertical rise, 2025 dollars, fitted to Vermont Act 250
    // filings brought to 2025 by CPI. Worked out from the design; nothing saved.
    public sealed class LiftCostTests
    {
        const double Ft = 0.3048;

        static IEnumerable<string> CatalogIds()
        {
            yield return LiftCatalogIds.SesselliftFgq4;
            yield return LiftCatalogIds.MontaFg4;
            yield return LiftCatalogIds.SesselliftStern4;
            yield return LiftCatalogIds.SesselliftStern3;
            yield return LiftCatalogIds.SesselliftStern2;
            yield return LiftCatalogIds.MontaTbar;
            yield return LiftCatalogIds.MontaJbar;
            yield return LiftCatalogIds.GlidepathCarpet;
            yield return LiftCatalogIds.GlidepathCarpet2Covered;
            foreach (string carrier in LiftCatalogIds.Carriers)
                foreach (string maker in LiftCatalogIds.Makers)
                    yield return LiftCatalogIds.Of(carrier, maker);
        }

        static LiftCostBreakdown Cost(string id, double lengthFt, double riseFt) => LiftCost.Of(LiftType.Of(id), lengthFt * Ft, riseFt * Ft);

        [Test]
        public void EveryBuildableTypeHasAPrice()
        {
            int priced = 0;
            foreach (string id in CatalogIds())
            {
                if (!LiftCatalogIds.Buildable(id)) continue;
                // Surface lifts have no price yet (owner Q4, v0.3.2): their own task.
                if (LiftCatalogIds.IsTow(id) || LiftCatalogIds.IsBelt(id)) { Assert.That(LiftPrices.For(LiftType.Of(id)), Is.Null, id); continue; }
                var cost = Cost(id, 4000, 1000);
                Assert.That(cost, Is.Not.Null, id);
                Assert.That(cost.TotalUsd, Is.GreaterThan(1_000_000), id);
                priced++;
            }
            Assert.That(priced, Is.GreaterThanOrEqualTo(11));
        }

        [Test]
        public void TheFamilyComesFromSeatsAndGrip()
        {
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.SesselliftFgq4)), Is.SameAs(LiftPrices.FixedQuad));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.MontaFg4)), Is.SameAs(LiftPrices.FixedQuad));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.SesselliftFgt3)), Is.SameAs(LiftPrices.FixedTriple));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.MontaFg2)), Is.SameAs(LiftPrices.FixedDouble));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.SesselliftD4)), Is.SameAs(LiftPrices.DetachQuad));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.SesselliftD6)), Is.SameAs(LiftPrices.DetachSix));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.Of("detach-six", "chairworks"))), Is.SameAs(LiftPrices.DetachSix));
            // The Sessellift Stern (B2, bottom drive only) prices as its seats say; the drive end does not change the price.
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.SesselliftStern4)), Is.SameAs(LiftPrices.FixedQuad));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.SesselliftStern3)), Is.SameAs(LiftPrices.FixedTriple));
            Assert.That(LiftPrices.For(LiftType.Of(LiftCatalogIds.SesselliftStern2)), Is.SameAs(LiftPrices.FixedDouble));
            Assert.That(LiftCost.Of(LiftType.Of(LiftCatalogIds.SesselliftFgq4, LiftDrive.Bottom), 1200, 300).TotalUsd,
                        Is.EqualTo(LiftCost.Of(LiftType.Of(LiftCatalogIds.SesselliftFgq4, LiftDrive.Top), 1200, 300).TotalUsd));
        }

        [Test]
        public void GondolasTramsAndEightsHaveNoPriceYet()
        {
            foreach (string carrier in new[] { "gondola-6", "gondola-8", "gondola-10", "tram-60", "tram-100", "detach-eight" })
                Assert.That(Cost(LiftCatalogIds.Of(carrier, "sessellift"), 4000, 1000), Is.Null, carrier);
            Assert.That(LiftCost.Of(null, 1000, 300), Is.Null);
        }

        [Test]
        public void TheCostIsTerminalsPlusLengthPlusRise()
        {
            var cost = Cost(LiftCatalogIds.SesselliftFgq4, 4000, 1000);
            Assert.That(cost.Line(LiftCost.Terminals), Is.EqualTo(1_300_000));
            Assert.That(cost.Line(LiftCost.Length), Is.EqualTo(1_200_000));
            Assert.That(cost.Line(LiftCost.Rise), Is.EqualTo(600_000));
            Assert.That(cost.TotalUsd, Is.EqualTo(3_100_000));
            Assert.That(cost.Lines.Count, Is.EqualTo(3));
        }

        [Test]
        public void TypicalLines()
        {
            Assert.That(Cost(LiftCatalogIds.SesselliftD4, 5000, 1200).TotalUsd, Is.EqualTo(7_758_000));
            Assert.That(Cost(LiftCatalogIds.SesselliftD6, 5000, 1200).TotalUsd, Is.EqualTo(8_758_000));
            Assert.That(Cost(LiftCatalogIds.SesselliftFgd2, 3000, 800).TotalUsd, Is.LessThan(Cost(LiftCatalogIds.SesselliftFgt3, 3000, 800).TotalUsd));
            Assert.That(Cost(LiftCatalogIds.SesselliftFgt3, 3000, 800).TotalUsd, Is.LessThan(Cost(LiftCatalogIds.SesselliftFgq4, 3000, 800).TotalUsd));
            Assert.That(Cost(LiftCatalogIds.SesselliftFgq4, 3000, 800).TotalUsd, Is.LessThan(Cost(LiftCatalogIds.SesselliftD4, 3000, 800).TotalUsd));
        }

        [Test]
        public void TheCostRisesWithLengthAndRise()
        {
            foreach (string id in CatalogIds())
            {
                if (LiftPrices.For(LiftType.Of(id)) == null) continue;
                long baseCost = Cost(id, 3000, 700).TotalUsd;
                Assert.That(Cost(id, 4000, 700).TotalUsd, Is.GreaterThan(baseCost), id + " longer");
                Assert.That(Cost(id, 3000, 900).TotalUsd, Is.GreaterThan(baseCost), id + " higher");
            }
        }

        [Test]
        public void AFallingLinePaysItsLengthOnly()
        {
            var cost = LiftCost.Of(LiftType.Of(LiftCatalogIds.SesselliftFgq4), 1000, -200);
            Assert.That(cost.Line(LiftCost.Rise), Is.Zero);
            Assert.That(cost.Line(LiftCost.Length), Is.GreaterThan(0));
        }

        [Test]
        public void OfLineUsesTheSlopeLength()
        {
            var type = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            Assert.That(LiftCost.OfLine(type, 1200, 500).TotalUsd, Is.EqualTo(LiftCost.Of(type, 1300, 500).TotalUsd));
        }

        [Test]
        public void TheSameLiftCostsTheSame()
        {
            var type = LiftType.Of(LiftCatalogIds.SesselliftD6);
            var a = LiftCost.Of(type, 1234.567, 345.678);
            var b = LiftCost.Of(LiftType.Of(LiftCatalogIds.SesselliftD6), 1234.567, 345.678);
            Assert.That(b.TotalUsd, Is.EqualTo(a.TotalUsd));
            for (int i = 0; i < a.Lines.Count; i++) Assert.That(b.Lines[i].Usd, Is.EqualTo(a.Lines[i].Usd));
        }

        [Test]
        public void ALaterLineJoinsTheTotal()
        {
            var cost = Cost(LiftCatalogIds.SesselliftFgq4, 4000, 1000);
            var withSoil = cost.With("Soil works", 250_000);
            Assert.That(withSoil.TotalUsd, Is.EqualTo(3_350_000));
            Assert.That(withSoil.Line("Soil works"), Is.EqualTo(250_000));
            Assert.That(cost.TotalUsd, Is.EqualTo(3_100_000), "the first stays as it was");
        }

        // The filings the table was fitted to (lift line only, 2025 $ by CPI; length and vertical in feet). The fixed quads
        // scatter about 20% (rms); the detachables sit within 10%.
        [TestCase(LiftCatalogIds.SesselliftFgq4, 3759, 1242, 3_030_000, 0.35)]
        [TestCase(LiftCatalogIds.SesselliftFgq4, 1695, 260, 1_510_000, 0.35)]
        [TestCase(LiftCatalogIds.SesselliftFgq4, 2171, 477, 1_670_000, 0.35)]
        [TestCase(LiftCatalogIds.SesselliftFgq4, 2250, 587, 2_700_000, 0.35)]
        [TestCase(LiftCatalogIds.SesselliftFgq4, 1791, 413, 2_670_000, 0.35)]
        [TestCase(LiftCatalogIds.SesselliftFgq4, 3870, 1449, 3_300_000, 0.35)]
        [TestCase(LiftCatalogIds.SesselliftFgt3, 3862, 824, 2_490_000, 0.2)]
        [TestCase(LiftCatalogIds.SesselliftD4, 5873, 2047, 9_230_000, 0.1)]
        [TestCase(LiftCatalogIds.SesselliftD4, 4606, 1371, 7_210_000, 0.1)]
        [TestCase(LiftCatalogIds.SesselliftD6, 5450, 1181, 9_380_000, 0.1)]
        [TestCase(LiftCatalogIds.SesselliftD6, 6617, 1670, 9_380_000, 0.1)]
        [TestCase(LiftCatalogIds.SesselliftD6, 4428, 1114, 7_690_000, 0.1)]
        public void TheFilingsSitInBand(string id, double lengthFt, double riseFt, double filedUsd, double band)
        {
            double ratio = Cost(id, lengthFt, riseFt).TotalUsd / filedUsd;
            Assert.That(ratio, Is.InRange(1 - band, 1 + band));
        }
    }

    // demo.bat 79: the price table, each family over three typical lines.
    public sealed class LiftCostTable
    {
        [Test]
        public void PrintThePriceTable()
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Lift prices, {LiftPrices.DollarYear} dollars: Terminals + a x slope length + b x vertical rise");
            text.AppendLine($"{"Family",-20}{"Terminals",12}{"a $/ft",9}{"b $/ft",9}{"2,500x600",12}{"4,000x1,000",13}{"6,000x1,600",13}");
            var lines = new[] { (2500.0, 600.0), (4000.0, 1000.0), (6000.0, 1600.0) };
            var sample = new Dictionary<LiftFamily, string>
            {
                [LiftFamily.FixedDouble] = LiftCatalogIds.SesselliftFgd2, [LiftFamily.FixedTriple] = LiftCatalogIds.SesselliftFgt3,
                [LiftFamily.FixedQuad] = LiftCatalogIds.SesselliftFgq4, [LiftFamily.DetachQuad] = LiftCatalogIds.SesselliftD4,
                [LiftFamily.DetachSix] = LiftCatalogIds.SesselliftD6,
            };
            foreach (var price in LiftPrices.All)
            {
                var type = LiftType.Of(sample[price.Family]);
                text.Append($"{price.Name,-20}{price.TerminalsUsd,12:#,0}{price.PerFootLengthUsd,9:#,0}{price.PerFootRiseUsd,9:#,0}");
                foreach (var (len, rise) in lines)
                    text.Append($"{LiftCost.Of(type, len * 0.3048, rise * 0.3048).TotalUsd / 1e6,11:0.00}M ".PadLeft(len == 2500 ? 12 : 13));
                text.AppendLine();
                text.AppendLine($"  {price.Basis}");
            }
            text.AppendLine("Lines are slope length x vertical rise, feet. Gondolas, trams, eights and surface lifts: no price yet.");
            TestContext.Out.Write(text.ToString());
            Assert.Pass();
        }
    }
}
