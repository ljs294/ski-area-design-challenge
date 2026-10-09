using System;
using System.IO;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -benchdownload &lt;scratch folder&gt; (task P2-06 acceptance: frame p95 within budget while a download runs): a real
    /// background download through <see cref="DownloadService"/> and the acquisition pipeline, into a scratch library
    /// emptied first, for the whole run. A benchmark skips the title flow, so this drives the service itself, pumped
    /// every frame as the flow pumps it, and starts the area again whenever it finishes. Never the owner's library.
    /// </summary>
    public sealed class BenchDownload : MonoBehaviour
    {
        public const string Argument = "-benchdownload";
        /// <summary>Crystal Mountain, 5 km: every stage, with 3DEP fallback terrain.</summary>
        static readonly GeoPoint Site = new GeoPoint(46.935, -121.474);
        const double SizeKm = 5;
        const string Marker = ".benchdownload";

        DownloadService _service;
        string _root;
        int _runs;
        string _stage = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void FromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, Argument);
            if (i < 0 || i + 1 >= args.Length) return;
            string root = Path.GetFullPath(args[i + 1]);
            if (string.Equals(root.TrimEnd('\\', '/'), MountainViewer.DataRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError("[BenchDownload] Refusing to download into the game's own library; pass a scratch folder.");
                return;
            }
            var go = new GameObject("Benchmark download");
            DontDestroyOnLoad(go);
            go.AddComponent<BenchDownload>()._root = root;
        }

        void Start()
        {
            // A fresh start, so it really downloads. Only a folder this made before (its marker file) is ever emptied.
            string marker = Path.Combine(_root, Marker);
            if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).GetEnumerator().MoveNext())
            {
                if (!File.Exists(marker))
                {
                    Debug.LogError($"[BenchDownload] {_root} isn't empty and isn't a benchmark download folder; pass an empty scratch folder.");
                    enabled = false;
                    return;
                }
                Directory.Delete(_root, true);
            }
            Directory.CreateDirectory(_root);
            File.WriteAllText(marker, "Scratch library for -benchdownload; emptied at every run.\n");
            _service = new DownloadService(_root, new PipelineDownloader(_root), () => DownloadService.UtcStamp(DateTime.UtcNow));
            _service.Finished += folder =>
            {
                Debug.Log($"[BenchDownload] Run {_runs} finished at {Time.realtimeSinceStartup:F0} s; starting again");
                try { Directory.Delete(folder, true); } catch (IOException) { }
                Begin();
            };
            _service.Failed += p => Debug.LogWarning($"[BenchDownload] Stopped: {p.Title} ({_service.View.Error})");
            _service.WaitStarted += p => Debug.LogWarning($"[BenchDownload] Waiting: {p.Title}");
            Begin();
        }

        void Begin()
        {
            _runs++;
            var d = DownloadService.Request(PickedSite.Create("Crystal Mountain", Albers6350.Forward(Site), SizeKm, false, default), DownloadService.UtcStamp(DateTime.UtcNow));
            _service.Start(d);
            Debug.Log($"[BenchDownload] Run {_runs} started at {Time.realtimeSinceStartup:F0} s into {_root}");
        }

        void Update()
        {
            if (_service == null) return;
            _service.Pump(Time.unscaledDeltaTime);
            if (_service.View.StageNames.Count == 0) return;
            // The stage, once per change (the log shows which stages the measured frames overlapped).
            string stage = _service.Current?.LastStage ?? "";
            if (stage.Length > 0 && stage != _stage)
            {
                _stage = stage;
                Debug.Log($"[BenchDownload] {Time.realtimeSinceStartup:F0} s: {stage} ({_service.View.Percent})");
            }
        }

        void OnDestroy()
        {
            if (_service != null && _service.Running) _service.Cancel(keep: false);
        }
    }
}
