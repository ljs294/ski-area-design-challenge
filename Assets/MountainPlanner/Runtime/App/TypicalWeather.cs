using System;
using System.IO;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.05 PR B1: the weather the simulation plays for an area. Its own <c>weather.rows</c> when the manifest lists them,
    /// otherwise the typical mountain's (<see cref="WeatherGeneric"/>): two frozen rows a build copies into
    /// StreamingAssets/Weather (Editor/DemoBundle), read in the editor from the repo's <c>TestData/weather</c>, where they're
    /// the Jackson Hole (West) and Jackson, New Hampshire (East) test sites' rows.
    /// </summary>
    public static class TypicalWeather
    {
        public const string FolderName = "Weather";
        /// <summary>The test sites whose frozen rows are the typical mountains (TestData/weather/&lt;site&gt;/weather.rows).</summary>
        public const string WestSite = "jackson-hole-2km", EastSite = "jackson-nh-4km";

        /// <summary>The template rows for <paramref name="longitude"/>, or null when this install has none (it says why in the log).</summary>
        public static WeatherRows Template(double longitude)
        {
            string file = WeatherGeneric.FileFor(longitude);
            string[] places = { Path.Combine(Application.streamingAssetsPath, FolderName, file), RepoRows(WeatherGeneric.East(longitude)) };
            foreach (string path in places)
            {
                if (!File.Exists(path)) continue;
                try { return WeatherRowsFile.FromBytes(File.ReadAllBytes(path)); }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning($"[Weather] the typical mountain's rows at {path} don't read: {e.Message}");
                }
            }
            Debug.LogWarning($"[Weather] no typical mountain's rows ({file}) in this install: the simulation plays the stand-in hour.");
            return null;
        }

        /// <summary>The repo's frozen rows a build ships as the typical mountain (TestData/weather, Git LFS): the editor reads them there.</summary>
        public static string RepoRows(bool east) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "TestData", "weather", east ? EastSite : WestSite, WeatherRowsFile.FileName);

        /// <summary>-simgolden's weather (SI-08): the western typical mountain's rows unmoved, as the Core tests play them.</summary>
        public static SimWeather Golden()
        {
            string bundled = Path.Combine(Application.streamingAssetsPath, FolderName, WeatherGeneric.WestFile);
            string path = File.Exists(bundled) ? bundled : RepoRows(false);
            return SimWeather.Own(WeatherRowsFile.FromBytes(File.ReadAllBytes(path)));
        }

        /// <summary>
        /// The area's weather: its own rows (checked against the manifest's hash), or the typical mountain's moved to its sun,
        /// at <paramref name="baseFeet"/> and <paramref name="summitFeet"/> when known (0: the layer's own range until the
        /// snowpack's heights say). Null when there are no rows at all.
        /// </summary>
        public static SimWeather For(PackageManifest m, string packageFolder, int baseFeet, int summitFeet)
        {
            if (m == null) return null;
            if (m.Weather != null && !string.IsNullOrEmpty(m.Weather.RowsFile) && packageFolder != null)
            {
                try { return SimWeather.Own(WeatherRowsFile.Read(packageFolder, m.Weather.RowsSha256)); }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"[Weather] the area's weather rows don't read, so it plays the typical mountain's: {e.Message}");
                }
            }
            var template = Template(m.Site.Longitude);
            if (template == null) return null;
            var rows = WeatherGeneric.MoveTo(template, m.Site.Latitude, m.Site.Longitude);
            if (baseFeet <= 0 || summitFeet <= baseFeet) (baseFeet, summitFeet) = LayerRangeFeet(m, template);
            return new SimWeather(rows, generic: true, baseFeet, summitFeet);
        }

        /// <summary>Before the snowpack's percentiles: the core heights' own range, a tenth up and a twentieth down.</summary>
        static (int, int) LayerRangeFeet(PackageManifest m, WeatherRows template)
        {
            var core = m.Layers.Find(l => l.Id == "heights-core");
            if (core == null || !(core.Max > core.Min)) return (template.BaseFeet, template.SummitFeet);
            double range = core.Max - core.Min;
            return ((int)Math.Round((core.Min + 0.10 * range) / 0.3048), (int)Math.Round((core.Min + 0.95 * range) / 0.3048));
        }
    }
}
