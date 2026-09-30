using System.Linq;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // The diorama base (A1, DioramaBase): walls follow the terrain's edge profile down to one flat base,
    // close at the corners, and face outward, with a plinth below.
    public sealed class DioramaBaseTests
    {
        static readonly Rect Ring = new Rect(-100, -60, 200, 120);
        static float Terrain(float x, float z) => 2000 + 30 * Mathf.Sin(x * 0.05f) + 20 * Mathf.Cos(z * 0.07f);
        const float BaseY = 1800;

        static Mesh Build() => DioramaBase.Build(Ring, Terrain, BaseY);

        [Test]
        public void WallTopsFollowTheProfileAndBottomsSitOnTheBase()
        {
            var mesh = Build();
            var v = mesh.vertices;
            var wall = mesh.GetIndices(0).Distinct().ToArray();
            int tops = 0, bottoms = 0;
            foreach (int i in wall)
            {
                if (Mathf.Approximately(v[i].y, BaseY)) { bottoms++; continue; }
                tops++;
                Assert.That(v[i].y, Is.EqualTo(Terrain(v[i].x, v[i].z) + DioramaBase.Lip).Within(1e-3f));
                bool onEdge = Mathf.Approximately(v[i].x, Ring.xMin) || Mathf.Approximately(v[i].x, Ring.xMax) ||
                              Mathf.Approximately(v[i].z, Ring.yMin) || Mathf.Approximately(v[i].z, Ring.yMax);
                Assert.That(onEdge, "every wall vertex stands on the cut");
            }
            Assert.That(tops, Is.EqualTo(bottoms));
            Assert.That(tops, Is.EqualTo(2 * (Mathf.CeilToInt(Ring.width / DioramaBase.Spacing) + Mathf.CeilToInt(Ring.height / DioramaBase.Spacing)) + 4),
                        "one column every 2 m, each side from corner to corner");
        }

        [Test]
        public void EveryFaceLooksOutward()
        {
            var mesh = Build();
            var v = mesh.vertices;
            var centre = new Vector3(Ring.center.x, 1900, Ring.center.y);
            for (int s = 0; s < 2; s++)
            {
                var t = mesh.GetIndices(s);
                for (int k = 0; k < t.Length; k += 3)
                {
                    Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
                    var normal = Vector3.Cross(b - a, c - a);     // Unity's front faces wind clockwise
                    if (normal.sqrMagnitude < 1e-8f) continue;
                    var mid = (a + b + c) / 3;
                    var outward = s == 0 ? new Vector3(mid.x - centre.x, 0, mid.z - centre.z) : mid - new Vector3(centre.x, BaseY - DioramaBase.PlinthHeight / 2, centre.z);
                    Assert.That(Vector3.Dot(normal, outward), Is.GreaterThan(0), $"submesh {s}, triangle {k / 3}");
                }
            }
        }

        [Test]
        public void CornersAreClosed()
        {
            var v = Build().vertices;
            foreach (var corner in new[] { new Vector2(Ring.xMin, Ring.yMin), new Vector2(Ring.xMax, Ring.yMin), new Vector2(Ring.xMax, Ring.yMax), new Vector2(Ring.xMin, Ring.yMax) })
            {
                var tops = v.Where(p => Mathf.Approximately(p.x, corner.x) && Mathf.Approximately(p.z, corner.y) && p.y > BaseY).ToArray();
                Assert.That(tops.Length, Is.EqualTo(2), "both walls meet at the corner");
                Assert.That(tops[0].y, Is.EqualTo(tops[1].y), "at the same height");
            }
        }

        [Test]
        public void MissingDataCarriesTheLastHeightOn()
        {
            var mesh = DioramaBase.Build(Ring, (x, z) => x > 0 ? float.NaN : 2000, BaseY);
            var v = mesh.vertices;
            var wall = mesh.GetIndices(0).Distinct().Select(i => v[i]).ToArray();
            Assert.That(wall.All(p => !float.IsNaN(p.y)));
            Assert.That(wall.Where(p => p.y > BaseY).All(p => Mathf.Approximately(p.y, 2000 + DioramaBase.Lip)),
                        "where the data runs out, the wall keeps the last real height");
        }
    }
}
