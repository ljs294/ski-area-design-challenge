#nullable enable
using System;
using MountainPlanner.Acquisition.IO;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>
    /// Puts <see cref="WeatherBuild.BuildAsync"/>'s steps on a download's <see cref="ProgressTracker"/> (task 4.05, PR A2):
    /// "Fetching weather history" from the weather servers, then "Calibrating the weather", which uses no network.
    /// </summary>
    public sealed class WeatherSteps : IProgress<(string Step, double Share)>
    {
        readonly ProgressTracker _tracker;
        string _step = "";
        double _share;

        public WeatherSteps(ProgressTracker tracker) { _tracker = tracker; }

        public void Report((string Step, double Share) value)
        {
            _share = Math.Max(0, Math.Min(1, value.Share));
            if (value.Step == _step) return;
            _step = value.Step;
            bool fetching = _step == WeatherBuild.FetchStep;
            if (fetching) _tracker.SetPhase(0, WeatherBuild.FetchShare);
            else _tracker.SetPhase(WeatherBuild.FetchShare, 1);
            // Each step has its own phase (fetching 0–70 %, calibrating 70–100 %), so its share runs across all of it.
            _tracker.BeginStep(_step, fetching ? 1 : 2, 2, () => _share, fetching ? Servers.Weather : null, wholePhase: true);
        }
    }
}
