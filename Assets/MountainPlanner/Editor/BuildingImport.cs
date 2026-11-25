using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.World.Lifts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Brings the Blender buildings (tools/assets/buildings/out, built by build_buildings.py; task 4.50 phase 6) into
    /// Unity: the FBX of each building in <see cref="Ids"/>, the buildings' palette (the lift palette plus the building
    /// swatches from 128), the BuildingStructure / BuildingWindow materials on the lifts' LiftStructure shader with the
    /// shared lift trim as detail, and one LODGroup prefab per building, listed in a <see cref="LiftModelSet"/> under
    /// Resources so the player can load it with no scene edit. LOD distances and shadow levels come from
    /// tools/assets/buildings/budgets.json. Every socket is checked against out/buildings.json to 1 mm.
    ///
    /// The buildings carry no livery: the materials' livery and accent are white, so the lamp and window masks (3, 4)
    /// show their palette colours. Lights and the night glow are not drawn yet (a later slice).
    /// </summary>
    public static class BuildingImport
    {
        public const string Source = "tools/assets/buildings/out";
        public const string BudgetsPath = "tools/assets/buildings/budgets.json";
        public const string Root = "Assets/MountainPlanner/Art/Buildings";
        public const string SetPath = Root + "/Resources/" + SetName + ".asset";
        /// <summary>The set's name under Resources (Resources.Load).</summary>
        public const string SetName = "BuildingModels";
        public const string Palette = "building_palette.png";
        public const string TrimPath = "Assets/MountainPlanner/Art/Lifts/Textures/lift_trim.png";
        /// <summary>The buildings brought into the game so far: slice 1 the owner's pump house and garage; slice 2 every lift
        /// terminal's extras (the three huts, the three unload ramps, the operator's pedestal and the three stop gates).</summary>
        public static readonly string[] Ids =
        {
            "bldg_pumphouse", "bldg_garage",
            "bldg_hut_large", "bldg_hut_small", "bldg_hut_mini",
            "bldg_ramp_quad", "bldg_ramp_triple", "bldg_ramp_double",
            "bldg_ramp_quad_wide", "bldg_ramp_triple_wide", "bldg_ramp_double_wide",
            "bldg_op_pedestal", "bldg_stop_gate_sessellift", "bldg_stop_gate_stern", "bldg_stop_gate_monta",
            "bldg_stop_gate_sessellift_drive", "bldg_stop_gate_monta_drive",
        };
        const float Tolerance = 0.001f;

        [MenuItem("Mountain Planner/Import Buildings")]
        public static void Import()
        {
            string report = Path.Combine(Source, "buildings.json");
            if (!File.Exists(report)) throw new FileNotFoundException($"{report} is missing: run tools/assets/buildings/build-buildings.bat first.");
            var built = JObject.Parse(File.ReadAllText(report));
            if (built["failures"] is JArray failures && failures.Count > 0)
                throw new System.InvalidOperationException("The building build reported failures: " + string.Join("; ", failures));
            var budgets = JObject.Parse(File.ReadAllText(BudgetsPath));
            var assets = (JObject)built["assets"];

            foreach (string sub in new[] { "Models", "Textures", "Materials", "Prefabs", "Resources" }) Directory.CreateDirectory(Path.Combine(Root, sub));
            // An FBX's bytes change with every export (its object ids), so an unchanged building keeps the file it has: each
            // import records the build's mesh digests and sockets (Models/imported.json), and copies only what changed.
            string recordPath = Path.Combine(Root, "Models", "imported.json");
            var record = File.Exists(recordPath) ? JObject.Parse(File.ReadAllText(recordPath)) : new JObject();
            foreach (string id in Ids)
            {
                var info = (JObject)assets[id] ?? throw new System.InvalidOperationException($"{report} has no {id}: build it first");
                string dst = Path.Combine(Root, "Models", (string)info["fbx"]);
                string signature = Signature(info);
                bool known = record[id] != null;
                if (File.Exists(dst) && (known ? (string)record[id] == signature : !ForceCopy))
                    Debug.Log($"[BuildingImport] {id}: unchanged, kept");
                else File.Copy(Path.Combine(Source, (string)info["fbx"]), dst, true);
                record[id] = signature;
            }
            File.WriteAllText(recordPath, record.ToString(Newtonsoft.Json.Formatting.Indented) + "\n");
            File.Copy(Path.Combine(Source, "textures", "lift_palette.png"), Path.Combine(Root, "Textures", Palette), true);
            AssetDatabase.Refresh();
            ConfigurePalette($"{Root}/Textures/{Palette}");

            var materials = Materials();
            var prefabs = new List<GameObject>();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (string id in Ids)
            {
                var info = (JObject)assets[id];
                string fbx = $"{Root}/Models/{(string)info["fbx"]}";
                var importer = (ModelImporter)AssetImporter.GetAtPath(fbx) ?? throw new FileNotFoundException(fbx);
                ConfigureModel(importer, materials);
                importer.SaveAndReimport();
                prefabs.Add(BuildPrefab(id, info, fbx, (JObject)budgets[(string)info["kind"]]));
            }

            var set = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<LiftModelSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            set.Maker = "";
            set.ModelName = "Buildings";
            set.Prefabs = prefabs.ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log($"[BuildingImport] {prefabs.Count} building prefabs → {SetPath}");
        }

        /// <summary>Copy every FBX even when its record is missing (a record-less FBX already in place is otherwise kept as
        /// it is: the slice 1 buildings, imported before the record).</summary>
        public static bool ForceCopy;

        /// <summary>What an import must match to keep an FBX: every level's mesh digests and the sockets (buildings.json).</summary>
        static string Signature(JObject info)
        {
            var lods = (JArray)info["lods"];
            var parts = new List<string>();
            foreach (var lod in lods) parts.Add(lod["digest"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
            parts.Add(info["sockets"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
            using var sha = System.Security.Cryptography.SHA1.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts)));
            return System.BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        static void ConfigurePalette(string path)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = false;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Point;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.anisoLevel = 0;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SaveAndReimport();
        }

        /// <summary>Material name in the FBX → Unity material (both opaque: a building's windows are dark glass).</summary>
        static Dictionary<string, Material> Materials()
        {
            var shader = Shader.Find(LiftImport.ShaderName) ?? Shader.Find("Universal Render Pipeline/Lit");
            var palette = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/{Palette}");
            var trim = AssetDatabase.LoadAssetAtPath<Texture2D>(TrimPath);
            return new Dictionary<string, Material>
            {
                ["BuildingStructure"] = MakeMaterial("BuildingStructure", shader, palette, trim),
                ["BuildingWindow"] = MakeMaterial("BuildingWindow", shader, palette, trim),
            };
        }

        static Material MakeMaterial(string name, Shader shader, Texture2D palette, Texture2D detail)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            if (m.HasProperty("_PaletteMap")) m.SetTexture("_PaletteMap", palette);
            if (m.HasProperty("_DetailMap")) m.SetTexture("_DetailMap", detail);
            if (m.HasProperty("_DetailStrength")) m.SetFloat("_DetailStrength", detail != null ? 1f : 0f);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
            if (m.HasProperty("_LiveryColor")) m.SetColor("_LiveryColor", Color.white);
            if (m.HasProperty("_AccentColor")) m.SetColor("_AccentColor", Color.white);
            if (m.HasProperty("_Glass")) m.SetFloat("_Glass", 0);
            m.DisableKeyword("_LIFT_GLASS");
            m.SetOverrideTag("RenderType", "Opaque");
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.One);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.Zero);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1);
            m.renderQueue = -1;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static void ConfigureModel(ModelImporter importer, Dictionary<string, Material> materials)
        {
            importer.globalScale = 1;
            importer.useFileScale = true;
            importer.bakeAxisConversion = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.weldVertices = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.isReadable = false;
            importer.generateSecondaryUV = false;
            importer.preserveHierarchy = true;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.importBlendShapes = false;
            importer.addCollider = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var kv in materials) importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
        }

        static GameObject BuildPrefab(string id, JObject info, string fbx, JObject budget)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = id;
            var existing = go.GetComponent<LODGroup>();
            if (existing != null) Object.DestroyImmediate(existing);

            var lodSpecs = (JArray)budget["lods"];
            int lodCount = lodSpecs.Count;
            int shadowLods = (int)budget["shadowLods"];
            var byLod = new List<Renderer>[lodCount];
            for (int i = 0; i < lodCount; i++) byLod[i] = new List<Renderer>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                int at = r.name.LastIndexOf("_LOD");
                if (at < 0 || !int.TryParse(r.name.Substring(at + 4), out int lod) || lod >= lodCount)
                    throw new System.InvalidOperationException($"{id}: renderer {r.name} has no valid _LODn suffix");
                byLod[lod].Add(r);
                r.shadowCastingMode = lod < shadowLods ? ShadowCastingMode.On : ShadowCastingMode.Off;
                r.receiveShadows = true;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;
                r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }
            for (int i = 0; i < lodCount; i++)
                if (byLod[i].Count == 0) throw new System.InvalidOperationException($"{id}: no renderer for LOD{i}");

            // As LiftImport: assign the renderers (placeholder heights), measure the group, then put each switch at its
            // budgets.json distance (at lodBias 1, Unity's default 60° camera).
            var group = go.AddComponent<LODGroup>();
            var lods = new LOD[lodCount];
            for (int i = 0; i < lodCount; i++) lods[i] = new LOD(0.5f / (i + 1), byLod[i].ToArray());
            group.SetLODs(lods);
            group.RecalculateBounds();
            float size = group.size;
            float tanHalf = Mathf.Tan(LiftImport.ReferenceFov * 0.5f * Mathf.Deg2Rad);
            for (int i = 0; i < lodCount; i++)
                lods[i].screenRelativeTransitionHeight = Mathf.Min(0.999f, size / (2f * (float)lodSpecs[i]["untilM"] * tanHalf));
            for (int i = 1; i < lodCount; i++) lods[i].screenRelativeTransitionHeight = Mathf.Min(lods[i].screenRelativeTransitionHeight, lods[i - 1].screenRelativeTransitionHeight * 0.99f);
            bool crossFade = (bool)budget["crossFade"];
            group.fadeMode = crossFade ? LODFadeMode.CrossFade : LODFadeMode.None;
            group.animateCrossFading = crossFade;
            group.SetLODs(lods);

            // The FBX round trip must keep every socket where the build put it (buildings.json, Unity's frame).
            var all = go.GetComponentsInChildren<Transform>(true);
            foreach (var s in ((JObject)info["sockets"]).Properties())
            {
                var t = all.FirstOrDefault(x => x.name == $"{id}_socket_{s.Name}")
                    ?? throw new System.InvalidOperationException($"{id}: socket {s.Name} is missing from the FBX");
                var a = (JArray)s.Value;
                var expected = new Vector3((float)a[0], (float)a[1], (float)a[2]);
                var local = go.transform.InverseTransformPoint(t.position);
                if ((local - expected).magnitude > Tolerance)
                    throw new System.InvalidOperationException($"{id}: {t.name} imported at {local:F4}, expected {expected:F4} (buildings.json)");
            }

            string path = $"{Root}/Prefabs/{id}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
