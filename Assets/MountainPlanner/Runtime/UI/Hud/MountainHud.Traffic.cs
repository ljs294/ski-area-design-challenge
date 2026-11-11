using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// SN-31, the Traffic info layer's legend card and readout (task 4.13 PR C). The card shows the runs' and the lifts'
    /// five steps with their real figures, what a lift's circle means, and two switches: the window (the last hour, or
    /// today's average hour) and what colours the dots (ability, state or satisfaction). The readout under the pointer
    /// names the run or lift there with its numbers.
    /// </summary>
    public sealed partial class MountainHud
    {
        /// <summary>The Traffic layer reads today's average hour instead of the last hour.</summary>
        public bool TrafficToday { get; private set; }
        /// <summary>What colours the dots: 0 ability, 1 state, 2 satisfaction (the dots' own modes).</summary>
        public int DotColourMode { get; private set; }

        static readonly string[] WindowWords = { "Last hour", "Today" };
        static readonly string[] DotWords = { "Ability", "State", "Happiness" };

        Button[] _trafficWindow, _dotMode;

        void BuildTrafficLegend(UnitSystem units)
        {
            _legendTitle.text = InfoLegend.TrafficTitle;
            _legendBody.Add(Note(InfoLegend.TrafficRunsHead(units)));
            foreach (var e in InfoLegend.TrafficRuns) _legendBody.Add(LegendRow(e.Colour, false, e.Label, e.Figure));
            _legendBody.Add(Note(InfoLegend.TrafficLiftsHead));
            foreach (var e in InfoLegend.TrafficLifts) _legendBody.Add(LegendRow(e.Colour, false, e.Label, e.Figure));
            _legendBody.Add(Note(InfoLegend.TrafficNote));
            _legendBody.Add(Switch("traffic-window", WindowWords, () => TrafficToday || _windowForced ? 1 : 0, i => TrafficToday = i == 1, out _trafficWindow));
            _trafficWindow[0].SetEnabled(!_windowForced);
            _legendBody.Add(Note("Dots by"));
            _legendBody.Add(Switch("traffic-dots", DotWords, () => DotColourMode, i => DotColourMode = i, out _dotMode));
        }

        static VisualElement Switch(string name, string[] words, System.Func<int> get, System.Action<int> set, out Button[] buttons)
        {
            var seg = new VisualElement { name = name };
            seg.AddToClassList("mp-seg");
            seg.AddToClassList("legend__seg");
            var made = new Button[words.Length];
            for (int i = 0; i < words.Length; i++)
            {
                int index = i;
                var b = made[i] = new Button { name = name + "-" + i, text = words[i] };
                b.AddToClassList("mp-seg__opt");
                if (i == 0) b.AddToClassList("mp-seg__opt--first");
                b.clicked += () =>
                {
                    set(index);
                    for (int k = 0; k < made.Length; k++) made[k].EnableInClassList("mp-seg__opt--on", k == get());
                };
                seg.Add(b);
            }
            for (int k = 0; k < made.Length; k++) made[k].EnableInClassList("mp-seg__opt--on", k == get());
            buttons = made;
            return seg;
        }

        bool _windowForced;

        /// <summary>
        /// Past the stop the layer reads today by itself: the switch shows Today and Last hour rests until the next
        /// morning. Cheap to call every second (it changes classes only when the state does).
        /// </summary>
        public void SetTrafficDayOver(bool over)
        {
            if (over == _windowForced || _trafficWindow == null) { _windowForced = over; return; }
            _windowForced = over;
            _trafficWindow[0].SetEnabled(!over);
            for (int k = 0; k < _trafficWindow.Length; k++) _trafficWindow[k].EnableInClassList("mp-seg__opt--on", k == (over || TrafficToday ? 1 : 0));
        }

        long _shownTraffic = long.MinValue;

        /// <summary>
        /// The Traffic readout under the elevation: a swatch of the step there, then the thing's name in bold and its numbers
        /// ("Moose Run · 3 on it · 84 an hour · 412 today", "Summit Quad · 42 in line · 1:45 · 76% full"). The caller passes a
        /// key that changes whenever the text would, so the text is made only then. Key 0 hides the line.
        /// </summary>
        public void SetTrafficReadout(long key, Color swatch, string name, string figures)
        {
            if (key == _shownTraffic) return;
            _shownTraffic = key;
            _readout.EnableInClassList("hidden", key == 0);
            if (key == 0) return;
            _readoutSwatch.EnableInClassList("hidden", false);
            _readoutSwatch.style.backgroundColor = swatch;
            _readoutLead.text = name;
            _readoutFigure.text = figures;
            _readoutTail.text = "";
            _readoutFigure.EnableInClassList("hidden", string.IsNullOrEmpty(figures));
            _readoutTail.EnableInClassList("hidden", true);
        }

        /// <summary>Traffic is off: the next Traffic readout writes its line again.</summary>
        public void ClearTrafficReadout() { _shownTraffic = long.MinValue; _shownInfo = long.MinValue; }
    }
}
