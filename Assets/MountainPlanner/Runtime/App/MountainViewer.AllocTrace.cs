using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.26 BB22: <c>-alloctrace &lt;file&gt;</c> with <c>-benchmark</c> in a Development player records the first
    /// <see cref="AllocTraceFrames"/> measured frames to a profiler log with allocation call stacks, so an allocation the
    /// garbage check finds can be traced to its code (Mountain Planner's AllocTrace.Report reads the log in the editor). The
    /// profiler costs time and memory of its own, so a run with it is for finding garbage, not for its numbers.
    /// </summary>
    public sealed partial class MountainViewer
    {
        const int AllocTraceFrames = 300;

        static string AllocTraceFile(string[] args)
        {
            int i = Array.IndexOf(args, "-alloctrace");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Called each measured frame (0 first): starts the recording at frame 0 and stops it after the last.</summary>
        static void AllocTraceFrame(string file, int frame)
        {
            if (file == null || !Debug.isDebugBuild) return;
            if (frame == 0)
            {
                Profiler.maxUsedMemory = 256 * 1024 * 1024;
                Profiler.logFile = file;
                Profiler.enableBinaryLog = true;
                Profiler.enableAllocationCallstacks = true;
                Profiler.enabled = true;
                Debug.Log($"[AllocTrace] recording {AllocTraceFrames} frames with allocation call stacks to {file}.raw");
            }
            else if (frame == AllocTraceFrames)
            {
                Profiler.enabled = false;
                Profiler.enableBinaryLog = false;
                Profiler.logFile = "";
                Debug.Log("[AllocTrace] recording stopped");
            }
        }
    }
}
