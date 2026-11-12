using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-08, SI-22: runs a world headless through a script of world commands, each given at the tick it's staged for, and
    /// reports the state hash at every face hour. Steps are as long as the world allows (the idle step) but never pass a
    /// scripted command's tick, so the result is the same at any <paramref name="maxStep"/> (TM-03). The Core tests, the
    /// nightly job and the player's <c>-simgolden</c> all play the same script through here.
    /// </summary>
    public static class SeasonRunner
    {
        public readonly struct Scripted
        {
            public readonly long Tick;
            public readonly SimCommand Command;
            public Scripted(long tick, SimCommand command) { Tick = tick; Command = command; }
        }

        /// <summary>Runs until <paramref name="untilFaceMs"/>; calls <paramref name="hour"/> with each face hour's index and hash.</summary>
        public static void Run(SimWorld w, IReadOnlyList<Scripted> script, long untilFaceMs, Action<long, ulong> hour, int maxStep = int.MaxValue)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            script ??= Array.Empty<Scripted>();
            for (int i = 1; i < script.Count; i++)
                if (script[i].Tick < script[i - 1].Tick) throw new ArgumentException("The script must be in tick order.", nameof(script));
            int next = 0;
            long lastHour = w.HourIndex;
            while (w.FaceMs < untilFaceMs)
            {
                while (next < script.Count && script[next].Tick <= w.Tick) w.Stage(script[next++].Command);
                long room = next < script.Count ? script[next].Tick - w.Tick : long.MaxValue;
                int k = (int)Math.Max(1, Math.Min(Math.Min(room, maxStep), w.Pacing.IdleTickFactor));
                w.Step(k);
                if (w.HourIndex != lastHour)
                {
                    lastHour = w.HourIndex;
                    hour?.Invoke(lastHour, w.HourHash);
                }
            }
        }

        /// <summary>
        /// The golden script (SI-08): a two-lift, one-trail resort opened in pre-season, a lift added mid-season, the seed
        /// pinned and released, the mountain closed in March and the next season begun. Its hashes are pinned in the tests.
        /// </summary>
        public static SimDesign GoldenDesign(long revision, bool thirdLift)
        {
            var lifts = thirdLift
                ? new[] { new LiftInput(101, true), new LiftInput(202, true), new LiftInput(303, true) }
                : new[] { new LiftInput(101, true), new LiftInput(202, true) };
            return new SimDesign(revision, 0x9E37_79B9_7F4A_7C15UL ^ (ulong)revision, 0x5EED_0001UL, Domain.Design.DayHours.Default, lifts, 1);
        }

        /// <summary>
        /// SI-23, task 4.12: a resort guests can actually ski, for the headless day (<c>-simguests</c>) and for anything
        /// else that needs a crowd without a package. The golden design above has no network on purpose (its hashes are
        /// pinned), and without one there are no bases to arrive at, so this is its own small mountain: a base, two tops,
        /// a quad to each, and a green and a blue home from each top — enough for GU-05's gate and GU-07's choice to
        /// matter, with a green way down from everywhere so no band is ever stranded.
        /// </summary>
        public static SimDesign GuestsDesign(long revision = 1)
        {
            const int Base = 0, LowTop = 1, HighTop = 2;
            var trails = new ulong[] { 11, 12, 13, 14 };
            var edges = new List<(int from, int to, int trail)>
            {
                (LowTop, Base, 0),    // 11: a green
                (LowTop, Base, 1),    // 12: a blue
                (HighTop, Base, 2),   // 13: a green, the long way round
                (HighTop, Base, 3),   // 14: a black
            };
            var facts = new List<TrailEdgeFacts>
            {
                Facts(0, 120, 1200), Facts(1, 80, 900), Facts(0, 100, 1800), Facts(2, 60, 1000),
            };
            var lengths = new List<int> { 1_200_000, 900_000, 1_800_000, 1_000_000 };
            var net = SimNetwork.Of(3, edges, trails, facts, lengths);
            var lifts = new[]
            {
                new LiftInput(101, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 300_000, LowTop, Base),
                new LiftInput(202, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 420_000, HighTop, Base),
            };
            return new SimDesign(revision, 0x51BE_5700_0000_0001UL ^ (ulong)revision, 0x5EED_0042UL,
                                 Domain.Design.DayHours.Default, lifts, trails.Length, net);
        }

        /// <summary>One trail's facts for <see cref="GuestsDesign"/>: its band, its width in feet and its length.</summary>
        static TrailEdgeFacts Facts(byte band, int widthFeet, int metres)
        {
            int tenths = band == 0 ? 130 : band == 1 ? 200 : band == 2 ? 250 : 270;
            int widthCm = (int)(widthFeet * 30.48);
            int areaM2 = (int)(metres * (widthFeet * 0.3048));
            int skiMs = (int)(metres * 10_000L / (tenths * 447L / 100));
            return TrailEdgeFacts.SteadyPitch(band, band, widthCm, areaM2, skiMs);   // GU-10 v3: every band its own time
        }

        public static List<Scripted> GoldenScript(Pacing p)
        {
            // Ticks: a pre-season face hour is 150 ticks, a season one 900 (or 150 outside the hours); the script names ticks
            // well inside the days it means, and the world says what day each one landed in (the hashes pin it all).
            return new List<Scripted>
            {
                new Scripted(0, SimCommand.Of(SimCommandKind.SetMountainOpen, 1)),
                new Scripted(40_000, SimCommand.Of(SimCommandKind.PinSeasonSeed, 0x1234_5678)),
                new Scripted(90_000, SimCommand.Of(SimCommandKind.SetDesign, 0, GoldenDesign(2, true))),
                new Scripted(120_000, SimCommand.Of(SimCommandKind.UnpinSeasonSeed)),
                new Scripted(400_000, SimCommand.Of(SimCommandKind.SetMountainOpen, 0)),
                new Scripted(400_001, SimCommand.Of(SimCommandKind.SetMountainOpen, 1)),   // refused: summer
            };
        }

        /// <summary>SI-08's pinned fold of the golden script over two label years. A behaviour change re-pins it, approved.</summary>
        public const ulong GoldenFoldValue = 0x86882E603CE5D69AUL;   // re-pinned at 4.05 PR B2 (Pacing v6: the map's static-input and PR C's forecast numbers; owner OK 2026-11-11, closing plan), over B1 0xF3B0438B4C621B3D; B1 (the weather plays at WindScale 0.85, real top-station gusts, every wind check a boundary, Pacing v5; owner OK 2026-11-11), over 4.13 0x5921F6F22F4F9B46

        /// <summary>
        /// One fold of every face-hour hash of the golden run to the given face day (the pinned value). Task 4.05: the golden
        /// plays weather, the western typical mountain's rows as they ship (<c>generic-west.rows</c>, the frozen Jackson Hole
        /// rows): the Core tests read them from <c>TestData/weather</c>, the player's <c>-simgolden</c> from its StreamingAssets
        /// (the Simulation reads no files). Null plays the stand-in hour.
        /// </summary>
        public static ulong GoldenFold(Pacing p, int faceDays, int maxStep = int.MaxValue, SimSite site = default, SimWeather weather = null)
        {
            var w = new SimWorld(p, GoldenDesign(1, false), site, 2026, weather: weather);
            var h = new StateHasher();
            Run(w, GoldenScript(p), faceDays * SimWorld.DayMs, (i, x) => { h.Add(i); h.Add(x); }, maxStep);
            return h.Value;
        }
    }
}
