using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// -benchres &lt;width&gt;x&lt;height&gt; (benchmarks and review captures): a fixed window for this run only, so results
    /// compare with the baselines in docs/perf/. Unity saves the window it quits in as the player's window mode
    /// (HKCU\Software\Ski Area Design Challenge, the Screenmanager values), which is why demo.bat no longer passes
    /// -screen-width/-screen-height/-screen-fullscreen: they became the owner's mode for every later run. This records
    /// the window the player opened in (the saved mode, borderless full screen by default), switches to a window of
    /// the asked-for size, and puts the recorded window back before any quit, so nothing about the run persists.
    /// </summary>
    public sealed class BenchmarkResolution : MonoBehaviour
    {
        public const string Argument = "-benchres";

        /// <summary>How long a mode switch may take to show in <see cref="Screen"/> before going on regardless.</summary>
        const float SwitchSeconds = 3f;

        FullScreenMode _mode;
        int _width, _height;
        bool _restored, _restoring;

        /// <summary>The size after -benchres ("1920x1080"), or false if the argument is missing or malformed.</summary>
        public static bool TryParse(string[] args, out Vector2Int size)
        {
            size = default;
            int i = Array.IndexOf(args, Argument);
            if (i < 0 || i + 1 >= args.Length) return false;
            var parts = args[i + 1].Split('x', 'X');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
                || w < 64 || h < 64) return false;
            size = new Vector2Int(w, h);
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void FromCommandLine()
        {
            if (Application.isEditor) return;   // the Game view has its own size; nothing to save or restore
            var args = Environment.GetCommandLineArgs();
            if (!TryParse(args, out var size))
            {
                if (Array.IndexOf(args, Argument) >= 0) Debug.LogWarning($"[BenchmarkResolution] {Argument} wants <width>x<height>, such as 1920x1080; ignored.");
                return;
            }
            var go = new GameObject(nameof(BenchmarkResolution));
            DontDestroyOnLoad(go);
            go.AddComponent<BenchmarkResolution>().Begin(size);
        }

        void Begin(Vector2Int size)
        {
            _mode = Screen.fullScreenMode;
            _width = Screen.width;
            _height = Screen.height;
            Application.wantsToQuit += WantsToQuit;
            Debug.Log($"[BenchmarkResolution] {size.x}x{size.y} windowed for this run (the player opened {_mode} at {_width}x{_height}; restored before quitting).");
            StartCoroutine(Switch(size.x, size.y, FullScreenMode.Windowed));
        }

        void OnDestroy() => Application.wantsToQuit -= WantsToQuit;

        /// <summary>Holds the first quit back until the recorded window is in place again, then quits for real.</summary>
        bool WantsToQuit()
        {
            if (_restored) return true;
            if (!_restoring)
            {
                _restoring = true;
                StartCoroutine(RestoreAndQuit());
            }
            return false;
        }

        IEnumerator RestoreAndQuit()
        {
            yield return Switch(_width, _height, _mode);
            _restored = true;
            Debug.Log($"[BenchmarkResolution] restored {Screen.fullScreenMode} at {Screen.width}x{Screen.height}.");
            Application.Quit();
        }

        static IEnumerator Switch(int width, int height, FullScreenMode mode)
        {
            Screen.SetResolution(width, height, mode);
            float until = Time.realtimeSinceStartup + SwitchSeconds;
            while ((Screen.width != width || Screen.height != height || Screen.fullScreenMode != mode) && Time.realtimeSinceStartup < until)
                yield return null;
            yield return null;   // one more frame drawn in the new window
            if (Screen.width != width || Screen.height != height || Screen.fullScreenMode != mode)
                Debug.LogWarning($"[BenchmarkResolution] asked for {mode} at {width}x{height}, got {Screen.fullScreenMode} at {Screen.width}x{Screen.height}.");
        }
    }
}
