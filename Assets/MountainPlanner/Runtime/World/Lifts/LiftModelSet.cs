using UnityEngine;

namespace MountainPlanner.World.Lifts
{
    /// <summary>
    /// The lift asset library as the game sees it: one prefab per asset (terminals and chairs), built by
    /// Mountain Planner ▸ Import Lifts from tools/assets/lifts (decision LP1).
    /// </summary>
    [CreateAssetMenu(menuName = "Mountain Planner/Lift Model Set")]
    public sealed class LiftModelSet : ScriptableObject
    {
        public string Maker;
        public string ModelName;
        public GameObject[] Prefabs;

        public GameObject Find(string assetId)
        {
            foreach (var p in Prefabs)
                if (p != null && p.name == assetId) return p;
            return null;
        }
    }
}
