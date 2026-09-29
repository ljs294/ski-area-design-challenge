using UnityEngine;

namespace MountainPlanner.World
{
    /// <summary>
    /// The tree library as the game sees it (task 08): one prefab per species variant, in prototype
    /// order (model × 3 + variant; see Domain.Flora.SpeciesMap), with each model's own height so placed
    /// trees scale to their calibrated heights. Built by Mountain Planner ▸ Import Trees.
    /// </summary>
    [CreateAssetMenu(menuName = "Mountain Planner/Tree Prototype Set")]
    public sealed class TreePrototypeSet : ScriptableObject
    {
        public GameObject[] Prefabs;
        /// <summary>Each prefab's height in metres at scale 1.</summary>
        public float[] NativeHeights;
        /// <summary>
        /// Brightness correction per prototype and LOD (index prototype × 4 + LOD), measured at import against
        /// LOD0 (TreeImport's fidelity check), so a tree doesn't brighten or darken when it switches LOD.
        /// </summary>
        public float[] LodBrightness;
        /// <summary>Snow correction per prototype and LOD, measured the same way: each LOD shows LOD0's snow.</summary>
        public float[] LodSnow;

        public float Brightness(int prototype, int lod) => Get(LodBrightness, prototype, lod);
        public float Snow(int prototype, int lod) => Get(LodSnow, prototype, lod);

        static float Get(float[] values, int prototype, int lod) =>
            values != null && prototype * 4 + lod < values.Length ? values[prototype * 4 + lod] : 1f;
    }
}
