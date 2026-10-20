using MountainPlanner.Persistence;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.World
{
    /// <summary>
    /// Turns cached cliff shells (<see cref="CliffMeshData"/>) into meshes on the tiles (style tile). The
    /// vertex arrays are prepared on worker threads; <see cref="Create"/> runs on the main thread.
    /// </summary>
    public static class CliffShells
    {
        public sealed class Prepared
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] Weights;
            public int[] Indices;
        }

        /// <summary>Safe on worker threads.</summary>
        public static Prepared Prepare(CliffMeshData data)
        {
            if (data.Indices.Length == 0) return null;
            int n = data.VertexCount;
            var p = new Prepared { Vertices = new Vector3[n], Normals = new Vector3[n], Weights = new Vector2[n], Indices = data.Indices };
            for (int i = 0; i < n; i++)
            {
                p.Vertices[i] = new Vector3(data.Positions[i * 3], data.Positions[i * 3 + 1], data.Positions[i * 3 + 2]);
                p.Normals[i] = new Vector3(data.Normals[i * 3], data.Normals[i * 3 + 1], data.Normals[i * 3 + 2]);
                p.Weights[i] = new Vector2(data.Weights[i], 0);
            }
            return p;
        }

        /// <summary>A shell on the mountain, kept so terrain edits can hide it where the ground moved.</summary>
        public sealed class Placed
        {
            public Mesh Mesh;
            public Vector3 Origin;
            public Vector3[] Vertices;
            public int[] All;
            public bool Trimmed;
        }

        /// <summary><see cref="Create"/>, kept as a <see cref="Placed"/> shell.</summary>
        public static Placed Place(Transform parent, string name, Vector3 origin, Prepared shell, Material material)
        {
            var go = Create(parent, name, origin, shell, material);
            return new Placed { Mesh = go.GetComponent<MeshFilter>().sharedMesh, Origin = origin, Vertices = shell.Vertices, All = shell.Indices };
        }

        /// <summary>
        /// Hides the shell's triangles over ground a terrain edit moved (owner's report 2026-10-19: rock shells left hanging
        /// over graded trails): a triangle goes when any of its corners stands over <paramref name="moved"/> ground (local
        /// x, z). The shell comes back whole once nothing under it is moved (an undo). Display only; runs per changed tile.
        /// </summary>
        public static void Trim(Placed shell, System.Func<float, float, bool> moved)
        {
            var keep = new System.Collections.Generic.List<int>(shell.All.Length);
            var v = shell.Vertices;
            var o = shell.Origin;
            for (int t = 0; t + 2 < shell.All.Length; t += 3)
            {
                int a = shell.All[t], b = shell.All[t + 1], c = shell.All[t + 2];
                if (moved(o.x + v[a].x, o.z + v[a].z) || moved(o.x + v[b].x, o.z + v[b].z) || moved(o.x + v[c].x, o.z + v[c].z)) continue;
                keep.Add(a); keep.Add(b); keep.Add(c);
            }
            bool trimmed = keep.Count < shell.All.Length;
            if (!trimmed && !shell.Trimmed) return;
            shell.Mesh.SetIndices(trimmed ? keep.ToArray() : shell.All, MeshTopology.Triangles, 0, true);
            shell.Trimmed = trimmed;
        }

        /// <summary>A shell under <paramref name="parent"/>, its origin at the tile's south-west corner (y = 0).</summary>
        public static GameObject Create(Transform parent, string name, Vector3 origin, Prepared shell, Material material)
        {
            var mesh = new Mesh { name = name, indexFormat = shell.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(shell.Vertices);
            mesh.SetNormals(shell.Normals);
            mesh.SetUVs(0, shell.Weights);
            mesh.SetIndices(shell.Indices, MeshTopology.Triangles, 0, true);
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = origin;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            return go;
        }
    }
}
