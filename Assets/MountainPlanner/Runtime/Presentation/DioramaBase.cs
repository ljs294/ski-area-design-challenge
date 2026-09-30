using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The diorama base (A1, 0.5 §2): where the downloaded data ends the terrain is cut cleanly
    /// (MountainTerrain.shader) and side walls of rock strata run down to a thin plinth, like a model
    /// railway's section-cut base. Built once per opened mountain from the terrain's own edge profile and
    /// drawn with DioramaWall.shader: submesh 0 the walls, submesh 1 the plinth.
    /// </summary>
    public static class DioramaBase
    {
        /// <summary>Metres between edge-profile samples: the ring heightmap's own spacing.</summary>
        public const float Spacing = 2f;
        /// <summary>
        /// The wall stands this far above the profile, capped in snow: distant terrain patches drop detail and
        /// would otherwise open a gap along the cut. The wall faces outward only, so the lip is invisible from inside.
        /// </summary>
        public const float Lip = 1.5f;
        /// <summary>The walls go down to one flat base this far below the package's lowest point.</summary>
        public const float BaseDepth = 120f;
        /// <summary>The plinth: a slab under the walls, reaching out past them as a ledge.</summary>
        public const float PlinthHeight = 60f, PlinthLedge = 40f;

        /// <summary>
        /// Builds the walls around <paramref name="ring"/> (local x/z) down to <paramref name="baseY"/>, and the
        /// plinth below. <paramref name="heightAt"/> gives the terrain height (NaN where there is none). UV0 on
        /// the walls: x metres around the perimeter, y metres below the top of the wall.
        /// </summary>
        public static Mesh Build(Rect ring, Func<float, float, float> heightAt, float baseY)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var walls = new List<int>();
            float perimeter = 0;
            float last = float.NaN;   // the last real height, carried on (across corners too) where there is no data
            // Each side runs left to right as seen from outside, so every quad winds the same way (Unity's
            // front faces are clockwise) and faces outward.
            var sides = new (Vector2 From, Vector2 To, Vector3 Out)[]
            {
                (new Vector2(ring.xMin, ring.yMin), new Vector2(ring.xMax, ring.yMin), Vector3.back),      // south
                (new Vector2(ring.xMax, ring.yMin), new Vector2(ring.xMax, ring.yMax), Vector3.right),     // east
                (new Vector2(ring.xMax, ring.yMax), new Vector2(ring.xMin, ring.yMax), Vector3.forward),   // north
                (new Vector2(ring.xMin, ring.yMax), new Vector2(ring.xMin, ring.yMin), Vector3.left),      // west
            };
            foreach (var (from, to, outward) in sides)
            {
                float length = Vector2.Distance(from, to);
                int segments = Mathf.Max(1, Mathf.CeilToInt(length / Spacing));
                int first = positions.Count;
                for (int k = 0; k <= segments; k++)
                {
                    var p = Vector2.Lerp(from, to, (float)k / segments);
                    float h = heightAt(p.x, p.y);
                    if (float.IsNaN(h)) h = float.IsNaN(last) ? baseY : last;   // no data: carry the last height on
                    last = h;
                    float top = Mathf.Max(h + Lip, baseY);
                    float u = perimeter + length * k / segments;
                    positions.Add(new Vector3(p.x, top, p.y));
                    positions.Add(new Vector3(p.x, baseY, p.y));
                    normals.Add(outward);
                    normals.Add(outward);
                    uvs.Add(new Vector2(u, 0));
                    uvs.Add(new Vector2(u, top - baseY));
                }
                perimeter += length;
                for (int k = 0; k < segments; k++)
                {
                    int t0 = first + 2 * k, b0 = t0 + 1, t1 = t0 + 2, b1 = t0 + 3;
                    walls.AddRange(new[] { t0, t1, b1, t0, b1, b0 });
                }
            }

            // The plinth: a box under the walls, a ledge wider on every side.
            var plinth = new List<int>();
            float x0 = ring.xMin - PlinthLedge, x1 = ring.xMax + PlinthLedge, z0 = ring.yMin - PlinthLedge, z1 = ring.yMax + PlinthLedge;
            float y1 = baseY, y0 = baseY - PlinthHeight;
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)   // a b c d clockwise seen from outside
            {
                int i = positions.Count;
                positions.AddRange(new[] { a, b, c, d });
                normals.AddRange(new[] { n, n, n, n });
                uvs.AddRange(new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero });
                plinth.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            Face(new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Vector3.up);
            Face(new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), Vector3.down);
            Face(new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), Vector3.back);
            Face(new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), Vector3.right);
            Face(new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), Vector3.forward);
            Face(new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), Vector3.left);

            var mesh = new Mesh { name = "DioramaBase", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(walls, 0);
            mesh.SetTriangles(plinth, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds the base and adds it under <paramref name="parent"/>. Returns the wall and plinth materials
        /// (copies of <paramref name="material"/>), so snow can be switched on the walls' top.
        /// </summary>
        public static (GameObject Base, Material Walls, Material Plinth) Create(Transform parent, Rect ring, Func<float, float, float> heightAt,
                                                                                 float baseY, Material material)
        {
            var go = new GameObject("Diorama base");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Build(ring, heightAt, baseY);
            var walls = new Material(material) { name = "Diorama walls" };
            var plinth = new Material(material) { name = "Diorama plinth" };
            plinth.SetFloat("_Plinth", 1);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { walls, plinth };
            renderer.shadowCastingMode = ShadowCastingMode.Off;   // outside the shadow distance anyway
            return (go, walls, plinth);
        }
    }
}
