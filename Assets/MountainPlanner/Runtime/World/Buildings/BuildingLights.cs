using System;
using UnityEngine;

namespace MountainPlanner.World.Buildings
{
    /// <summary>
    /// A building's lights as the Blender kit recorded them (tools/assets/buildings, buildings.json's <c>lights</c> and
    /// <c>glow</c>; task 4.50 phase 6 slice 3), stored on its prefab by BuildingImport. Positions and aims are in the
    /// prefab's own frame, so a mirrored hut (scale z -1) puts each lamp where its lens is. The lamps' lenses (glow mask
    /// 3) and the windows (mask 4) glow by these schedules; LampLightMap lights the walls and ground from the lamps.
    /// </summary>
    public sealed class BuildingLights : MonoBehaviour
    {
        [Serializable]
        public struct Lamp
        {
            /// <summary>The record's name, its socket's ("light_down_1").</summary>
            public string Name;
            public bool Spot;
            /// <summary>The lens centre, prefab-local metres.</summary>
            public Vector3 Position;
            /// <summary>Where it points, prefab-local (a unit vector).</summary>
            public Vector3 Aim;
            public int Kelvin;
            public int Lumens;
            public float RangeMetres;
            public float ConeDegrees;
            /// <summary>"dusk", "running", "lift_open" or "night_shift" (tools/assets/buildings/README.md).</summary>
            public string Schedule;
            /// <summary>
            /// The outward facing of the wall the lamp is on, prefab-local and horizontal (a unit vector); zero for a
            /// free-standing lamp (a yard pole), which lights all round. Found at import by casting rays against the
            /// building itself (BuildingImport); the lamp's light stays on this side of the wall.
            /// </summary>
            public Vector3 Facing;
            /// <summary>How far the wall is behind the lens, metres (0 when free-standing).</summary>
            public float WallMetres;
        }

        public Lamp[] Lamps = Array.Empty<Lamp>();
        /// <summary>The windows' glow colour temperature, K (0: the building has no glowing glass).</summary>
        public int GlowKelvin;
        public string GlowSchedule;
    }
}
