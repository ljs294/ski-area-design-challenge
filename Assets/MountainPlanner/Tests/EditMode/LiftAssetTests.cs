using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    // Guards the imported lift assets (decisions LP1-LP8, tools/assets/lifts/README.md): triangle budgets
    // per LOD from budgets.json, shadows only on the near LODs, moving parts centred on their axles, sockets
    // on the rope, the mesh channels the lift shader reads, the lift frame's handedness, and our naming.
    public sealed class LiftAssetTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const string ArtRoot = "Assets/MountainPlanner/Art/Lifts";
        const float Mm = 0.001f;

        [Serializable] sealed class LodBudget { public int lod; public int maxTris; public float untilM; }
        [Serializable] sealed class KindBudget { public LodBudget[] lods; public int shadowLods; public bool crossFade; }
        [Serializable] sealed class Budgets { public KindBudget terminal; public KindBudget chair; }
        [Serializable] sealed class Common { public float ropeElevation; public float lineGauge; }
        [Serializable] sealed class Spec { public Common common; }

        static string RepoPath(string relative) => Path.Combine(Path.GetDirectoryName(Application.dataPath), relative);

        static Budgets LoadBudgets() => JsonUtility.FromJson<Budgets>(File.ReadAllText(RepoPath("tools/assets/lifts/budgets.json")));
        static Spec LoadSpec() => JsonUtility.FromJson<Spec>(File.ReadAllText(RepoPath("tools/assets/lifts/sessellift_fgq4.json")));

        static KindBudget BudgetFor(Budgets b, string kind) => kind switch
        {
            "terminal" => b.terminal,
            "chair" => b.chair,
            _ => throw new ArgumentException(kind),
        };

        static IEnumerable<GameObject> Prefabs()
        {
            var set = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (set == null) yield break;
            foreach (var p in set.Prefabs) yield return p;
        }

        static IEnumerable<TestCaseData> Cases() => Prefabs().Select(p => new TestCaseData(p).SetName("{m}(" + p.name + ")"));

        static int Triangles(Renderer r)
        {
            var mesh = r.GetComponent<MeshFilter>().sharedMesh;
            int tris = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) tris += (int)mesh.GetIndexCount(s) / 3;
            return tris;
        }

        [Test]
        public void TheLiftLibraryIsImported()
        {
            var set = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            Assert.That(set, Is.Not.Null, "Run Mountain Planner ▸ Import Lifts (demo.bat 20).");
            Assert.That(set.Prefabs, Is.Not.Empty);
            Assert.That(set.Prefabs, Has.None.Null);
        }

        [TestCaseSource(nameof(Cases))]
        public void EveryLodIsWithinItsTriangleBudget(GameObject prefab)
        {
            var rig = prefab.GetComponent<LiftRig>();
            var budget = BudgetFor(LoadBudgets(), rig.Kind);
            var lods = prefab.GetComponent<LODGroup>().GetLODs();
            Assert.That(lods.Length, Is.EqualTo(budget.lods.Length));
            int previous = int.MaxValue;
            for (int i = 0; i < lods.Length; i++)
            {
                int tris = lods[i].renderers.Sum(Triangles);
                Assert.That(tris, Is.LessThanOrEqualTo(budget.lods[i].maxTris), $"LOD{i}");
                Assert.That(tris, Is.LessThan(previous), $"LOD{i} must have fewer triangles than LOD{i - 1}");
                previous = tris;
                if (i > 0) Assert.That(lods[i].screenRelativeTransitionHeight, Is.LessThan(lods[i - 1].screenRelativeTransitionHeight));
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void OnlyTheNearLodsCastShadows(GameObject prefab)
        {
            var budget = BudgetFor(LoadBudgets(), prefab.GetComponent<LiftRig>().Kind);
            var lods = prefab.GetComponent<LODGroup>().GetLODs();
            for (int i = 0; i < lods.Length; i++)
                foreach (var r in lods[i].renderers)
                    Assert.That(r.shadowCastingMode, Is.EqualTo(i < budget.shadowLods ? ShadowCastingMode.On : ShadowCastingMode.Off), r.name);
        }

        [TestCaseSource(nameof(Cases))]
        public void MovingPartsAreCentredOnTheirAxles(GameObject prefab)
        {
            var rig = prefab.GetComponent<LiftRig>();
            Assert.That(rig.Pivots.Length, Is.EqualTo(rig.PivotAxes.Length));
            for (int i = 0; i < rig.Pivots.Length; i++)
            {
                var pivot = rig.Pivots[i];
                Assert.That(pivot.localRotation, Is.EqualTo(Quaternion.identity), pivot.name);
                Assert.That(pivot.localScale, Is.EqualTo(Vector3.one), pivot.name);
                var axis = rig.PivotAxes[i].normalized;
                foreach (var filter in pivot.GetComponentsInChildren<MeshFilter>(true))
                {
                    Assert.That(filter.transform.localPosition.magnitude, Is.LessThan(Mm), filter.name);
                    var centre = filter.sharedMesh.bounds.center;
                    var across = centre - axis * Vector3.Dot(centre, axis);
                    Assert.That(across.magnitude, Is.LessThan(Mm), $"{filter.name} is off its axle by {across.magnitude * 1000:F1} mm");
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void RopeSocketsAreOnTheRope(GameObject prefab)
        {
            var rig = prefab.GetComponent<LiftRig>();
            if (rig.Kind == "chair") Assert.Ignore("Chairs hang from the rope; no rope sockets.");
            var common = LoadSpec().common;
            var line = rig.Socket("line");
            Assert.That(line, Is.Not.Null, "socket_line");
            var rope = rig.Sockets.Where(s => s.name.Contains("_socket_rope_")).ToArray();
            Assert.That(rope, Is.Not.Empty);
            foreach (var s in rope)
            {
                var p = prefab.transform.InverseTransformPoint(s.position);
                var l = prefab.transform.InverseTransformPoint(line.position);
                Assert.That(Mathf.Abs(Mathf.Abs(p.x - l.x) - common.lineGauge * Mm / 2), Is.LessThan(2 * Mm), s.name);
                Assert.That(Mathf.Abs(p.y - common.ropeElevation * Mm), Is.LessThan(2 * Mm), s.name);
                Assert.That(s.localRotation, Is.EqualTo(Quaternion.identity), s.name);
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void MeshesCarryTheLiftShaderChannels(GameObject prefab)
        {
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                var uv0 = new List<Vector2>();
                var data = new List<Vector2>();
                var swatch = new List<Vector2>();
                mesh.GetUVs(0, uv0);
                mesh.GetUVs(1, data);
                mesh.GetUVs(2, swatch);
                Assert.That(uv0.Count, Is.EqualTo(mesh.vertexCount), $"{filter.name} UV0 (detail)");
                Assert.That(data.Count, Is.EqualTo(mesh.vertexCount), $"{filter.name} UV1 (snow, livery)");
                Assert.That(swatch.Count, Is.EqualTo(mesh.vertexCount), $"{filter.name} UV2 (palette)");
                Assert.That(mesh.colors.Length, Is.EqualTo(mesh.vertexCount), $"{filter.name} vertex colour (AO)");
                Assert.That(data.All(d => d.x >= 0 && d.x <= 1 && d.y >= 0 && d.y <= 1), $"{filter.name} data out of range");
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void EveryAssetUsesOurNaming(GameObject prefab)
        {
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                Assert.That(t.name, Does.StartWith("sessellift_fgq4_"), t.name);
        }

        [Test]
        public void ArtFilesUseOurNaming()
        {
            foreach (string path in Directory.GetFiles(ArtRoot, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".meta")))
            {
                string name = Path.GetFileName(path);
                Assert.That(name.StartsWith("sessellift_fgq4_") || name.StartsWith("lift_") || name.StartsWith("Lift"), name);
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void TerminalsFaceTheLine(GameObject prefab)
        {
            // Lift frame: +Z toward the other terminal, +X right looking along +Z, +Y up. The bullwheel sits behind
            // the pier (-Z), the rope leaves toward the line (+Z), and the foundation reaches below the 0.00 level.
            var rig = prefab.GetComponent<LiftRig>();
            if (rig.Kind != "terminal") Assert.Ignore("Terminals only.");
            var bullwheel = rig.Pivot("bullwheel");
            Assert.That(bullwheel, Is.Not.Null);
            Assert.That(bullwheel.localPosition.z, Is.LessThan(-1f), "the bullwheel must be behind the pier (-Z)");
            Assert.That(Mathf.Abs(bullwheel.localPosition.x), Is.LessThan(Mm), "the bullwheel is on the line centre");
            foreach (string side in new[] { "left", "right" })
            {
                var outgoing = rig.Socket($"rope_{side}_out");
                var atWheel = rig.Socket($"rope_{side}_bw");
                Assert.That(outgoing.localPosition.z, Is.GreaterThan(atWheel.localPosition.z), $"{side} rope must run toward the line (+Z)");
                Assert.That(Mathf.Sign(outgoing.localPosition.x), Is.EqualTo(side == "left" ? -1f : 1f), $"{side} rope must be on the {side} (X)");
            }
            Assert.That(rig.Socket("foundation_base").localPosition.y, Is.LessThan(-2f), "foundations reach at least 2 m below grade (LP7)");
            var loadOrUnload = rig.Socket("chair_load") ?? rig.Socket("chair_unload");
            Assert.That(loadOrUnload, Is.Not.Null, "a terminal marks where riders load or unload");
            // Loaded chairs ride up on the right rope (counter-clockwise from above): +X at the bottom, -X at the top.
            Assert.That(Mathf.Sign(loadOrUnload.localPosition.x), Is.EqualTo(rig.Socket("chair_load") != null ? 1f : -1f));
        }
    }
}
