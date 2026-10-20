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
                shown.View?.Dispose();
                var view = new LiftView(_resort.Root.transform, Lifts, _liftMaterials, _cableMaterial, lift.Name);
                view.Build(lift, heights, _resort.Frame, new Color(0.72f, 0.12f, 0.09f));
                _liftViews[lift.Id] = (lift, view);
                var rope = view.Rope;
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
            foreach (var id in gone)
            {
                _liftViews[id].View.Dispose();
                _liftViews.Remove(id);
            }
            if (trails > 0 || _liftViews.Count > 0) Debug.Log($"[Built] {trails} trails, {_liftViews.Count} lifts drawn");
        }

        void DrawChairs()
        {
            if (_liftViews.Count == 0 || Camera == null) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            foreach (var kv in _liftViews) kv.Value.View.DrawChairs(cam, _chairSeconds);
        }

        void ReleaseBuiltWork()
        {
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
