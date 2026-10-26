using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.World.Lifts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// The tower in hand (task 09, F4, LT38): base, mast and the head the game picks for it, standing on the line at the
    /// pointer, see-through and tinted green where it can stand and red where it can't (LT22's look). The model is made
    /// again only when its head, height or lean step changes; moving it and re-tinting allocate nothing.
    /// </summary>
    public sealed class TowerGhost : IDisposable
    {
        readonly LiftView _view;
        readonly Material[] _tints;
        readonly List<Renderer> _renderers = new List<Renderer>();
        Material[][] _byTint = Array.Empty<Material[]>();
        long _key = long.MinValue;
        string _kit;   // the type's tower kit shown (a catalog constant: compared by reference)
        GhostTint _tint = (GhostTint)255;

        public bool Shown => _view.Root.activeSelf && _renderers.Count > 0;
        public GhostTint Tint => _tint;
        /// <summary>Where its foot stands now (tests).</summary>
        public Vector3 Foot => _view.Root.transform.position;

        public TowerGhost(Transform parent, LiftModelSet models, LiftMaterials materials)
        {
            _view = new LiftView(parent, models, materials, null, "Tower ghost");
            _view.Root.SetActive(false);
            _tints = TerminalGhost.MakeTints("Tower ghost");
        }

        /// <summary>
        /// Shows the tower with its foot at <paramref name="foot"/> (on the ground), on a line running up
        /// <paramref name="uphill"/>, the rope <paramref name="steel"/> metres up its mast (the stored height), leaned
        /// <paramref name="leanRadians"/> down the line (TW-14).
        /// </summary>
        public void Show(LiftRope.Head head, Vector3 foot, Vector3 uphill, float leanRadians, float steel, GhostTint tint, string kit = null)
        {
            long key = (long)head * 1_000_000_000L + Mathf.RoundToInt(steel * 100) * 1000L + Mathf.RoundToInt(leanRadians * Mathf.Rad2Deg / 2.5f);
            if (key != _key || !ReferenceEquals(kit, _kit)) Make(head, leanRadians, steel, key, kit);
            if (!_view.Root.activeSelf) _view.Root.SetActive(true);
            uphill.y = 0;
            var facing = uphill.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(-uphill.normalized) : Quaternion.identity;
            _view.Root.transform.SetPositionAndRotation(foot, facing);
            if (tint != _tint) Retint(tint);
        }

        public void Hide()
        {
            if (_view.Root.activeSelf) _view.Root.SetActive(false);
        }

        void Make(LiftRope.Head head, float leanRadians, float steel, long key, string kit)
        {
            _key = key;
            _kit = kit;
            _view.BuildLoneTower(head, leanRadians, steel, new LiftLivery(Color.white, Color.white), kit);
            _renderers.Clear();
            // The nearest detail only: a LODGroup would swap in models the ghost material never reached.
            var nearest = new HashSet<Renderer>();
            var grouped = new HashSet<Renderer>();
            foreach (var group in _view.Root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                for (int l = 0; l < lods.Length; l++)
                    foreach (var r in lods[l].renderers)
                        if (r != null) { grouped.Add(r); if (l == 0) nearest.Add(r); }
                group.enabled = false;
            }
            foreach (var r in _view.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (grouped.Contains(r) && !nearest.Contains(r)) { r.enabled = false; continue; }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _renderers.Add(r);
            }
            _byTint = new Material[3 * _renderers.Count][];
            for (int t = 0; t < 3; t++)
                for (int i = 0; i < _renderers.Count; i++)
                {
                    var mats = new Material[_renderers[i].sharedMaterials.Length];
                    for (int k = 0; k < mats.Length; k++) mats[k] = _tints[t];
                    _byTint[t * _renderers.Count + i] = mats;
                }
            _tint = (GhostTint)255;
        }

        void Retint(GhostTint tint)
        {
            _tint = tint;
            int t = (int)tint, n = _renderers.Count;
            for (int i = 0; i < n; i++) _renderers[i].sharedMaterials = _byTint[t * n + i];
        }

        public void Dispose()
        {
            _view.Dispose();
            foreach (var m in _tints) if (m != null) Object.Destroy(m);
        }
    }
}
