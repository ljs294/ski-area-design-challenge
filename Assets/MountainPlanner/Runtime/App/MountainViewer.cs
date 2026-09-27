using System;
using System.IO;
using System.Linq;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// The Mountain Viewer scene (task 06): opens a downloaded mountain from the library and shows it with
    /// the temporary fly camera. It's a stepping stone: the menu, library and HUD screens replace this
    /// overlay in tasks 13–14.
    ///
    /// Which mountain: the -package &lt;folder&gt; command-line argument, else the library's Jackson Hole
    /// (the demo), else the first mountain in the library. The library is
    /// %LOCALAPPDATA%\SkiAreaDesignChallenge, where tools/acquire (demo.bat 11–14) puts downloads.
    /// </summary>
    public sealed class MountainViewer : MonoBehaviour
    {
        public TerrainDetail Detail = TerrainDetail.High;
        /// <summary>URP Terrain/Lit, referenced from the scene so builds keep the shader.</summary>
        public Material TerrainMaterial;
        public DebugFlyCamera Camera;

        string _status = "Starting";
        float _fraction;
        OpenedResort _resort;
        string _error;
        float _fps;
        bool _help = true;

        public static string DataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge");

        async void Start()
        {
            Application.targetFrameRate = -1;
            // Task 15 investigates instanced terrain; -instancing turns it on for that work.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-instancing") >= 0) TerrainTiles.DrawInstanced = true;
            string folder = PickPackage();
            if (folder == null)
            {
                _error = "No mountains downloaded yet.\nRun demo.bat and choose 12 (Jackson Hole, 5 km), then start the viewer again.";
                return;
            }
            try
            {
                var progress = new Progress<OpenProgress>(p => { _status = p.Detail; _fraction = p.Fraction; });
                _resort = await ResortOpener.OpenAsync(folder, null, Detail, progress, destroyCancellationToken, TerrainMaterial);
                _status = $"Opened in {_resort.Seconds:F1} s";
                Debug.Log($"[MountainViewer] {_resort.Manifest.Site.Name}: {_resort.Tiles.Count} tiles opened in {_resort.Seconds:F2} s");
                if (Camera != null)
                {
                    Camera.Surface = _resort.Surface;
                    float centre = _resort.Surface.HeightAt(0, 0);
                    Camera.Frame(new Vector3(0, float.IsNaN(centre) ? 2500 : centre, 0), _resort.Manifest.Site.SizeMetres * 1.1f);
                }
                // Unattended check: -screenshot <file.png> captures the view once it has settled, then quits.
                string[] args = Environment.GetCommandLineArgs();
                int shot = Array.IndexOf(args, "-screenshot");
                if (shot >= 0 && shot + 1 < args.Length) StartCoroutine(CaptureAndQuit(args[shot + 1]));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _error = "Couldn't open the mountain: " + e.Message;
                Debug.LogException(e);
            }
        }

        System.Collections.IEnumerator CaptureAndQuit(string path)
        {
            for (int i = 0; i < 90; i++) yield return null; // let LOD and shadows settle
            var cam = Camera != null ? Camera.transform.position : Vector3.zero;
            Debug.Log($"[MountainViewer] camera {cam}, ground below {_resort.Surface.HeightAt(cam.x, cam.z):F0} m, target {(Camera != null ? Camera.Target : Vector3.zero)}, " +
                      $"active terrains {Terrain.activeTerrains.Length}, material {(TerrainMaterial != null ? TerrainMaterial.shader.name : "none")}");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log($"[MountainViewer] Screenshot saved to {path}; {_fps:F0} fps");
            Application.Quit();
        }

        static string PickPackage()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-package");
            if (i >= 0 && i + 1 < args.Length && Directory.Exists(args[i + 1])) return args[i + 1];
            var entries = ResortLibrary.Scan(DataRoot);
            var demo = entries.Where(e => e.Name == "Jackson Hole").OrderByDescending(e => e.SizeKm).FirstOrDefault();
            return (demo ?? entries.FirstOrDefault())?.Folder;
        }

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            var keys = Keyboard.current;
            if (keys != null && keys.hKey.wasPressedThisFrame) _help = !_help;
            if (keys != null && keys.escapeKey.wasPressedThisFrame) Application.Quit();
        }

        void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 15, wordWrap = true };
            if (_error != null)
            {
                GUI.Box(new Rect(20, 20, 520, 90), _error, style);
                return;
            }
            string text = _resort == null
                ? $"{_status}\n[{new string('#', (int)(_fraction * 30)).PadRight(30, '.')}] {_fraction * 100:F0}%"
                : $"{_resort.Manifest.Site.Name} · {_resort.Manifest.Site.SizeMetres / 1000.0:0.#} km · {_status} · {_fps:F0} fps\n" +
                  $"{_resort.Manifest.Quality.OneLiner}\n{_resort.Manifest.Flora.OneLiner}";
            if (_resort != null && _help)
                text += "\nRight-drag orbit · Middle-drag pan · Wheel zoom · WASD move · Q/E down/up · Shift faster · H hide · Esc quit";
            GUI.Box(new Rect(20, 20, 760, _resort == null ? 60 : (_help ? 110 : 88)), text, style);
        }
    }
}
