using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Flora;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEditor;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Placement sizes krummholz from <see cref="Treeline.NativeHeights"/> (each form stays within 15% of its own
    /// size), so those constants must match the imported models.
    /// </summary>
    public sealed class KrummholzModelTests
    {
        [Test]
        public void KrummholzHeightsMatchTheImportedModels()
        {
            int model = SpeciesMap.IndexOf("krummholz");
            var set = AssetDatabase.LoadAssetAtPath<TreePrototypeSet>("Assets/MountainPlanner/Art/Trees/TreePrototypes.asset");
            if (model < 0 || set == null || set.NativeHeights.Length < (model + 1) * SpeciesMap.VariantsPerModel)
                Assert.Ignore("No krummholz model in the tree library yet.");
            for (int v = 0; v < SpeciesMap.VariantsPerModel; v++)
                Assert.That(set.NativeHeights[model * SpeciesMap.VariantsPerModel + v], Is.EqualTo(Treeline.NativeHeights[v]).Within(3).Percent,
                            $"krummholz variant {v}");
        }
    }
}
