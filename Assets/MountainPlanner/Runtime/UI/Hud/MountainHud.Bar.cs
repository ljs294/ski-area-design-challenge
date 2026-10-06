using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The status bar (task P2-02, the mockup's .bar): Toolbox and Analysis, the resort's name, pause, the day and date,
    /// the clock, the four speeds, then (greyed until the simulation, Phase 4) the bank balance, the open lifts and
    /// trails and the guests, the elevation under the pointer with the info layer's reading, and Save.
    /// </summary>
    public sealed partial class MountainHud
    {
        VisualElement _bar, _readout, _readoutSwatch;
        Label _day, _date, _clockHm, _clockS, _clockAmPm, _elevation, _readoutLead, _readoutFigure, _readoutTail;
        Button _pause, _when;
        HudIcon _pauseIcon;
        readonly Button[] _speeds = new Button[ViewClockRunner.MaxSpeed];
        int _shownSecond = -1, _shownSeasonDay = -1, _shownSpeed = -1, _shownElevation = int.MinValue;
        int _shownPaused = -1;
        long _shownInfo = long.MinValue;
        UnitSystem _shownUnits = (UnitSystem)(-1);

        /// <summary>
        /// "10,029 ft" for every elevation the mountain has, per unit system, made once when it opens
        /// (<see cref="PrepareElevations"/>): the readout changes almost every refresh while the camera moves.
        /// </summary>
        readonly string[][] _elevationText = new string[2][];
        readonly int[] _elevationFirstKey = new int[2];
        readonly string[] _noElevation = { "– – – m", "– – – ft" };
        /// <summary>The info readout's figures ("47%", "SE · 135°", "2 ft 4 in"), made the first time each shows and kept.</summary>
        readonly System.Collections.Generic.Dictionary<long, string> _infoText = new System.Collections.Generic.Dictionary<long, string>();

        void WireBar()
        {
            _bar = _root.Q("bar");
            _day = _root.Q<Label>("bar-day");
            _date = _root.Q<Label>("bar-date");
            _clockHm = _root.Q<Label>("bar-clock-hm");
            _clockS = _root.Q<Label>("bar-clock-s");
            _clockAmPm = _root.Q<Label>("bar-clock-ampm");
            _elevation = _root.Q<Label>("bar-elev-value");
            _readout = _root.Q("bar-readout");
            _readoutSwatch = _root.Q("bar-readout-swatch");
            _readoutLead = _root.Q<Label>("bar-readout-lead");
            _readoutFigure = _root.Q<Label>("bar-readout-figure");
            _readoutTail = _root.Q<Label>("bar-readout-tail");
            _pause = _root.Q<Button>("bar-pause");
            _pauseIcon = _root.Q<HudIcon>("bar-pause-icon");
            _when = _root.Q<Button>("bar-when");
            _bar.RegisterCallback<GeometryChangedEvent>(_ => FitBar());
            _pause.clicked += () => PauseChosen?.Invoke();
            for (int i = 0; i < _speeds.Length; i++)
            {
                int speed = i + 1;
                _speeds[i] = _root.Q<Button>("bar-speed-" + speed);
                _speeds[i].clicked += () => SpeedChosen?.Invoke(speed);
            }
            _root.Q<Button>("bar-toolbox").clicked += ToggleToolbox;
            _root.Q<Button>("bar-analysis").clicked += ToggleAnalysis;
            _root.Q<Button>("bar-resort").clicked += () => SetStats(!_statsOpen);
            _when.clicked += () => OpenAnalysisTab("weather");
        }

        bool _compact;
        float _fullBar;

        /// <summary>The bar goes compact when its cells don't fit the screen's width, and back when they would.</summary>
        void FitBar()
        {
            float sum = 0;
            foreach (var cell in _bar.Children())
                if (!cell.ClassListContains("grow") && !cell.ClassListContains("hud-plate")) sum += cell.layout.width;   // the see-through plate spans the bar
            float room = _bar.contentRect.width;
            if (float.IsNaN(sum) || room <= 0) return;
            if (!_compact)
            {
                _fullBar = sum;
                if (sum > room + 0.5f) SetCompact(true);
            }
            else if (_fullBar <= room) SetCompact(false);
        }

        void SetCompact(bool compact)
        {
            _compact = compact;
            _bar.EnableInClassList("bar--compact", compact);
        }

        /// <summary>The view time, and the clock's speed (1–4) and whether it's paused. Changes only what the bar shows differently.</summary>
        public void SetClock(ViewTime now, int speed, bool paused)
        {
            if (now.SecondOfDay != _shownSecond)
            {
                int was = _shownSecond;
                _shownSecond = now.SecondOfDay;
                if (was < 0 || was / 60 != now.SecondOfDay / 60) _clockHm.text = HudText.HoursMinutes(now.SecondOfDay);
                _clockS.text = HudText.Seconds(now.SecondOfDay);
                _clockAmPm.text = HudText.AmPm(now.SecondOfDay);
            }
            int seasonDay = HudText.SeasonDay(now.Year, now.DayOfYear);
            if (seasonDay != _shownSeasonDay)
            {
                _shownSeasonDay = seasonDay;
                _day.text = HudText.Day(seasonDay);
                _date.text = HudText.Date(now.Year, now.DayOfYear);
            }
            int p = paused ? 1 : 0;
            if (speed == _shownSpeed && p == _shownPaused) return;
            _shownSpeed = speed;
            _shownPaused = p;
            for (int i = 0; i < _speeds.Length; i++) _speeds[i].EnableInClassList("spd__b--on", speed > i);
            _bar.EnableInClassList("bar--paused", paused);
            _pauseIcon.Icon = paused ? "s-play" : "s-pause";
            _pause.tooltip = paused ? "Play (Space)" : "Pause (Space)";
            RetintSoon();
        }

        /// <summary>When the units change, every figure redraws: the elevation, the readout, the legend and the stats.</summary>
        void CheckUnits()
        {
            var units = DisplayUnits.Current;
            if (units == _shownUnits) return;
            _shownUnits = units;
            _shownElevation = int.MinValue + 1;
            _shownInfo = long.MinValue;
            _shownLegend = "\0";   // forces the legend to rebuild
            if (_statsOpen) FillStats();
        }

        /// <summary>
        /// Makes the elevation readout's text for every elevation between <paramref name="minMetres"/> and
        /// <paramref name="maxMetres"/> (the mountain's range, with a margin), in both unit systems, so the readout never
        /// allocates while the camera moves. Call when a mountain opens.
        /// </summary>
        public void PrepareElevations(double minMetres, double maxMetres)
        {
            foreach (UnitSystem units in new[] { UnitSystem.Metric, UnitSystem.Imperial })
            {
                int first = UnitFormat.ElevationKey(minMetres, units), last = UnitFormat.ElevationKey(maxMetres, units);
                var text = new string[Mathf.Max(0, last - first + 1)];
                for (int i = 0; i < text.Length; i++) text[i] = UnitFormat.ElevationFromKey(first + i, units);
                _elevationText[(int)units] = text;
                _elevationFirstKey[(int)units] = first;
            }
        }

        string ElevationText(int key, UnitSystem units)
        {
            var text = _elevationText[(int)units];
            int i = key - _elevationFirstKey[(int)units];
            return text != null && i >= 0 && i < text.Length ? text[i] : UnitFormat.ElevationFromKey(key, units);
        }

        /// <summary>The elevation under the pointer in metres (NaN: off the terrain, or over the HUD).</summary>
        public void SetElevation(float elevation)
        {
            CheckUnits();
            int shown = float.IsNaN(elevation) ? int.MinValue : UnitFormat.ElevationKey(elevation, _shownUnits);
            if (shown == _shownElevation) return;
            _shownElevation = shown;
            bool none = shown == int.MinValue;
            _elevation.text = none ? _noElevation[(int)_shownUnits] : ElevationText(shown, _shownUnits);
            _elevation.EnableInClassList("elev--none", none);
        }

        /// <summary>
        /// What the info layer that's on reads under the pointer, under the elevation (task 12b.2), as the mockup shows it:
        /// a swatch of the layer's colour there, then "Slope 47% · Most difficult", "Faces SE · 135°" (or "Flat · under
        /// 10%") or "Snow 2 ft 4 in", the figure in bold. A NaN hides the figures ("– – –"); no info layer hides the
        /// line. The text is rebuilt only when the shown figure changes.
        /// </summary>
        public void SetInfoReadout(string infoId, float slopePercent, float bearingDegrees, float snowMetres)
        {
            CheckUnits();
            var units = _shownUnits;
            long key;
            switch (infoId)
            {
                case MapLayers.SlopeAngle: key = float.IsNaN(slopePercent) ? 5L << 40 : 1L << 40 | (uint)Mathf.RoundToInt(slopePercent); break;
                case MapLayers.Exposure:
                    key = float.IsNaN(slopePercent) ? 5L << 40 : slopePercent < SlopeBands.FlatPercent ? 2L << 40 : 3L << 40 | (uint)Mathf.RoundToInt(Mathf.Repeat(bearingDegrees, 360)) % 360; break;
                case MapLayers.SnowDepth: key = float.IsNaN(snowMetres) ? 5L << 40 : 4L << 40 | (uint)UnitFormat.SnowDepthKey(snowMetres, units); break;
                default: key = 0; break;
            }
            if (key == _shownInfo) return;
            _shownInfo = key;
            _readout.EnableInClassList("hidden", key == 0);
            if (key == 0) return;
            long kind = key >> 40;
            int value = (int)(key & 0xFFFFFFFF);
            _readoutSwatch.EnableInClassList("hidden", kind == 5);
            string figure = "", tail = "";
            switch (kind)
            {
                case 1:
                    int band = (int)SlopeBands.Of(value);
                    _readoutSwatch.style.backgroundColor = InfoLegend.SlopeAngle[band].Colour;
                    _readoutLead.text = "Slope";
                    figure = InfoFigure(key, kind, value, units, snowMetres);
                    tail = SlopeTails[band];
                    break;
                case 2:
                    _readoutSwatch.style.backgroundColor = InfoLegend.ExposureFlat;
                    _readoutLead.text = "Flat · under 10%";
                    break;
                case 3:
                    _readoutSwatch.style.backgroundColor = InfoLegend.ExposureColours[(int)Mathf.Floor((value + 22.5f) / 45) % 8];
                    _readoutLead.text = "Faces";
                    figure = InfoFigure(key, kind, value, units, snowMetres);
                    break;
                case 4:
                    _readoutSwatch.style.backgroundColor = SnowDepthColour(snowMetres, units);
                    _readoutLead.text = "Snow";
                    figure = InfoFigure(key | (long)units << 48, kind, value, units, snowMetres);
                    break;
                default:
                    _readoutLead.text = "– – –";
                    break;
            }
            _readoutFigure.text = figure;
            _readoutTail.text = tail;
            _readoutFigure.EnableInClassList("hidden", figure.Length == 0);
            _readoutTail.EnableInClassList("hidden", tail.Length == 0);
        }

        /// <summary>The bold figure for a readout key, made the first time it shows.</summary>
        string InfoFigure(long cacheKey, long kind, int value, UnitSystem units, float snowMetres)
        {
            if (_infoText.TryGetValue(cacheKey, out string text)) return text;
            text = kind == 1 ? value + "%" : kind == 3 ? SlopeBands.CompassPoint(value) + " · " + value + "°" : UnitFormat.SnowDepth(snowMetres, units);
            _infoText[cacheKey] = text;
            return text;
        }

        static readonly double[][] DepthStops = { UnitFormat.SnowDepthStops(UnitSystem.Metric), UnitFormat.SnowDepthStops(UnitSystem.Imperial) };

        /// <summary>The snow-depth legend's colour for a depth: the stop at or below it.</summary>
        static Color SnowDepthColour(float metres, UnitSystem units)
        {
            var stops = DepthStops[(int)units];
            int k = 0;
            while (k + 1 < stops.Length && metres >= stops[k + 1]) k++;
            return InfoLegend.SnowDepthColours[k];
        }

        static readonly string[] SlopeTails = MakeSlopeTails();

        static string[] MakeSlopeTails()
        {
            var tails = new string[SlopeBands.Names.Length];
            for (int i = 0; i < tails.Length; i++) tails[i] = "· " + SlopeBands.Names[i];
            return tails;
        }
    }
}
