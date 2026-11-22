using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Task 4.26 BB22: reads a profiler log a Development player wrote with <c>-alloctrace</c> and lists every managed
    /// allocation by thread and call stack, the largest first. Batch mode:
    /// <c>-executeMethod MountainPlanner.Editor.AllocTrace.Report -traceraw &lt;file.raw&gt; -traceout &lt;report.txt&gt;</c>.
    /// </summary>
    public static class AllocTrace
    {
        const int StackDepth = 8;

        public static void Report()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            string raw = Arg("-traceraw"), output = Arg("-traceout") ?? "alloctrace.txt";
            try
            {
                File.WriteAllText(output, Build(raw));
                Debug.Log($"[AllocTrace] wrote {output}");
                EditorExit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorExit(1);
            }
        }

        static void EditorExit(int code)
        {
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(code);
        }

        sealed class Site { public string Thread, Stack; public long Bytes; public int Count; public HashSet<int> Frames = new HashSet<int>(); }

        static string Build(string raw)
        {
            if (raw == null || !File.Exists(raw)) throw new FileNotFoundException("-traceraw: no profiler log", raw);
            if (!ProfilerDriver.LoadProfile(raw, false)) throw new InvalidOperationException("the profiler couldn't read " + raw);
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            var sites = new Dictionary<string, Site>();
            var stack = new List<ulong>();
            int frames = 0, withAlloc = 0;
            long total = 0;
            for (int frame = first; frame <= last; frame++)
            {
                frames++;
                bool any = false;
                for (int thread = 0; ; thread++)
                {
                    using var view = ProfilerDriver.GetRawFrameDataView(frame, thread);
                    if (view == null || !view.valid) break;
                    int alloc = view.GetMarkerId("GC.Alloc");
                    if (alloc == FrameDataView.invalidMarkerId) continue;
                    for (int s = 0; s < view.sampleCount; s++)
                    {
                        if (view.GetSampleMarkerId(s) != alloc) continue;
                        long bytes = view.GetSampleMetadataCount(s) > 0 ? view.GetSampleMetadataAsLong(s, 0) : 0;
                        view.GetSampleCallstack(s, stack);
                        var names = new List<string>();
                        foreach (ulong address in stack)
                        {
                            var m = view.ResolveMethodInfo(address);
                            if (string.IsNullOrEmpty(m.methodName)) continue;
                            names.Add(m.sourceFileLine > 0 ? $"{m.methodName} ({Path.GetFileName(m.sourceFileName)}:{m.sourceFileLine})" : m.methodName);
                            if (names.Count == StackDepth) break;
                        }
                        string key = view.threadName + "\n" + string.Join("\n", names);
                        if (!sites.TryGetValue(key, out var site))
                            sites[key] = site = new Site { Thread = view.threadName, Stack = names.Count > 0 ? string.Join("\n      ", names) : "(no managed stack)" };
                        site.Bytes += bytes;
                        site.Count++;
                        site.Frames.Add(frame);
                        total += bytes;
                        any = true;
                    }
                }
                if (any) withAlloc++;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"{raw}: {frames} frames, {withAlloc} with an allocation, {total:N0} B in all ({(frames > 0 ? total / (double)frames : 0):F1} B a frame)");
            foreach (var s in sites.Values.OrderByDescending(s => s.Bytes))
            {
                sb.AppendLine();
                sb.AppendLine($"{s.Bytes:N0} B in {s.Count} allocations over {s.Frames.Count} frames, thread {s.Thread}:");
                sb.AppendLine("      " + s.Stack);
            }
            return sb.ToString();
        }
    }
}
