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
