using System.Diagnostics;
using System.Threading.Tasks;
using MountainPlanner.Domain.Cover;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.28: the ground's moisture map (<see cref="GroundMoisture"/>) for the opened mountain, which the grass draws
    /// from. Built on a worker once the 8 m heights and the water bodies are in, and again after earthwork or a pond changes
    /// them (once the edit has settled, never while a brush is still moving), so the grass follows what the player builds.
    /// Nothing is saved and the simulation never reads it.
    /// </summary>
    public sealed partial class MountainViewer
    {
        Task<byte[]> _moistureTask;
        OpenedResort _moistureFor;
        ulong _moistureWater;
        int _moistureEdits, _moistureTaskEdits;
        ulong _moistureTaskWater;
        bool _moistureBuilt;
        float _moistureDue;
        Stopwatch _moistureClock;
        /// <summary>How long the heights and the water must stay unchanged before the map follows them, seconds.</summary>
        const float MoistureSettleSeconds = 0.75f;

        /// <summary>The map is on the mountain as it stands (captures wait for this).</summary>
        bool MoistureReady => _moistureBuilt && _moistureTask == null && _moistureFor == _resort;

        void UpdateMoisture()
        {
            var r = _resort;
            if (r?.States == null || r.SnowMm == null || r.Root == null) return;
            if (_moistureFor != r)
            {
                _moistureFor = r;
                _moistureTask = null;   // another mountain's map: let it finish unseen
                _moistureBuilt = false;
                _moistureDue = 0;
            }
            int edits = _edits?.Pipeline?.SnowInputsVersion ?? 0;
            ulong water = r.WaterBodies?.Hash ?? 0;
            if (_moistureTask != null)
            {
                if (!_moistureTask.IsCompleted) return;
                var done = _moistureTask;
                _moistureTask = null;
                if (done.Status == TaskStatus.RanToCompletion && r.States.SetMoisture(done.Result))
                {
                    if (!_moistureBuilt) UnityEngine.Debug.Log($"[MountainViewer] ground moisture: {r.States.Snow.Width}×{r.States.Snow.Height} cells in {_moistureClock.Elapsed.TotalMilliseconds:F0} ms");
                    _moistureBuilt = true;
                    _moistureEdits = _moistureTaskEdits;
                    _moistureWater = _moistureTaskWater;
                }
                else if (done.IsFaulted) UnityEngine.Debug.LogException(done.Exception);
                return;
            }
            bool stale = !_moistureBuilt || edits != _moistureEdits || water != _moistureWater;
            if (!stale) { _moistureDue = 0; return; }
            if (_moistureBuilt)
            {
                // Wait for the edit to settle: the brush has let go and the snow's inputs hold every edit.
                if (_edits?.Pipeline != null && !_edits.Pipeline.SnowSettled) { _moistureDue = 0; return; }
                if (_moistureDue == 0) { _moistureDue = Time.unscaledTime + MoistureSettleSeconds; return; }
                if (Time.unscaledTime < _moistureDue) return;
            }
            var site = r.Manifest.Site;
            var sun = GroundMoisture.SunSide(site.Latitude, r.Manifest.Crs.GridConvergenceDegrees);
            _moistureTaskEdits = edits;
            _moistureTaskWater = water;
            _moistureDue = 0;
            _moistureClock = Stopwatch.StartNew();
            _moistureTask = StartMoisture(r.States.Snow.Width, r.States.Snow.Height, r.SnowMm, r.WaterBodies?.Mask, sun.X, sun.Y);
        }

        /// <summary>The build on a worker, from copies (earthwork keeps writing the resort's heights). Static, so the per-frame
        /// caller holds no closure (BB22).</summary>
        static Task<byte[]> StartMoisture(int width, int depth, int[] mm, byte[] water, int sunX, int sunY)
        {
            if (mm.Length != width * depth || water != null && water.Length != width * depth) return Task.FromResult<byte[]>(null);
            var heights = (int[])mm.Clone();
            var mask = (byte[])water?.Clone();
            return Task.Run(() => GroundMoisture.Build(width, depth, heights, mask, sunX, sunY));
        }
    }
}
