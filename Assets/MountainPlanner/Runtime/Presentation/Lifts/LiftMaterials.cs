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
    /// the colour shows), each glass material (LiftGlass, the carpet gallery's clearer LiftGlassClear: any with the
    /// shader's Glass toggle on) one copy per accent colour (panes marked for the accent take it, owner C16), chairs
    /// one copy per livery colour (a cabin's shell takes it), and <see cref="SetSnowLoad"/> changes all of them. Copies are cached, so placing many lifts
    /// in the same colours adds no materials.
    /// </summary>
    public sealed class LiftMaterials : IDisposable
    {
        static readonly int SnowLoadId = Shader.PropertyToID("_SnowLoad");
        static readonly int LiveryId = Shader.PropertyToID("_LiveryColor");
        static readonly int AccentId = Shader.PropertyToID("_AccentColor");
        static readonly int GlassId = Shader.PropertyToID("_Glass");
        static readonly int LampGlowId = Shader.PropertyToID("_LampGlow");
        static readonly int WindowGlowId = Shader.PropertyToID("_WindowGlow");

        /// <summary>Task 4.50 slice 3: a lit building's lens (mask 3) and window (mask 4) glow at full night, in display
        /// units (the shader takes the post exposure back out): the lenses burn white-hot, the windows a warm amber.</summary>
        public const float LampGlow = 4f, WindowGlow = 0.55f;

        readonly Material _structure;
        readonly Material _glass;
        readonly Material _chair;
        readonly Dictionary<(Color32, Color32), Material> _livery = new Dictionary<(Color32, Color32), Material>();
        readonly Dictionary<(Material, Color32), Material> _glasses = new Dictionary<(Material, Color32), Material>();
        readonly List<Material> _copies = new List<Material>();
        readonly HashSet<Material> _made = new HashSet<Material>();   // the copies, never copied again
        readonly Dictionary<Color32, Material> _chairs = new Dictionary<Color32, Material>();

        public float SnowLoad { get; private set; } = 1f;

        public LiftMaterials(Material structure, Material glass, Material chair)
        {
            _structure = structure;
            _glass = glass;
            _chair = chair;
        }

        /// <summary>
        /// The chair material in a lift's livery (task 4.24; cached per colour): a cabin's livery-masked shell takes the
        /// lift's colour as the stations do. The chairs mask none of their faces, so they look the same in every colour.
        /// </summary>
        public Material ChairFor(LiftLivery livery)
        {
            if (_chair == null) return null;
            var key = (Color32)livery.Primary;
            if (_chairs.TryGetValue(key, out var m)) return m;
            m = Copy(_chair);
            if (m.HasProperty(LiveryId)) m.SetColor(LiveryId, livery.Primary);
            _chairs.Add(key, m);
            return m;
        }

        Material Copy(Material source)
        {
            if (source == null) return null;
            var m = new Material(source) { name = source.name + " (runtime)" };
            if (m.HasProperty(SnowLoadId)) m.SetFloat(SnowLoadId, SnowLoad);
            _copies.Add(m);
            _made.Add(m);
            return m;
        }

        /// <summary>The structure material in a lift's two colours (LT41; cached per pair).</summary>
        public Material StructureFor(LiftLivery livery)
        {
            var key = ((Color32)livery.Primary, (Color32)livery.Accent);
            if (_livery.TryGetValue(key, out var m)) return m;
            m = Copy(_structure);
            m.name = $"{_structure.name} ({key.Item1.r},{key.Item1.g},{key.Item1.b} / {key.Item2.r},{key.Item2.g},{key.Item2.b})";
            m.SetColor(LiveryId, livery.Primary);
            if (m.HasProperty(AccentId)) m.SetColor(AccentId, livery.Accent);
            _livery.Add(key, m);
            return m;
        }

        /// <summary>
        /// A glass material in a lift's accent colour (cached per glass and accent): LiftGlass, or any other material
        /// with the shader's Glass toggle on (LiftGlassClear). Null for a material that is not glass.
        /// </summary>
        public Material GlassFor(Material glass, Color accent)
        {
            if (!IsGlass(glass)) return null;
            var key = (glass, (Color32)accent);
            if (_glasses.TryGetValue(key, out var m)) return m;
            m = Copy(glass);
            m.name = $"{glass.name} ({key.Item2.r},{key.Item2.g},{key.Item2.b})";
            if (m.HasProperty(AccentId)) m.SetColor(AccentId, accent);
            _glasses.Add(key, m);
            return m;
        }

        bool IsGlass(Material m) => m != null && !_made.Contains(m) && (m == _glass || m.HasProperty(GlassId) && m.GetFloat(GlassId) > 0.5f);

        /// <summary>One colour (the Lift Lab's swatches): the primary, with the default accent.</summary>
        public Material StructureFor(Color livery) => StructureFor(new LiftLivery(livery, LiftLivery.DarkAccent));

        /// <summary>Points every renderer of a placed lift asset at the runtime copies, in a livery colour.</summary>
        public void Apply(GameObject instance, Color livery) => Apply(instance, new LiftLivery(livery, LiftLivery.DarkAccent));

        /// <summary>Points every renderer of a placed lift asset at the runtime copies, in the lift's two colours.</summary>
        public void Apply(GameObject instance, LiftLivery livery)
        {
            var structure = StructureFor(livery);
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var shared = r.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    if (shared[i] == _structure) shared[i] = structure;
                    else if (shared[i] == _chair) shared[i] = ChairFor(livery);
                    else if (IsGlass(shared[i])) shared[i] = GlassFor(shared[i], livery.Accent);
                }
                r.sharedMaterials = shared;
            }
        }

        readonly Dictionary<Material, Material> _plain = new Dictionary<Material, Material>();
        readonly Dictionary<Material, Material> _plainDark = new Dictionary<Material, Material>();
        readonly Dictionary<Material, Material> _plainSource = new Dictionary<Material, Material>();

        /// <summary>
        /// Task 4.50 slice 2: points every renderer of a placed building (a terminal's hut, ramp, gate or pedestal: their own
        /// white-livery materials) at one cached runtime copy per material, so <see cref="SetSnowLoad"/> lays the snow on them
        /// as on the lifts and the imported materials stay untouched. Slice 3: two copies per material, <paramref name="lit"/>
        /// (its lenses and windows glow at night) or dark; calling it again on the same building swaps between them, so a
        /// hut goes dark as its lift closes without a property block (the SRP batcher keeps it).
        /// </summary>
        public void ApplyPlain(GameObject instance, bool lit = true)
        {
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var shared = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    var m = shared[i];
                    if (m == null) continue;
                    if (!_plainSource.TryGetValue(m, out var source))
                    {
                        if (_made.Contains(m)) continue;   // a lift's copy, not a building's
                        source = m;
                    }
                    var copy = PlainFor(source, lit);
                    if (copy == m) continue;
                    shared[i] = copy;
                    changed = true;
                }
                if (changed) r.sharedMaterials = shared;
            }
        }

        Material PlainFor(Material source, bool lit)
        {
            var map = lit ? _plain : _plainDark;
            if (map.TryGetValue(source, out var copy)) return copy;
            copy = Copy(source);
            if (lit) copy.name = source.name + " (runtime, lit)";
            if (copy.HasProperty(LampGlowId)) copy.SetFloat(LampGlowId, lit ? LampGlow : 0f);
            if (copy.HasProperty(WindowGlowId)) copy.SetFloat(WindowGlowId, lit ? WindowGlow : 0f);
            map.Add(source, copy);
            _plainSource.Add(copy, source);
            return copy;
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
                if (m != null)
                {
                    if (Application.isPlaying) Object.Destroy(m);
                    else Object.DestroyImmediate(m);   // edit-mode tests and tools
                }
            _copies.Clear();
            _livery.Clear();
            _glasses.Clear();
            _chairs.Clear();
            _plain.Clear();
            _plainDark.Clear();
            _plainSource.Clear();
            _made.Clear();
        }
    }
}
