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
    /// Brings the Blender lift assets (tools/assets/lifts/out, built by build-lifts.bat) into Unity: copies
    /// the FBX files and textures, makes the LiftStructure / LiftGlass / LiftChair materials, and saves one
    /// LODGroup prefab per asset with a <see cref="LiftRig"/> (pivots, sockets), plus the
    /// <see cref="LiftModelSet"/>. LOD switch distances and budgets come from tools/assets/lifts/budgets.json.
    ///
    /// Every pivot and socket is checked against out/lifts.json (to 1 mm) so an axis or pivot mistake in the
    /// FBX round trip fails the import instead of shipping.
    /// </summary>
    public static class LiftImport
    {
        public const string Source = "tools/assets/lifts/out";
        public const string BudgetsPath = "tools/assets/lifts/budgets.json";
        public const string SpecPath = "tools/assets/lifts/sessellift_fgq4.json";
        public const string Root = "Assets/MountainPlanner/Art/Lifts";
        public const string SetPath = Root + "/LiftModels.asset";
        public const string ShaderName = "MountainPlanner/LiftStructure";
        /// <summary>Vertical field of view the LOD distances are converted with (Unity's default camera).</summary>
        public const float ReferenceFov = 60f;
        const float Tolerance = 0.001f;

        [MenuItem("Mountain Planner/Import Lifts")]
        public static void Import()
        {
            string report = Path.Combine(Source, "lifts.json");
            if (!File.Exists(report)) throw new FileNotFoundException($"{report} is missing: run tools/assets/lifts/build-lifts.bat first.");
            var lifts = JObject.Parse(File.ReadAllText(report));
            if (lifts["failures"] is JArray failures && failures.Count > 0)
                throw new System.InvalidOperationException("The lift build reported failures: " + string.Join("; ", failures));
            var budgets = JObject.Parse(File.ReadAllText(BudgetsPath));
            var spec = JObject.Parse(File.ReadAllText(SpecPath));

            foreach (string sub in new[] { "Models", "Textures", "Materials", "Prefabs" }) Directory.CreateDirectory(Path.Combine(Root, sub));
            foreach (string f in Directory.GetFiles(Source, "*.fbx")) File.Copy(f, Path.Combine(Root, "Models", Path.GetFileName(f)), true);
            foreach (string f in Directory.GetFiles(Path.Combine(Source, "textures"), "*.png")) File.Copy(f, Path.Combine(Root, "Textures", Path.GetFileName(f)), true);
            AssetDatabase.Refresh();
            ConfigureTextures();

            var materials = Materials();
            var prefabs = new List<GameObject>();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (var asset in ((JObject)lifts["assets"]).Properties())
            {
                var info = (JObject)asset.Value;
                string fbx = $"{Root}/Models/{(string)info["fbx"]}";
                var importer = (ModelImporter)AssetImporter.GetAtPath(fbx) ?? throw new FileNotFoundException(fbx);
                ConfigureModel(importer, materials);
                importer.SaveAndReimport();
                prefabs.Add(BuildPrefab(asset.Name, info, fbx, (JObject)budgets[(string)info["kind"]], spec));
            }

            var set = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<LiftModelSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            set.Maker = (string)spec["catalog"]["maker"];
            set.ModelName = (string)spec["catalog"]["name"];
            set.Prefabs = prefabs.ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log($"[LiftImport] {prefabs.Count} lift prefabs → {SetPath}");
        }

        static void ConfigureTextures()
        {
            foreach (string path in Directory.GetFiles(Path.Combine(Root, "Textures"), "*.png").Select(p => p.Replace('\\', '/')))
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                bool palette = Path.GetFileName(path) == "lift_palette.png";
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = palette;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
                ti.mipmapEnabled = !palette;
                ti.filterMode = palette ? FilterMode.Point : FilterMode.Trilinear;
                ti.wrapMode = palette ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                ti.anisoLevel = palette ? 0 : 4;
                ti.textureCompression = palette ? TextureImporterCompression.Uncompressed : TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
        }

        /// <summary>Material name in the FBX → Unity material.</summary>
        static Dictionary<string, Material> Materials()
        {
            var shader = Shader.Find(ShaderName) ?? Shader.Find("Universal Render Pipeline/Lit");
            var palette = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/lift_palette.png");
            var trim = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/lift_trim.png");
            var chairDetail = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/sessellift_fgq4_chair_detail.png");
            return new Dictionary<string, Material>
            {
                ["LiftStructure"] = MakeMaterial("LiftStructure", shader, palette, trim, false),
                ["LiftGlass"] = MakeMaterial("LiftGlass", shader, palette, trim, true),
                ["LiftChair"] = MakeMaterial("LiftChair", shader, palette, chairDetail, false),
            };
        }

        static Material MakeMaterial(string name, Shader shader, Texture2D palette, Texture2D detail, bool glass)
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
            // no trim/detail texture yet (Gate 2): switch the detail off rather than rely on the default texture
            if (m.HasProperty("_DetailStrength")) m.SetFloat("_DetailStrength", detail != null ? 1f : 0f);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
            if (m.HasProperty("_LiveryColor")) m.SetColor("_LiveryColor", new Color(0.72f, 0.12f, 0.09f));
            if (m.HasProperty("_Glass")) m.SetFloat("_Glass", glass ? 1 : 0);
            if (glass) m.EnableKeyword("_LIFT_GLASS"); else m.DisableKeyword("_LIFT_GLASS");
            if (glass)
            {
                m.SetOverrideTag("RenderType", "Transparent");
                if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0);
                m.renderQueue = (int)RenderQueue.Transparent;
                m.SetShaderPassEnabled("ShadowCaster", false);
                m.SetShaderPassEnabled("DepthOnly", false);
                m.SetShaderPassEnabled("DepthNormals", false);
            }
            else
            {
                m.SetOverrideTag("RenderType", "Opaque");
                if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.One);
                if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.Zero);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1);
                m.renderQueue = -1;
            }
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

        static GameObject BuildPrefab(string id, JObject info, string fbx, JObject budget, JObject spec)
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

            var group = go.AddComponent<LODGroup>();
            // The group's size comes from its renderers, so assign them first (placeholder heights), measure,
            // then set the heights that put each switch at its budgets.json distance (at lodBias 1).
            var lods = new LOD[lodCount];
            for (int i = 0; i < lodCount; i++) lods[i] = new LOD(0.5f / (i + 1), byLod[i].ToArray());
            group.SetLODs(lods);
            group.RecalculateBounds();
            float size = group.size;
            float tanHalf = Mathf.Tan(ReferenceFov * 0.5f * Mathf.Deg2Rad);
            for (int i = 0; i < lodCount; i++)
                lods[i].screenRelativeTransitionHeight = Mathf.Min(0.999f, size / (2f * (float)lodSpecs[i]["untilM"] * tanHalf));
            for (int i = 1; i < lodCount; i++) lods[i].screenRelativeTransitionHeight = Mathf.Min(lods[i].screenRelativeTransitionHeight, lods[i - 1].screenRelativeTransitionHeight * 0.99f);
            bool crossFade = (bool)budget["crossFade"];
            group.fadeMode = crossFade ? LODFadeMode.CrossFade : LODFadeMode.None;
            group.animateCrossFading = crossFade;
            group.SetLODs(lods);

            var rig = go.AddComponent<LiftRig>();
            rig.AssetId = id;
            rig.Kind = (string)info["kind"];
            string part = id.Substring(id.LastIndexOf('_') + 1);
            rig.CatalogName = (string)spec["catalog"]["parts"]?[part] ?? id;
            var all = go.GetComponentsInChildren<Transform>(true);
            var pivots = ((JObject)info["pivots"]).Properties().ToList();
            rig.Pivots = pivots.Select(p => all.First(t => t.name == $"{id}_pivot_{p.Name}")).ToArray();
            rig.PivotAxes = pivots.Select(p => ToVector((JArray)p.Value["axis"])).ToArray();
            var sockets = ((JObject)info["sockets"]).Properties().ToList();
            rig.Sockets = sockets.Select(s => all.FirstOrDefault(t => t.name == $"{id}_socket_{s.Name}")
                ?? throw new System.InvalidOperationException($"{id}: socket {s.Name} is missing from the FBX")).ToArray();
            rig.LiveryRenderers = go.GetComponentsInChildren<Renderer>(true).Where(r => r.name.StartsWith(id + "_LOD") && rig.Kind == "terminal").ToArray();

            // The FBX round trip must keep every pivot and socket where the build put it.
            for (int i = 0; i < pivots.Count; i++) Expect(id, rig.Pivots[i], ToVector((JArray)pivots[i].Value["pos"]), go.transform);
            for (int i = 0; i < sockets.Count; i++) Expect(id, rig.Sockets[i], ToVector((JArray)sockets[i].Value), go.transform);

            string path = $"{Root}/Prefabs/{id}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        static void Expect(string id, Transform t, Vector3 expected, Transform root)
        {
            var local = root.InverseTransformPoint(t.position);
            if ((local - expected).magnitude > Tolerance)
                throw new System.InvalidOperationException($"{id}: {t.name} imported at {local:F4}, expected {expected:F4} (lifts.json)");
            if (Quaternion.Angle(t.rotation, root.rotation) > 0.01f || (t.lossyScale - Vector3.one).magnitude > 1e-4f)
                throw new System.InvalidOperationException($"{id}: {t.name} imported with rotation {t.localEulerAngles} / scale {t.lossyScale}; expected identity");
        }

        static Vector3 ToVector(JArray a) => new Vector3((float)a[0], (float)a[1], (float)a[2]);
    }
}
