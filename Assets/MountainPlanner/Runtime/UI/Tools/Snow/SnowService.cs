using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;

namespace MountainPlanner.UI.Tools
{
    /// <summary>Task 4.15: the edited ground under a design point (TE5's frame through the earthwork's site), mm.</summary>
    public sealed class SnowGround : IGroundHeights
    {
        readonly EditedHeights _heights;
        readonly EarthworkSite _site;

        public SnowGround(EditedHeights heights, EarthworkSite site)
        {
            _heights = heights ?? throw new ArgumentNullException(nameof(heights));
            _site = site;
        }

        public bool TryHeightMm(PointMm p, out int heightMm)
        {
            _site.PointOf(p, out double x, out double y);
            double m = _heights.At(x, y);
            if (double.IsNaN(m) || double.IsInfinity(m)) { heightMm = 0; return false; }
            heightMm = (int)Math.Round(m * 1000);
            return true;
        }
    }

    /// <summary>
    /// Task 4.15 (HY-08, "snappy calculations"): solves a snowmaking network the way the game plays it, on any thread: the
    /// same compile (buried, sampled), the same solve (<see cref="SnowSolve"/>) and the same guns (<see cref="SnowAsks"/>). The
    /// app makes one per ground, water and weather; the tools solve their drafts with it while you build, and the map draws
    /// the game's own solve from the simulation.
    /// <para>
    /// A preview stands its sources at full pool, supplying (the game's solve reads their water now); everything else is the
    /// game's.
    /// </para>
    /// </summary>
    public sealed class SnowService
    {
        public readonly IGroundHeights Ground;
        readonly Func<ulong, int?> _levelMm;
        readonly WeatherHour? _hour;
        readonly int _zrefFeet;
        readonly WeatherTerrain _terrain;
        readonly Pacing _pacing;

        /// <param name="levelMm">A source's full pool (mm above the datum), or null for one the area doesn't have.</param>
        /// <param name="hour">This face hour's weather, or null before the first (guns then run warm).</param>
        public SnowService(IGroundHeights ground, Func<ulong, int?> levelMm, WeatherHour? hour = null, int zrefFeet = 0,
                           WeatherTerrain terrain = null, Pacing pacing = null)
        {
            Ground = ground ?? throw new ArgumentNullException(nameof(ground));
            _levelMm = levelMm ?? (_ => null);
            _hour = hour;
            _zrefFeet = zrefFeet;
            _terrain = terrain;
            _pacing = pacing ?? Pacing.Default;
        }

        /// <summary>The same ground and water at a new face hour's weather.</summary>
        public SnowService WithWeather(WeatherHour? hour, int zrefFeet, WeatherTerrain terrain) =>
            new SnowService(Ground, _levelMm, hour, zrefFeet, terrain, _pacing);

        public bool HasWeather => _hour.HasValue;

        /// <summary>Compiles a network on this ground (any thread).</summary>
        public HydraulicGraph Compile(SnowNetwork net) =>
            HydraulicGraph.Compile(net, Ground, SnowRules.BuryMm, SnowRules.SampleMm, _levelMm);

        /// <summary>Solves a network: the guns as set, or every gun (Check network). Any thread; tick −1 marks a preview.</summary>
        public SnowSolution Solve(SnowNetwork net, bool everyGun) => Solve(Compile(net), everyGun);

        public SnowSolution Solve(HydraulicGraph g, bool everyGun)
        {
            var asks = new GunAsk[g.Guns.Length];
            SnowAsks.Fill(g, asks, everyGun, _hour, _zrefFeet, _terrain, _pacing);
            var s = new SnowSolve(g, _pacing.MaxResolves, _pacing.PipeClass);
            s.Run(asks, Level, cold: true);
            return new SnowSolution(s, -1, everyGun);
        }

        SourceLevel Level(ulong id) => _levelMm(id) is int mm ? new SourceLevel(mm, true, false, false) : new SourceLevel(0, false, true, false);
    }
}
