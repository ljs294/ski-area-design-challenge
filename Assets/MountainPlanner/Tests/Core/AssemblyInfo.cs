using System.Runtime.CompilerServices;

// The EditMode tests reuse these fixtures to check the worker's garbage under Unity's Mono, where some library calls
// allocate that .NET's do not (task 4.13).
[assembly: InternalsVisibleTo("MountainPlanner.Tests.EditMode")]
