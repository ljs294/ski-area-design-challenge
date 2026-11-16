using System;
using System.Collections.Generic;
using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// A job the snow crew runs in phases: each phase is <see cref="Units"/> independent units (bands of tile rows) that may
    /// run on any thread in any order, because no unit reads what another writes; between phases the thread that finished
    /// the last unit runs <see cref="PhaseDone"/> alone (the drift's scatter), then starts the next phase or signals done.
    /// So the bytes never depend on the thread count or the order (BG-02 v4).
    /// </summary>
    internal abstract class SnowJob
    {
        /// <summary>Units in the current phase, and the most threads that may work it at once.</summary>
        internal int Units, Threads = 1;
        internal int Next, Active, Finished;
        internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(true);
        internal Exception Error;

        internal abstract void RunUnit(int unit);
        /// <summary>The phase's units are all done: the sequential step, then <see cref="SnowCrew.Run"/> again or return true when
        /// the job is finished.</summary>
        internal abstract bool PhaseDone();
    }

    /// <summary>
    /// BG-02 v4 (task 4.07 PR A2): the snow's own threads, shared by every world in the process (the game has one; tests run
    /// several side by side). Raw threads with a lock: no thread pool, no Parallel (the repo check's unordered parallelism),
    /// and nothing allocated per hour. A thread that has had no work for ten minutes exits; the next job starts it again.
    /// </summary>
    internal static class SnowCrew
    {
        const int MaxThreads = 16, IdleMs = 600_000;
        static readonly object Gate = new object();
        static readonly List<SnowJob> Jobs = new List<SnowJob>(64);
        static int _threads;

        /// <summary>Starts the job's current phase (Units, Threads set; Next, Active, Finished are reset here).</summary>
        internal static void Run(SnowJob job)
        {
            lock (Gate)
            {
                job.Next = 0; job.Active = 0; job.Finished = 0;
                if (job.Units <= 0) { /* an empty phase finishes at once, below */ }
                else
                {
                    Jobs.Add(job);
                    int want = Math.Min(MaxThreads, Math.Max(1, job.Threads));
                    while (_threads < want)
                    {
                        _threads++;
                        new Thread(Loop) { IsBackground = true, Name = "Snow " + _threads }.Start();
                    }
                    Monitor.PulseAll(Gate);
                    return;
                }
            }
            Finish(job);
        }

        /// <summary>Starts a job: its Done is reset and set again when the last phase ends.</summary>
        internal static void Start(SnowJob job)
        {
            job.Error = null;
            job.Done.Reset();
            Run(job);
        }

        static void Finish(SnowJob job)
        {
            bool over;
            try { over = job.PhaseDone(); }
            catch (Exception e) { job.Error = e; over = true; }
            if (over) job.Done.Set();
        }

        static void Loop()
        {
            while (true)
            {
                SnowJob job = null;
                int unit = -1;
                lock (Gate)
                {
                    while (job == null)
                    {
                        for (int k = 0; k < Jobs.Count; k++)
                        {
                            var j = Jobs[k];
                            if (j.Next < j.Units && j.Active < j.Threads) { job = j; unit = j.Next++; j.Active++; break; }
                        }
                        if (job != null) break;
                        if (!Monitor.Wait(Gate, IdleMs) && !AnyWork()) { _threads--; return; }
                    }
                }
                try { if (job.Error == null) job.RunUnit(unit); }
                catch (Exception e) { job.Error = e; }
                bool last;
                lock (Gate)
                {
                    job.Active--;
                    last = ++job.Finished == job.Units;
                    if (last) Jobs.Remove(job);
                    else Monitor.PulseAll(Gate);   // a slot under the job's thread cap is free again
                }
                if (last)
                {
                    if (job.Error != null) job.Done.Set();
                    else Finish(job);
                }
            }
        }

        static bool AnyWork()
        {
            for (int k = 0; k < Jobs.Count; k++) if (Jobs[k].Next < Jobs[k].Units) return true;
            return false;
        }
    }
}
