using UnityEngine;

namespace MountainPlanner.World.Lifts
{
    /// <summary>
    /// The attachment points of an imported lift asset (terminal or chair), filled by Mountain Planner ▸
    /// Import Lifts. The prefab's local frame is the lift frame: +Z along the line toward the other
    /// terminal, +X to the right looking along +Z, +Y up; the origin is the mast centreline at the 0.00
    /// load/unload level (a chair's origin is its grip on the rope).
    ///
    /// Moving parts (bullwheel, sheaves) sit under pivot transforms whose origins are on the real axles;
    /// a future simulation spins a pivot about <see cref="PivotAxes"/> (local). They are static for now.
    /// </summary>
    public sealed class LiftRig : MonoBehaviour
    {
        public string AssetId;
        /// <summary>"terminal" or "chair" (budgets.json kind).</summary>
        public string Kind;
        public string CatalogName;
        public Transform[] Pivots;
        public Vector3[] PivotAxes;
        public Transform[] Sockets;
        /// <summary>Renderers whose material carries the player's livery colour (the drive hood).</summary>
        public Renderer[] LiveryRenderers;

        public Transform Socket(string name)
        {
            string suffix = "_socket_" + name;
            foreach (var s in Sockets)
                if (s != null && s.name.EndsWith(suffix)) return s;
            return null;
        }

        public Transform Pivot(string name)
        {
            string suffix = "_pivot_" + name;
            foreach (var p in Pivots)
                if (p != null && p.name.EndsWith(suffix)) return p;
            return null;
        }
    }
}
