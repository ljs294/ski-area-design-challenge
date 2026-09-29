using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Lights tree crowns as volumes (tree realism review). Imported trees are loose cards, and lighting
    /// them card by card gives a flat, speckled crown with no lit side, no shaded side and a bright
    /// interior. When a tree FBX imports, this measures its crown from LOD0's foliage (radius per height)
    /// and, for every LOD:
    ///   normals  foliage normals lean toward the crown's outward surface normal, so a crown shades as a
    ///            volume (the card's own facing survives in UV3 for snow)
    ///   UV3      x: ambient occlusion (inside and low in the crown is darker, the trunk inside the crown
    ///            darkest, the trunk's foot where it meets the ground), y/z/w: the card's own face normal
    ///            (y, x, z), so snow stays on its upper side
    /// LOD1 and LOD2 use LOD0's crown, so every LOD shades alike and switching LOD doesn't pop.
    /// </summary>
    public sealed class TreeModelPostprocessor : AssetPostprocessor
    {
        public const string ModelsFolder = "Assets/MountainPlanner/Art/Trees/Models/";

        static bool IsTree(string path) => path.StartsWith(ModelsFolder) && path.EndsWith(".fbx");

        void OnPreprocessModel()
        {
            if (!IsTree(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            importer.importNormals = ModelImporterNormals.Import;
            importer.isReadable = true;
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!IsTree(assetPath)) return;
            TreeShading.Apply(root);
        }
    }

    public static class TreeShading
    {
        /// <summary>How far foliage normals lean to the crown surface (0 = card, 1 = crown).</summary>
        public const float ConiferCrownNormal = 0.75f, BroadleafCrownNormal = 0.55f;
        /// <summary>Bark darkening where the trunk meets the ground, fading out over this height.</summary>
        public const float GroundContactAO = 0.7f, GroundContactMetres = 1.5f;

        public sealed class Crown
        {
            public float Bottom, Top;
            public float[] Radius;   // per band, smoothed
            public bool Conifer;

            public float RadiusAt(float y)
            {
                if (y < Bottom || y > Top) return 0;
                float f = (y - Bottom) / (Top - Bottom) * (Radius.Length - 1);
                int i = Mathf.Min((int)f, Radius.Length - 2);
                return Mathf.Lerp(Radius[i], Radius[i + 1], f - i);
            }

            public float SlopeAt(float y)
            {
                float h = (Top - Bottom) / Radius.Length;
                return (RadiusAt(Mathf.Min(Top, y + h)) - RadiusAt(Mathf.Max(Bottom, y - h))) / (2 * h);
            }
        }

        public static void Apply(GameObject root)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>();
            var lod0 = filters.FirstOrDefault(f => f.name.EndsWith("_LOD0"));
            if (lod0 == null) return;
            var crown = Measure(lod0.sharedMesh);
            foreach (var f in filters) Shade(f.sharedMesh, crown);
        }

        /// <summary>The crown's radius per height band from LOD0's foliage (90th percentile of distance from the trunk axis).</summary>
        public static Crown Measure(Mesh mesh)
        {
            var v = mesh.vertices;
            var foliage = new HashSet<int>();
            for (int s = 1; s < mesh.subMeshCount; s++) foreach (int i in mesh.GetIndices(s)) foliage.Add(i);
            var crown = new Crown { Conifer = mesh.subMeshCount <= 2 };
            if (foliage.Count == 0) { crown.Bottom = 0; crown.Top = 1; crown.Radius = new float[4]; return crown; }
            crown.Bottom = foliage.Min(i => v[i].y);
            crown.Top = foliage.Max(i => v[i].y);
            const int bands = 24;
            var lists = new List<float>[bands];
            for (int b = 0; b < bands; b++) lists[b] = new List<float>();
            foreach (int i in foliage)
            {
                int b = Mathf.Clamp((int)((v[i].y - crown.Bottom) / (crown.Top - crown.Bottom + 1e-5f) * bands), 0, bands - 1);
                lists[b].Add(Mathf.Sqrt(v[i].x * v[i].x + v[i].z * v[i].z));
            }
            var r = new float[bands];
            for (int b = 0; b < bands; b++)
            {
                if (lists[b].Count < 4) { r[b] = -1; continue; }
                lists[b].Sort();
                r[b] = lists[b][(int)(lists[b].Count * 0.9f)];
            }
            // Fill empty bands from their neighbours, then smooth twice.
            for (int b = 0; b < bands; b++)
                if (r[b] < 0)
                {
                    int lo = b, hi = b;
                    while (lo >= 0 && r[lo] < 0) lo--;
                    while (hi < bands && r[hi] < 0) hi++;
                    r[b] = lo >= 0 && hi < bands ? (r[lo] + r[hi]) / 2 : lo >= 0 ? r[lo] : hi < bands ? r[hi] : 0.5f;
                }
            for (int pass = 0; pass < 2; pass++)
            {
                var s = (float[])r.Clone();
                for (int b = 0; b < bands; b++) r[b] = (s[Mathf.Max(0, b - 1)] + 2 * s[b] + s[Mathf.Min(bands - 1, b + 1)]) / 4;
            }
            crown.Radius = r;
            return crown;
        }

        public static void Shade(Mesh mesh, Crown crown)
        {
            var v = mesh.vertices;
            var n = mesh.normals;
            if (n == null || n.Length != v.Length) { mesh.RecalculateNormals(); n = mesh.normals; }
            var foliage = new bool[v.Length];
            for (int s = 1; s < mesh.subMeshCount; s++) foreach (int i in mesh.GetIndices(s)) foliage[i] = true;
            float lean = crown.Conifer ? ConiferCrownNormal : BroadleafCrownNormal;
            float inner = crown.Conifer ? 0.32f : 0.62f;
            var uv3 = new List<Vector4>(v.Length);
            var normals = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                var p = v[i];
                var face = n[i].normalized;
                float r = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                float R = crown.RadiusAt(p.y);
                float ao;
                Vector3 hull;
                if (R < 0.05f)
                {
                    // Outside the crown's height (bare trunk below it, the leader above it).
                    ao = p.y < crown.Bottom ? 0.9f : 1f;
                    hull = p.y > crown.Top - 0.5f ? Vector3.up : (r > 1e-3f ? new Vector3(p.x / r, 0, p.z / r) : Vector3.up);
                }
                else
                {
                    float depth = Mathf.Clamp01(r / R);
                    float rise = Mathf.Clamp01((p.y - crown.Bottom) / (crown.Top - crown.Bottom));
                    ao = Mathf.Lerp(inner, 1f, Mathf.Pow(depth, 0.75f)) * (crown.Conifer ? Mathf.Lerp(0.78f, 1f, Mathf.Pow(rise, 0.6f)) : Mathf.Lerp(0.9f, 1f, rise));
                    var radial = r > 1e-3f ? new Vector3(p.x / r, 0, p.z / r) : Vector3.zero;
                    hull = (radial - Vector3.up * crown.SlopeAt(p.y)).normalized;
                    // The crown's top is a dome, not a point: lean to straight up over its last tenth.
                    hull = Vector3.Slerp(hull, Vector3.up, Mathf.Clamp01((rise - 0.9f) * 10f)).normalized;
                    if (hull.sqrMagnitude < 0.5f) hull = Vector3.up;
                }
                normals[i] = face;
                if (foliage[i])
                {
                    var outward = Vector3.Dot(face, hull) < 0 ? -face : face;
                    normals[i] = Vector3.Lerp(outward, hull, lean).normalized;
                }
                else
                    ao *= Mathf.Lerp(GroundContactAO, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.2f, GroundContactMetres, p.y)));
                uv3.Add(new Vector4(ao, face.y, face.x, face.z));
            }
            mesh.normals = normals;
            mesh.SetUVs(3, uv3);
        }
    }
}
