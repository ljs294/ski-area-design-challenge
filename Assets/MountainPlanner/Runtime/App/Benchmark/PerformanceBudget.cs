using MountainPlanner.Presentation;

namespace MountainPlanner.App
{
    /// <summary>
    /// The frozen budgets a benchmark run is checked against (0.3 §8; 0.6 §2 exit criteria 4 and 5; T13, M1), per
    /// quality preset, at 1080p on the reference PC (RTX 3060 Ti):
    ///   High:   frame p95 ≤20 ms, p99 ≤33.3 ms, under 1% of frames over 50 ms, graphics memory ≤7 GB.
    ///   Medium: the minimum-spec stand-in (an RTX 2060 is about 55–60% as fast): p95 ≤18 ms, graphics memory ≤6 GB.
    ///   Low:    a smoke run: p95 ≤33.3 ms (30 FPS).
    ///   Ultra:  measured, not checked.
    /// Every checked preset also needs 0 bytes of garbage per frame while the camera moves.
    /// </summary>
    public readonly struct PerformanceBudget
    {
        public readonly bool Checked;
        public readonly float P95Ms, P99Ms, PercentOver50Ms, GfxMemoryMB;

        PerformanceBudget(bool isChecked, float p95, float p99, float over50, float gfxMB)
        {
            Checked = isChecked; P95Ms = p95; P99Ms = p99; PercentOver50Ms = over50; GfxMemoryMB = gfxMB;
        }

        public static PerformanceBudget For(QualityPreset preset) => preset switch
        {
            QualityPreset.High => new PerformanceBudget(true, 20f, 33.3f, 1f, 7 * 1024f),
            QualityPreset.Medium => new PerformanceBudget(true, 18f, 33.3f, 1f, 6 * 1024f),
            QualityPreset.Low => new PerformanceBudget(true, 33.3f, 50f, 1f, 6 * 1024f),
            _ => new PerformanceBudget(false, 0, 0, 0, 0),
        };
    }
}
