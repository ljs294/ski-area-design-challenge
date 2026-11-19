using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.15 (SG-01, plan Q4): which guns a solve asks to run, and at what wet bulb. One rule for the worker's solve and
    /// the tools' live preview, so the numbers a tool shows while you build are the numbers the game then plays.
    /// - Until 4.16's schedules a gun runs only when set On (forced: SG-01's stage at its wet bulb, or the forced stage when
    ///   too warm); Auto waits. "Every gun" (Check network) forces them all.
    /// - The wet bulb is the gun's own height's (WX-35's air at the hydrant's grade); with no weather yet it is too warm to
    ///   make snow, so a forced gun runs warm.
    /// </summary>
    public static class SnowAsks
    {
        /// <summary>Fills <paramref name="asks"/> (one per <see cref="HydraulicGraph.Guns"/>); <paramref name="hour"/> null: no weather yet.</summary>
        public static void Fill(HydraulicGraph g, GunAsk[] asks, bool everyGun, WeatherHour? hour, int zrefFeet, WeatherTerrain terrain, Pacing pacing)
        {
            if (asks.Length < g.Guns.Length) throw new ArgumentException("one ask per gun", nameof(asks));
            for (int i = 0; i < g.Guns.Length; i++)
            {
                var gun = g.Guns[i];
                bool on = gun.Override == GunOverride.On;
                int wb = GunCatalog.TooWarmTenthsF + 1;
                int node = g.GunNode[i];
                if (hour.HasValue && node >= 0)
                {
                    int feet = (int)Math.Round(g.GradeFt[node]);
                    WeatherFields.AirAt(hour.Value, zrefFeet, feet, terrain, pacing, out _, out _, out _, out wb, out _, out _);
                }
                asks[i] = new GunAsk { Wanted = everyGun || on, Forced = everyGun || on, WetBulbTenthsF = wb, Priority = 3 };
            }
        }
    }
}
