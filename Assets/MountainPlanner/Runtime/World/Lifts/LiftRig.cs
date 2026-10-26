using UnityEngine;

namespace MountainPlanner.World.Lifts
{
    /// <summary>One of a lift asset's paths (<see cref="LiftRig.Paths"/>).</summary>
    [System.Serializable]
    public struct LiftPath
    {
        public string Name;
        public Vector3[] Points;
    }

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
    ///
    /// Posable parts (task P3-07): pivots nest (<see cref="PivotParents"/>), so the game can pose a sheave train level by
    /// level from the rope (equaliser, yokes, rockers, sheaves) and a chair's grip along the rope with its hanger
    /// plumb. Each pivot's transform is a child of its parent pivot's, and at rest every pivot has the identity
    /// rotation.
    /// </summary>
    public sealed class LiftRig : MonoBehaviour
    {
        public string AssetId;
        /// <summary>"terminal", "terminal_footing", "chair", "tower_head", "tower_mast", "tower_base" or "snowgun"
        /// (budgets.json kind).</summary>
        public string Kind;
        /// <summary>The maker's code name (Sessellift, SLE); never a real maker (decision LP3).</summary>
        public string Maker;
        public string CatalogName;
        public Transform[] Pivots;
        public Vector3[] PivotAxes;
        /// <summary>Index into <see cref="Pivots"/> of each pivot's parent pivot, or -1 for none (indexed like Pivots).</summary>
        public int[] PivotParents;
        /// <summary>True where a pivot's part swings about a pin off its middle (a train's yoke, a gun's lance) rather
        /// than turning centred on its axle (indexed like Pivots).</summary>
        public bool[] PivotHinged;
        /// <summary>Safe turn from the rest pose in degrees, (min, max), for Quaternion.AngleAxis(angle, PivotAxes[i]):
        /// beyond it a part meets the structure or a neighbour (the build's clash sweep; wheels get the whole turn).</summary>
        public Vector2[] PivotRange;
        public Transform[] Sockets;
        /// <summary>Renderers whose material carries the player's livery colour (the drive hood).</summary>
        public Renderer[] LiveryRenderers;
        /// <summary>Index into <see cref="Pivots"/> of the pivot each one turns with in the world, whatever its parents do
        /// (a walkway on its links follows its train, G8), or -1 (indexed like Pivots).</summary>
        public int[] PivotFollows;
        /// <summary>
        /// Polylines the game follows, in the prefab's frame (LT41): a detachable terminal's <c>chairs</c>, the way a
        /// carrier's grip runs round the station on its rail, from where it leaves the rope to where it rejoins it.
        /// </summary>
        public LiftPath[] Paths;

        /// <summary>A named path's points, or null.</summary>
        public Vector3[] Path(string name)
        {
            if (Paths == null) return null;
            foreach (var p in Paths) if (p.Name == name) return p.Points;
            return null;
        }

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
