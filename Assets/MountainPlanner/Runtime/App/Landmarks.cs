using System.Collections.Generic;
using MountainPlanner.Domain.Geo;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Famous runs drawn on the demo mountain as a bright line draped over the terrain, so testers can
    /// find a known place. A stand-in until trail data arrives with the drawing tools; landmarks outside
    /// the opened mountain are skipped.
    ///
    /// Run lines © OpenStreetMap contributors (ODbL), fetched once from the Overpass API on 2026-09-27.
    /// </summary>
    public static class Landmarks
    {
        public sealed class Landmark
        {
            public string Name;
            public GeoPoint[] Line;
        }

        public static readonly Landmark[] Known =
        {
            new Landmark
            {
                Name = "Corbet's Couloir", // OSM way 882553696, top (under the tram) to bottom
                Line = new[]
                {
                    new GeoPoint(43.5959216, -110.8685970), new GeoPoint(43.5959306, -110.8685036),
                    new GeoPoint(43.5959379, -110.8684329), new GeoPoint(43.5959377, -110.8682788),
                    new GeoPoint(43.5959327, -110.8682355), new GeoPoint(43.5959475, -110.8681899),
                    new GeoPoint(43.5960036, -110.8681731), new GeoPoint(43.5961425, -110.8678599),
                    new GeoPoint(43.5963236, -110.8675373), new GeoPoint(43.5964064, -110.8672859),
                    new GeoPoint(43.5964616, -110.8670137), new GeoPoint(43.5965275, -110.8664592),
                },
            },
        };

        /// <summary>A landmark placed on the opened terrain: its draped line and the point to label.</summary>
        public sealed class Placed
        {
            public string Name;
            public LineRenderer Line;
            public Vector3 LabelAt;
            public Vector3 Centre;
        }

        const float Lift = 2f;      // metres above the ground, so the line isn't hidden by the terrain
        const float Spacing = 2f;   // drape sample spacing in metres

        public static List<Placed> Place(Transform parent, LocalFrame frame, ITerrainSurface surface, Material material)
        {
            var placed = new List<Placed>();
            foreach (var landmark in Known)
            {
                var points = new List<Vector3>();
                Vector2? previous = null;
                foreach (var geo in landmark.Line)
                {
                    var (x, z) = frame.ToLocal(Albers6350.Forward(geo));
                    var here = new Vector2((float)x, (float)z);
                    if (previous is Vector2 from)
                    {
                        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, here) / Spacing));
                        for (int i = 1; i <= steps; i++) Add(points, surface, Vector2.Lerp(from, here, i / (float)steps));
                    }
                    else Add(points, surface, here);
                    previous = here;
                }
                if (points.Count < 2 || points.Exists(p => float.IsNaN(p.y))) continue; // not on this mountain

                var go = new GameObject($"Landmark: {landmark.Name}");
                go.transform.SetParent(parent, false);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.sharedMaterial = material;
                line.positionCount = points.Count;
                line.SetPositions(points.ToArray());
                line.numCapVertices = 4;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                var centre = Vector3.zero;
                foreach (var p in points) centre += p;
                placed.Add(new Placed { Name = landmark.Name, Line = line, LabelAt = points[0] + Vector3.up * 12f, Centre = centre / points.Count });
            }
            return placed;
        }

        static void Add(List<Vector3> points, ITerrainSurface surface, Vector2 p) =>
            points.Add(new Vector3(p.x, surface.HeightAt(p.x, p.y) + Lift, p.y));
    }
}
