using System;
using MountainPlanner.Presentation.Skiers;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Task 4.22: the bake's reference points (skiers.json "reference": LOD0 vertices skinned in Blender's Python, in a
    /// few clips and frames) skinned again here from the file. A wrong axis, bone order, row order or weight unpacking
    /// moves them, and the import (and its EditMode test) fails instead of shipping a broken figure.
    /// </summary>
    public static class SkierReference
    {
        public const float Tolerance = 0.001f;

        /// <summary>Returns how many points were checked; throws on the first that is off by more than 1 mm.</summary>
        public static int Check(SkierFigureData data, string json)
        {
            var root = JObject.Parse(json);
            int n = 0;
            foreach (var kindProp in ((JObject)root["kinds"]).Properties())
            {
                var kind = data.Find(kindProp.Name) ?? throw new InvalidOperationException($"no {kindProp.Name} in skiers.bytes");
                foreach (var p in (JArray)kindProp.Value["reference"])
                {
                    var clip = kind.Clips[kind.ClipIndex((string)p["clip"])];
                    int frame = clip.First + (int)p["frame"];
                    int vertex = (int)p["vertex"];
                    SkierFigureData.Skin(kind, 0, vertex, frame, out float x, out float y, out float z);
                    var want = (JArray)p["posed"];
                    float dx = x - (float)want[0], dy = y - (float)want[1], dz = z - (float)want[2];
                    float off = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (off > Tolerance)
                        throw new InvalidOperationException($"{kind.Name} {clip.Name} frame {(int)p["frame"]} vertex {vertex}: {off * 1000:F1} mm off Blender");
                    n++;
                }
            }
            if (n == 0) throw new InvalidOperationException("skiers.json has no reference points");
            return n;
        }
    }
}
