using System.IO;
using MountainPlanner.Presentation.Skiers;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Task 4.22: brings the Blender skier and snowboarder (tools/assets/skiers/out/skiers.bytes, built by
    /// build-skiers.bat) into the game: checks the file reads and that its reference points (vertices skinned in Blender's
    /// Python) skin to the same places here, then copies it as it is into Resources, where <see cref="SkierFigures"/>
    /// loads it. No FBX, no Animator: the figures are skinned in their own shader from the baked matrices.
    /// </summary>
    public static class SkierImport
    {
        public const string Source = "tools/assets/skiers/out";
        public const string Target = "Assets/MountainPlanner/Art/Skiers/Resources/MountainPlannerSkiers/skiers.bytes";
        public const string Summary = "Assets/MountainPlanner/Art/Skiers/skiers.json";

        [MenuItem("Mountain Planner/Import Skiers")]
        public static void Import()
        {
            string bytesPath = Path.Combine(Source, "skiers.bytes");
            if (!File.Exists(bytesPath)) throw new FileNotFoundException($"{bytesPath} is missing: run tools/assets/skiers/build-skiers.bat first.");
            byte[] bytes = File.ReadAllBytes(bytesPath);
            var data = SkierFigureData.Read(bytes);
            string json = File.ReadAllText(Path.Combine(Source, "skiers.json"));
            int checkedPoints = SkierReference.Check(data, json);
            Directory.CreateDirectory(Path.GetDirectoryName(Target));
            File.WriteAllBytes(Target, bytes);
            // The summary (clips, triangle counts, the reference points) beside it, outside Resources: the EditMode test
            // checks the shipped file against it.
            File.WriteAllText(Summary, json);
            AssetDatabase.ImportAsset(Target, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(Summary, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[SkierImport] {bytes.Length / 1024} KB, {data.Kinds.Length} figures, {checkedPoints} reference points match Blender: {Target}");
        }
    }
}
