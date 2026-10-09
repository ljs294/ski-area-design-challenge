using System.Globalization;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// The loading box's bar and Details list (task P2-03.1), read from a <see cref="LoadProgress"/>. Nothing is formatted
    /// unless something shown changed: tile counts and percentages come from tables built once, and a stage's final
    /// count and time are formatted once when it finishes.
    /// </summary>
    public sealed class LoadingViewModel
    {
        public sealed class Row
        {
            public LoadStage Stage;
            public string Name = "";
            public StageState State;
            /// <summary>False for a stage that doesn't apply (Preparing on a cached open, no cliffs, no forest).</summary>
            public bool Shown;
            public string Count = "";
            public string Time = "";
        }

        static readonly string[] Names =
        {
            "Reading the area", "Preparing terrain (first open only)", "Terrain", "Ground cover", "Snowpack", "Cliffs", "Forest",
        };

        public readonly Row[] Rows = new Row[LoadProgress.StageCount];
        public float Fraction { get; private set; }
        /// <summary>"64%".</summary>
        public string Percent { get; private set; } = PercentText(0);
        /// <summary>Seconds since opening began, "6.1 s", to the tenth.</summary>
        public string Elapsed { get; private set; } = "";
        /// <summary>Moves when a row's state, visibility, count or time changes (the list redraws its texts).</summary>
        public int RowsVersion { get; private set; }

        readonly string[][] _tileTexts = new string[LoadProgress.StageCount][];
        int _seenVersion = -1, _seenTenths = -1, _seenPercent = -1;
        LoadProgress _seen;

        public LoadingViewModel()
        {
            for (int i = 0; i < Rows.Length; i++) Rows[i] = new Row { Stage = (LoadStage)i, Name = Names[i] };
            Reset();
        }

        /// <summary>Before any viewer reports: reading the area.</summary>
        public void Reset()
        {
            _seen = null;
            _seenVersion = _seenTenths = _seenPercent = -1;
            Fraction = 0;
            Percent = PercentText(0);
            Elapsed = "";
            foreach (var r in Rows)
            {
                r.State = r.Stage == LoadStage.Package ? StageState.Current : StageState.Waiting;
                r.Shown = r.Stage != LoadStage.Preparing;
                r.Count = r.Time = "";
            }
            for (int i = 0; i < _tileTexts.Length; i++) _tileTexts[i] = null;
            RowsVersion++;
        }

        /// <summary>Takes the latest progress; true when anything shown changed. <paramref name="p"/> may be null (no viewer yet).</summary>
        public bool Refresh(LoadProgress p, double now)
        {
            if (p == null) return false;
            if (!ReferenceEquals(p, _seen))
            {
                Reset();
                _seen = p;
            }
            bool changed = false;
            if (p.Version != _seenVersion)
            {
                _seenVersion = p.Version;
                for (int i = 0; i < Rows.Length; i++) UpdateRow(Rows[i], p);
                RowsVersion++;
                changed = true;
            }
            Fraction = p.Fraction;
            int percent = (int)(Fraction * 100);
            if (percent != _seenPercent)
            {
                _seenPercent = percent;
                Percent = PercentText(percent);
                changed = true;
            }
            int tenths = (int)(p.TotalSeconds(now) * 10);
            if (tenths != _seenTenths)
            {
                _seenTenths = tenths;
                Elapsed = TenthsText(tenths);
                changed = true;
            }
            return changed;
        }

        void UpdateRow(Row r, LoadProgress p)
        {
            var state = p.State(r.Stage);
            bool wasDone = r.State == StageState.Done;   // a finished stage's count and time are formatted once
            r.Shown = state != LoadState.Skipped && (r.Stage != LoadStage.Preparing || state != LoadState.Waiting);
            r.State = state == LoadState.Done ? StageState.Done : state == LoadState.Current ? StageState.Current : StageState.Waiting;
            long done = p.Done(r.Stage), total = p.Total(r.Stage);
            switch (r.Stage)
            {
                case LoadStage.Preparing:
                case LoadStage.Terrain:
                case LoadStage.GroundCover:
                    r.Count = total > 0 && state != LoadState.Waiting ? TileText(r.Stage, done, total) : "";
                    break;
                case LoadStage.Cliffs:
                    r.Count = state == LoadState.Done ? (wasDone ? r.Count : done.ToString("N0", CultureInfo.InvariantCulture) + " triangles")
                            : state == LoadState.Current && total > 0 ? PercentText((int)(100 * done / total)) : "";
                    break;
                case LoadStage.Forest:
                    r.Count = state == LoadState.Done ? (wasDone ? r.Count : done.ToString("N0", CultureInfo.InvariantCulture) + " trees")
                            : state == LoadState.Current && total > 0 ? PercentText((int)(100 * done / total)) : "";
                    break;
                default:
                    r.Count = "";
                    break;
            }
            if (state == LoadState.Done) { if (!wasDone) r.Time = TenthsText((int)(p.Seconds(r.Stage, 0) * 10)); }
            else r.Time = "";
        }

        /// <summary>"37 of 121 tiles", made once per count per open.</summary>
        string TileText(LoadStage stage, long done, long total)
        {
            int i = (int)stage;
            if (total > 100000) return "";
            var table = _tileTexts[i];
            if (table == null || table.Length != total + 1) table = _tileTexts[i] = new string[total + 1];
            int n = (int)System.Math.Max(0, System.Math.Min(done, total));
            return table[n] ??= n.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + " tiles";
        }

        static readonly string[] Percents = new string[101];
        static readonly string[] Tenths = new string[1000];

        public static string PercentText(int percent)
        {
            percent = percent < 0 ? 0 : percent > 100 ? 100 : percent;
            return Percents[percent] ??= percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>"6.1 s" for 61 tenths; past 99.9 s, whole seconds.</summary>
        public static string TenthsText(int tenths)
        {
            if (tenths < 0) tenths = 0;
            if (tenths >= Tenths.Length) return (tenths / 10).ToString(CultureInfo.InvariantCulture) + " s";
            return Tenths[tenths] ??= (tenths / 10).ToString(CultureInfo.InvariantCulture) + "." + (tenths % 10).ToString(CultureInfo.InvariantCulture) + " s";
        }
    }
}
