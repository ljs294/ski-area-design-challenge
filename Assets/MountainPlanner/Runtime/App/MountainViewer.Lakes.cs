using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MountainPlanner.Domain.Water;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    public sealed partial class MountainViewer
    {
        /// <summary>
        /// <c>-lakereport &lt;folder&gt;</c> (task 4.09, demo 101): once the area's water bodies are built, plays one label year of
        /// this area's own weather over them on a scratch world (no lifts, nothing saved; the player's game is untouched), and
        /// writes <c>bodies.csv</c> (each body's facts), <c>daily.csv</c> (each body's ice and snow every face day),
        /// <c>dates.csv</c> and <c>bodies.png</c> (the map, a colour per body, the streams blue-grey), then quits.
        /// <c>tools/lakes/report.py</c> draws the charts.
        /// </summary>
        internal void StartLakeReport(string[] args)
        {
            int i = Array.IndexOf(args, "-lakereport");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(LakeReport(Path.GetFullPath(args[i + 1])));
        }

        System.Collections.IEnumerator LakeReport(string folder)
        {
            float give = Time.unscaledTime + 10 * 60;
            while ((_resort == null || _resort.CoverReady == null || !_resort.CoverReady.IsCompleted || _simNow == null) && Time.unscaledTime < give) yield return null;
            Directory.CreateDirectory(folder);
            var table = _resort?.WaterBodies;
            if (table == null)
            {
                Debug.Log("[Lakes] no water-body table (the cover build failed): nothing to report");
                Application.Quit(1);
                yield break;
            }
            var weather = _simWeather;
            var terrain = _resort.WeatherTerrain;
            int startYear = Math.Max(2, _simNow.LabelYear);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var run = Task.Run(() => PlayLakeYear(table, weather, terrain, startYear));
            while (!run.IsCompleted) yield return null;
            if (run.IsFaulted)
            {
                Debug.LogError($"[Lakes] the year failed: {run.Exception}");
                Application.Quit(1);
                yield break;
            }
            var (daily, on, off, days) = run.Result;
            var inv = CultureInfo.InvariantCulture;
            var cal = new LabelCalendar(Pacing.Default);

            var bodies = new StringBuilder("index,kind,id,acres,depth_ft,surface_ft,clipped,centroid_x_m,centroid_y_m,ice_on_day,ice_out_day\n");
            for (int b = 0; b < table.Count; b++)
            {
                var w = table[b];
                bodies.Append(string.Format(inv, "{0},{1},{2:X16},{3:0.00},{4:0.0},{5},{6},{7:0},{8:0},{9},{10}\n", b, w.Kind, w.Id, w.AreaSquareMetres / 4046.8564224,
                                            (w.Kind == WaterBodyKind.Streams ? Pacing.Default.StreamsDepth : w.DepthTenthsFt) / 10.0, w.SurfaceFeet, w.Clipped ? 1 : 0,
                                            w.CentroidXMm / 1000.0, w.CentroidYMm / 1000.0, on[b], off[b]));
            }
            File.WriteAllText(Path.Combine(folder, "bodies.csv"), bodies.ToString());

            var csv = new StringBuilder("day,month,dom");
            for (int b = 0; b < table.Count; b++) csv.Append(",ice_").Append(b).Append(",snow_").Append(b);
            csv.Append('\n');
            for (int d = 0; d < days; d++)
            {
                csv.Append(d).Append(',').Append(cal.Month(d)).Append(',').Append(cal.Day(d));
                for (int b = 0; b < table.Count; b++)
                    csv.Append(',').Append((daily[d, 2 * b] / 100.0).ToString(inv)).Append(',').Append((daily[d, 2 * b + 1] / 10.0).ToString(inv));
                csv.Append('\n');
            }
            File.WriteAllText(Path.Combine(folder, "daily.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(folder, "site.txt"),
                $"area {_resort.Manifest?.PackageId}\nweather {(weather == null ? "none (stand-in hour)" : weather.Generic ? "typical mountain rows" : "the area's own rows")}\n" +
                $"bodies {table.Count} (lakes {table.Count - 1}), grid {table.Width}x{table.Depth} of {WaterBodyTable.CellMetres} m, table hash {table.Hash:X16}\nstart label year {startYear}\n");

            // The map: a hue per lake, the streams blue-grey, land dark.
            var png = new Texture2D(table.Width, table.Depth, TextureFormat.RGB24, false);
            var pixels = new Color32[table.Cells];
            for (int k = 0; k < pixels.Length; k++)
            {
                int body = table.Mask[k] - 1;
                pixels[k] = body < 0 ? new Color32(28, 30, 34, 255) : body == WaterBodyTable.Streams ? new Color32(120, 140, 160, 255)
                          : (Color32)Color.HSVToRGB(body * 0.618034f % 1f, 0.75f, 0.95f);
            }
            png.SetPixels32(pixels);
            png.Apply(false);
            File.WriteAllBytes(Path.Combine(folder, "bodies.png"), png.EncodeToPNG());
            Destroy(png);
            Debug.Log($"[Lakes] {table.Count} bodies over {days} face days in {clock.Elapsed.TotalSeconds:F1} s: report data in {folder}");
            Application.Quit();
        }

        /// <summary>A label year of the area's weather over the bodies on a scratch world: each face day's ice (0.01 in) and snow (0.1 in) per body.</summary>
        static (int[,] Daily, int[] On, int[] Off, int Days) PlayLakeYear(WaterBodyTable table, SimWeather weather, Domain.Weather.WeatherTerrain terrain, int startYear)
        {
            var p = Pacing.Default;
            var w = new SimWorld(p, SimDesign.Empty(0), SimSite.None, startYear, weather: weather);
            if (terrain != null) w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, terrain));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, table));
            int days = new LabelCalendar(p).YearFaceDays;
            var daily = new int[days, table.Count * 2];
            SeasonRunner.Run(w, null, days * SimWorld.DayMs - SimWorld.HourMs, (i, x) =>
            {
                if ((i + 1) % 24 != 0) return;
                int d = (int)(i / 24);
                for (int b = 0; b < table.Count; b++) { daily[d, 2 * b] = w.IceOf(b); daily[d, 2 * b + 1] = w.IceSnowOf(b); }
            });
            var on = new int[table.Count];
            var off = new int[table.Count];
            for (int b = 0; b < table.Count; b++) { on[b] = w.IceOnOf(b); off[b] = w.IceOutOf(b); }
            return (daily, on, off, days);
        }
    }
}
