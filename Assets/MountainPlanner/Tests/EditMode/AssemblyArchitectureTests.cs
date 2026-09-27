using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Guards the assembly table in docs/plans/phase0-0.3-technical-architecture.md §2 (T1): references
    // only point down the table, each assembly uses only the assemblies it is allowed to, and the
    // engine-free core never touches Unity (see AGENTS.md "Assemblies").
    public sealed class AssemblyArchitectureTests
    {
        static readonly string[] LayerOrder =
        {
            "MountainPlanner.Domain",
            "MountainPlanner.Simulation",
            "MountainPlanner.Persistence",
            "MountainPlanner.Acquisition",
            "MountainPlanner.World",
            "MountainPlanner.Presentation",
            "MountainPlanner.UI",
            "MountainPlanner.App",
        };

        static readonly string[] EngineFree =
        {
            "MountainPlanner.Domain",
            "MountainPlanner.Simulation",
            "MountainPlanner.Persistence",
            "MountainPlanner.Acquisition",
            "MountainPlanner.Tests.Core",
        };

        // Which project assemblies each layer may use (0.3 §2).
        static readonly Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>
        {
            ["MountainPlanner.Domain"] = Array.Empty<string>(),
            ["MountainPlanner.Simulation"] = new[] { "Domain" },
            ["MountainPlanner.Persistence"] = new[] { "Domain" },
            ["MountainPlanner.Acquisition"] = new[] { "Domain", "Persistence" },
            ["MountainPlanner.World"] = new[] { "Domain", "Simulation", "Persistence" },
            ["MountainPlanner.Presentation"] = new[] { "Domain", "Simulation", "Persistence", "World" },
            ["MountainPlanner.UI"] = new[] { "Domain", "Simulation", "Persistence", "World", "Presentation" },
            ["MountainPlanner.App"] = new[] { "Domain", "Simulation", "Persistence", "Acquisition", "World", "Presentation", "UI" },
        };

        static Assembly Load(string name)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            Assert.That(assembly, Is.Not.Null, $"{name} is not loaded; check its .asmdef compiles.");
            return assembly;
        }

        static string[] ProjectReferences(string name) => Load(name).GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(reference => reference.StartsWith("MountainPlanner.", StringComparison.Ordinal))
            .ToArray();

        [Test]
        public void AllRuntimeAssembliesAreLoaded()
        {
            foreach (var name in LayerOrder) Load(name);
        }

        [Test]
        public void EveryLayerHasAnAllowList()
        {
            Assert.That(Allowed.Keys, Is.EquivalentTo(LayerOrder));
        }

        [TestCaseSource(nameof(EngineFree))]
        public void EngineFreeAssembliesHaveNoEngineReferences(string name)
        {
            var engineReferences = Load(name).GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(reference => reference.StartsWith("UnityEngine", StringComparison.Ordinal)
                    || reference.StartsWith("UnityEditor", StringComparison.Ordinal)
                    || reference.StartsWith("Unity.", StringComparison.Ordinal))
                .ToArray();

            Assert.That(engineReferences, Is.Empty);
        }

        [Test]
        public void RuntimeAssembliesOnlyReferenceEarlierLayers()
        {
            for (var layer = 0; layer < LayerOrder.Length; layer++)
            {
                var laterLayers = LayerOrder.Skip(layer + 1).ToArray();
                var backwardReferences = ProjectReferences(LayerOrder[layer]).Where(laterLayers.Contains).ToArray();

                Assert.That(backwardReferences, Is.Empty, $"{LayerOrder[layer]} references a later layer.");
            }
        }

        [TestCaseSource(nameof(LayerOrder))]
        public void RuntimeAssembliesUseOnlyAllowedLayers(string name)
        {
            var allowed = Allowed[name].Select(layer => "MountainPlanner." + layer).ToArray();
            var disallowed = ProjectReferences(name).Where(reference => !allowed.Contains(reference)).ToArray();

            Assert.That(disallowed, Is.Empty, $"{name} uses assemblies outside its allow-list.");
        }
    }
}
