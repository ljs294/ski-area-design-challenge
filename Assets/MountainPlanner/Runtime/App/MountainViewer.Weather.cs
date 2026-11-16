using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Weather;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.06: the weather on screen. After the clock reads the newest snapshot, <see cref="WeatherView"/> eases its weather
    /// and sets the fog, clouds, grade and wind. A tool or an info view thins the fog so nothing hides the work.
    /// </summary>
    public sealed partial class MountainViewer
    {
        readonly WeatherView _weatherView = new WeatherView();

        /// <summary>The weather on screen (shots, the HUD's readout, 4.17's banner).</summary>
        public WeatherView WeatherView => _weatherView;

        /// <summary><c>-wxoff</c>: no weather on screen, whatever the setting says.</summary>
        bool _wxOffSwitch;

        void UpdateWeather()
        {
            _weatherView.Enabled = !_wxOffSwitch && WeatherOptions.OnScreen;   // Settings › Weather › Weather on screen
            UpdateWeatherDemoKeys();   // demo 108 only (-wxdemo)
            if (TitleMode || _resort == null || Camera == null)
            {
                _weatherView.Update(null, 0, Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null, Camera != null ? Camera.Target : Vector3.zero, Camera != null ? Camera.Distance : 3500,
                                    false, Time.unscaledDeltaTime, 0, Lighting, null);
                return;
            }
            bool thin = (_tools != null && _tools.Armed) || (_layers != null && _layers.Info != MapLayers.InfoView.None);
            int zref = _simWeather?.Rows.ZrefFeet ?? 0;
            _weatherView.Area = _resort.WeatherTerrain;
            // Settings › Weather › Cloud ceiling: 1,000 ft over the map's highest ground.
            _weatherView.CeilingFloor = (float)(_resort.Cache.HeightMin + _resort.Cache.HeightRange) + WeatherOptions.CeilingClearance;
            _weatherView.Update(_simNow, zref, Camera.GetComponent<UnityEngine.Camera>(), Camera.Target, Camera.Distance, thin, Time.unscaledDeltaTime,
                                _resort.Manifest.Crs.GridConvergenceDegrees, Lighting, Forest?.Wind);
        }
    }
}
