using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.World;
using MountainPlanner.World.Lifts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Buildings
{
    /// <summary>
    /// Task 4.50 phase 6, slice 1 (dev only, never saved): stands the imported buildings on the terrain from the command
    /// line, so the owner can see them in the game before the real placement exists (4.16 draws the pump houses at the
    /// snow network's). <c>-buildings pumphouse@x,z,yaw;garage@x,z,yaw</c>: a building's id without its "bldg_" prefix,
    /// at x, z metres in the resort's frame (as -view), turned yaw degrees about the vertical (0: the building's u along
    /// +z, its yard side +v toward +x). Each stands with its floor at the highest ground under it (the earth, sampled on a
    /// grid over its footprint), so it never sinks into a slope; where the ground falls further than its concrete skirt,
    /// the log says so (no grading yet). The roofs take the site's snow load like the lifts and trees. Slice 3: their lamps
    /// and windows glow at night (BuildingGlow), in use all night until the simulation runs them (a pump house's pumps,
    /// the garage's night shift).
    /// </summary>
    public sealed class DevBuildingPlacement : MonoBehaviour
    {
        public const string Flag = "-buildings";
        /// <summary>The imported buildings (Editor BuildingImport), loaded from Resources.</summary>
        public const string SetName = "BuildingModels";
        /// <summary>How far below its floor a building's skirt reaches (the garage's 1.2 m; the pump house's is deeper).</summary>
        public const float SkirtMetres = 1.2f;
        const int Grid = 6;
        static readonly int SnowLoadId = Shader.PropertyToID("_SnowLoad");
        static readonly int LampGlowId = Shader.PropertyToID("_LampGlow"), WindowGlowId = Shader.PropertyToID("_WindowGlow");

        public struct Request
        {
            public string Id;
            public float X, Z, Yaw;
        }

        readonly List<Material> _materials = new List<Material>();
        Func<float> _snow;
        float _load = -1;

        /// <summary>Reads <see cref="Flag"/> and places its buildings under <paramref name="parent"/>; null if there is no flag.</summary>
        public static DevBuildingPlacement FromArgs(string[] args, Transform parent, ITerrainSurface surface, Func<float> snow)
        {
            int at = Array.IndexOf(args, Flag);
            if (at < 0 || at + 1 >= args.Length) return null;
            var set = Resources.Load<LiftModelSet>(SetName);
            if (set == null)
            {
                Debug.LogWarning($"[Buildings] {Flag}: no {SetName} in Resources (run Mountain Planner ▸ Import Buildings)");
                return null;
            }
            var go = new GameObject("Dev buildings");
            go.transform.SetParent(parent, false);
            var placement = go.AddComponent<DevBuildingPlacement>();
            placement._snow = snow;
            foreach (var r in Parse(args[at + 1])) placement.Place(set, r, surface);
            return placement;
        }

        /// <summary><c>pumphouse@x,z,yaw;garage@x,z</c> → requests (yaw 0 when left out); malformed entries are skipped.</summary>
        public static List<Request> Parse(string value)
        {
            var list = new List<Request>();
            foreach (string entry in value.Split(';'))
            {
                int at = entry.IndexOf('@');
                if (at <= 0) continue;
                var v = entry.Substring(at + 1).Split(',');
                if (v.Length < 2) continue;
                var n = new float[3];
                bool ok = true;
                for (int i = 0; i < Math.Min(3, v.Length); i++)
                    ok &= float.TryParse(v[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]);
                if (!ok) continue;
                string id = entry.Substring(0, at).Trim();
                list.Add(new Request { Id = id.StartsWith("bldg_") ? id : "bldg_" + id, X = n[0], Z = n[1], Yaw = n[2] });
            }
            return list;
        }

        /// <summary>The highest and lowest ground over a footprint (x0..x1, z0..z1), on a grid; NaN samples are skipped.</summary>
        public static (float high, float low) GroundUnder(Func<float, float, float> heightAt, float x0, float x1, float z0, float z1, int grid = Grid)
        {
            float high = float.NegativeInfinity, low = float.PositiveInfinity;
            for (int i = 0; i <= grid; i++)
                for (int j = 0; j <= grid; j++)
                {
                    float h = heightAt(Mathf.Lerp(x0, x1, i / (float)grid), Mathf.Lerp(z0, z1, j / (float)grid));
                    if (float.IsNaN(h)) continue;
                    high = Mathf.Max(high, h);
                    low = Mathf.Min(low, h);
                }
            return (high, low);
        }

        void Place(LiftModelSet set, Request r, ITerrainSurface surface)
        {
            var prefab = set.Find(r.Id);
            if (prefab == null)
            {
                Debug.LogWarning($"[Buildings] no building {r.Id} in {SetName}");
                return;
            }
            var go = Object.Instantiate(prefab, transform);
            go.transform.localPosition = new Vector3(r.X, 0, r.Z);
            go.transform.localRotation = Quaternion.Euler(0, r.Yaw, 0);
            // its footprint on the ground: every renderer's box (pads and aprons included), in the resort's frame
            var box = new Bounds(go.transform.localPosition, Vector3.zero);
            foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
            {
                var b = rend.bounds;
                box.Encapsulate(transform.InverseTransformPoint(b.min));
                box.Encapsulate(transform.InverseTransformPoint(b.max));
            }
            var (high, low) = GroundUnder(surface.HeightAt, box.min.x, box.max.x, box.min.z, box.max.z);
            if (float.IsInfinity(high))
            {
                Debug.LogWarning($"[Buildings] {r.Id} at {r.X:F0},{r.Z:F0} is off the terrain");
                Object.Destroy(go);
                return;
            }
            go.transform.localPosition = new Vector3(r.X, high, r.Z);
            string gap = high - low > SkirtMetres ? $"; the ground falls {high - low:F1} m, past its {SkirtMetres} m skirt" : "";
            Debug.Log($"[Buildings] {r.Id} at {r.X:F0},{r.Z:F0} yaw {r.Yaw:F0}, floor {high:F1} m{gap}");
            // its own material copies, so the snow load never touches the imported materials
            var copies = new Dictionary<Material, Material>();
            foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
            {
                var shared = rend.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    if (shared[i] == null) continue;
                    if (!copies.TryGetValue(shared[i], out var copy))
                    {
                        copy = new Material(shared[i]);
                        if (copy.HasProperty(LampGlowId)) copy.SetFloat(LampGlowId, Lifts.LiftMaterials.LampGlow);
                        if (copy.HasProperty(WindowGlowId)) copy.SetFloat(WindowGlowId, Lifts.LiftMaterials.WindowGlow);
                        copies.Add(shared[i], copy);
                        _materials.Add(copy);
                    }
                    shared[i] = copy;
                }
                rend.sharedMaterials = shared;
            }
            var lights = go.GetComponent<World.Buildings.BuildingLights>();
            if (lights != null) go.AddComponent<BuildingGlowState>().Init(lights, true);
        }

        void Update()
        {
            float load = _snow != null ? _snow() : 0;
            if (load == _load) return;
            _load = load;
            foreach (var m in _materials) m.SetFloat(SnowLoadId, load);
        }

        void OnDestroy()
        {
            foreach (var m in _materials) Destroy(m);
            _materials.Clear();
        }
    }
}
