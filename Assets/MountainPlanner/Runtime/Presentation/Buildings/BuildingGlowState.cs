using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.World.Buildings;
using UnityEngine;

namespace MountainPlanner.Presentation.Buildings
{
    /// <summary>
    /// Task 4.50 slice 3: a placed building's lamps for their glare (<see cref="LampHaloLayer"/>), with whether it is in use
    /// (a hut while its lift runs; BuildingGlow). Added where a building with <see cref="BuildingLights"/> is spawned; it
    /// registers while enabled. The lamps' world points are worked out when it starts and when it moves (buildings stand
    /// still), so the halos cost nothing per lamp per frame but the copy.
    /// </summary>
    public sealed class BuildingGlowState : MonoBehaviour
    {
        static readonly List<BuildingGlowState> _all = new List<BuildingGlowState>();
        /// <summary>Every enabled building's state (the halo layer's walk; no allocation).</summary>
        public static IReadOnlyList<BuildingGlowState> All => _all;

        public BuildingLights Lights { get; private set; }
        public bool InUse { get; set; }

        Vector3[] _world = System.Array.Empty<Vector3>(), _aim = System.Array.Empty<Vector3>();
        Matrix4x4 _at;

        public void Init(BuildingLights lights, bool inUse)
        {
            Lights = lights;
            InUse = inUse;
            _at = Matrix4x4.zero;
        }

        /// <summary>Lamp <paramref name="i"/>'s lens in the world, nudged 5 cm along its aim (out of its fixture).</summary>
        public Vector3 LampAt(int i) { Place(); return _world[i]; }

        /// <summary>Where lamp <paramref name="i"/> points in the world (a unit vector).</summary>
        public Vector3 LampAim(int i) { Place(); return _aim[i]; }

        /// <summary>Lamp <paramref name="i"/>'s colour (<see cref="LampLightMap.LampColour"/>), worked out once: Unity's
        /// kelvin-to-colour call costs 44 B of garbage each time in the player (the lit-lamp benchmark, 2026-11-26).</summary>
        public Color LampColour(int i) { Place(); return _colour[i]; }

        Color[] _colour = System.Array.Empty<Color>();

        void Place()
        {
            var m = transform.localToWorldMatrix;
            if (m == _at && _world.Length == Lights.Lamps.Length) return;
            _at = m;
            if (_world.Length != Lights.Lamps.Length)
            {
                _world = new Vector3[Lights.Lamps.Length];
                _aim = new Vector3[Lights.Lamps.Length];
                _colour = new Color[Lights.Lamps.Length];
            }
            for (int k = 0; k < _world.Length; k++)
            {
                var lamp = Lights.Lamps[k];
                _world[k] = m.MultiplyPoint3x4(lamp.Position + lamp.Aim * 0.05f);
                _aim[k] = m.MultiplyVector(lamp.Aim).normalized;
                _colour[k] = LampLightMap.LampColour(lamp.Kelvin);
            }
        }

        /// <summary>How strongly lamp <paramref name="i"/> shows (BuildingGlow.Level), 0–1.</summary>
        public float LampLevel(int i, float night, float sunElevation) =>
            (float)BuildingGlow.Level(Lights.Lamps[i].Schedule, InUse, night, sunElevation);

        void OnEnable() { if (!_all.Contains(this)) _all.Add(this); }
        void OnDisable() => _all.Remove(this);
    }
}
