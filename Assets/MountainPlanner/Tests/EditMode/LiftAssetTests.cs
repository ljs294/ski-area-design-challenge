using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.World.Lifts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    // Guards the imported lift assets (decisions LP1-LP8, tools/assets/lifts/README.md): triangle budgets
    // per LOD from budgets.json, shadows only on the near LODs, moving parts centred on their axles, sockets
    // on the rope (each terminal against its own maker's spec), the mesh channels the lift shader reads, the lift
    // frame's handedness, snow guns standing at grade on a hinge, and our naming; and the posable parts (task P3-07):
    // nested pivots that round-trip through the FBX, parents that carry their children rigidly, and safe ranges.
    public sealed class LiftAssetTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const string ArtRoot = "Assets/MountainPlanner/Art/Lifts";
        const float Mm = 0.001f;
        /// <summary>Our makers' code names, as asset id prefixes (decision LP3).</summary>
        static readonly string[] Makers = { "sessellift_fgq4_", "sle_", "monta_fg4_", "chairworks_chair_" };

        [Serializable] sealed class LodBudget { public int lod; public int maxTris; public float untilM; }
        [Serializable] sealed class KindBudget { public LodBudget[] lods; public int shadowLods; public bool crossFade; }
        [Serializable] sealed class Budgets { public KindBudget terminal; public KindBudget terminal_footing; public KindBudget chair; public KindBudget tower_head; public KindBudget tower_mast; public KindBudget tower_base; public KindBudget snowgun; }
        [Serializable] sealed class Common { public float ropeElevation; public float lineGauge; }
        [Serializable] sealed class Spec { public Common common; }

        static string RepoPath(string relative) => Path.Combine(Path.GetDirectoryName(Application.dataPath), relative);

        static Budgets LoadBudgets() => JsonUtility.FromJson<Budgets>(File.ReadAllText(RepoPath("tools/assets/lifts/budgets.json")));
        /// <summary>The spec an asset was built from: its maker's (the Sessellift's unless another maker's prefix).</summary>
        static Spec LoadSpec(string assetId) => JsonUtility.FromJson<Spec>(File.ReadAllText(RepoPath(
            assetId.StartsWith("monta_fg4_", StringComparison.Ordinal) ? "tools/assets/lifts/monta_fg4.json" : "tools/assets/lifts/sessellift_fgq4.json")));

        static KindBudget BudgetFor(Budgets b, string kind) => kind switch
        {
            "terminal" => b.terminal,
            "terminal_footing" => b.terminal_footing,
            "chair" => b.chair,
            "tower_head" => b.tower_head,
            "tower_mast" => b.tower_mast,
            "tower_base" => b.tower_base,
            "snowgun" => b.snowgun,
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
            Assert.That(set, Is.Not.Null, "Run Mountain Planner ▸ Import Lifts (demo.bat 22).");
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
        public void LodsSwitchAtTheBudgetedDistances(GameObject prefab)
        {
            // At lodBias 1 and the reference 60-degree field of view, LODn must hand over at budgets.json untilM.
            var budget = BudgetFor(LoadBudgets(), prefab.GetComponent<LiftRig>().Kind);
            var group = prefab.GetComponent<LODGroup>();
            var lods = group.GetLODs();
            float tanHalf = Mathf.Tan(30f * Mathf.Deg2Rad);
            for (int i = 0; i < lods.Length; i++)
            {
                float metres = group.size / (2f * tanHalf * lods[i].screenRelativeTransitionHeight);
                Assert.That(metres, Is.EqualTo(budget.lods[i].untilM).Within(budget.lods[i].untilM * 0.05f), $"LOD{i} switches at {metres:F0} m");
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
            Assert.That(rig.PivotHinged, Has.Length.EqualTo(rig.Pivots.Length), "PivotHinged");
            for (int i = 0; i < rig.Pivots.Length; i++)
            {
                var pivot = rig.Pivots[i];
                Assert.That(pivot.localRotation, Is.EqualTo(Quaternion.identity), pivot.name);
                Assert.That(pivot.localScale, Is.EqualTo(Vector3.one), pivot.name);
                var axis = rig.PivotAxes[i].normalized;
                // only the pivot's own meshes: a nested pivot's are checked under that pivot. A hinged part (a snow
                // gun's lance, a train's yoke, the chair's hanger) swings about a pin off its middle.
                foreach (Transform child in pivot)
                {
                    var filter = child.GetComponent<MeshFilter>();
                    if (filter == null) continue;
                    Assert.That(filter.transform.localPosition.magnitude, Is.LessThan(Mm), filter.name);
                    if (rig.PivotHinged[i]) continue;
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
            if (rig.Kind == "tower_mast" || rig.Kind == "tower_base") Assert.Ignore("Mast sections and bases carry no rope.");
            if (rig.Kind == "snowgun") Assert.Ignore("Snow guns carry no rope.");
            if (rig.Kind == "terminal_footing") Assert.Ignore("A terminal's footing carries no rope.");
            var common = LoadSpec(prefab.name).common;
            if (rig.Kind == "tower_head")
            {
                // a tower head carries both ropes at the line gauge, level with each other, at the height its type
                // puts them (support over the crossbeam, hold-down below it)
                var left = rig.Socket("rope_left");
                var right = rig.Socket("rope_right");
                Assert.That(left, Is.Not.Null, "socket_rope_left");
                Assert.That(right, Is.Not.Null, "socket_rope_right");
                Assert.That(Mathf.Abs(right.localPosition.x - common.lineGauge * Mm / 2), Is.LessThan(2 * Mm), "rope_right on the line gauge");
                Assert.That(Mathf.Abs(left.localPosition.x + common.lineGauge * Mm / 2), Is.LessThan(2 * Mm), "rope_left on the line gauge");
                Assert.That(Mathf.Abs(left.localPosition.y - right.localPosition.y), Is.LessThan(Mm), "both ropes at one height");
                return;
            }
            var line = rig.Socket("line");
            Assert.That(line, Is.Not.Null, "socket_line");
            var rope = rig.Sockets.Where(s => s.name.Contains("_socket_rope_")).ToArray();
            Assert.That(rope, Is.Not.Empty);
            foreach (var s in rope)
            {
                var p = prefab.transform.InverseTransformPoint(s.position);
                var l = prefab.transform.InverseTransformPoint(line.position);
                Assert.That(Mathf.Abs(Mathf.Abs(p.x - l.x) - common.lineGauge * Mm / 2), Is.LessThan(2 * Mm), s.name);
                // level at the rope elevation, except where the rope leaves a hold-down row climbing (the return's
                // out sockets, owner): above it by less than a metre and a half, further up the line than the row
                string name = s.name.Substring(s.name.IndexOf("_socket_", StringComparison.Ordinal) + "_socket_".Length);
                var hold = name.EndsWith("_out", StringComparison.Ordinal) ? rig.Socket(name.Substring(0, name.Length - 4) + "_hold") : null;
                if (hold != null)
                {
                    Assert.That(p.y, Is.GreaterThan(common.ropeElevation * Mm + Mm).And.LessThan(common.ropeElevation * Mm + 1.5f), s.name);
                    Assert.That(p.z, Is.GreaterThan(prefab.transform.InverseTransformPoint(hold.position).z), s.name);
                }
                else
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
            Assert.That(Makers.Any(m => prefab.name.StartsWith(m, StringComparison.Ordinal)), prefab.name);
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                Assert.That(t.name, Does.StartWith(prefab.name), t.name);
        }

        [Test]
        public void ArtFilesUseOurNaming()
        {
            foreach (string path in Directory.GetFiles(ArtRoot, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".meta")))
            {
                string name = Path.GetFileName(path);
                Assert.That(Makers.Any(m => name.StartsWith(m, StringComparison.Ordinal)) || name.StartsWith("lift_") || name.StartsWith("Lift"), name);
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void SnowGunsStandAtGradeAndAimOnAHinge(GameObject prefab)
        {
            // A snow gun's origin is on its mast (or under its tripod's pivot) at grade. Its one moving part, the lance
            // or the ground gun's gun, hinges about X up on the mast or the tripod, and the nozzle is above and ahead
            // of the hinge: the gun fires along +Z.
            var rig = prefab.GetComponent<LiftRig>();
            if (rig.Kind != "snowgun") Assert.Ignore("Snow guns only.");
            Assert.That(rig.Socket("base").localPosition.magnitude, Is.LessThan(Mm), "the base socket is the origin, at grade");
            Assert.That(rig.Pivots.Length, Is.EqualTo(1), "one hinged part");
            Assert.That(Mathf.Abs(Vector3.Dot(rig.PivotAxes[0].normalized, Vector3.right)), Is.GreaterThan(0.999f), "hinged about X");
            var hinge = rig.Pivots[0].localPosition;
            Assert.That(hinge.y, Is.GreaterThan(0.9f), "the hinge stands on the mast or the tripod, not at the snow");
            var nozzle = rig.Socket("nozzle").localPosition;
            Assert.That(nozzle.y, Is.GreaterThan(hinge.y), "the nozzle is above the hinge");
            Assert.That(nozzle.z, Is.GreaterThan(hinge.z), "the gun fires along +Z");
        }

        [TestCaseSource(nameof(Cases))]
        public void TerminalsFaceTheLine(GameObject prefab)
        {
            // Lift frame: +Z toward the other terminal, +X right looking along +Z, +Y up. The bullwheel sits on or
            // behind the origin (-Z: the Sessellift's origin is its pier, the Monta's the wheel's axle), the rope
            // leaves toward the line (+Z), and the foundation reaches below the 0.00 level.
            var rig = prefab.GetComponent<LiftRig>();
            if (rig.Kind != "terminal") Assert.Ignore("Terminals only.");
            var bullwheel = rig.Pivot("bullwheel");
            Assert.That(bullwheel, Is.Not.Null);
            Assert.That(bullwheel.localPosition.z, Is.LessThanOrEqualTo(Mm), "the bullwheel must be on or behind the origin (-Z)");
            Assert.That(Mathf.Abs(bullwheel.localPosition.x), Is.LessThan(Mm), "the bullwheel is on the line centre");
            foreach (string side in new[] { "left", "right" })
            {
                var outgoing = rig.Socket($"rope_{side}_out");
                var atWheel = rig.Socket($"rope_{side}_bw");
                Assert.That(outgoing.localPosition.z, Is.GreaterThan(atWheel.localPosition.z), $"{side} rope must run toward the line (+Z)");
                Assert.That(Mathf.Sign(outgoing.localPosition.x), Is.EqualTo(side == "left" ? -1f : 1f), $"{side} rope must be on the {side} (X)");
            }
            if (rig.Socket("pier_top") != null)
            {
                // the Sessellift's really tall piers (task P3-07): the footing is its own prefab; the pier runs on far
                // below grade under its top, so a raised terminal still stands on it
                Assert.That(rig.Socket("foundation_base"), Is.Null, "the footing (and its foundation_base) is its own prefab");
                Assert.That(rig.Socket("pier_top").localPosition.y, Is.GreaterThan(0f), "the pier's top is above the 0.00 level");
                float lowest = prefab.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name == prefab.name + "_LOD0")
                    .Min(f => f.transform.localPosition.y + f.sharedMesh.bounds.min.y);
                Assert.That(lowest, Is.LessThan(-10f), "the pier reaches more than 10 m below the 0.00 level");
            }
            else
                Assert.That(rig.Socket("foundation_base").localPosition.y, Is.LessThan(-2f), "foundations reach at least 2 m below grade (LP7)");
            var loadOrUnload = rig.Socket("chair_load") ?? rig.Socket("chair_unload");
            Assert.That(loadOrUnload, Is.Not.Null, "a terminal marks where riders load or unload");
            // Loaded chairs ride up on the right rope (counter-clockwise from above): +X at the bottom, -X at the top.
            Assert.That(Mathf.Sign(loadOrUnload.localPosition.x), Is.EqualTo(rig.Socket("chair_load") != null ? 1f : -1f));
        }

        [TestCaseSource(nameof(Cases))]
        public void TerminalFootingsSitBelowGrade(GameObject prefab)
        {
            // A terminal's footing (task P3-07) is placed by the game at the ground or pad under the pier: origin at
            // grade, the block wholly below it, its underside at least 2 m down (LP7).
            var rig = prefab.GetComponent<LiftRig>();
            if (rig.Kind != "terminal_footing") Assert.Ignore("Terminal footings only.");
            Assert.That(rig.Socket("foundation_base").localPosition.y, Is.LessThan(-2f), "foundation_base");
            Assert.That(rig.Socket("pier_seat").localPosition.y, Is.LessThan(0f), "pier_seat below grade");
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                Assert.That(filter.transform.localPosition.y + filter.sharedMesh.bounds.max.y, Is.LessThanOrEqualTo(Mm), filter.name);
        }

        [TestCaseSource(nameof(Cases))]
        public void NestedPivotsRoundTripThroughTheFbx(GameObject prefab)
        {
            // Every pivot sits under its parent pivot's transform with no rotation at rest, at the position the Blender
            // build put it (to 1 mm, against tools/assets/lifts/out/lifts.json when a build is on disk).
            var rig = prefab.GetComponent<LiftRig>();
            Assert.That(rig.PivotParents, Has.Length.EqualTo(rig.Pivots.Length), "PivotParents");
            for (int i = 0; i < rig.Pivots.Length; i++)
            {
                int parent = rig.PivotParents[i];
                if (parent >= 0)
                    Assert.That(rig.Pivots[i].parent, Is.SameAs(rig.Pivots[parent]), rig.Pivots[i].name);
                else
                    Assert.That(rig.Pivots, Has.No.SameAs(rig.Pivots[i].parent), rig.Pivots[i].name + " is a top pivot");
                Assert.That(Quaternion.Angle(rig.Pivots[i].rotation, prefab.transform.rotation), Is.LessThan(0.01f), rig.Pivots[i].name);
            }
            string report = RepoPath("tools/assets/lifts/out/lifts.json");
            if (!File.Exists(report)) Assert.Ignore("No lift build on disk (tools/assets/lifts/out/lifts.json) to compare with.");
            var built = JObject.Parse(File.ReadAllText(report))["assets"]?[prefab.name]?["pivots"] as JObject;
            if (built == null) Assert.Ignore($"{prefab.name} is not in the build on disk.");
            Assert.That(built.Count, Is.EqualTo(rig.Pivots.Length), "pivot count");
            foreach (var p in built.Properties())
            {
                var pivot = rig.Pivot(p.Name);
                Assert.That(pivot, Is.Not.Null, p.Name);
                var pos = (JArray)p.Value["pos"];
                var want = new Vector3((float)pos[0], (float)pos[1], (float)pos[2]);
                var got = prefab.transform.InverseTransformPoint(pivot.position);
                Assert.That((got - want).magnitude, Is.LessThan(Mm), $"{p.Name} at {got:F4}, built at {want:F4}");
                if (p.Value["parent"]?.Type == JTokenType.String)
                    Assert.That(pivot.parent, Is.SameAs(rig.Pivot((string)p.Value["parent"])), $"{p.Name}'s parent");
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void TurningAParentPivotMovesItsChildrenRigidly(GameObject prefab)
        {
            // Turn each pivot that carries others by up to 10 degrees about its axis (within its range): every pivot
            // and mesh under it must keep its place relative to it, a rigid turn about the parent's axle.
            var rig = prefab.GetComponent<LiftRig>();
            if (rig.PivotParents == null || !rig.PivotParents.Any(p => p >= 0)) Assert.Ignore("No nested pivots.");
            var go = UnityEngine.Object.Instantiate(prefab);
            try
            {
                var r = go.GetComponent<LiftRig>();
                for (int i = 0; i < r.Pivots.Length; i++)
                {
                    if (!r.PivotParents.Contains(i)) continue;
                    var pivot = r.Pivots[i];
                    float angle = Mathf.Clamp(10f, r.PivotRange[i].x, r.PivotRange[i].y);
                    if (Mathf.Abs(angle) < 0.5f) angle = 10f;   // a rigid part (range 0) still turns rigidly in the test
                    var below = pivot.GetComponentsInChildren<Transform>(true).Where(t => t != pivot).ToArray();
                    var before = below.Select(t => go.transform.InverseTransformPoint(t.position)).ToArray();
                    var centre = go.transform.InverseTransformPoint(pivot.position);
                    var turn = Quaternion.AngleAxis(angle, r.PivotAxes[i]);
                    pivot.localRotation = turn;
                    for (int k = 0; k < below.Length; k++)
                    {
                        var want = centre + turn * (before[k] - centre);
                        var got = go.transform.InverseTransformPoint(below[k].position);
                        Assert.That((got - want).magnitude, Is.LessThan(Mm), $"{below[k].name} under {pivot.name}");
                    }
                    pivot.localRotation = Quaternion.identity;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void PivotRangesContainTheRestPose(GameObject prefab)
        {
            var rig = prefab.GetComponent<LiftRig>();
            Assert.That(rig.PivotRange, Has.Length.EqualTo(rig.Pivots.Length), "PivotRange");
            for (int i = 0; i < rig.Pivots.Length; i++)
            {
                Assert.That(rig.PivotRange[i].x, Is.LessThanOrEqualTo(0f).And.GreaterThanOrEqualTo(-180f), rig.Pivots[i].name);
                Assert.That(rig.PivotRange[i].y, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(180f), rig.Pivots[i].name);
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void PosableHeadsCarryTheirTrainsAtEveryLod(GameObject prefab)
        {
            // Contract P3-07 M2: up close every train level is its own part on its pin; far out each side's whole train
            // is one part under that side's top pivot, never folded into the body, so it turns with the train.
            var rig = prefab.GetComponent<LiftRig>();
            string[] topKinds = { "_pivot_eq_", "_pivot_yoke_", "_pivot_block_", "_pivot_beam_" };
            var tops = Enumerable.Range(0, rig.Pivots.Length).Where(i => rig.PivotParents != null && rig.PivotParents[i] < 0)
                .Select(i => rig.Pivots[i]).Where(p => topKinds.Any(p.name.Contains)).ToArray();
            if (tops.Length == 0) Assert.Ignore("No posable sheave trains.");
            Assert.That(tops.Length, Is.EqualTo(2), "one train top pivot per rope");
            int lods = prefab.GetComponent<LODGroup>().lodCount;
            foreach (var top in tops)
                for (int lod = 0; lod < lods; lod++)
                    Assert.That(top.GetComponentsInChildren<MeshFilter>(true).Any(f => f.name.EndsWith("_LOD" + lod)), $"{top.name} carries a LOD{lod} part");
        }

        [TestCaseSource(nameof(Cases))]
        public void ChairGripsFollowTheRopeWithThePlumbHangerBelow(GameObject prefab)
        {
            // Contract P3-07 M3: up close the Sessellift chair is a grip on the rope (its origin) carrying a hanger hinged
            // across the rope; the seats and the safety bar's hinge ride with the hanger.
            if (prefab.name != "sessellift_fgq4_chair") Assert.Ignore("The Sessellift chair only.");
            var rig = prefab.GetComponent<LiftRig>();
            var grip = rig.Pivot("grip");
            var hanger = rig.Pivot("hanger");
            Assert.That(grip, Is.Not.Null, "grip");
            Assert.That(hanger, Is.Not.Null, "hanger");
            Assert.That(grip.localPosition.magnitude, Is.LessThan(Mm), "the grip pivot is on the rope at the origin");
            Assert.That(hanger.parent, Is.SameAs(grip), "the hanger hangs from the grip");
            Assert.That(hanger.localPosition.y, Is.LessThan(0f), "the hinge is below the rope");
            int i = Array.IndexOf(rig.Pivots, hanger);
            Assert.That(Mathf.Abs(Vector3.Dot(rig.PivotAxes[i].normalized, Vector3.right)), Is.GreaterThan(0.999f), "hinged across the rope (X)");
            foreach (string s in new[] { "seat_1", "seat_2", "seat_3", "seat_4", "bar_hinge" })
                Assert.That(rig.Socket(s).IsChildOf(hanger), s + " rides with the hanger");
        }
    }
}
