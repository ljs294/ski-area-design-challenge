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
    /// One head of a fixed-grip terminal (<see cref="LiftRig.Heads"/>): the player chooses which end of the lift drives,
    /// so every fixed-grip terminal carries a head for each end, and a top head comes with a 4, 6 or 8 sheave row for a
    /// gentler or sharper summit. The game shows one head at a time (<see cref="LiftRig.ShowHead"/>).
    /// </summary>
    [System.Serializable]
    public struct LiftHead
    {
        /// <summary>"bottom", "top", "top6" or "top8".</summary>
        public string Key;
        /// <summary>The end it serves: "bottom" (riders get on, the rope held down) or "top" (riders get off).</summary>
        public string End;
        /// <summary>The head the terminal was built with: shown on import, its pivots and sockets under plain names.</summary>
        public bool Default;
        /// <summary>"" for the default head, else the key and an underscore (top6_) on every one of its pivots and
        /// sockets (top6_eq_l, top6_rope_right_out).</summary>
        public string Prefix;
        /// <summary>Indices into <see cref="LiftRig.Pivots"/> of the head's pivots (its steel's fixed <c>&lt;key&gt;_head</c>
        /// pivot and its trains').</summary>
        public int[] Pivots;
        /// <summary>The sockets the head sets, by full name (the default head's plain: rope exits, chair socket).</summary>
        public string[] Sockets;
        /// <summary>Sheaves per rope in its row, or 0 where the build doesn't say.</summary>
        public int Sheaves;
        /// <summary>The row's arc ("flat", "breakover", "station"), or empty.</summary>
        public string Arc;
        /// <summary>The rope's angle leaving the row at rest, degrees (up +), or NaN where the build doesn't say.</summary>
        public float RopeExitDeg;
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
        /// <summary>A fixed-grip terminal's heads (drive at either end), the default first; empty for every other asset.
        /// Only the shown head's pivots are active.</summary>
        public LiftHead[] Heads;

        /// <summary>The head shown now: its index into <see cref="Heads"/>, or -1 (no heads).</summary>
        public int ShownHead = -1;

        /// <summary>Shows the head <paramref name="key"/> and hides every other (their pivots inactive); false, and nothing
        /// changed, when the asset has no such head.</summary>
        public bool ShowHead(string key)
        {
            int shown = Heads == null ? -1 : System.Array.FindIndex(Heads, h => h.Key == key);
            if (shown < 0) return false;
            for (int h = 0; h < Heads.Length; h++)
                if (h != shown) foreach (int p in Heads[h].Pivots) Pivots[p].gameObject.SetActive(false);
            foreach (int p in Heads[shown].Pivots) Pivots[p].gameObject.SetActive(true);
            ShownHead = shown;
            return true;
        }

        /// <summary>A socket as the shown head has it, by its plain name (rope_right_out): the head's own, else the body's
        /// (rope_right_bw); null where only a hidden head has one (the default head's rope_right_hold).</summary>
        public Transform HeadSocket(string name)
        {
            if (ShownHead < 0 || Heads[ShownHead].Default) return Socket(name);
            var own = Socket(Heads[ShownHead].Prefix + name);
            if (own != null) return own;
            foreach (var h in Heads)
                if (h.Default && System.Array.IndexOf(h.Sockets, name) >= 0) return null;
            return Socket(name);
        }

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
