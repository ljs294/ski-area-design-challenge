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
    }
}
