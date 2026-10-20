using System;
using System.Collections.Generic;
using MountainPlanner.App.Edits;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Built work on the mountain (task 07, decision SK11): built trails through the terrain shader's trail map (groomed
    /// in winter, mown in summer, earth faces) and built lifts as their models, rope and moving chairs. Plans stay with
    /// the DesignOverlay's lines. It follows the terrain edit session's history and redraws once the edits it changed
    /// have been laid over the mountain.
    /// </summary>
    public sealed partial class MountainViewer
    {
        [Tooltip("The lift models (Mountain Planner ▸ Import Lifts): terminals, towers and chairs for built lifts.")]
        public LiftModelSet Lifts;
        public Material LiftStructureMaterial, LiftGlassMaterial, LiftChairMaterial;
        [Tooltip("MountainPlanner/LiftCable: the haul rope.")]
        public Shader CableShader;

        TrailSurfaceMap _trailMap;
        LiftMaterials _liftMaterials;
        Material _cableMaterial;
        readonly Dictionary<LiftId, (Lift Lift, LiftView View)> _liftViews = new Dictionary<LiftId, (Lift, LiftView)>();
        TerrainEditSession _builtFor;
        bool _builtDirty;
        float _liftSnow = -1;
        double _chairSeconds;

        /// <summary>The built lifts' views (captures and tests).</summary>
        public IEnumerable<LiftView> LiftViews
        {
            get { foreach (var kv in _liftViews) yield return kv.Value.View; }
        }

        /// <summary>True while built work waits to be redrawn: captures wait for it.</summary>
        bool BuiltBusy => _builtDirty;

        void FollowBuiltWork(TerrainEditSession session)
        {
            if (_builtFor != null) _builtFor.History.Changed -= MarkBuiltDirty;
            _builtFor = session;
            if (session == null) return;
            session.History.Changed += MarkBuiltDirty;
            _trailMap ??= new TrailSurfaceMap();
            _liftMaterials ??= new LiftMaterials(LiftStructureMaterial, LiftGlassMaterial, LiftChairMaterial);
            if (_cableMaterial == null && CableShader != null) _cableMaterial = new Material(CableShader) { name = "Haul rope" };
            _builtDirty = true;
        }

        void MarkBuiltDirty(DesignSnapshot _) => _builtDirty = true;

        /// <summary>Once the edits are laid, redraws the trail map and any lift that changed. Called every frame; cheap when nothing did.</summary>
        void UpdateBuiltWork()
        {
            if (_builtFor == null || _resort == null) return;
            float snow = _layers != null && _layers.SnowOn ? 1 : 0;
            if (_liftSnow != snow)
            {
                _liftSnow = snow;
                _liftMaterials?.SetSnowLoad(snow);
            }
            if (_clock != null && !_clock.Paused) _chairSeconds += Time.unscaledDeltaTime;
            ShowLifts(_layers == null || _layers.LiftsOn, false);
            if (!_builtDirty || EditsBusy) return;
            _builtDirty = false;
            if (_designOverlay != null) _designOverlay.ShowBuilt = !ReferenceEquals(_designOverlay.History, _builtFor.History);
            var doc = _builtFor.History.Document;
            var heights = _builtFor.Pipeline.Heights;
            int trails = _trailMap.Rebuild(doc, heights, _resort.Frame, t => _edits?.Earthwork?.DetailOf(t.Id.Value) as TrailBuild);
            RebuildTrailLayer(doc, heights);   // the Trails map layer (MountainViewer.Trails.cs, task 08)
            var keep = new HashSet<LiftId>();
            foreach (var lift in doc.Lifts)
            {
                if (lift.Stage != WorkStage.Built) continue;
                keep.Add(lift.Id);
                if (_liftViews.TryGetValue(lift.Id, out var shown) && ReferenceEquals(shown.Lift, lift)) continue;
                if (shown.View != null && shown.View == _hovered) _hovered = null;
                if (shown.View != null && shown.View == _held) _held = null;
                shown.View?.Dispose();
                _liftViews.Remove(lift.Id);
                // A lift built before towers came to the tool (task 09) has none: it can't be drawn until it's edited or
                // re-placed. One lift that can't be drawn never stops the others.
                if (lift.Towers.Count == 0)
                {
                    Debug.LogWarning($"[Built] {lift.Name}: no towers (built before task 09's placement); not drawn until it's edited");
                    continue;
                }
                var view = new LiftView(_resort.Root.transform, Lifts, _liftMaterials, _cableMaterial, lift.Name);
                try
                {
                    view.Build(lift, heights, _resort.Frame, new Color(0.72f, 0.12f, 0.09f));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    view.Dispose();
                    continue;
                }
                _liftViews[lift.Id] = (lift, view);
                var rope = view.Rope;
                // LT10: the lift's trail-map line, ticks at its towers.
                if (_mapLines == null) _mapLines = new LiftMapLines(_resort.Root.transform);
                var b0 = new Vector3((float)lift.Bottom.XMetres, 0, (float)lift.Bottom.YMetres);
                var t0 = new Vector3((float)lift.Top.XMetres, 0, (float)lift.Top.YMetres);
                var along = (t0 - b0).normalized;
                var ticks = new List<Vector3>(rope.Towers.Count);
                foreach (var tower in rope.Towers) ticks.Add(b0 + along * (float)tower.BaseS);
                var surface = _resort.Surface;
                _mapLines.Set(lift.Id.Value, b0, t0, ticks, (x, z) => surface.HeightAt(x, z));
                Debug.Log($"[Built] {lift.Name}: {rope.Towers.Count} towers ({string.Join(", ", view.Heads)}), {view.ChairCount} chairs, " +
                          $"TW-01 v2 {(rope.ClearanceOk ? "OK" : "SHORT")} (lowest chair {rope.LowestClearance / 0.3048:F1} ft at {rope.LowestClearanceS / 0.3048:F0} ft, " +
                          $"shortfall {rope.Shortfall / 0.3048:F1} ft at {rope.ShortfallS / 0.3048:F0} ft), top tension {rope.TopTensionShare:P0} of rating, " +
                          $"trains {(view.Posed ? $"posed (tangent within {view.PoseError * 180 / Math.PI:F3}°{(view.SideMismatches > 0 ? $", {view.SideMismatches} head(s) disagree with the rope's bend:{view.MismatchNote}" : "")})" : "not posable (shipped models)")}, " +
                          $"rope leaves the bottom at {rope.BottomExitLoaded * Mathf.Rad2Deg:F1}° loaded / {rope.BottomExitEmpty * Mathf.Rad2Deg:F1}° empty and reaches the top at " +
                          $"{rope.TopEntryLoaded * Mathf.Rad2Deg:F1}° / {rope.TopEntryEmpty * Mathf.Rad2Deg:F1}° ({(rope.TerminalsOk ? "within" : "OVER")} TW-16), " +
                          (rope.Towers.Count > 0   // a lift built before task 09 places its towers has none
                              ? $"station foundations {rope.Towers[0].Reveal / 0.3048:F1} / {rope.Towers[rope.Towers.Count - 1].Reveal / 0.3048:F1} ft"
                              : "no towers yet"));
            }
            var gone = new List<LiftId>();
            foreach (var kv in _liftViews) if (!keep.Contains(kv.Key)) gone.Add(kv.Key);
            if (_mapLines != null)
            {
                var shown = new HashSet<ulong>();
                foreach (var kv in _liftViews) if (!gone.Contains(kv.Key)) shown.Add(kv.Key.Value);
                _mapLines.KeepOnly(shown);
            }
            foreach (var id in gone)
            {
                if (_liftViews[id].View == _hovered) _hovered = null;
                if (_liftViews[id].View == _held) _held = null;
                _liftViews[id].View.Dispose();
                _liftViews.Remove(id);
            }
            ShowLifts(_liftsShown, true);   // new views take the layer
            int chairs = 0;
            foreach (var kv in _liftViews) chairs += kv.Value.View != null ? kv.Value.View.ChairCount : 0;
            if (trails > 0 || _liftViews.Count > 0) Debug.Log($"[Built] {trails} trails, {_liftViews.Count} lifts drawn, {chairs} chairs");
        }

        // The Lifts map layer (task 09, LT7): the models, rope and chairs and the map lines, all together.
        bool _liftsShown = true;
        void ShowLifts(bool on, bool force)
        {
            if (on == _liftsShown && !force) return;
            _liftsShown = on;
            foreach (var kv in _liftViews) if (kv.Value.View?.Root != null) kv.Value.View.Root.SetActive(on);
            _mapLines?.SetVisible(on);
        }

        void DrawChairs()
        {
            if (_liftViews.Count == 0 || Camera == null || !_liftsShown) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            using (ChairsMarker.Auto())
                foreach (var kv in _liftViews) kv.Value.View.DrawChairs(cam, _chairSeconds);
            _chairTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _chairFrames++;
        }

        // The chairs' main-thread cost (task 09's benchmark): a profiler marker, and a mean logged when the viewer goes.
        static readonly Unity.Profiling.ProfilerMarker ChairsMarker = new Unity.Profiling.ProfilerMarker("MountainPlanner.Lifts.Chairs");
        long _chairTicks;
        int _chairFrames;

        void LogChairCost()
        {
            if (_chairFrames == 0) return;
            double ms = _chairTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _chairFrames;
            Debug.Log($"[Chairs] {ms:F3} ms a frame on the main thread over {_chairFrames} frames, {_liftViews.Count} lifts");
        }

        // ---------- the hovered lift glows (task 09, LT23) ----------

        LiftOutline _outline;
        LiftView _hovered;
        ulong _hoveredId;
        // The lift a tool holds lit (Remove lift's first click), and its view.
        ulong _heldLiftId;
        LiftView _held;
        LiftMapLines _mapLines;

        /// <summary>The built lifts' map lines (captures and tests).</summary>
        public LiftMapLines LiftMapLines => _mapLines;

        /// <summary>The lift lit under the pointer now, or null (captures and tests).</summary>
        public LiftView HoveredLift => _hovered;

        /// <summary>For captures and tests: where the pointer is (a fraction of the screen), or null for the mouse.</summary>
        internal Vector2? HoverPointerForCapture;
        bool _hoverFirstLift = Array.IndexOf(Environment.GetCommandLineArgs(), "-hoverlift") >= 0;

        // Every frame: with no tool armed, the pointer off the panels and out of photo mode, the nearest lift part under the
        // pointer (within a few pixels, in front of the ground) lights its whole lift. Allocates nothing.
        void UpdateLiftHover()
        {
            LiftView hit = null;
            ulong hitId = 0;
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (_mapLines != null && cam != null) _mapLines.Follow(cam, Camera.Distance, _hoveredId != 0 ? _hoveredId : _heldLiftId);
            bool picking = _tools == null || !_tools.Armed || _tools.Active is MountainPlanner.UI.Tools.IPicksLifts;
            if (_liftViews.Count > 0 && cam != null && picking && !PhotoOpen && _liftsShown)
            {
                // -hoverlift (captures): the pointer on the first built lift's rope, a fifth of the way round.
                if (_hoverFirstLift && HoverPointerForCapture == null)
                    foreach (var kv in _liftViews)
                    {
                        var at = cam.WorldToScreenPoint(kv.Value.View.RopePoint(0.2f));
                        if (at.z > 0) HoverPointerForCapture = new Vector2(at.x / Screen.width, at.y / Screen.height);
                        break;
                    }
                var mouse = UnityEngine.InputSystem.Mouse.current;
                Vector2? screen = HoverPointerForCapture.HasValue ? new Vector2(HoverPointerForCapture.Value.x * Screen.width, HoverPointerForCapture.Value.y * Screen.height)
                    : mouse != null ? mouse.position.ReadValue() : (Vector2?)null;
                if (screen.HasValue && !PointerOverUi(screen.Value))
                {
                    var ray = cam.ScreenPointToRay(screen.Value);
                    float ground = Camera.GroundAlong(ray, 30000);
                    if (float.IsNaN(ground)) ground = 30000;
                    // About 8 pixels' worth of slack at each distance.
                    float slack = 8 * 2 * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight);
                    float best = float.MaxValue;
                    foreach (var kv in _liftViews)
                    {
                        var view = kv.Value.View;
                        if (view != null && view.Pick(ray, ground + 2, slack, out float t) && t < best) { best = t; hit = view; hitId = kv.Key.Value; }
                    }
                }
            }
            // A lift a tool holds (Remove lift's first click) stays lit with the hovered one.
            if (_heldLiftId != 0 && !picking) _heldLiftId = 0;
            LiftView held = null;
            if (_heldLiftId != 0) foreach (var kv in _liftViews) if (kv.Key.Value == _heldLiftId) { held = kv.Value.View; break; }
            if (hit == _hovered && held == _held) return;
            _hovered?.SetHighlight(false);
            _held?.SetHighlight(false);
            _hovered = hit;
            _hoveredId = hitId;
            _held = held;
            if (_hoverFirstLift) Debug.Log(hit != null ? $"[Hover] lift {hitId:x16} lit" : "[Hover] nothing lit");
            _hovered?.SetHighlight(true);
            _held?.SetHighlight(true);
            bool lit = hit != null || held != null;
            if (_outline == null && lit && cam != null) _outline = new LiftOutline(cam);
            if (_outline != null) _outline.Lit = lit;
        }

        void ReleaseBuiltWork()
        {
            _hovered = null;
            _hoveredId = 0;
            _held = null;
            _heldLiftId = 0;
            _outline?.Dispose();
            _outline = null;
            _mapLines?.Dispose();
            _mapLines = null;
            FollowBuiltWork(null);
            foreach (var kv in _liftViews) kv.Value.View.Dispose();
            _liftViews.Clear();
            _trailMap?.Dispose();
            _trailMap = null;
            _liftMaterials?.Dispose();
            _liftMaterials = null;
            if (_cableMaterial != null) Destroy(_cableMaterial);
            _cableMaterial = null;
        }
    }
}
