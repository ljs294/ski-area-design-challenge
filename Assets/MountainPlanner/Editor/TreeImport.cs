using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Domain.Flora;
using MountainPlanner.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Brings the Blender tree library (tools/assets/trees/out, built by build-trees.bat) into Unity:
    /// copies the FBX files and textures (crown shading runs as they import: <see cref="TreeShading"/>),
    /// makes URP materials, bakes an impostor of each variant, and saves one LODGroup prefab per variant
    /// plus the <see cref="TreePrototypeSet"/>.
    ///
    ///   LOD0 (near) · LOD1 · LOD2 · impostor (one camera-facing quad) · culled
    ///
    /// Impostors are baked from LOD0 in 8 × 8 hemi-octahedral view directions (horizon to straight down),
    /// 96 texels a frame, supersampled: albedo and coverage, plus the crown normal, occlusion and snow
    /// capacity, so TreeImpostor.shader lights them live like the mesh LODs.
    ///
    /// Every import also writes a LOD fidelity report (Art/Trees/fidelity.json): each LOD's crown coverage
    /// and lit brightness relative to LOD0, seen from three elevations. A LOD that loses its crown shows
    /// up there as a number, not as a thin forest in the game.
    ///
    /// Winter (iteration 1): deciduous leaves use an invisible material, so aspens and birches are bare
    /// twigs; beech keeps its dry leaves.
    ///
    /// <c>-treeModels id,id</c> on the command line imports only those models. Every other model keeps its
    /// committed FBX, textures, materials, impostors, prototype-set entries and fidelity lines byte for byte,
    /// so adding a species doesn't re-bake (and re-commit to Git LFS) the whole library.
    /// </summary>
    public static class TreeImport
    {
        const string Source = "tools/assets/trees/out";
        const string Root = "Assets/MountainPlanner/Art/Trees";
        public const string SetPath = Root + "/TreePrototypes.asset";
        public const string ImpostorShaderPath = "Assets/MountainPlanner/Art/Shaders/TreeImpostor.shader";
        const string BakeShaderPath = "Assets/MountainPlanner/Art/Shaders/ImpostorBake.shader";
        const string QuadPath = Root + "/ImpostorQuad.asset";
        public const int Frames = 8, FrameSize = 96, Supersample = 2;
        /// <summary>The runtime shader's alpha cutoff for foliage (ForestRenderer); the bake and the report use the same.</summary>
        public const float FoliageCutoff = 0.4f;

        /// <summary>Screen heights for the prefab's LODGroup (editor previews; the game's forest uses ForestRenderer's).</summary>
        static readonly float[] Transitions = { 0.30f, 0.12f, 0.05f, 0.004f };

        [MenuItem("Mountain Planner/Import Trees")]
        public static void Import() => Import(ModelsFromCommandLine());

        /// <summary>The models named by <c>-treeModels id,id</c>, or null (all of them).</summary>
        static HashSet<string> ModelsFromCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-treeModels");
            return i >= 0 && i + 1 < args.Length ? new HashSet<string>(args[i + 1].Split(',')) : null;
        }

        /// <summary>The model a library file belongs to (its file name starts with the model id), or null.</summary>
        static string ModelOf(string path)
        {
            string name = Path.GetFileName(path);
            return SpeciesMap.Models.Where(m => name.StartsWith(m + "_")).OrderByDescending(m => m.Length).FirstOrDefault();
        }

        /// <summary>Imports the given models (null: every model); see the class summary.</summary>
        public static void Import(HashSet<string> only)
        {
            if (only != null && only.Any(m => SpeciesMap.IndexOf(m) < 0)) throw new System.ArgumentException($"Unknown tree model in -treeModels: {string.Join(",", only)}");
            bool Selected(string model) => only == null || (model != null && only.Contains(model));
            if (!Directory.Exists(Source)) throw new DirectoryNotFoundException($"{Source} is missing: run tools/assets/trees/build-trees.bat first.");
            foreach (string sub in new[] { "Models", "Textures", "Materials", "Impostors", "Prefabs" }) Directory.CreateDirectory(Path.Combine(Root, sub));
            AssetDatabase.DeleteAsset(Root + "/Cards");   // the crossed-card far LOD, replaced by impostors
            foreach (string f in Directory.GetFiles(Source, "*.fbx").Where(f => Selected(ModelOf(f)))) File.Copy(f, Path.Combine(Root, "Models", Path.GetFileName(f)), true);
            foreach (string f in Directory.GetFiles(Path.Combine(Source, "textures"), "*.png").Where(f => Selected(ModelOf(f))))
                File.Copy(f, Path.Combine(Root, "Textures", Path.GetFileName(f)), true);
            AssetDatabase.Refresh();

            foreach (string path in Directory.GetFiles(Path.Combine(Root, "Textures"), "*.png").Where(p => Selected(ModelOf(p))).Select(p => p.Replace('\\', '/')))
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                bool normal = path.EndsWith("_normal.png");   // bark normal maps
                bool card = !path.EndsWith("_bark.png") && !normal;
                ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.alphaIsTransparency = card;
                ti.mipmapEnabled = true;
                ti.mipMapsPreserveCoverage = card;
                ti.alphaTestReferenceValue = FoliageCutoff;
                ti.wrapMode = card ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                ti.SaveAndReimport();
            }

            var quad = ImpostorQuad();
            var prototypes = new List<string>();   // prototype names, in order (model_vN)
            var heights = new List<float>();
            var brightness = new List<float>();
            var snow = new List<float>();
            var report = new StringBuilder("{\n  \"note\": \"Crown coverage and lit brightness of each LOD relative to LOD0 (1.0 = identical), from 0, 22 and 53 degrees above the horizon.\",\n  \"trees\": {\n");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Models left out keep what the last import committed. Read after the new scene, which unloads assets
            // nothing in it uses (the set loaded before it came back destroyed); the numbers are copied, and the
            // kept prefabs load by path.
            var kept = AssetDatabase.LoadAssetAtPath<TreePrototypeSet>(SetPath);
            int keptCount = kept != null && kept.NativeHeights != null ? kept.NativeHeights.Length : 0;
            var keptHeights = keptCount > 0 ? kept.NativeHeights.ToArray() : new float[0];
            var keptBrightness = Enumerable.Range(0, keptCount * 4).Select(i => kept.Brightness(i / 4, i % 4)).ToArray();
            var keptSnow = Enumerable.Range(0, keptCount * 4).Select(i => kept.Snow(i / 4, i % 4)).ToArray();
            var keptFidelity = KeptFidelity();
            using (var baker = new ImpostorBaker(AssetDatabase.LoadAssetAtPath<Shader>(BakeShaderPath)))
            {
                bool first = true;
                foreach (string model in SpeciesMap.Models)
                {
                    if (!Selected(model))
                    {
                        for (int v = 0; v < SpeciesMap.VariantsPerModel; v++)
                        {
                            int p = SpeciesMap.IndexOf(model) * SpeciesMap.VariantsPerModel + v;
                            string name = $"{model}_v{v}";
                            var keptPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/Prefabs/{name}.prefab");
                            if (keptPrefab == null || p >= keptCount || !keptFidelity.ContainsKey(name))
                                throw new InvalidDataException($"{name} has no committed import to keep (prefab {(keptPrefab != null ? "found" : "missing")}, " +
                                                               $"{keptCount} prototypes in the set, fidelity line {(keptFidelity.ContainsKey(name) ? "found" : "missing")}): add it to -treeModels.");
                            prototypes.Add(name);
                            heights.Add(keptHeights[p]);
                            brightness.AddRange(keptBrightness.Skip(p * 4).Take(4));
                            snow.AddRange(keptSnow.Skip(p * 4).Take(4));
                            report.Append(first ? "" : ",\n").Append("    \"").Append(name).Append("\": ").Append(keptFidelity[name]);
                            first = false;
                        }
                        continue;
                    }
                    var materials = SpeciesMaterials(model);
                    for (int v = 0; v < SpeciesMap.VariantsPerModel; v++)
                    {
                        string fbx = $"{Root}/Models/{model}_v{v}.fbx";
                        var importer = (ModelImporter)AssetImporter.GetAtPath(fbx) ?? throw new FileNotFoundException(fbx);
                        importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                        foreach (var kv in materials) importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
                        importer.importCameras = false;
                        importer.importLights = false;
                        importer.addCollider = false;
                        importer.SaveAndReimport();   // TreeModelPostprocessor shades the crown
                        var (prefab, height, fidelity, lodBrightness, lodSnow) = BuildPrefab(model, v, fbx, baker, quad);
                        if (model == "krummholz") CheckDownwind(prefab);
                        prototypes.Add(prefab.name);
                        heights.Add(height);
                        brightness.AddRange(lodBrightness);
                        snow.AddRange(lodSnow);
                        report.Append(first ? "" : ",\n").Append("    \"").Append(model).Append("_v").Append(v).Append("\": ").Append(fidelity);
                        first = false;
                    }
                }
            }
            report.Append("\n  }\n}\n");
            File.WriteAllText(Root + "/fidelity.json", report.ToString());

            var set = AssetDatabase.LoadAssetAtPath<TreePrototypeSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<TreePrototypeSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            // Every prototype by its saved path: references held across the bakes' imports can come back unloaded.
            set.Prefabs = prototypes.Select(n => AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/Prefabs/{n}.prefab")).ToArray();
            if (set.Prefabs.Any(f => f == null)) throw new InvalidDataException("A tree prefab is missing after the import.");
            set.NativeHeights = heights.ToArray();
            set.LodBrightness = brightness.ToArray();
            set.LodSnow = snow.ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(Root + "/fidelity.json");
            Debug.Log($"[TreeImport] {prototypes.Count} tree prefabs from {SpeciesMap.Models.Length} models ({(only == null ? "all imported" : "imported: " + string.Join(", ", only))}) → {SetPath}; LOD fidelity in {Root}/fidelity.json");
        }

        /// <summary>Each variant's line in the committed fidelity report, verbatim, by name ("model_vN").</summary>
        static Dictionary<string, string> KeptFidelity()
        {
            var lines = new Dictionary<string, string>();
            string path = Root + "/fidelity.json";
            if (!File.Exists(path)) return lines;
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                int q = t.IndexOf("\": {", System.StringComparison.Ordinal);
                if (!t.StartsWith("\"") || q < 0) continue;
                string name = t.Substring(1, q - 1);
                if (name != "trees") lines[name] = t.Substring(q + 3).TrimEnd(',');
            }
            return lines;
        }

        /// <summary>
        /// Krummholz reaches downwind, and the placement code turns a prefab's +Z to face downwind, so its LOD0
        /// foliage must lie toward +Z (built toward Blender -Y: tools/assets/trees/README.md). Fails the import
        /// otherwise, so a mirrored or turned export can't reach the game.
        /// </summary>
        static void CheckDownwind(GameObject prefab)
        {
            var lod0 = prefab.GetComponentsInChildren<MeshFilter>().First(f => f.name.EndsWith("_LOD0"));
            var mesh = lod0.sharedMesh;
            var toPrefab = prefab.transform.worldToLocalMatrix * lod0.transform.localToWorldMatrix;
            var v = mesh.vertices;
            var sum = Vector3.zero;
            int n = 0;
            for (int s = 1; s < mesh.subMeshCount; s++)
                foreach (int i in mesh.GetIndices(s))
                {
                    sum += toPrefab.MultiplyPoint3x4(v[i]);
                    n++;
                }
            var centroid = n > 0 ? sum / n : Vector3.zero;
            Debug.Log($"[TreeImport] {prefab.name}: LOD0 foliage centroid {centroid.ToString("F2")} (+Z is downwind)");
            if (centroid.z <= 0.05f || Mathf.Abs(centroid.x) > centroid.z)
                throw new InvalidDataException($"{prefab.name}'s foliage doesn't reach toward +Z (centroid {centroid}): check the FBX export axes.");
        }

        static Dictionary<string, Material> SpeciesMaterials(string model)
        {
            string tex = $"{Root}/Textures/{model}";
            bool conifer = File.Exists($"{tex}_spray.png");
            var result = new Dictionary<string, Material>
            {
                [$"{model}_Bark"] = MakeMaterial($"{model}_Bark", $"{tex}_bark.png", false, 0.15f, $"{tex}_bark_normal.png"),
            };
            if (conifer)
            {
                result[$"{model}_Foliage"] = MakeMaterial($"{model}_Foliage", $"{tex}_spray.png", true, 0.1f);
                result[$"{model}_Cluster"] = MakeMaterial($"{model}_Cluster", $"{tex}_cluster.png", true, 0.1f);   // LOD1-2: branch clusters
            }
            else
            {
                result[$"{model}_Leaves"] = Hidden();                                   // winter: summer leaves are off
                result[$"{model}_Twigs"] = MakeMaterial($"{model}_Twigs", $"{tex}_twigs.png", true, 0.1f);
                string kept = File.Exists($"{tex}_leaves_kept.png") ? $"{tex}_leaves_kept.png" : null;
                result[$"{model}_LeavesKept"] = kept != null ? MakeMaterial($"{model}_LeavesKept", kept, true, 0.1f) : Hidden();
            }
            return result;
        }

        static Material MakeMaterial(string name, string texturePath, bool cutout, float smoothness, string normalPath = null)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0);
            m.SetFloat("_AlphaClip", cutout ? 1 : 0);
            m.SetFloat("_Cutoff", 0.5f);
            m.SetFloat("_Cull", cutout ? (float)CullMode.Off : (float)CullMode.Back);
            if (cutout) m.EnableKeyword("_ALPHATEST_ON"); else m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = cutout ? (int)RenderQueue.AlphaTest : -1;
            var normal = normalPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath) : null;
            m.SetTexture("_BumpMap", normal);
            m.SetFloat("_BumpScale", 1f);
            if (normal != null) m.EnableKeyword("_NORMALMAP"); else m.DisableKeyword("_NORMALMAP");
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Draws nothing: every pixel is clipped (winter leaves).</summary>
        static Material Hidden()
        {
            string path = $"{Root}/Materials/HiddenLeaves.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.SetFloat("_AlphaClip", 1);
            m.SetFloat("_Cutoff", 1);
            m.SetColor("_BaseColor", new Color(0, 0, 0, 0));
            m.EnableKeyword("_ALPHATEST_ON");
            m.enableInstancing = true;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        public static bool IsHidden(Material m) => m == null || m.name.StartsWith("Hidden");

        static Mesh ImpostorQuad()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(QuadPath);
            if (mesh != null) return mesh;
            mesh = new Mesh { name = "ImpostorQuad" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100);   // placed by the shader
            AssetDatabase.CreateAsset(mesh, QuadPath);
            return mesh;
        }

        static (GameObject Prefab, float Height, string Fidelity, float[] LodBrightness, float[] LodSnow) BuildPrefab(string model, int variant, string fbx, ImpostorBaker baker, Mesh quad)
        {
            string name = $"{model}_v{variant}";
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var instance = (GameObject)Object.Instantiate(source);
            instance.name = name;
            foreach (var old in instance.GetComponentsInChildren<LODGroup>()) Object.DestroyImmediate(old);
            var lods = new Renderer[3];
            foreach (var r in instance.GetComponentsInChildren<MeshRenderer>())
                for (int i = 0; i < 3; i++)
                    if (r.name.EndsWith("_LOD" + i)) lods[i] = r;
            if (lods.Any(r => r == null)) throw new InvalidDataException($"{fbx} doesn't have LOD0-2.");
            foreach (var r in lods)
            {
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
            }

            // The impostor's frame: a cylinder around the crown, from LOD0's vertices (tree space).
            var meshes = lods.Select(r => r.GetComponent<MeshFilter>().sharedMesh).ToArray();
            var bounds = meshes[0].bounds;
            var centre = bounds.center;
            float halfWidth = 0;
            foreach (var m in meshes)
                foreach (var p in m.vertices)
                    halfWidth = Mathf.Max(halfWidth, new Vector2(p.x - centre.x, p.z - centre.z).magnitude);
            float height = bounds.size.y;

            string prefix = $"{Root}/Impostors/{name}";
            var (albedo, data) = baker.BakeAtlas(meshes[0], lods[0].sharedMaterials, centre, halfWidth, height);
            SavePng(prefix + "_albedo.png", albedo, Frames * FrameSize, true);
            SavePng(prefix + "_data.png", data, Frames * FrameSize, false);
            var material = ImpostorMaterial(name, prefix, centre, halfWidth, height);
            var (fidelity, ratios, snowRatios) = baker.Fidelity(meshes, lods.Select(r => r.sharedMaterials).ToArray(), centre, halfWidth, height);
            // Correct each LOD's brightness (within ±25%) and snow (within ×0.5–2) to LOD0's, the mean over the views.
            var lodBrightness = new float[4];
            var lodSnow = new float[4];
            for (int l = 0; l < 4; l++)
            {
                lodBrightness[l] = l < ratios.Length && ratios[l] > 0 ? Mathf.Clamp(1f / ratios[l], 0.75f, 1.25f) : 1f;
                lodSnow[l] = l < snowRatios.Length && snowRatios[l] > 0 ? Mathf.Clamp(1f / snowRatios[l], 0.5f, 2f) : 1f;
            }

            var impostor = new GameObject($"{name}_Impostor");
            impostor.transform.SetParent(instance.transform, false);
            impostor.AddComponent<MeshFilter>().sharedMesh = quad;
            var impostorRenderer = impostor.AddComponent<MeshRenderer>();
            impostorRenderer.sharedMaterial = material;
            impostorRenderer.shadowCastingMode = ShadowCastingMode.Off;

            var group = instance.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            group.SetLODs(new[]
            {
                new LOD(Transitions[0], new[] { lods[0] }),
                new LOD(Transitions[1], new[] { lods[1] }),
                new LOD(Transitions[2], new[] { lods[2] }),
                new LOD(Transitions[3], new Renderer[] { impostorRenderer }),
            });
            group.RecalculateBounds();

            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, $"{Root}/Prefabs/{name}.prefab");
            Object.DestroyImmediate(instance);
            return (prefab, bounds.max.y, fidelity, lodBrightness, lodSnow);
        }

        static void SavePng(string path, Color[] pixels, int size, bool colour)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, !colour);
            if (colour)
                for (int i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    pixels[i] = new Color(Mathf.LinearToGammaSpace(c.r), Mathf.LinearToGammaSpace(c.g), Mathf.LinearToGammaSpace(c.b), c.a);
                }
            tex.SetPixels(pixels);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.sRGBTexture = colour;
            ti.alphaIsTransparency = false;      // already dilated
            ti.mipmapEnabled = true;
            ti.mipMapsPreserveCoverage = colour;
            ti.alphaTestReferenceValue = 0.35f;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
        }

        static Material ImpostorMaterial(string name, string prefix, Vector3 centre, float halfWidth, float height)
        {
            string path = $"{Root}/Impostors/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(AssetDatabase.LoadAssetAtPath<Shader>(ImpostorShaderPath));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_ImpAlbedo", AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_albedo.png"));
            m.SetTexture("_ImpData", AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_data.png"));
            m.SetFloat("_ImpFrames", Frames);
            m.SetFloat("_ImpFrameSize", FrameSize);
            m.SetVector("_ImpCenter", centre);
            m.SetVector("_ImpSize", new Vector4(halfWidth, height, 0, 0));
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---- baking ---------------------------------------------------------------------------

        /// <summary>
        /// Renders trees into impostor frames with the bake shader, straight from command buffers (no
        /// camera, no pipeline), supersampled and with MSAA so coverage is soft.
        /// </summary>
        sealed class ImpostorBaker : System.IDisposable
        {
            readonly Shader _shader;
            readonly Dictionary<Material, Material> _bake = new Dictionary<Material, Material>();

            public ImpostorBaker(Shader shader)
            {
                _shader = shader ?? throw new FileNotFoundException(BakeShaderPath);
            }

            Material BakeMaterial(Material source)
            {
                if (_bake.TryGetValue(source, out var m)) return m;
                m = new Material(_shader);
                m.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
                bool cutout = source.IsKeywordEnabled("_ALPHATEST_ON");
                m.SetFloat("_Cutoff", cutout ? FoliageCutoff : 0);
                m.SetFloat("_Foliage", cutout ? 1 : 0);
                _bake[source] = m;
                return m;
            }

            public static Vector3 HemiOctDecode(Vector2 uv)
            {
                var e = uv * 2 - Vector2.one;
                float x = (e.x + e.y) * 0.5f, z = (e.x - e.y) * 0.5f;
                return new Vector3(x, 1 - Mathf.Abs(x) - Mathf.Abs(z), z).normalized;
            }

            public static void ViewBasis(Vector3 d, out Vector3 right, out Vector3 up)
            {
                var reference = Mathf.Abs(d.y) > 0.999f ? Vector3.forward : Vector3.up;
                right = Vector3.Cross(reference, d).normalized;
                up = Vector3.Cross(d, right);
            }

            public static Vector2 Extent(Vector3 d, float halfWidth, float height)
            {
                float s = d.y, c = Mathf.Sqrt(Mathf.Clamp01(1 - s * s));
                return new Vector2(2 * halfWidth, height * c + 2 * halfWidth * s) * 1.04f;
            }

            /// <summary>Renders one view (tree space direction d towards the viewer) of a mesh into a viewport.</summary>
            void DrawView(CommandBuffer cmd, Mesh mesh, Material[] materials, Vector3 centre, float halfWidth, float height, Vector3 d, Rect viewport, int pass)
            {
                ViewBasis(d, out var right, out var up);
                var e = Extent(d, halfWidth, height);
                float distance = height + 2 * halfWidth + 10;
                var eye = centre + d * distance;
                // View matrix rows: right, up, d (towards the viewer), as the impostor shader maps frames.
                var view = new Matrix4x4(
                    new Vector4(right.x, up.x, d.x, 0), new Vector4(right.y, up.y, d.y, 0),
                    new Vector4(right.z, up.z, d.z, 0), new Vector4(-Vector3.Dot(right, eye), -Vector3.Dot(up, eye), -Vector3.Dot(d, eye), 1));
                var proj = Matrix4x4.Ortho(-e.x / 2, e.x / 2, -e.y / 2, e.y / 2, 0.1f, 2 * distance);
                cmd.SetViewport(viewport);
                cmd.SetViewProjectionMatrices(view, proj);
                cmd.SetGlobalVector("_BakeViewDir", d);
                for (int s = 0; s < mesh.subMeshCount && s < materials.Length; s++)
                {
                    if (IsHidden(materials[s])) continue;
                    cmd.DrawMesh(mesh, Matrix4x4.identity, BakeMaterial(materials[s]), s, pass);
                }
            }

            /// <summary>Renders views into a cells × cells grid; returns (albedo, data) after supersample reduction.</summary>
            (Color[] Albedo, Color[] Data) Render(Mesh mesh, Material[] materials, Vector3 centre, float halfWidth, float height, Vector3[] directions, int cells, int cellSize)
            {
                int cell = cellSize * Supersample, size = cells * cell;
                var target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { antiAliasing = 4 };
                var resolved = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                var result = new Color[2][];
                for (int pass = 0; pass < 2; pass++)
                {
                    var cmd = new CommandBuffer { name = "Impostor bake" };
                    cmd.SetRenderTarget(target);
                    cmd.ClearRenderTarget(true, true, Color.clear);
                    for (int k = 0; k < directions.Length; k++)
                        DrawView(cmd, mesh, materials, centre, halfWidth, height, directions[k], new Rect(k % cells * cell, k / cells * cell, cell, cell), pass);
                    Graphics.ExecuteCommandBuffer(cmd);
                    cmd.Release();
                    Graphics.Blit(target, resolved);   // MSAA resolve
                    var previous = RenderTexture.active;
                    RenderTexture.active = resolved;
                    var tex = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
                    tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    tex.Apply(false);
                    RenderTexture.active = previous;
                    result[pass] = tex.GetPixels();
                    Object.DestroyImmediate(tex);
                }
                target.Release();
                resolved.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(resolved);
                return Reduce(result[0], result[1], cells * cellSize);
            }

            /// <summary>Averages supersamples, unpremultiplies by coverage and dilates colour into transparent texels.</summary>
            static (Color[] Albedo, Color[] Data) Reduce(Color[] albedo, Color[] data, int size)
            {
                int ss = Supersample, big = size * ss;
                var a = new Color[size * size];
                var d = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        Color ca = Color.clear, cd = Color.clear;
                        for (int j = 0; j < ss; j++)
                            for (int i = 0; i < ss; i++)
                            {
                                int k = (y * ss + j) * big + x * ss + i;
                                ca += albedo[k];
                                cd += data[k];
                            }
                        float cover = ca.a;
                        int o = y * size + x;
                        if (cover > 1e-4f)
                        {
                            a[o] = new Color(ca.r / cover, ca.g / cover, ca.b / cover, cover / (ss * ss));
                            d[o] = new Color(cd.r / cover, cd.g / cover, cd.b / cover, cd.a / cover);
                        }
                        else
                        {
                            a[o] = new Color(0, 0, 0, 0);
                            d[o] = new Color(-1, 0, 0, 0);   // empty marker for dilation
                        }
                    }
                Dilate(a, d, size, 6);
                return (a, d);
            }

            static void Dilate(Color[] a, Color[] d, int size, int passes)
            {
                for (int pass = 0; pass < passes; pass++)
                {
                    var ca = (Color[])a.Clone();
                    var cd = (Color[])d.Clone();
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            int o = y * size + x;
                            if (cd[o].r >= 0) continue;
                            Color sa = Color.clear, sd = Color.clear;
                            int n = 0;
                            for (int j = -1; j <= 1; j++)
                                for (int i = -1; i <= 1; i++)
                                {
                                    int xx = x + i, yy = y + j;
                                    if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                                    int k = yy * size + xx;
                                    if (cd[k].r < 0) continue;
                                    sa += ca[k];
                                    sd += cd[k];
                                    n++;
                                }
                            if (n == 0) continue;
                            a[o] = new Color(sa.r / n, sa.g / n, sa.b / n, 0);
                            d[o] = new Color(sd.r / n, sd.g / n, sd.b / n, sd.a / n);
                        }
                }
                for (int o = 0; o < d.Length; o++)
                    if (d[o].r < 0) d[o] = new Color(0.5f, 1f, 1f, 0);   // far from any tree: an upward normal
            }

            public (Color[] Albedo, Color[] Data) BakeAtlas(Mesh mesh, Material[] materials, Vector3 centre, float halfWidth, float height)
            {
                var directions = new Vector3[Frames * Frames];
                for (int j = 0; j < Frames; j++)
                    for (int i = 0; i < Frames; i++)
                        directions[j * Frames + i] = HemiOctDecode(new Vector2(i, j) / (Frames - 1));
                return Render(mesh, materials, centre, halfWidth, height, directions, Frames, FrameSize);
            }

            /// <summary>
            /// Coverage and lit brightness of LOD1, LOD2 and the impostor's nearest frame relative to LOD0, from
            /// the horizon, 22° and 53° (the frames in the atlas's diagonal), as JSON.
            /// </summary>
            public (string Json, float[] BrightnessRatio, float[] SnowRatio) Fidelity(Mesh[] lods, Material[][] materials, Vector3 centre, float halfWidth, float height)
            {
                var views = new[] { new Vector2(7, 7), new Vector2(6, 6), new Vector2(5, 5) }.Select(g => HemiOctDecode(g / (Frames - 1))).ToArray();
                var cover = new float[lods.Length, views.Length];
                var bright = new float[lods.Length, views.Length];
                var snowy = new float[lods.Length, views.Length];
                for (int l = 0; l < lods.Length; l++)
                {
                    var (albedo, data) = Render(lods[l], materials[l], centre, halfWidth, height, views, views.Length, FrameSize);
                    int size = views.Length * FrameSize;
                    for (int v = 0; v < views.Length; v++)
                    {
                        // A sun from above and in front of the viewer, a little to the side.
                        ViewBasis(views[v], out var right, out _);
                        var sun = (views[v] * 0.5f + Vector3.up * 0.75f + right * 0.4f).normalized;
                        double c = 0, b = 0, sn = 0;
                        for (int y = 0; y < FrameSize; y++)
                            for (int x = 0; x < FrameSize; x++)
                            {
                                int o = y * size + v * FrameSize + x;
                                var a = albedo[o];
                                if (a.a <= 0) continue;
                                var dd = data[o];
                                var n = OctDecode(dd.r, dd.g);
                                float wrap = Mathf.Clamp01((Vector3.Dot(n, sun) + 0.3f) / 1.3f);
                                float lum = 0.2126f * a.r + 0.7152f * a.g + 0.0722f * a.b;
                                c += a.a;
                                b += a.a * lum * (wrap * Mathf.Lerp(1, dd.b, 0.6f) + 0.45f * dd.b);
                                sn += a.a * dd.a;
                            }
                        cover[l, v] = (float)(c / (FrameSize * FrameSize));
                        bright[l, v] = (float)(c > 0 ? b / c : 0);
                        snowy[l, v] = (float)(c > 0 ? sn / c : 0);
                    }
                }
                var sb = new StringBuilder("{");
                for (int l = 1; l < lods.Length; l++)
                {
                    sb.Append(l > 1 ? ", " : "").Append("\"LOD").Append(l).Append("\": {\"coverage\": [");
                    for (int v = 0; v < views.Length; v++) sb.Append(v > 0 ? ", " : "").Append((cover[l, v] / Mathf.Max(1e-4f, cover[0, v])).ToString("F2", CultureInfo.InvariantCulture));
                    sb.Append("], \"brightness\": [");
                    for (int v = 0; v < views.Length; v++) sb.Append(v > 0 ? ", " : "").Append((bright[l, v] / Mathf.Max(1e-4f, bright[0, v])).ToString("F2", CultureInfo.InvariantCulture));
                    sb.Append("], \"snow\": [");
                    for (int v = 0; v < views.Length; v++) sb.Append(v > 0 ? ", " : "").Append((snowy[l, v] / Mathf.Max(1e-4f, snowy[0, v])).ToString("F2", CultureInfo.InvariantCulture));
                    sb.Append("]}");
                }
                sb.Append(", \"LOD0coverage\": [");
                for (int v = 0; v < views.Length; v++) sb.Append(v > 0 ? ", " : "").Append(cover[0, v].ToString("F3", CultureInfo.InvariantCulture));
                sb.Append("]}");
                var ratios = new float[lods.Length];
                var snowRatios = new float[lods.Length];
                for (int l = 0; l < lods.Length; l++)
                {
                    float sum = 0, sumSnow = 0;
                    for (int v = 0; v < views.Length; v++)
                    {
                        sum += bright[l, v] / Mathf.Max(1e-4f, bright[0, v]);
                        sumSnow += snowy[0, v] > 0.01f ? snowy[l, v] / snowy[0, v] : 1f;
                    }
                    ratios[l] = sum / views.Length;
                    snowRatios[l] = sumSnow / views.Length;
                }
                return (sb.ToString(), ratios, snowRatios);
            }

            static Vector3 OctDecode(float ex, float ey)
            {
                float fx = ex * 2 - 1, fy = ey * 2 - 1;
                var n = new Vector3(fx, 1 - Mathf.Abs(fx) - Mathf.Abs(fy), fy);
                float t = Mathf.Clamp01(-n.y);
                n.x += n.x >= 0 ? -t : t;
                n.z += n.z >= 0 ? -t : t;
                return n.normalized;
            }

            public void Dispose()
            {
                foreach (var m in _bake.Values) Object.DestroyImmediate(m);
                _bake.Clear();
            }
        }
    }
}
