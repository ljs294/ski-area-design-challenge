using UnityEngine;

namespace MountainPlanner.World.Lifts
{
    /// <summary>
    /// The attachment points of an imported lift asset (terminal, chair, tower piece or snow gun), filled by Mountain
    /// Planner ▸ Import Lifts. The prefab's local frame is the lift frame: +Z along the line toward the other
    /// terminal, +X to the right looking along +Z, +Y up; the origin is the mast centreline at the 0.00
    /// load/unload level (a chair's origin is its grip on the rope). A snow gun's origin is on its mast (the stick
    /// gun) or under its tripod's pivot (the ground gun) at grade, and +Z is the way it fires.
    ///
    /// Moving parts (bullwheel, sheaves) sit under pivot transforms whose origins are on the real axles;
    /// a future simulation spins a pivot about <see cref="PivotAxes"/> (local). They are static for now. A snow
    /// gun's one moving part is hinged: its lance (stick gun) or its gun (ground gun) tilts about the pivot to aim;
    /// turning the whole gun about +Y is the placement's yaw.
    /// </summary>
    public sealed class LiftRig : MonoBehaviour
    {
        public string AssetId;
        /// <summary>"terminal", "chair", "tower_head", "tower_mast", "tower_base" or "snowgun" (budgets.json kind).</summary>
        public string Kind;
        /// <summary>The maker's code name (Sessellift, SLE); never a real maker (decision LP3).</summary>
        public string Maker;
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
