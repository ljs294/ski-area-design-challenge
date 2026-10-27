using System;
using System.Collections.Generic;
using MountainPlanner.World.Lifts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>How the terminal being placed reads (LT22): it can stand, its pad will be levelled a lot (LF-12 v4), or it can't.</summary>
    public enum GhostTint : byte { Ok, Raised, Bad }

    /// <summary>
    /// The terminal being placed (task 09, LT19, LT22): its own model following the pointer like a plopped building,
    /// see-through and tinted green, amber or red, turned and set at a level by the tool. The model is made once per
    /// terminal type (a type or end change swaps it); moving, turning and re-tinting each frame allocate nothing.
    /// </summary>
    public sealed class TerminalGhost : IDisposable
    {
        public const string ShaderResource = "MountainPlannerLifts/LiftGhost";

        readonly Transform _parent;
        readonly LiftModelSet _models;
        readonly Material[] _tints;
        readonly List<Renderer> _renderers = new List<Renderer>();
        Material[][] _byTint = Array.Empty<Material[]>();
        GameObject _instance;
        string _model;
        Vector3 _centre;   // the bullwheel's centre in the model's frame (its rope_*_bw sockets), level
        GhostTint _tint = (GhostTint)255;

        /// <summary>The model shown now (an asset id), or null.</summary>
        public string Model => _instance != null && _instance.activeSelf ? _model : null;
        public GhostTint Tint => _tint;
        /// <summary>Where it stands now (its origin, at its level) and which way it faces (+Z toward the other terminal).</summary>
        public Vector3 Origin => _instance != null ? _instance.transform.position : Vector3.zero;
        public Quaternion Rotation => _instance != null ? _instance.transform.rotation : Quaternion.identity;
        /// <summary>The bullwheel point it was last shown at (local metres; NW-14's catchment ring centres on it).</summary>
        public Vector3 Bullwheel { get; private set; }
        public bool Shown => _instance != null && _instance.activeSelf;

        public TerminalGhost(Transform parent, LiftModelSet models)
        {
            _parent = parent;
            _models = models;
            _tints = MakeTints("Terminal ghost");
        }

        /// <summary>The see-through green, amber and red (LT22), in <see cref="GhostTint"/> order; the caller destroys them.</summary>
        internal static Material[] MakeTints(string name)
        {
            var shader = Resources.Load<Shader>(ShaderResource);
            var colours = new[] { new Color(0.2f, 0.78f, 0.32f, 0.62f), new Color(1f, 0.58f, 0.05f, 0.62f), new Color(0.92f, 0.16f, 0.1f, 0.62f) };
            var tints = new Material[3];
            for (int i = 0; i < 3; i++)
            {
                tints[i] = shader != null ? new Material(shader) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                tints[i].name = name + " " + (GhostTint)i;
                tints[i].color = colours[i];
            }
            return tints;
        }

        /// <summary>
        /// Shows <paramref name="assetId"/>'s model with its bullwheel's centre over (<paramref name="bullwheel"/>.x, .z), its
        /// origin at <paramref name="level"/> (metres) and its line side facing <paramref name="facing"/> (a horizontal
        /// direction toward the other terminal).
        /// </summary>
        public void Show(string assetId, Vector3 bullwheel, float level, Vector3 facing, GhostTint tint)
        {
            if (assetId != _model || _instance == null) Make(assetId);
            if (_instance == null) return;
            if (!_instance.activeSelf) _instance.SetActive(true);
            Bullwheel = bullwheel;
            facing.y = 0;
            var rotation = facing.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(facing.normalized) : Quaternion.identity;
            var origin = bullwheel - rotation * _centre;
            origin.y = level;
            _instance.transform.SetPositionAndRotation(origin, rotation);
            if (tint != _tint) Retint(tint);
        }

        public void Hide()
        {
            if (_instance != null && _instance.activeSelf) _instance.SetActive(false);
        }

        void Make(string assetId)
        {
            Clear();
            _model = assetId;
            var prefab = _models != null ? _models.Find(assetId) : null;
            if (prefab == null) return;
            _instance = Object.Instantiate(prefab, _parent);
            _instance.name = "Terminal ghost " + assetId;
            var rig = _instance.GetComponent<LiftRig>();
            var l = rig != null ? rig.Socket("rope_left_bw") : null;
            var r = rig != null ? rig.Socket("rope_right_bw") : null;
            _centre = l != null && r != null ? (l.localPosition + r.localPosition) / 2 : Vector3.zero;
            _centre.y = 0;
            // The nearest detail only: a LODGroup would swap in models the ghost material never reached.
            var group = _instance.GetComponent<LODGroup>();
            var keep = new HashSet<Renderer>();
            if (group != null)
            {
                var lods = group.GetLODs();
                if (lods.Length > 0) foreach (var rr in lods[0].renderers) if (rr != null) keep.Add(rr);
                group.enabled = false;
            }
            var byTint = new List<Material[]>[3] { new List<Material[]>(), new List<Material[]>(), new List<Material[]>() };
            foreach (var rr in _instance.GetComponentsInChildren<Renderer>(true))
            {
                if (group != null && !keep.Contains(rr)) { rr.enabled = false; continue; }
                rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rr.receiveShadows = false;
                _renderers.Add(rr);
                int n = rr.sharedMaterials.Length;
                for (int t = 0; t < 3; t++)
                {
                    var mats = new Material[n];
                    for (int k = 0; k < n; k++) mats[k] = _tints[t];
                    byTint[t].Add(mats);
                }
            }
            // One array of arrays per tint, built once, so a re-tint only assigns.
            _byTint = new Material[3 * _renderers.Count][];
            for (int t = 0; t < 3; t++)
                for (int i = 0; i < _renderers.Count; i++) _byTint[t * _renderers.Count + i] = byTint[t][i];
            _tint = (GhostTint)255;
            _instance.SetActive(false);
        }

        void Retint(GhostTint tint)
        {
            _tint = tint;
            int t = (int)tint, n = _renderers.Count;
            for (int i = 0; i < n; i++) _renderers[i].sharedMaterials = _byTint[t * n + i];
        }

        void Clear()
        {
            if (_instance != null) Object.Destroy(_instance);
            _instance = null;
            _model = null;
            _renderers.Clear();
            _byTint = Array.Empty<Material[]>();
        }

        public void Dispose()
        {
            Clear();
            foreach (var m in _tints) if (m != null) Object.Destroy(m);
        }
    }
}
