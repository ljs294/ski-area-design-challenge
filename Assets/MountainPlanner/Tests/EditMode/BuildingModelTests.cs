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
        };

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
