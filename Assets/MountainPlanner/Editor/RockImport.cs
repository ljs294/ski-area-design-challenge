using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Cover;
using MountainPlanner.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Procedural rocks (style tile, owner request): six boulder and four outcrop meshes, each at four
    /// levels of detail (1,280 / 320 / 80 / 20 triangles), all generated from keyed noise, so they are
    /// repeatable. Boulders are lumpy, flattened stones; outcrops are wide slabs with stepped strata
    /// ledges. Every mesh is 1 m tall at scale 1; placement scales them (<see cref="RockPlacement"/>).
    /// Drawn with the tree shader's triplanar rock mode, with snow on their upper faces.
    /// </summary>
    public static class RockImport
    {
        const string Root = "Assets/MountainPlanner/Art/Rocks";
        public const string SetPath = Root + "/RockPrototypes.asset";
        static readonly int[] Levels = { 3, 2, 1, 0 };
        static readonly float[] Transitions = { 0.15f, 0.06f, 0.025f, 0.003f };

        [MenuItem("Mountain Planner/Generate Rocks")]
        public static void Generate()
        {
            Directory.CreateDirectory(Root + "/Meshes");
            Directory.CreateDirectory(Root + "/Prefabs");
            var material = RockMaterial();
            var prefabs = new List<GameObject>();
            for (int p = 0; p < RockPlacement.Prototypes; p++)
            {
                bool outcrop = p >= RockPlacement.BoulderVariants;
                string name = outcrop ? $"outcrop_{p - RockPlacement.BoulderVariants}" : $"boulder_{p}";
                var root = new GameObject(name);
                var renderers = new List<Renderer>();
                for (int l = 0; l < Levels.Length; l++)
                {
                    var mesh = Build(Levels[l], p, outcrop);
                    mesh.name = $"{name}_LOD{l}";
                    string meshPath = $"{Root}/Meshes/{mesh.name}.asset";
                    AssetDatabase.DeleteAsset(meshPath);
                    AssetDatabase.CreateAsset(mesh, meshPath);
                    var child = new GameObject(mesh.name);
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = child.AddComponent<MeshRenderer>();
                    r.sharedMaterial = material;
                    r.shadowCastingMode = l <= 1 ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    renderers.Add(r);
                }
                var group = root.AddComponent<LODGroup>();
                var lods = new LOD[Levels.Length];
                for (int l = 0; l < lods.Length; l++) lods[l] = new LOD(Transitions[l], new[] { renderers[l] });
                group.SetLODs(lods);
                prefabs.Add(PrefabUtility.SaveAsPrefabAsset(root, $"{Root}/Prefabs/{name}.prefab"));
                Object.DestroyImmediate(root);
            }

            var set = AssetDatabase.LoadAssetAtPath<TreePrototypeSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<TreePrototypeSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            set.Prefabs = prefabs.ToArray();
            set.NativeHeights = new float[prefabs.Count];
            for (int i = 0; i < prefabs.Count; i++) set.NativeHeights[i] = 1;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log($"[RockImport] {prefabs.Count} rock prototypes → {SetPath}");
        }

        /// <summary>The rock material: the tree shader in triplanar mode with the granite ground texture.</summary>
        static Material RockMaterial()
        {
            string texPath = Root + "/RockAlbedo.png";
            var array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.AlbedoPath);
            if (array == null)
            {
                GroundTextures.Generate();
                array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.AlbedoPath);
            }
            var rock = new Texture2D(array.width, array.height, TextureFormat.RGBA32, false);
            rock.SetPixels32(array.GetPixels32(3, 0));   // slot 3: rock
            rock.Apply();
            File.WriteAllBytes(texPath, rock.EncodeToPNG());
            Object.DestroyImmediate(rock);
            AssetDatabase.ImportAsset(texPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.alphaIsTransparency = false;
            ti.SaveAndReimport();

            string path = Root + "/Rock.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/TreeInstanced.shader"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetFloat("_Triplanar", 1);
            m.SetFloat("_TriplanarScale", 1f / 7f);
            m.SetFloat("_Cutoff", 0);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---- shapes ----------------------------------------------------------------------------

        static Mesh Build(int level, int prototype, bool outcrop)
        {
            var (vertices, triangles) = Icosphere(level);
            ulong seed = 0xB0C4UL + (ulong)prototype * 7919UL;
            float sx = 1 + 0.4f * Hash(seed, 1), sz = 0.8f + 0.4f * Hash(seed, 2), sy = 1;
            // Outcrops are crags: taller than wide, narrow front to back, so they rise out of a cliff.
            if (outcrop) { sx *= 0.9f; sz *= 0.6f; sy = 2.4f; }
            var shaped = new Vector3[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                var d = vertices[i];
                float lump = Fbm(d * 1.6f, seed, 4);
                float r = 1 + 0.35f * (lump - 0.5f);
                var p = new Vector3(d.x * r * sx, d.y * r * sy, d.z * r * sz);
                if (outcrop)
                {
                    // Strata: the slab steps in at each bedding plane, with a lip under each ledge.
                    float bed = p.y * 2.5f + 0.3f * Fbm(d * 3, seed + 7, 2);
                    float step = bed - Mathf.Floor(bed);
                    float inset = 1 - 0.12f * Mathf.SmoothStep(0.55f, 0.95f, step);
                    p.x *= inset;
                    p.z *= inset;
                }
                p.y = Mathf.Max(p.y, -0.55f * sy);   // flat base, sits on (and a little into) the ground
                shaped[i] = p;
            }
            // Base at y = 0 and exactly 1 m tall, so placement scales to a real height.
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in shaped) { min = Mathf.Min(min, p.y); max = Mathf.Max(max, p.y); }
            float scale = 1 / (max - min);
            for (int i = 0; i < shaped.Length; i++) shaped[i] = new Vector3(shaped[i].x * scale, (shaped[i].y - min) * scale, shaped[i].z * scale);

            var mesh = new Mesh { vertices = shaped, triangles = triangles.ToArray() };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static (List<Vector3> Vertices, List<int> Triangles) Icosphere(int level)
        {
            float t = (1 + Mathf.Sqrt(5)) / 2;
            var v = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
            var f = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            for (int l = 0; l < level; l++)
            {
                var cache = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (cache.TryGetValue(key, out int m)) return m;
                    v.Add(((v[a] + v[b]) * 0.5f).normalized);
                    cache[key] = v.Count - 1;
                    return v.Count - 1;
                }
                var next = new List<int>();
                for (int i = 0; i < f.Count; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                f = next;
            }
            return (v, f);
        }

        static float Hash(ulong seed, int k) => (float)((CoverNoise.Lattice(seed, k, 0) + 1) * 0.5);

        /// <summary>3D value noise from the keyed 2D lattice (one lattice per integer z).</summary>
        static float Value3(Vector3 p, ulong seed)
        {
            int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y), z0 = Mathf.FloorToInt(p.z);
            float tx = p.x - x0, ty = p.y - y0, tz = p.z - z0;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty); tz = tz * tz * (3 - 2 * tz);
            float L(int x, int y, int z) => (float)((CoverNoise.Lattice(seed ^ ((ulong)(uint)z * 0x9E3779B97F4A7C15UL), x, y) + 1) * 0.5);
            float a = Mathf.Lerp(Mathf.Lerp(L(x0, y0, z0), L(x0 + 1, y0, z0), tx), Mathf.Lerp(L(x0, y0 + 1, z0), L(x0 + 1, y0 + 1, z0), tx), ty);
            float b = Mathf.Lerp(Mathf.Lerp(L(x0, y0, z0 + 1), L(x0 + 1, y0, z0 + 1), tx), Mathf.Lerp(L(x0, y0 + 1, z0 + 1), L(x0 + 1, y0 + 1, z0 + 1), tx), ty);
            return Mathf.Lerp(a, b, tz);
        }

        static float Fbm(Vector3 p, ulong seed, int octaves)
        {
            float sum = 0, amp = 0.5f, norm = 0;
            for (int o = 0; o < octaves; o++, p *= 2.03f, amp *= 0.5f)
            {
                sum += amp * Value3(p + new Vector3(17.3f, 5.1f, 9.7f), seed + (ulong)o);
                norm += amp;
            }
            return sum / norm;
        }
    }
}
