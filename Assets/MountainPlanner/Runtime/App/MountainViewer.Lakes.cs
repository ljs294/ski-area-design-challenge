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

        // ---------------- Task 4.09 PR B: each body on screen ----------------

        WaterBodyTable _lakeTable;
        byte[] _lakeMap;
        long _lakeCopiedHour = -1;
        int _lakeCopiedCount = -1;
        bool _lakeFlagsRead, _lakeFollowSim, _lakeMix;
        LabelCalendar _lakeCalendar;
        long _lakeShownKey;
        string _lakeShownLine;

        /// <summary>
        /// Binds the area's body table to the screen (again whenever it changes, 4.14's ponds), then copies each body's surface
        /// from the snapshot when its face hour or body count moved, matched by id. A capture or benchmark keeps iteration 1's
        /// look unless <c>-lakesim</c> (as the sun does), so stored captures and baselines don't move; <c>-lakemix</c> draws
        /// odd bodies open and the rest snow on ice (the GPU check); an override (the panel, <c>-lake</c>) stops the copy.
        /// </summary>
        void ShowLakes(SimSnapshot s)
        {
            var states = _resort?.States;
            var table = _resort?.WaterBodies;
            if (states == null || table == null) return;
            if (!_lakeFlagsRead)
            {
                _lakeFlagsRead = true;
                var args = Environment.GetCommandLineArgs();
                _lakeMix = Array.IndexOf(args, "-lakemix") >= 0;
                _lakeFollowSim = !Flow.AppFlow.CaptureRun || Array.IndexOf(args, "-lakesim") >= 0;
            }
            if (states.BoundTable != table.Hash || _lakeTable != table)
            {
                var map = LakeSurfaces.Dilated(table);
                if (!states.SetBodies(table, map)) { _lakeTable = table; _lakeMap = null; return; }
                _lakeTable = table;
                _lakeMap = map;
                _lakeCopiedHour = -1;
                if (_lakeMix)
                    for (int b = 0; b < states.Water.Count; b++)
                        states.Water.Set(b, b % 2 == 1 ? new WaterSurface(WaterSurfaceState.OpenWater, 0, 0) : WaterSurface.IterationOne);
            }
            if (_lakeMap == null || _lakeMix || !_lakeFollowSim || states.Water.Override.HasValue || s.WaterBodyCount == 0) return;
            long hour = s.FaceDay * 24L + s.FaceMs / SimWorld.HourMs;
            if (hour == _lakeCopiedHour && s.WaterBodyCount == _lakeCopiedCount) return;
            _lakeCopiedHour = hour;
            _lakeCopiedCount = s.WaterBodyCount;
            for (int b = 0; b < s.WaterBodyCount; b++)
            {
                int i = table.IndexOf(s.WaterBodyIds[b]);
                if (i < 0 || i >= states.Water.Count) continue;
                states.Water.Set(i, LakeSurfaces.Surface(s.WaterStates[b], s.IceHundredthsIn[b], s.IceSnowTenthsIn[b]));
            }
        }

        /// <summary>The panel and <c>-lake</c>: one look on every body until "From the simulation" (null) clears it.</summary>
        void SetLakeOverride(WaterSurface? surface)
        {
            var water = _resort?.States?.Water;
            if (water == null) return;
            water.SetOverride(surface);
            _lakeCopiedHour = -1;   // the next snapshot copies again
        }

        /// <summary>
        /// The readout over a body (the dilated map, the cells the shader draws): its line, or null over land or without the
        /// simulation's numbers for it. <paramref name="key"/> changes only when the line would, so the caller formats it
        /// only then.
        /// </summary>
        bool LakeUnder(float x, float z, out long key, out int snapshotIndex)
        {
            key = 0; snapshotIndex = -1;
            var s = _simNow;
            if (_lakeMap == null || _lakeTable == null || s == null || s.WaterBodyCount == 0) return false;
            int cell = LakeCell(x, z);
            if (cell < 0) return false;
            int id = _lakeMap[cell];
            if (id == 0) return false;
            ulong bodyId = _lakeTable[id - 1].Id;
            for (int b = 0; b < s.WaterBodyCount; b++)
                if (s.WaterBodyIds[b] == bodyId) { snapshotIndex = b; break; }
            if (snapshotIndex < 0) return false;
            int k = snapshotIndex;
            key = ((long)(id & 0xFF) << 56) ^ ((long)s.WaterStates[k] << 52) ^ ((long)(s.IceHundredthsIn[k] / 25) << 32)
                  ^ ((long)(s.IceSnowTenthsIn[k] / 5) << 20) ^ ((long)(s.IceOnDay[k] & 0x3FF) << 10) ^ (s.IceOutDay[k] & 0x3FF);
            return true;
        }

        /// <summary>The body map's cell under a point (local metres), or −1 off the map.</summary>
        int LakeCell(float x, float z)
        {
            int cx = Mathf.FloorToInt((x - _resort.Ring.xMin) / WaterBodyTable.CellMetres), cy = Mathf.FloorToInt((z - _resort.Ring.yMin) / WaterBodyTable.CellMetres);
            return cx < 0 || cy < 0 || cx >= _lakeTable.Width || cy >= _lakeTable.Depth ? -1 : cy * _lakeTable.Width + cx;
        }

        string LakeLine(int k, int body, Domain.Measure.UnitSystem units)
        {
            var s = _simNow;
            _lakeCalendar ??= new LabelCalendar(Pacing.Default);
            int on = s.IceOnDay[k], off = s.IceOutDay[k];
            int days = _lakeCalendar.YearFaceDays;
            bool hasOn = on >= 0 && on < days, hasOff = off >= 0 && off < days;
            return LakeSurfaces.Readout(_lakeTable[body].Kind, s.WaterStates[k], s.IceHundredthsIn[k], s.IceSnowTenthsIn[k],
                                        hasOn ? _lakeCalendar.Month(on) : 0, hasOn ? _lakeCalendar.Day(on) : 0,
                                        hasOff ? _lakeCalendar.Month(off) : 0, hasOff ? _lakeCalendar.Day(off) : 0, units);
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
