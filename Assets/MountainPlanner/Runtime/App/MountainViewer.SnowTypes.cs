using System;
using System.Globalization;
using System.IO;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.08 PR B, a capture flag for the snow's looks: <c>-snowtypes [metres] [passes] [moguls]</c> draws the eleven snow
    /// types side by side, stripes across x in a square round the view's target (220 m by default, 20 m a type), on the screen only
    /// (<see cref="Presentation.SurfaceStates.ShowTypeGallery"/>; the simulation's snow is untouched). The trails' fine cells
    /// in it carry <c>passes</c> since their last grooming (0 by default), so the worn-in tracks show by type, and <c>moguls</c>
    /// (0-255, 0 by default).
    /// </summary>
    public sealed partial class MountainViewer
    {
        static (float Metres, int Passes, int Moguls)? _snowTypesWanted;

        /// <summary><c>-snowtypes</c>'s square (metres; 0: none) and passes. Read once (the command line is a new array each time).</summary>
        internal static (float Metres, int Passes, int Moguls) SnowTypesWanted => _snowTypesWanted ??= ReadSnowTypes();

        static (float Metres, int Passes, int Moguls) ReadSnowTypes()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-snowtypes");
            if (i < 0) return (0, 0, 0);
            float metres = i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float m) ? Mathf.Clamp(m, 22, 2000) : 220;
            int passes = i + 2 < args.Length && int.TryParse(args[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? Mathf.Clamp(p, 0, 1020) : 0;
            int moguls = i + 3 < args.Length && int.TryParse(args[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) ? Mathf.Clamp(g, 0, 255) : 0;
            return (metres, passes, moguls);
        }

        /// <summary>A capture's last step before its picture: the gallery round the view's target, then a few frames to upload it.</summary>
        System.Collections.IEnumerator ShowSnowTypes()
        {
            var (metres, passes, moguls) = SnowTypesWanted;
            if (metres <= 0 || _resort?.States == null || Camera == null) yield break;
            var target = Camera.Target;
            var window = new Rect(target.x - metres / 2, target.z - metres / 2, metres, metres);
            yield return ShowGallery(window, passes, moguls);
        }

        System.Collections.IEnumerator ShowGallery(Rect window, int passes, int moguls)
        {
            _resort.States.ShowTypeGallery(window, passes, moguls);
            Debug.Log($"[SnowTypes] the eleven types in {window.width / 11:F0} m stripes across x from ({window.xMin:F0}, {window.yMin:F0}), " +
                      $"{window.width:F0} m square; {passes} passes and {moguls} moguls on the trails' cells; on the screen only");
            _snowTypesWindow = window;
            for (int f = 0; f < 60 && (_resort.States.TilesWaiting > 0 || _resort.States.Fine.SlotsWaiting > 0); f++) yield return null;
        }

        Rect _snowTypesWindow;

        /// <summary>
        /// <c>-lookshots &lt;list&gt; &lt;folder&gt;</c> (task 4.08 PR B review): many pictures from one run of the opened save, one per
        /// line of the list: <c>name x,z,distance,yaw,pitch [HH:MM] [gx,gz,metres,passes,moguls] [gallery=off] [sun=toward|away|side]
        /// [wx=look]</c> ('#' starts a comment). The five-number part is a <c>-snowtypes</c> gallery centred at (gx, gz); it
        /// replaces the one before it, and <c>gallery=off</c> clears it. With <c>sun=</c> the heading comes from the sun at that
        /// time (toward it, away from it or across it) and the line's yaw is added to it; the run stops if the picture then
        /// doesn't face as asked (V1). <c>wx=</c> pins a weather look, as <c>-wxlook</c>. Each picture is
        /// <c>folder/name.png</c>, its stripes and its heading to the sun logged as <c>[SnowTypes] name ...</c>; then the run
        /// quits. A line that can't be read stops the run with an error (R21).
        /// </summary>
        static readonly int CordLightOffId = Shader.PropertyToID("_CordLightOff");

        System.Collections.IEnumerator RunLookShots(string list, string folder)
        {
            yield return WaitForCaptureReady();
            // Task 4.08 G1: with -simto, the pictures wait for the date and are taken on the paused world (the groomed night's
            // corduroy, without a save and reopen between).
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-simto") >= 0)
            {
                float give = Time.unscaledTime + 120;
                while (!_simToRunning && Time.unscaledTime < give) yield return null;
                while (_simToRunning) yield return null;
            }
            Directory.CreateDirectory(folder);
            int taken = 0, number = 0;
            foreach (var raw in File.ReadAllLines(list))
            {
                number++;
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                if (!LookShot.TryRead(line, out var shot, out string why))
                {
                    Debug.LogError($"[SnowTypes] line {number} of {list}: {why}: {line}");
                    Application.Quit(1);
                    yield break;
                }
                Shader.SetGlobalFloat(CordLightOffId, shot.CordOff ? 1 : 0);
                if (shot.Weather != null && !PinLook(shot.Weather))
                {
                    Debug.LogError($"[SnowTypes] line {number} of {list}: no weather look named {shot.Weather}");
                    Application.Quit(1);
                    yield break;
                }
                if (shot.Seconds >= 0)
                {
                    var now = Lighting.Clock.Now;
                    Lighting.SetTime(new ViewTime(now.Year, now.DayOfYear, shot.Seconds % ViewTime.SecondsPerDay));
                    yield return null;   // the sun moves with the time
                }
                float yaw = shot.Yaw;
                Vector4 toSun = Shader.GetGlobalVector("_SkySunDirection");
                var sunFlat = new Vector2(toSun.x, toSun.z);
                if (shot.Sun != null)
                {
                    if (sunFlat.sqrMagnitude < 1e-4f)
                    {
                        Debug.LogError($"[SnowTypes] {shot.Name}: sun={shot.Sun}, but the sun is straight overhead or not set");
                        Application.Quit(1);
                        yield break;
                    }
                    yaw += Mathf.Atan2(sunFlat.x, sunFlat.y) * Mathf.Rad2Deg + (shot.Sun == "away" ? 180 : shot.Sun == "side" ? 90 : 0);
                }
                Camera.Frame(new Vector3(shot.X, _resort.Surface.HeightAt(shot.X, shot.Z), shot.Z), shot.Distance);
                Camera.SetAngles(yaw, shot.Pitch);
                if (shot.GalleryOff) { _resort.States.ClearTypeGallery(); _snowTypesWindow = default; }
                if (shot.Metres > 0)
                {
                    float m = Mathf.Clamp(shot.Metres, 22, 2000);
                    yield return ShowGallery(new Rect(shot.Gx - m / 2, shot.Gz - m / 2, m, m), shot.Passes, shot.Moguls);
                }
                for (int i = 0; i < 90; i++) yield return null;   // the camera's ease, LOD, shadows and uploads settle
                var look = Camera.transform.forward;
                var lookFlat = new Vector2(look.x, look.z);
                float facing = lookFlat.sqrMagnitude > 1e-6f && sunFlat.sqrMagnitude > 1e-6f ? Vector2.Dot(lookFlat.normalized, sunFlat.normalized) : 0;
                bool wrong = (shot.Sun == "toward" && Mathf.Abs(shot.Yaw) <= 30 && facing < 0.8f) || (shot.Sun == "away" && Mathf.Abs(shot.Yaw) <= 30 && facing > -0.8f);
                if (wrong)
                {
                    Debug.LogError($"[SnowTypes] {shot.Name}: asked sun={shot.Sun}, but the picture faces {facing:F2} of the way to the sun");
                    Application.Quit(1);
                    yield break;
                }
                LogSnowTypesOnScreen(shot.Name);
                LogCorduroyOnScreen(shot.Name);
                string path = Path.Combine(folder, shot.Name + ".png");
                ScreenCapture.CaptureScreenshot(path);
                yield return null;
                yield return null;
                Debug.Log(FormattableString.Invariant($"[SnowTypes] {shot.Name}: camera {Camera.transform.position}, target {Camera.Target}, time {Lighting.Clock.Now}, facing the sun {facing:F2}, sun up {toSun.y:F2}, weather {shot.Weather ?? "the game's"}, saved {path}"));
                taken++;
            }
            Debug.Log($"[SnowTypes] {taken} pictures in {folder}");
            Shader.SetGlobalFloat(CordLightOffId, 0);
            Application.Quit();
        }

        /// <summary>One line of a <c>-lookshots</c> list.</summary>
        struct LookShot
        {
            public string Name, Sun, Weather;
            /// <summary>Task 4.08 G1: <c>cord=off</c> leaves out the corduroy's groove light (the A/B pictures; dev only).</summary>
            public bool CordOff;
            public float X, Z, Distance, Yaw, Pitch, Gx, Gz, Metres;
            public int Seconds, Passes, Moguls;
            public bool GalleryOff;

            public static bool TryRead(string line, out LookShot shot, out string why)
            {
                shot = new LookShot { Seconds = -1 };
                why = null;
                var inv = CultureInfo.InvariantCulture;
                var part = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (part.Length < 2) { why = "a shot needs a name and x,z,distance,yaw,pitch"; return false; }
                shot.Name = part[0];
                try
                {
                    var v = Array.ConvertAll(part[1].Split(','), t => float.Parse(t, inv));
                    if (v.Length != 5) { why = "the view is x,z,distance,yaw,pitch"; return false; }
                    shot.X = v[0]; shot.Z = v[1]; shot.Distance = v[2]; shot.Yaw = v[3]; shot.Pitch = v[4];
                    for (int k = 2; k < part.Length; k++)
                    {
                        string p = part[k];
                        if (p == "gallery=off") shot.GalleryOff = true;
                        else if (p.StartsWith("sun=", StringComparison.Ordinal))
                        {
                            shot.Sun = p.Substring(4);
                            if (shot.Sun != "toward" && shot.Sun != "away" && shot.Sun != "side") { why = "sun= is toward, away or side"; return false; }
                        }
                        else if (p.StartsWith("wx=", StringComparison.Ordinal)) shot.Weather = p.Substring(3);
                        else if (p == "cord=off") shot.CordOff = true;
                        else if (p.Contains(":"))
                        {
                            if (!TimeSpan.TryParse(p, inv, out var hm)) { why = $"no time of day in {p}"; return false; }
                            shot.Seconds = (int)hm.TotalSeconds;
                        }
                        else
                        {
                            var g = Array.ConvertAll(p.Split(','), t => float.Parse(t, inv));
                            if (g.Length < 3 || g.Length > 5) { why = "the gallery is gx,gz,metres[,passes[,moguls]]"; return false; }
                            shot.Gx = g[0]; shot.Gz = g[1]; shot.Metres = g[2];
                            shot.Passes = g.Length > 3 ? (int)g[3] : 0; shot.Moguls = g.Length > 4 ? (int)g[4] : 0;
                        }
                    }
                }
                catch (FormatException) { why = "a number can't be read"; return false; }
                return true;
            }
        }

        /// <summary>Task 4.08 G1: how much groomed corduroy the picture shows, logged right before the capture, so a still with
        /// none can't pass unnoticed again (p4-08-findings V7). A 32 × 18 grid of screen points on the ground: how many lie on
        /// groomed fine snow (the corduroy's cells, SnowCorduroyAt's twin), their mean wear (0 new, 1 skied to a quarter), and
        /// up to 24 of them and 24 points off the trails (fractions from the top left), for the measured steps.</summary>
        void LogCorduroyOnScreen(string tag)
        {
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            var fine = _resort?.States?.Fine;
            if (cam == null || fine == null) { Debug.Log($"[SnowTypes] {tag} corduroy: no fine snow to read"); return; }
            int ground = 0, trail = 0, groomed = 0, listedOn = 0, listedOff = 0;
            float wear = 0;
            var on = new System.Text.StringBuilder();
            var off = new System.Text.StringBuilder();
            for (int j = 0; j < 18; j++)
                for (int i = 0; i < 32; i++)
                {
                    float fx = (i + 0.5f) / 32, fy = (j + 0.5f) / 18;
                    var ray = cam.ScreenPointToRay(new Vector3(fx * Screen.width, (1 - fy) * Screen.height, 0));
                    float along = Camera.GroundAlong(ray, 30000);
                    if (float.IsNaN(along)) continue;
                    var g = ray.GetPoint(along);
                    ground++;
                    var st = fine.StateAt(g.x, g.z);
                    string at = FormattableString.Invariant($" ({fx:F3}, {fy:F3})");
                    if (st.Type < 0)
                    {
                        if (listedOff < 24 && (i + j) % 3 == 0) { off.Append(at); listedOff++; }
                        continue;
                    }
                    trail++;
                    if (!st.Groomed) continue;
                    groomed++;
                    wear += Mathf.Clamp01(st.Passes / 800f);
                    if (listedOn < 24 && (i + j) % 2 == 0) { on.Append(at); listedOn++; }
                }
            Debug.Log(FormattableString.Invariant($"[SnowTypes] {tag} corduroy: {groomed} of {ground} ground points groomed ({trail} on trail snow), mean wear {(groomed > 0 ? wear / groomed : 0):F2}; groomed at:{on}; off the trails at:{off}"));
        }

        /// <summary>Each stripe on the picture (fractions from the top left), logged right before the capture so the camera has
        /// settled; a review still is labelled from the game rather than by eye. One line per type, its centre line sampled at the
        /// window's middle, toward both ends and between (the labeller takes the clearest spot: p4-08-findings V5).</summary>
        void LogSnowTypesOnScreen(string tag = null)
        {
            var window = _snowTypesWindow;
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (window.width <= 0 || cam == null || _resort?.Surface == null) return;
            var names = (SnowType[])Enum.GetValues(typeof(SnowType));
            for (int k = 1; k <= 11; k++)
            {
                float x = window.xMin + (k - 0.5f) * window.width / 11;
                var sb = new System.Text.StringBuilder($"[SnowTypes] {(tag != null ? tag + " " : "")}stripe {k} {(k < names.Length ? names[k].ToString() : "?")} x {x:F0}:");
                foreach (float t in new[] { 0.5f, 0.15f, 0.85f, 0.3f, 0.7f })
                {
                    float z = window.yMin + t * window.height;
                    var at = cam.WorldToScreenPoint(new Vector3(x, _resort.Surface.HeightAt(x, z), z));
                    bool on = at.z > 0 && at.x >= 0 && at.y >= 0 && at.x <= Screen.width && at.y <= Screen.height;
                    sb.Append(on ? FormattableString.Invariant($" ({at.x / Screen.width:F3}, {1 - at.y / Screen.height:F3})") : " (off)");
                }
                Debug.Log(sb.ToString());
            }
        }
    }
}
