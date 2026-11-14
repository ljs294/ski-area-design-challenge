using System.Runtime.CompilerServices;
using NUnit.Framework;

// Task 0.4 (CI speed): fixtures run side by side; the tests inside one fixture still run one after another. The worker
// count comes from the runner (tools/domain-tests/Tests/ci.runsettings on CI); Unity's runner ignores this and runs in
// order. A fixture that writes process-wide state (Http.NetworkDisabled, OsmFeatures.ServerOverride), measures the
// whole process's memory, or holds a wall-clock limit on CI is [Parallelizable(ParallelScope.None)] (Unity's NUnit 3.5
// has no [NonParallelizable]): it runs with nothing beside it. A new fixture that does any of those needs the same.
[assembly: Parallelizable(ParallelScope.Fixtures)]

// The EditMode tests reuse these fixtures to check the worker's garbage under Unity's Mono, where some library calls
// allocate that .NET's do not (task 4.13).
[assembly: InternalsVisibleTo("MountainPlanner.Tests.EditMode")]
