using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Geo;
using MountainPlanner.UI.Picker;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.App.Picker
{
    /// <summary>
    /// The Picker Lab (task 13): the site picker on its own, for the demo (demo.bat) and for screenshots.
    /// It is not the game's flow (task 14 owns title → picker → download → card → open). Download writes
    /// the chosen site as arguments for the acquire CLI to <c>-out</c> (default
    /// %LOCALAPPDATA%\SkiAreaDesignChallenge\picked-site.args) and quits; demo.bat hands them to the
    /// existing downloader.
    /// Unattended: -offline (no network: shows the offline panel), -search "text" (as if typed and Enter
    /// pressed), -place lat,lon, -size km, -zoom n, -imagery, -screenshot file.png (after the map settles, then quits).
    /// </summary>
    public sealed class PickerLab : MonoBehaviour
    {
        public SitePicker Picker;

        string _out;

        IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge");
            _out = Arg("-out") ?? Path.Combine(data, "picked-site.args");
            if (args.Contains("-offline")) Http.NetworkDisabled = true;

            Picker.Services = new SitePickerServices(Path.Combine(data, "download-cache", "picker"));
            Picker.SiteChosen += OnChosen;
            Picker.Cancelled += () => Quit(null);
            Picker.Show();
            yield return null;

            if (Arg("-size") is string size) Picker.Model.SetSize(double.Parse(size, CultureInfo.InvariantCulture));
            if (args.Contains("-imagery")) Picker.Map.Imagery = true;
            if (Arg("-place") is string place)
            {
                var parts = place.Split(',');
                var point = new GeoPoint(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
                int zoom = Arg("-zoom") is string z ? int.Parse(z, CultureInfo.InvariantCulture) : 12;
                Picker.Map.SetCentre(point, zoom);
                Picker.PlaceAt(point);
            }
            if (Arg("-search") is string search)
            {
                Picker.Document.rootVisualElement.Q<TextField>("search").value = search;
                Picker.RunSearch();
            }

            if (Arg("-screenshot") is string shot)
            {
                // Long enough for tiles, the coverage overlay, the name lookup and the estimate to arrive.
                yield return new WaitForSecondsRealtime(15f);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shot)));
                ScreenCapture.CaptureScreenshot(Path.GetFullPath(shot));
                yield return null;
                yield return null;
                Quit(null);
            }
        }

        void OnChosen(PickedSite site)
        {
            string name = new string(site.Name.Where(c => c >= ' ' && "\"&|<>^%!".IndexOf(c) < 0).ToArray()).Trim();
            if (name.Length == 0) name = "New Mountain";
            string line = string.Format(CultureInfo.InvariantCulture, "--name \"{0}\" --lat {1:R} --lon {2:R} --km {3:0.0}",
                name, site.Centre.Latitude, site.Centre.Longitude, site.SizeKm);
            Quit(line);
        }

        void Quit(string line)
        {
            try
            {
                if (line != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_out));
                    File.WriteAllText(_out, line + "\r\n", new UTF8Encoding(false));
                    Debug.Log($"[PickerLab] Chosen: {line} → {_out}");
                }
                else if (File.Exists(_out)) File.Delete(_out);
            }
            catch (Exception ex) { Debug.LogError($"[PickerLab] {ex.Message}"); }
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
