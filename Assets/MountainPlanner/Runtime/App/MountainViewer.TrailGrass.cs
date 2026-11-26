using System.Threading.Tasks;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.29 PR B: each trail's grass on the ground (SN-35). The trail layout last sent to the worker says which trail
    /// each 8 m cell draws as (<see cref="TrailCellOwners"/>, built off the main thread); the snapshot's
    /// <see cref="SimSnapshot.TrailGrass"/> gives each trail's height, copied into <see cref="SurfaceStates"/> when the trail
    /// snow's stamp moves (an hour's board, a design, a reopened save), so the table uploads only when a height changed.
    /// </summary>
    public sealed partial class MountainViewer
    {
        TrailSnowLayout _grassLayout;
        Task<ushort[]> _grassOwnersJob;
        TrailSnowLayout _grassOwnersFor;
        SurfaceStates _grassBoundOn;
        bool _grassBound;
        long _grassStamp = -1;
        byte[] _grassScratch = System.Array.Empty<byte>();
        /// <summary>A capture's grass, % per layout trail in turn (<c>-trailgrass 10,50,100</c>: the first trail short, the
        /// next long, the next brush, and round again), so a picture shows the three side by side. The look only; the
        /// simulation never hears of it.</summary>
        byte[] _grassForced;

        void ReadTrailGrassArgs(string[] args)
        {
            int i = System.Array.IndexOf(args, "-trailgrass");
            if (i < 0 || i + 1 >= args.Length) return;
            var v = new System.Collections.Generic.List<byte>();
            foreach (var part in args[i + 1].Split(',')) if (int.TryParse(part, out int pct) && pct >= 0 && pct <= 100) v.Add((byte)pct);
            if (v.Count == 0) return;
            _grassForced = v.ToArray();
            Debug.Log($"[TrailGrass] -trailgrass: the trails draw {string.Join(", ", v)} % in turn (the look only)");
        }

        /// <summary>A capture's wait: no built trails, or their grass is on the ground (the map bound and a snapshot's heights in).</summary>
        bool TrailGrassReady
        {
            get
            {
                var doc = Design?.Document;
                bool anyBuilt = false;
                if (doc != null) foreach (var t in doc.Trails) if (t.Stage == Domain.Design.WorkStage.Built) { anyBuilt = true; break; }
                return !anyBuilt || _grassBound && _grassStamp >= 0;
            }
        }

        void ResetTrailGrassLook()
        {
            _grassLayout = null;
            _grassOwnersJob = null;
            _grassOwnersFor = null;
            _grassBound = false;
            _grassStamp = -1;
        }

        /// <summary>The layout just sent: its cells' owners are worked out on a worker, then bound.</summary>
        void ShowTrailGrassLayout(TrailSnowLayout layout)
        {
            if (layout == null) return;
            _grassLayout = layout;
            _grassOwnersFor = layout;
            _grassOwnersJob = BuildGrassOwners(layout);
        }

        static Task<ushort[]> BuildGrassOwners(TrailSnowLayout layout) => Task.Run(() => TrailCellOwners.Build(layout));

        /// <summary>Per frame, from the newest snapshot.</summary>
        void ShowTrailGrass(SimSnapshot s)
        {
            var states = _resort?.States;
            if (states == null) return;
            if (_grassBoundOn != states) { _grassBoundOn = states; _grassBound = false; _grassStamp = -1; if (_grassLayout != null && _grassOwnersJob == null) ShowTrailGrassLayout(_grassLayout); }
            if (_grassOwnersJob != null)
            {
                if (!_grassOwnersJob.IsCompleted) return;
                var job = _grassOwnersJob;
                _grassOwnersJob = null;
                if (job.Status != TaskStatus.RanToCompletion || _grassOwnersFor != _grassLayout)
                {
                    if (job.IsFaulted) Debug.LogWarning($"[TrailGrass] the trails' cells failed: {job.Exception?.GetBaseException().Message}");
                    return;
                }
                _grassBound = states.SetTrailGrassMap(_grassLayout, job.Result);
                _grassStamp = -1;
            }
            if (!_grassBound || s == null || s.TrailSnowStamp == _grassStamp) return;
            _grassStamp = s.TrailSnowStamp;
            int n = _grassLayout.Trails;
            if (_grassScratch.Length < n) _grassScratch = new byte[n];
            for (int t = 0; t < n; t++)
            {
                ulong id = _grassLayout.TrailId[t];
                int k = s.IndexOfTrail(id);
                byte g = k >= 0 ? s.TrailGrass[k] : SimSnapshot.NoGrass;
                if (_grassForced != null) g = _grassForced[t % _grassForced.Length];
                _grassScratch[t] = g > 100 ? SurfaceStates.TrailGrassUnknown : g;
            }
            states.SetTrailGrass(_grassScratch, n);
        }
    }
}
