using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Presentation.Buildings;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.50 phase 6, slice 1: the imported buildings (Mountain Planner ▸ Import Buildings) load from Resources with
    /// their four levels of detail and the sockets the game will use, and the dev -buildings flag reads its entries and
    /// stands a building on the highest ground under it.
    /// </summary>
    public sealed class BuildingModelTests
    {
        static readonly (string id, string[] sockets)[] Expected =
        {
            ("bldg_pumphouse", new[] { "intake", "discharge", "door_main", "label" }),
            ("bldg_garage", new[] { "door_1", "door_2", "door_man", "bay_1", "bay_2", "fuel", "fuel_fill", "label" }),
            // slice 2: every lift terminal's extras
            ("bldg_hut_large", new[] { "door", "label", "view" }),
            ("bldg_hut_small", new[] { "door", "label", "view" }),
            ("bldg_hut_mini", new[] { "door", "label", "view" }),
            ("bldg_ramp_quad", new[] { "chair_unload", "breakover", "ramp_end", "hut", "pedestal", "walkway" }),
            ("bldg_ramp_triple", new[] { "chair_unload", "breakover", "ramp_end", "hut", "pedestal", "walkway" }),
            ("bldg_ramp_double", new[] { "chair_unload", "breakover", "ramp_end", "hut", "pedestal", "walkway" }),
            // the Monta drive's wide ramps (owner 2026-11-25)
            ("bldg_ramp_quad_wide", new[] { "chair_unload", "breakover", "ramp_end", "hut", "pedestal", "walkway" }),
            ("bldg_ramp_triple_wide", new[] { "chair_unload", "breakover", "ramp_end", "hut", "pedestal", "walkway" }),
            ("bldg_ramp_double_wide", new[] { "chair_unload", "breakover", "ramp_end", "hut", "pedestal", "walkway" }),
            ("bldg_op_pedestal", new[] { "stop", "phone" }),
            ("bldg_stop_gate_sessellift", new[] { "pivot", "bar_end" }),
            ("bldg_stop_gate_stern", new[] { "pivot", "bar_end" }),
            ("bldg_stop_gate_monta", new[] { "pivot", "bar_end" }),
            ("bldg_stop_gate_sessellift_drive", new[] { "pivot", "bar_end" }),
            ("bldg_stop_gate_monta_drive", new[] { "pivot", "bar_end" }),
        };

        [Test]
        public void TheRampsTerminalTableMatchesTheLiftModels()
        {
            // TerminalExtras.WheelU (unloadramp.json terminals): the top terminal's bullwheel axle past its unload point, as the
            // lift models have it (the rope_*_bw sockets' middle and the head's chair_unload), within 2 cm.
            var lifts = UnityEditor.AssetDatabase.LoadAssetAtPath<LiftModelSet>("Assets/MountainPlanner/Art/Lifts/LiftModels.asset");
            if (lifts == null) Assert.Ignore("The lift library isn't imported.");
            foreach (var (model, maker) in new[] { ("sessellift_fgq4_return", "sessellift"), ("sessellift_stern4_return", "stern"), ("monta_fg4_return", "monta"),
                                                   ("sessellift_fgq4_drive", "sessellift_drive"), ("monta_fg4_drive", "monta_drive") })
            {
                var rig = lifts.Find(model)?.GetComponent<LiftRig>();
                Assert.That(rig, Is.Not.Null, model);
                var l = rig.HeadSocket("rope_left_bw");
                var r = rig.HeadSocket("rope_right_bw");
                var unload = rig.HeadSocket("chair_unload")
                    ?? rig.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.EndsWith("chair_unload"));
                Assert.That(l != null && r != null && unload != null, model + ": sockets");
                float wheel = (rig.transform.InverseTransformPoint(l.position).z + rig.transform.InverseTransformPoint(r.position).z) / 2;
                float unloadZ = rig.transform.InverseTransformPoint(unload.position).z;
                Assert.That(unloadZ - wheel, Is.EqualTo((float)Domain.Design.TerminalExtras.WheelU(maker)).Within(0.02f), model);
            }
        }

        [Test]
        public void TheBuildingsLoadWithFourLevelsAndTheirSockets()
        {
            var set = Resources.Load<LiftModelSet>(DevBuildingPlacement.SetName);
            Assert.That(set, Is.Not.Null, "Art/Buildings/Resources/BuildingModels.asset");
            foreach (var (id, sockets) in Expected)
            {
                var prefab = set.Find(id);
                Assert.That(prefab, Is.Not.Null, id);
                var group = prefab.GetComponent<LODGroup>();
                Assert.That(group, Is.Not.Null, id);
                var lods = group.GetLODs();
                Assert.That(lods.Length, Is.EqualTo(4), id);
                Assert.That(lods.All(l => l.renderers.Length > 0), id + ": every level has its meshes");
                var names = prefab.GetComponentsInChildren<Transform>(true).Select(t => t.name).ToHashSet();
                foreach (string s in sockets) Assert.That(names, Does.Contain($"{id}_socket_{s}"));
                // the building's own materials, never a lift's (whose livery would tint the window and lamp masks)
                Assert.That(prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                    .All(m => m != null && m.name.StartsWith("Building")), id);
            }
        }

        [Test]
        public void EveryLightSocketHasItsRecord()
        {
            // Task 4.50 slice 3: BuildingImport stores each light's record on its prefab, one per light_* socket, at its lens.
            var set = Resources.Load<LiftModelSet>(DevBuildingPlacement.SetName);
            Assert.That(set, Is.Not.Null);
            int lit = 0;
            foreach (var (id, _) in Expected)
            {
                var prefab = set.Find(id);
                var sockets = prefab.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith($"{id}_socket_light_")).ToList();
                var lights = prefab.GetComponent<World.Buildings.BuildingLights>();
                if (sockets.Count == 0) { Assert.That(lights == null || lights.Lamps.Length == 0, id + ": no lights"); continue; }
                lit++;
                Assert.That(lights, Is.Not.Null, id);
                Assert.That(lights.Lamps.Select(l => $"{id}_socket_{l.Name}"), Is.EquivalentTo(sockets.Select(t => t.name)), id);
                foreach (var l in lights.Lamps)
                {
                    var t = sockets.First(s => s.name == $"{id}_socket_{l.Name}");
                    Assert.That((prefab.transform.InverseTransformPoint(t.position) - l.Position).magnitude, Is.LessThan(0.001f), $"{id} {l.Name}");
                    Assert.That(l.Schedule, Is.Not.Empty);
                    Assert.That(l.Aim.magnitude, Is.EqualTo(1).Within(1e-3));
                }
                Assert.That(lights.GlowKelvin, Is.GreaterThan(0), id + ": its windows glow");
            }
            Assert.That(lit, Is.EqualTo(5), "the pump house, the garage and the three huts");
            // The owner's hut rule: the door sconce burns all night, the downlights only while the lift runs.
            var mini = set.Find("bldg_hut_mini").GetComponent<World.Buildings.BuildingLights>();
            Assert.That(mini.Lamps.First(l => l.Name == "light_sconce").Schedule, Is.EqualTo("dusk"));
            Assert.That(mini.Lamps.First(l => l.Name == "light_down_1").Schedule, Is.EqualTo("lift_open"));
        }

        [Test]
        public void EveryWallLampKnowsItsWall()
        {
            // Slice 3 part 2: the import finds the wall behind each lamp (its light stays on that side). A wall lamp faces
            // out from its building, horizontally; the yard poles stand free and light all round.
            var set = Resources.Load<LiftModelSet>(DevBuildingPlacement.SetName);
            var free = new List<string>();
            foreach (var (id, _) in Expected)
            {
                var prefab = set.Find(id);
                var lights = prefab.GetComponent<World.Buildings.BuildingLights>();
                if (lights == null) continue;
                var door = prefab.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == $"{id}_socket_door");
                foreach (var l in lights.Lamps)
                {
                    if (l.Facing == Vector3.zero) { free.Add($"{id} {l.Name}"); continue; }
                    Assert.That(l.Facing.magnitude, Is.EqualTo(1).Within(1e-3), $"{id} {l.Name}");
                    Assert.That(l.Facing.y, Is.EqualTo(0).Within(1e-3), $"{id} {l.Name}");
                    Assert.That(l.WallMetres, Is.InRange(0.02f, 1.2f), $"{id} {l.Name}: the wall just behind it");
                    // a hut's door sconce is on its door's wall: the door sits on the wall's plane, WallMetres behind the lens
                    if (l.Name == "light_sconce" && door != null)
                        Assert.That(Vector3.Dot(prefab.transform.InverseTransformPoint(door.position) - l.Position, l.Facing),
                                    Is.EqualTo(-l.WallMetres).Within(0.1f), $"{id} {l.Name} is on the door wall");
                }
            }
            Assert.That(free, Is.EquivalentTo(new[] { "bldg_pumphouse light_pad", "bldg_garage light_fuel" }));
            // the mini hut's door sconce is on its door wall (local -z), its downlight on the glass side (+x)
            var mini = set.Find("bldg_hut_mini").GetComponent<World.Buildings.BuildingLights>();
            Assert.That(Vector3.Dot(mini.Lamps.First(l => l.Name == "light_sconce").Facing, Vector3.back), Is.GreaterThan(0.99f));
            Assert.That(Vector3.Dot(mini.Lamps.First(l => l.Name == "light_down_1").Facing, Vector3.right), Is.GreaterThan(0.99f));
        }

        [Test]
        public void TheFlagReadsEachBuilding()
        {
            var r = DevBuildingPlacement.Parse("pumphouse@120.5,-40,90;bldg_garage@-10,25;bad;garage@x,1");
            Assert.That(r.Count, Is.EqualTo(2));
            Assert.That(r[0].Id, Is.EqualTo("bldg_pumphouse"));
            Assert.That(r[0].X, Is.EqualTo(120.5f));
            Assert.That(r[0].Z, Is.EqualTo(-40f));
            Assert.That(r[0].Yaw, Is.EqualTo(90f));
            Assert.That(r[1].Id, Is.EqualTo("bldg_garage"));
            Assert.That(r[1].Yaw, Is.EqualTo(0f));
        }

        [Test]
        public void ABuildingStandsOnTheHighestGroundUnderIt()
        {
            // a slope rising 0.1 m per metre to +x, with a hole (no terrain) at the far corner
            float Height(float x, float z) => x > 9.5f && z > 9.5f ? float.NaN : 100 + 0.1f * x;
            var (high, low) = DevBuildingPlacement.GroundUnder(Height, 0, 10, 0, 10);
            Assert.That(high, Is.EqualTo(101f).Within(1e-4f), "the high edge, holes skipped");
            Assert.That(low, Is.EqualTo(100f).Within(1e-4f));
        }
    }
}
