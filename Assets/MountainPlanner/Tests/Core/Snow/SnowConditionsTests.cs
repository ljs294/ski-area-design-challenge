using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2: the surface rules one at a time, on one cell, through the hour's own function (SnowHour.Conditions):
    /// SN-13 v2's natural types, SN-14 v2's transitions (first match wins), SN-16 v2's quality and SN-18 v2's age.
    /// </summary>
    public sealed class SnowConditionsTests
    {
        static readonly SnowRules R = new SnowRules(Pacing.Default);
        const int Cold = -500, Warm = 0;          // the surface skin (0.01 °C): well below the refreeze, and at the melt
        const int ColdBulb = 200, WetBulb = 320;  // 20 °F and 32 °F (0.1 °F)

        sealed class Cell
        {
            public readonly SnowField F = new SnowField(1, 1);
            public int Day;

            public Cell(SnowType type, int age = 0)
            {
                F.Pack[0] = new SnowPack { Swe = 50_000, Density = 300_000, Albedo = 700_000 };
                F.Type[0] = (byte)type;
                F.Age[0] = (byte)age;
            }

            public SnowType Type => (SnowType)F.Type[0];
            public int Age => F.Age[0];

            /// <summary>One face hour: thawed or not, its skin, new snow and rain (µm of water), the wet bulb.</summary>
            public Cell Hour(bool thawed = false, int skin = Cold, int snow = 0, int rain = 0, int wetBulb = ColdBulb)
            {
                SnowHour.Conditions(R, Day, F, 0, thawed, skin, snow, rain, wetBulb);
                return this;
            }

            public Cell Hours(int n, bool thawed = false, int skin = Cold)
            {
                for (int k = 0; k < n; k++) Hour(thawed, skin);
                return this;
            }
        }

        [Test]
        public void SN13_SnowOnBareGroundStartsAsPowderOrWetPowder()
        {
            var cold = new Cell(SnowType.None).Hour(snow: 500);
            var wet = new Cell(SnowType.None).Hour(snow: 500, wetBulb: WetBulb);
            Assert.That(cold.Type, Is.EqualTo(SnowType.P));
            Assert.That(wet.Type, Is.EqualTo(SnowType.WP), "above a 31 °F wet bulb");
            var gone = new Cell(SnowType.P);
            gone.F.Pack[0].Swe = 0;
            gone.Hour();
            Assert.That(gone.Type, Is.EqualTo(SnowType.None), "bare ground has no type");
        }

        [Test]
        public void SN14_PowderOnceATenthOfAnInchOfWaterHasFallenAndTheAgeStartsAgain()
        {
            var c = new Cell(SnowType.FG, age: 80).Hour(snow: 1500);
            Assert.That(c.Type, Is.EqualTo(SnowType.FG), "0.06 in is not enough");
            Assert.That(c.Age, Is.EqualTo(81));
            c.Hour(snow: 1100);   // 2,600 µm since the crust: over 0.1 in (2,540)
            Assert.That(c.Type, Is.EqualTo(SnowType.P));
            Assert.That(c.Age, Is.Zero, "SN-18 v2: the age starts again at a powder reset");
        }

        [Test]
        public void SN14_AThawTurnsPowderWetFirstThenGranular()
        {
            var c = new Cell(SnowType.P).Hour(thawed: true, skin: Warm);
            Assert.That(c.Type, Is.EqualTo(SnowType.WP));
            c.Hour(thawed: true, skin: Warm);
            Assert.That(c.Type, Is.EqualTo(SnowType.WG));
            Assert.That(new Cell(SnowType.FG).Hour(rain: 300, skin: Warm).Type, Is.EqualTo(SnowType.WG), "rain is a thaw too");
        }

        [Test]
        public void SN14_ARefreezeTakesTwoColdHoursAndMakesACrustOrIceAfterRain()
        {
            var crust = new Cell(SnowType.WG).Hour();
            Assert.That(crust.Type, Is.EqualTo(SnowType.WG), "one cold hour is not a refreeze");
            crust.Hour();
            Assert.That(crust.Type, Is.EqualTo(SnowType.FG), "two are");
            var ice = new Cell(SnowType.FG).Hour(rain: 300, skin: Warm).Hours(2);
            Assert.That(ice.Type, Is.EqualTo(SnowType.IS), "after rain the refreeze is ice");
            var warm = new Cell(SnowType.WG).Hours(4, skin: -100);
            Assert.That(warm.Type, Is.EqualTo(SnowType.WG), "a skin above −2.2 °C does not refreeze");
        }

        [Test]
        public void SN14_TheThirdThawAndRefreezeWithinFiveDaysIsCorn()
        {
            // Two thaw–refreeze cycles, then a dry thaw: corn while it holds.
            var c = new Cell(SnowType.WG).Hours(2);
            c.Day = 1;
            c.Hour(thawed: true, skin: Warm).Hours(2);
            Assert.That(c.Type, Is.EqualTo(SnowType.FG));
            c.Day = 2;
            c.Hour(thawed: true, skin: Warm);
            Assert.That(c.Type, Is.EqualTo(SnowType.CO), "the third cycle's thaw");
            c.Hour(thawed: true, skin: Warm);
            Assert.That(c.Type, Is.EqualTo(SnowType.CO), "corn while the thaw holds");
            c.Hours(2);
            Assert.That(c.Type, Is.EqualTo(SnowType.FG), "and it refreezes");

            var late = new Cell(SnowType.WG).Hours(2);
            late.Day = 1;
            late.Hour(thawed: true, skin: Warm).Hours(2);
            late.Day = 9;
            late.Hour(thawed: true, skin: Warm);
            Assert.That(late.Type, Is.EqualTo(SnowType.WG), "cycles older than five face days do not count");

            var rained = new Cell(SnowType.WG).Hours(2);
            rained.Day = 1;
            rained.Hour(thawed: true, skin: Warm).Hours(2);
            rained.Day = 2;
            rained.Hour(rain: 300, skin: Warm);
            Assert.That(rained.Type, Is.EqualTo(SnowType.WG), "rain is not a corn thaw");
        }

        [Test]
        public void SN16_QualityIsTheTypesBaseLessFourThousandthsAFaceHourToSixTenths()
        {
            int p = Pacing.Default.QualityBase[(int)SnowType.P - 1];
            Assert.That(R.Quality((int)SnowType.P, 0), Is.EqualTo(p));
            Assert.That(R.Quality((int)SnowType.P, 48), Is.EqualTo(p * (1000 - 4 * 48) / 1000), "two face days old");
            Assert.That(R.Quality((int)SnowType.P, 255), Is.EqualTo(p * 600 / 1000), "the floor");
            Assert.That(R.Quality((int)SnowType.None, 0), Is.Zero, "bare ground");
        }

        [Test]
        public void SN18_TheAgeCountsFaceHoursToTwoHundredFiftyFive()
        {
            var c = new Cell(SnowType.FG, age: 250).Hours(10);
            Assert.That(c.Age, Is.EqualTo(255));
        }
    }
}
