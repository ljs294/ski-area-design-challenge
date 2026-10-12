using System;
using System.IO;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// The loading screen over every load (task P2-03.1; FlowScreens.Cover): up from the first frame, fed from the viewer's
    /// <see cref="LoadProgress"/>, and lifted once the mountain is whole. The scene camera is off under it, since nothing
    /// there needs drawing; when the mountain is in, the camera draws a few frames still under the picture (any first-frame
    /// stall happens there), then the box and picture fade over the live view.
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>The loading screen never outstays this, whatever happens underneath.</summary>
        const float CoverLimitSeconds = 90;
        /// <summary>Frames the live view draws under the picture before the fade, and the longest a frame may take for it to start.</summary>
        const int WarmFrames = 3;
        const float WarmFrameSeconds = 0.05f, WarmLimitSeconds = 1.5f;

        readonly LoadingViewModel _loading = new LoadingViewModel();
        float _coverSince, _coverLifted = float.MaxValue, _warmSince = -1;
        int _warmFrames;

        /// <summary>Keeps the loading screen up whatever the mountain does (captures and tests of the box itself).</summary>
        internal bool HoldCover { get; set; }

        /// <summary>The box shows a set stage of an open, not the viewer's (the -uicapture picture of the loading screen).</summary>
        bool _coverStaged;

        /// <summary>The box's line for an area: "Opening Jackson Hole".</summary>
        static string CoverText(string packageFolder)
        {
            string name = null;
            try { if (!string.IsNullOrEmpty(packageFolder)) name = ResortLibrary.DisplayName(packageFolder, ResortPackage.ReadManifest(packageFolder)); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException) { }
            return name != null ? "Opening " + name : "Opening";
        }

        /// <summary>Puts the loading screen up for the mountain about to open: "Opening Jackson Hole".</summary>
        void CoverFor(string packageFolder)
        {
            Screens.ShowCover(CoverText(packageFolder));
            _loading.Reset();
            Screens.RenderLoading(_loading);
            _coverSince = Time.realtimeSinceStartup;
            _warmSince = -1;
            _warmFrames = 0;
            _viewer = null;   // the scene going away is already whole; the box waits for the next one
        }

        /// <summary>
        /// The bar and Details follow the viewer's stages. The box lifts once the mountain is whole (after the live view has
        /// drawn a few frames under the picture), when opening failed (its message is then on screen), or after the limit.
        /// </summary>
        void UpdateCover()
        {
            if (!Screens.CoverUp || _coverStaged) return;
            bool expired = Time.realtimeSinceStartup - _coverSince > CoverLimitSeconds;
            if (_viewer == null)
            {
                if (expired && !HoldCover) LiftCover();   // a scene without a viewer drops it in OnSceneLoaded
                return;
            }
            if (_loading.Refresh(_viewer.Loading, Time.realtimeSinceStartupAsDouble)) Screens.RenderLoading(_loading);
            var sceneCamera = _viewer.Camera != null ? _viewer.Camera.GetComponent<UnityEngine.Camera>() : null;
            bool failed = _viewer.OpenError != null || expired;
            bool whole = _viewer.Ready && !HoldCover;
            // Nothing under the picture needs drawing until the mountain is whole: the frame time goes to opening it instead.
            if (sceneCamera != null) sceneCamera.enabled = whole || failed;
            if (failed && !HoldCover)
            {
                LiftCover();
                return;
            }
            if (!whole) return;
            if (_warmSince < 0) _warmSince = Time.realtimeSinceStartup;
            if (Time.unscaledDeltaTime < WarmFrameSeconds) _warmFrames++;
            if (_warmFrames >= WarmFrames || Time.realtimeSinceStartup - _warmSince > WarmLimitSeconds) LiftCover();
        }

        void LiftCover()
        {
            Screens.HideCover();
            _coverLifted = Time.realtimeSinceStartup;
            Debug.Log($"[AppFlow] The loading screen lifted at {_coverLifted:F1} s after launch");
        }
    }
}
