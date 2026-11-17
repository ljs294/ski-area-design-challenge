using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    public sealed partial class AppFlow
    {
        bool _waterLinesJob;

        /// <summary>
        /// Task 4.14 (WS-08, LK-10; owner Q5, automatic): once an area opened before 4.14 is on screen, it gets its stream lines
        /// and water names in the background, quietly, if the game is online and no other area job runs. The area keeps its id;
        /// the open viewer reads them at once. A failure says nothing: the next open tries again.
        /// </summary>
        void WaterLinesWhenOpen(string packageFolder)
        {
            if (string.IsNullOrEmpty(packageFolder) || BundledDemo.Contains(packageFolder)) return;
            try { if (!WaterLinesBackfill.Needed(ResortPackage.ReadManifest(packageFolder))) return; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is Newtonsoft.Json.JsonException) { return; }
            StartCoroutine(WaterLinesOnceShown(packageFolder));
        }

        IEnumerator WaterLinesOnceShown(string packageFolder)
        {
            float waited = 0;
            while ((_viewer == null || !_viewer.Ready || Screens.CoverUp || _weatherJob != null) && waited < 180f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (_viewer == null || MountainViewer.TitleMode || !SameFolder(_viewer.OpenPackage ?? "", packageFolder)) yield break;
            if (_waterLinesJob || _weatherJob != null || _waterAndRoads != null || Downloads.Active) yield break;
            if (OfflineState.IsOffline) { Debug.Log($"[AppFlow] Water lines for {packageFolder}: offline, next time"); yield break; }
            RunWaterLines(packageFolder);
        }

        async void RunWaterLines(string folder)
        {
            string cache = PipelineDownloader.CacheFolder(DataRoot);
            _waterLinesJob = true;
            bool added = false;
            try
            {
                added = await Task.Run(() =>
                {
                    WorkerLimits.LimitThisFlow(WorkerLimits.Background);
                    return WaterLinesBackfill.RunAsync(folder, cache, System.Threading.CancellationToken.None);
                });
                var w = ResortPackage.ReadManifest(folder).Water;
                Debug.Log($"[AppFlow] Water lines for {folder}: {(added ? "added" : "already there")}, {w?.Streams} streams, {w?.Names} names");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppFlow] Water lines for {folder}: {e.Message} (the next open tries again)");
            }
            finally
            {
                _waterLinesJob = false;
            }
            if (this == null) return;
            if (added && _viewer != null) _viewer.WaterLinesAdded(folder);
        }
    }
}
