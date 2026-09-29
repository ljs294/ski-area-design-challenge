using System;
using System.Collections.Generic;
using MountainPlanner.World.Lifts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// Runtime materials for placed lift assets (decisions LP5, LP6). The imported materials stay untouched:
    /// each livery colour gets its own copy of the structure material (the hood's livery mask decides where
    /// the colour shows), glass and chairs get one shared copy each, and <see cref="SetSnowLoad"/> changes
    /// all of them. Copies are cached, so placing many lifts in the same colour adds no materials.
    /// </summary>
    public sealed class LiftMaterials : IDisposable
    {
        static readonly int SnowLoadId = Shader.PropertyToID("_SnowLoad");
        static readonly int LiveryId = Shader.PropertyToID("_LiveryColor");

        readonly Material _structure;
        readonly Material _glass;
        readonly Material _chair;
        readonly Dictionary<Color32, Material> _livery = new Dictionary<Color32, Material>();
        readonly List<Material> _copies = new List<Material>();
        readonly Material _glassCopy;
        readonly Material _chairCopy;

        public float SnowLoad { get; private set; } = 1f;

        public LiftMaterials(Material structure, Material glass, Material chair)
        {
            _structure = structure;
            _glass = glass;
            _chair = chair;
            _glassCopy = Copy(glass);
            _chairCopy = Copy(chair);
        }

        Material Copy(Material source)
        {
            if (source == null) return null;
            var m = new Material(source) { name = source.name + " (runtime)" };
            if (m.HasProperty(SnowLoadId)) m.SetFloat(SnowLoadId, SnowLoad);
            _copies.Add(m);
            return m;
        }

        /// <summary>The structure material in a livery colour (cached per colour).</summary>
        public Material StructureFor(Color livery)
        {
            Color32 key = livery;
            if (_livery.TryGetValue(key, out var m)) return m;
            m = Copy(_structure);
            m.name = $"{_structure.name} ({key.r},{key.g},{key.b})";
            m.SetColor(LiveryId, livery);
            _livery.Add(key, m);
            return m;
        }

        /// <summary>Points every renderer of a placed lift asset at the runtime copies, in a livery colour.</summary>
        public void Apply(GameObject instance, Color livery)
        {
            var structure = StructureFor(livery);
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var shared = r.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    if (shared[i] == _structure) shared[i] = structure;
                    else if (shared[i] == _glass) shared[i] = _glassCopy;
                    else if (shared[i] == _chair) shared[i] = _chairCopy;
                }
                r.sharedMaterials = shared;
            }
        }

        public void SetSnowLoad(float load)
        {
            SnowLoad = Mathf.Clamp01(load);
            foreach (var m in _copies)
                if (m != null && m.HasProperty(SnowLoadId)) m.SetFloat(SnowLoadId, SnowLoad);
        }

        public void Dispose()
        {
            foreach (var m in _copies)
                if (m != null) Object.Destroy(m);
            _copies.Clear();
            _livery.Clear();
        }
    }
}
