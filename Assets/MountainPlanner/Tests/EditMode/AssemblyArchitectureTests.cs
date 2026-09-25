using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Guards the dependency direction Simulation -> World -> Presentation -> UI, and keeps the
    // deterministic Simulation core free of engine references (see AGENTS.md "Assemblies").
    public sealed class AssemblyArchitectureTests
    {
        static readonly string[] LayerOrder =
        {
            "MountainPlanner.Simulation",
            "MountainPlanner.World",
            "MountainPlanner.Presentation",
            "MountainPlanner.UI",
        };

        static Assembly Load(string name)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            Assert.That(assembly, Is.Not.Null, $"{name} is not loaded; check its .asmdef compiles.");
            return assembly;
        }

        [Test]
        public void AllRuntimeAssembliesAreLoaded()
        {
            foreach (var name in LayerOrder) Load(name);
        }

        [Test]
        public void SimulationHasNoEngineReferences()
        {
            var engineReferences = Load("MountainPlanner.Simulation").GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name.StartsWith("UnityEngine", StringComparison.Ordinal)
                    || name.StartsWith("UnityEditor", StringComparison.Ordinal)
                    || name.StartsWith("Unity.", StringComparison.Ordinal))
                .ToArray();

            Assert.That(engineReferences, Is.Empty);
        }

        [Test]
        public void RuntimeAssembliesOnlyReferenceEarlierLayers()
        {
            for (var layer = 0; layer < LayerOrder.Length; layer++)
            {
                var laterLayers = LayerOrder.Skip(layer + 1).ToArray();
                var backwardReferences = Load(LayerOrder[layer]).GetReferencedAssemblies()
                    .Select(reference => reference.Name)
                    .Where(laterLayers.Contains)
                    .ToArray();

                Assert.That(backwardReferences, Is.Empty, $"{LayerOrder[layer]} references a later layer.");
            }
        }
    }
}
