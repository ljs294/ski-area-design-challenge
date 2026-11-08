using System;
using System.Collections.Generic;

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
        public const ulong GoldenFoldValue = 0x70F2A53A52947E94UL;

        /// <summary>One fold of every face-hour hash of the golden run to the given face day (the pinned value).</summary>
        public static ulong GoldenFold(Pacing p, int faceDays, int maxStep = int.MaxValue, SimSite site = default)
        {
            var w = new SimWorld(p, GoldenDesign(1, false), site, 2026);
            var h = new StateHasher();
            Run(w, GoldenScript(p), faceDays * SimWorld.DayMs, (i, x) => { h.Add(i); h.Add(x); }, maxStep);
            return h.Value;
        }
    }
}
