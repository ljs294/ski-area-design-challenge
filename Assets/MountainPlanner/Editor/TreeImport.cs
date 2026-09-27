using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Flora;
using MountainPlanner.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Brings the Blender tree library (tools/assets/trees/out, built by build-trees.bat) into Unity
    /// (task 08): copies the FBX files and textures, makes URP materials, bakes a far "card" image of
    /// each variant, and saves one LODGroup prefab per variant plus the <see cref="TreePrototypeSet"/>.
    ///
    ///   LOD0 (near) · LOD1 · LOD2 · card (two crossed quads, 4 triangles) · culled
    ///
    /// Winter (iteration 1): deciduous leaves use an invisible material, so aspens and birches are bare
    /// twigs; beech keeps its dry leaves. Snow on branches comes with the tree shader (TR4).
    /// </summary>
    public static class TreeImport
    {
        const string Source = "tools/assets/trees/out";
        const string Root = "Assets/MountainPlanner/Art/Trees";
        public const string SetPath = Root + "/TreePrototypes.asset";
        const int CardWidth = 256, CardHeight = 512;

        /// <summary>Screen heights where each LOD hands over (the card lasts to about 4 km for a 20 m tree).</summary>
        static readonly float[] Transitions = { 0.30f, 0.12f, 0.05f, 0.004f };

        [MenuItem("Mountain Planner/Import Trees")]
        public static void Import()
        {
            if (!Directory.Exists(Source)) throw new DirectoryNotFoundException($"{Source} is missing: run tools/assets/trees/build-trees.bat first.");
            foreach (string sub in new[] { "Models", "Textures", "Materials", "Cards", "Prefabs" }) Directory.CreateDirectory(Path.Combine(Root, sub));
            foreach (string f in Directory.GetFiles(Source, "*.fbx")) File.Copy(f, Path.Combine(Root, "Models", Path.GetFileName(f)), true);
            foreach (string f in Directory.GetFiles(Path.Combine(Source, "textures"), "*.png")) File.Copy(f, Path.Combine(Root, "Textures", Path.GetFileName(f)), true);
            AssetDatabase.Refresh();

            foreach (string path in Directory.GetFiles(Path.Combine(Root, "Textures"), "*.png").Select(p => p.Replace('\\', '/')))
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                bool card = !path.EndsWith("_bark.png");
                ti.alphaIsTransparency = card;
                ti.mipmapEnabled = true;
                ti.mipMapsPreserveCoverage = card;
                ti.alphaTestReferenceValue = 0.5f;
                ti.wrapMode = card ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                ti.SaveAndReimport();
            }

            var prefabs = new List<GameObject>();
            var heights = new List<float>();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var baker = new CardBaker();
            try
            {
                foreach (string model in SpeciesMap.Models)
                {
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
                        importer.SaveAndReimport();
                        var (prefab, height) = BuildPrefab(model, v, fbx, baker);
                        prefabs.Add(prefab);
                        heights.Add(height);
                    }
                }
            }
            finally
            {
                baker.Dispose();
            }

            var set = AssetDatabase.LoadAssetAtPath<TreePrototypeSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<TreePrototypeSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            set.Prefabs = prefabs.ToArray();
            set.NativeHeights = heights.ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log($"[TreeImport] {prefabs.Count} tree prefabs from {SpeciesMap.Models.Length} species → {SetPath}");
        }

        static Dictionary<string, Material> SpeciesMaterials(string model)
        {
            string tex = $"{Root}/Textures/{model}";
            bool conifer = File.Exists($"{tex}_spray.png");
            var result = new Dictionary<string, Material>
            {
                [$"{model}_Bark"] = MakeMaterial($"{model}_Bark", $"{tex}_bark.png", false, 0.15f),
            };
            if (conifer) result[$"{model}_Foliage"] = MakeMaterial($"{model}_Foliage", $"{tex}_spray.png", true, 0.1f);
            else
            {
                result[$"{model}_Leaves"] = Hidden();                                   // winter: summer leaves are off
                result[$"{model}_Twigs"] = MakeMaterial($"{model}_Twigs", $"{tex}_twigs.png", true, 0.1f);
                string kept = File.Exists($"{tex}_leaves_kept.png") ? $"{tex}_leaves_kept.png" : null;
                result[$"{model}_LeavesKept"] = kept != null ? MakeMaterial($"{model}_LeavesKept", kept, true, 0.1f) : Hidden();
            }
            return result;
        }

        static Material MakeMaterial(string name, string texturePath, bool cutout, float smoothness)
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

        static (GameObject Prefab, float Height) BuildPrefab(string model, int variant, string fbx, CardBaker baker)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var instance = (GameObject)Object.Instantiate(source);
            instance.name = $"{model}_v{variant}";
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

            var bounds = lods[0].bounds;
            float height = bounds.max.y;
            instance.SetActive(false);   // only the baker's copy of LOD0 may be in the picture
            var (cardMesh, cardMaterial) = baker.Bake($"{model}_v{variant}", lods[0], bounds, Root);
            instance.SetActive(true);
            var card = new GameObject($"{model}_v{variant}_Card");
            card.transform.SetParent(instance.transform, false);
            card.AddComponent<MeshFilter>().sharedMesh = cardMesh;
            var cardRenderer = card.AddComponent<MeshRenderer>();
            cardRenderer.sharedMaterial = cardMaterial;
            cardRenderer.shadowCastingMode = ShadowCastingMode.Off;

            var group = instance.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            group.SetLODs(new[]
            {
                new LOD(Transitions[0], new[] { lods[0] }),
                new LOD(Transitions[1], new[] { lods[1] }),
                new LOD(Transitions[2], new[] { lods[2] }),
                new LOD(Transitions[3], new Renderer[] { cardRenderer }),
            });
            group.RecalculateBounds();

            string path = $"{Root}/Prefabs/{model}_v{variant}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return (prefab, height);
        }

        /// <summary>
        /// Renders a tree side-on into a card texture. Alpha comes from two renders, on black and on white
        /// (alpha = 1 − (white − black)), which works whatever the pipeline does with the alpha channel.
        /// </summary>
        sealed class CardBaker : System.IDisposable
        {
            readonly GameObject _cameraGo, _lightGo;
            readonly Camera _camera;
            readonly RenderTexture _target;

            public CardBaker()
            {
                _cameraGo = new GameObject("CardCamera");
                _camera = _cameraGo.AddComponent<Camera>();
                _camera.orthographic = true;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.enabled = false;
                _camera.nearClipPlane = 0.1f;
                _camera.farClipPlane = 200;
                _target = new RenderTexture(CardWidth, CardHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
                _camera.targetTexture = _target;
                _lightGo = new GameObject("CardLight");
                var light = _lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                light.shadows = LightShadows.None;
                _lightGo.transform.rotation = Quaternion.Euler(35, 160, 0);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.55f, 0.6f, 0.68f);
            }

            public (Mesh Mesh, Material Material) Bake(string name, Renderer lod, Bounds bounds, string root)
            {
                var copy = Object.Instantiate(lod.gameObject);
                copy.SetActive(true);
                copy.transform.SetPositionAndRotation(lod.transform.position, lod.transform.rotation);
                copy.transform.localScale = lod.transform.lossyScale;
                float w = Mathf.Max(bounds.size.x, bounds.size.z) * 1.04f, h = bounds.max.y * 1.02f;
                float size = Mathf.Max(h / 2, w / 2 * CardHeight / CardWidth);
                _camera.orthographicSize = size;
                _camera.transform.position = new Vector3(bounds.center.x, size, bounds.center.z - 100);
                _camera.transform.rotation = Quaternion.identity;

                var black = Grab(Color.black);
                var white = Grab(Color.white);
                Object.DestroyImmediate(copy);

                var pixels = new Color[black.Length];
                Color sum = Color.clear;
                int solid = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    float a = Mathf.Clamp01(1 - (white[i].g - black[i].g));
                    var c = a > 0.01f ? black[i] / a : Color.black;
                    pixels[i] = new Color(c.r, c.g, c.b, a > 0.5f ? 1 : 0);
                    if (a > 0.5f) { sum += pixels[i]; solid++; }
                }
                // Transparent pixels take the average colour, so mipmaps don't fringe the card dark.
                var fill = solid > 0 ? sum / solid : Color.gray;
                for (int i = 0; i < pixels.Length; i++) if (pixels[i].a == 0) pixels[i] = new Color(fill.r, fill.g, fill.b, 0);
                var tex = new Texture2D(CardWidth, CardHeight, TextureFormat.RGBA32, false);
                tex.SetPixels(pixels);
                tex.Apply();
                string png = $"{root}/Cards/{name}_card.png";
                File.WriteAllBytes(png, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(png);
                var ti = (TextureImporter)AssetImporter.GetAtPath(png);
                ti.alphaIsTransparency = true;
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.5f;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();

                var material = MakeMaterial(name + "_Card", png, true, 0.05f);
                var mesh = CrossedQuads(name + "_Card", 2 * size * CardWidth / CardHeight, 2 * size, bounds.center.x, bounds.center.z);
                string meshPath = $"{root}/Cards/{name}_card.asset";
                AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(mesh, meshPath);
                return (mesh, material);
            }

            Color[] Grab(Color background)
            {
                _camera.backgroundColor = background;
                _camera.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = _target;
                var tex = new Texture2D(CardWidth, CardHeight, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, CardWidth, CardHeight), 0, 0);
                tex.Apply();
                RenderTexture.active = previous;
                var pixels = tex.GetPixels();
                Object.DestroyImmediate(tex);
                return pixels;
            }

            /// <summary>Two vertical quads crossed at right angles, base at y = 0.</summary>
            static Mesh CrossedQuads(string name, float width, float height, float cx, float cz)
            {
                float h = width / 2;
                var v = new[]
                {
                    new Vector3(cx - h, 0, cz), new Vector3(cx + h, 0, cz), new Vector3(cx + h, height, cz), new Vector3(cx - h, height, cz),
                    new Vector3(cx, 0, cz - h), new Vector3(cx, 0, cz + h), new Vector3(cx, height, cz + h), new Vector3(cx, height, cz - h),
                };
                var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                // Normals lean upward so both faces light like a crown, not like a flat wall.
                var n = new[] { new Vector3(0, 0.6f, -0.8f), new Vector3(-0.8f, 0.6f, 0) };
                var mesh = new Mesh { name = name, vertices = v, uv = uv, normals = new[] { n[0], n[0], n[0], n[0], n[1], n[1], n[1], n[1] } };
                mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
                mesh.RecalculateBounds();
                return mesh;
            }

            public void Dispose()
            {
                _camera.targetTexture = null;
                _target.Release();
                Object.DestroyImmediate(_target);
                Object.DestroyImmediate(_cameraGo);
                Object.DestroyImmediate(_lightGo);
            }
        }
    }
}
