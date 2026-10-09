using System;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Auto tree detail (F4; task P2-05): a short timing, the first time an area opens on this PC at this screen
    /// size, picks the tree LOD bias. It tries Ultra's bias first and steps down (3, 2, 1.5, 1): at each it lets
    /// <see cref="SettleFrames"/> frames pass, times <see cref="SampleFrames"/>, and keeps the first bias whose
    /// 95th-percentile frame is within <see cref="TargetMs"/>, 60% of the 20 ms budget (B3), because the opening
    /// view is lighter than the benchmark's forest legs. If none is, Low's 1.0. A fast PC is done after about a
    /// second, a slow one in about four.
    ///
    /// The timing is a plain state machine fed one frame time at a time (<see cref="Feed"/>), so it runs the same in
    /// tests; the viewer's runner sets <see cref="Bias"/> before each frame and holds V-Sync and the cap off.
    /// Nothing allocates after construction.
    /// </summary>
    public sealed class TreeDetailTiming
    {
        public static readonly float[] Candidates = { 3f, 2f, 1.5f, 1f };
        public const int SettleFrames = 10, SampleFrames = 45;
        public const float TargetMs = 12f;

        readonly float[] _samples = new float[SampleFrames];
        int _candidate, _frame;

        /// <summary>The 95th-percentile frame at each candidate tried so far, ms (NaN where not tried).</summary>
        public readonly float[] P95 = { float.NaN, float.NaN, float.NaN, float.NaN };

        /// <summary>The bias to render the next frame with.</summary>
        public float Bias => Candidates[Math.Min(_candidate, Candidates.Length - 1)];
        public bool Done { get; private set; }
        /// <summary>The bias the timing chose (valid once <see cref="Done"/>).</summary>
        public float Chosen { get; private set; }

        /// <summary>Takes the last frame's time, ms. Returns true when the timing has finished.</summary>
        public bool Feed(float frameMs)
        {
            if (Done) return true;
            if (_frame >= SettleFrames) _samples[_frame - SettleFrames] = frameMs;
            if (++_frame < SettleFrames + SampleFrames) return false;
            float p95 = Percentile95(_samples);
            P95[_candidate] = p95;
            _frame = 0;
            if (p95 <= TargetMs || _candidate == Candidates.Length - 1)
            {
                Chosen = p95 <= TargetMs ? Candidates[_candidate] : Candidates[Candidates.Length - 1];
                Done = true;
            }
            else _candidate++;
            return Done;
        }

        /// <summary>The 95th percentile of the samples (sorted in place).</summary>
        public static float Percentile95(float[] samples)
        {
            Array.Sort(samples);
            int i = (int)Math.Ceiling(samples.Length * 0.95) - 1;
            return samples[Math.Clamp(i, 0, samples.Length - 1)];
        }

        /// <summary>The tree detail a bias stands for (for the Settings row: "Auto (High)").</summary>
        public static TreeDetail DetailOf(float bias) =>
            bias >= 3f ? TreeDetail.Ultra : bias >= 2f ? TreeDetail.High : bias >= 1.5f ? TreeDetail.Medium : TreeDetail.Low;

        /// <summary>
        /// False while Windows reports a tiny desktop (640×480 while the owner's console session is disconnected and
        /// they're remote): frames are capped and the screen isn't the player's, so a timing then would be wrong.
        /// The timing waits for a real desktop instead.
        /// </summary>
        public static bool DisplayTrustworthy(int desktopWidth, int desktopHeight) => desktopWidth >= 1024 && desktopHeight >= 720;

        // ---------- the remembered result ----------

        const string StoreKey = "TreeDetailAuto";

        /// <summary>What a result is valid for: the graphics card and the screen size, since both change the timing.</summary>
        public static string Machine(string gpu, int width, int height) => $"{gpu}|{width}x{height}";

        /// <summary>The bias measured before on this machine, or 0 if none (or it was measured on another card or screen size).</summary>
        public static float Remembered(string machine)
        {
            string text = SettingsStore.GetString(StoreKey, "");
            int bar = text.LastIndexOf('|');
            if (bar <= 0 || text.Substring(0, bar) != machine) return 0f;
            return float.TryParse(text.Substring(bar + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b) ? b : 0f;
        }

        public static void Remember(string machine, float bias) =>
            SettingsStore.SetString(StoreKey, machine + "|" + bias.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>Forgets the result, so the next open measures again (Settings › Graphics › Measure again).</summary>
        public static void Forget() => SettingsStore.Delete(StoreKey);

        /// <summary>Set by the open view: starts a timing now (Settings › Graphics › Measure again); null with no area open.</summary>
        public static Action Requested;
    }
}
