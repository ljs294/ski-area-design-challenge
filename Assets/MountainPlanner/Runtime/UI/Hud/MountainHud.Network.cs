using System;
using System.Collections.Generic;
using MountainPlanner.UI.Tools;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>One built lift as Analysis › Network lists it (task P3-10, NW-13).</summary>
    public struct NetworkLiftRow
    {
        public ulong Id;
        public string Name;
        /// <summary>Trails a descent from its top runs along.</summary>
        public int Reached;
        /// <summary>NW-03; false is "No way down".</summary>
        public bool Connected;
    }

    /// <summary>A built trail with an NW-12 warning, as Analysis › Network lists it.</summary>
    public struct NetworkTrailRow
    {
        public ulong Id;
        public string Name;
        public double RatingPercent;
        /// <summary>"No lift serves this" or "Leads nowhere".</summary>
        public string Warning;
    }

    /// <summary>
    /// Analysis › Network (task P3-10; NW-04, NW-13; owner NT4, NT5, NT11, NT12; the approved mockup): every built lift
    /// with its status and the trails its top reaches, then the trails with a warning; the status bar's lift and trail
    /// counts (built, amber when any has a warning, a click opens this tab); the Network view's key; and the lift and
    /// trail cards stacked under the legend in the right-hand column, the legend folded to its title while a card is
    /// open.
    /// </summary>
    public sealed partial class MountainHud
    {
        VisualElement _network;
        Label _networkLine;
        AnalysisList _networkLifts, _networkTrails;
        Button _legendFold;
        bool _legendPeek, _networkView;
        /// <summary>Game finding 2: the key's ✕ closed the Network key; opening the Network view again brings it back.</summary>
        bool _networkKeyClosed;
        int _networkLiftCount, _networkTrailCount;

        /// <summary>The Network view comes on or off: Analysis › Network opened or closed (the app greys the ground).</summary>
        public event Action<bool> NetworkViewChanged;
        /// <summary>A lift row in Analysis › Network clicked: the app picks the lift (finding 2).</summary>
        public event Action<ulong> NetworkLiftChosen;
        /// <summary>A trail row in Analysis › Network clicked: the app picks the trail from its head.</summary>
        public event Action<ulong> NetworkTrailChosen;
        /// <summary>A lift or trail row under the pointer in Analysis › Network (0 for none).</summary>
        public event Action<ulong> NetworkRowHovered;
        /// <summary>A lift's or trail's card closed (its ✕, Esc or another pick): the app clears the Network pick (NT11).</summary>
        public event Action CardClosed;

        /// <summary>Whether the Network view is on: Analysis open on its Network tab.</summary>
        public bool NetworkViewOn => _networkView;

        void WireNetwork()
        {
            _network = new VisualElement { name = "an-network" };
            _network.AddToClassList("an-network");
            _network.AddToClassList("hidden");
            _networkLine = new Label { name = "network-tools" };   // the mockup's name for the line
            _networkLine.AddToClassList("an-network__line");
            _network.Add(_networkLine);

            NetworkLiftRow L(AnalysisList.Row r) => (NetworkLiftRow)r.Data;
            _networkLifts = new AnalysisList("lifts", new[]
            {
                new AnalysisList.Column { Title = "Lift", Grow = 1.7f, Cell = r => L(r).Name },
                new AnalysisList.Column { Title = "Trails reached", Numeric = true, Grow = 0.9f, Cell = r => L(r).Reached.ToString(), SortKey = r => L(r).Reached },
                new AnalysisList.Column { Title = "Status", Grow = 1.1f, Cell = r => L(r).Connected ? "● Connected" : "● No way down",
                    Bin = r => L(r).Connected ? "ok" : "warn", Bins = new[] { ("ok", "Connected"), ("warn", "No way down") } },
            }, "network-lifts");
            _network.Add(_networkLifts.Root);
            NetworkTrailRow T(AnalysisList.Row r) => (NetworkTrailRow)r.Data;
            _networkTrails = new AnalysisList("trails", new[]
            {
                new AnalysisList.Column { Title = "Trail", Grow = 1.7f, Cell = r => $"{SymbolOf(T(r).RatingPercent)}  {T(r).Name}" },
                new AnalysisList.Column { Title = "Warning", Grow = 1.8f, Cell = r => "● " + T(r).Warning },
            }, "network-trails");
            _networkTrails.Root.AddToClassList("an-network__warnings");
            _network.Add(_networkTrails.Root);
            _root.Q("analysis-body").Add(_network);
            _networkLifts.Chosen += id => NetworkLiftChosen?.Invoke(id);
            _networkTrails.Chosen += id => NetworkTrailChosen?.Invoke(id);
            _networkLifts.Hovered += id => NetworkRowHovered?.Invoke(id);
            _networkTrails.Hovered += id => NetworkRowHovered?.Invoke(id);

            // The bar's lift and trail counts (NT5): built, a click opens Analysis › Network.
            foreach (string name in new[] { "bar-lifts", "bar-trails" })
            {
                if (!(_root.Q(name) is Button b)) continue;
                b.RemoveFromClassList("stat--dis");
                b.focusable = true;
                b.clicked += () => OpenAnalysisTab("network");
                b.Q<Label>(className: "count")?.RemoveFromClassList("nodata");
            }
            SetNetworkCounts(0, 0, 0, 0);

            // NT12: the cards stack under the legend in the right-hand column; the legend gets a fold.
            var rcol = _root.Q("rcol");
            foreach (var card in new[] { _trailCard, _liftCard })
            {
                if (card == null) continue;
                card.AddToClassList("an-card--stacked");
                rcol.Add(card);
            }
            _legendFold = new Button(() => { _legendPeek = !_legendPeek; FoldLegend(); }) { name = "legend-fold", text = "▸", tooltip = "Show the key" };
            _legendFold.AddToClassList("legend__fold");
            _legendFold.AddToClassList("hidden");
            _root.Q("legend-head").Insert(0, _legendFold);
        }

        /// <summary>The lists (rebuilt with the network, never per tick) and the line over them.</summary>
        public void SetNetwork(IReadOnlyList<NetworkLiftRow> lifts, IReadOnlyList<NetworkTrailRow> warnings, int trails, int junctions, int paths)
        {
            if (_network == null) return;
            var rows = new List<AnalysisList.Row>(lifts.Count);
            int noWay = 0;
            foreach (var l in lifts)
            {
                rows.Add(new AnalysisList.Row { Id = l.Id, Data = l });
                if (!l.Connected) noWay++;
            }
            _networkLifts.SetRows(rows, "A lift is connected when a way down from its top reaches a lift's bottom. A terminal gathers every trail within 100 ft of it, and terminals that close share one area, so you can unload and ride on. Click a lift, a stretch of trail or a node on the map to light everything you can ski to from it; Esc clears it.");
            var trows = new List<AnalysisList.Row>(warnings.Count);
            foreach (var t in warnings) trows.Add(new AnalysisList.Row { Id = t.Id, Data = t });
            _networkTrails.SetRows(trows, "");
            _networkTrails.Root.EnableInClassList("hidden", warnings.Count == 0);
            string Plural(int n, string w) => $"{n} {w}{(n == 1 ? "" : "s")}";
            string state = "Every lift has a way down";
            if (noWay > 0 && warnings.Count > 0) state = $"{Plural(noWay, "lift")} with no way down · {Plural(warnings.Count, "trail")} to look at";
            else if (noWay > 0) state = $"{Plural(noWay, "lift")} with no way down";
            else if (warnings.Count > 0) state = $"{Plural(warnings.Count, "trail")} to look at";
            _networkLine.text = $"{Plural(lifts.Count, "lift")} · {Plural(trails, "trail")} · {Plural(junctions, "junction")} · {Plural(paths, "path")}   {state}";
            _networkLine.EnableInClassList("an-network__line--warn", noWay > 0 || warnings.Count > 0);
            _networkLiftCount = lifts.Count;
            _networkTrailCount = trails;
            ShowNetworkTab();
        }

        /// <summary>The bar's counts (NT5): lifts and trails built, amber with how many have an NW-12 warning.</summary>
        public void SetNetworkCounts(int lifts, int liftWarnings, int trails, int trailWarnings)
        {
            void Count(string name, int n, int warn, string noun, string words)
            {
                var b = _root.Q(name);
                if (b == null) return;
                var label = b.Q<Label>(className: "count");
                if (label != null) label.text = n.ToString();
                b.EnableInClassList("stat--warn", warn > 0);
                b.tooltip = $"{n} {noun}{(n == 1 ? "" : "s")} built{(warn > 0 ? $" · {warn} {words}" : "")}: Analysis › Network";
            }
            Count("bar-lifts", lifts, liftWarnings, "lift", liftWarnings == 1 ? "has no way down" : "have no way down");
            Count("bar-trails", trails, trailWarnings, "trail", trailWarnings == 1 ? "has a warning" : "have warnings");
        }

        /// <summary>Lights a row (the Network pick's lift or trail).</summary>
        public void SetNetworkFocused(ulong id)
        {
            _networkLifts?.SetFocused(id);
            _networkTrails?.SetFocused(id);
        }

        /// <summary>The tab's lists show on Analysis › Network; the Network view follows the tab.</summary>
        void ShowNetworkTab()
        {
            if (_network == null) return;
            bool on = _analysisTab == "network";
            bool any = _networkLiftCount > 0 || _networkTrailCount > 0;
            _network.EnableInClassList("hidden", !on || !any);
            if (on)
            {
                _analysisEmpty.text = any ? "" : "No lifts or trails yet: build a lift, then a trail down from its top. The network joins them where they meet.";
                _analysisEmpty.EnableInClassList("hidden", any);
            }
            bool view = on && _analysisOpen;
            if (view == _networkView) return;
            _networkView = view;
            if (view) _networkKeyClosed = false;
            ShowNetworkKey();
            NetworkViewChanged?.Invoke(view);
        }

        // ---------- the Network view's key, and the legend folded under a card (NT12) ----------

        void ShowNetworkKey()
        {
            if (_networkView)
            {
                _legendBody.Clear();
                _legendTitle.text = "Network";
                _legendBody.Add(LegendRow(Hex(0xe0483a), false, "Lift, going up", ""));
                _legendBody.Add(LegendRow(Hex(0xdfe8ee), false, "Run, going down", ""));
                _legendBody.Add(LegendRow(Hex(0xf0b43c), false, "No lift serves it, or leads nowhere", ""));
                _legendBody.Add(LegendRow(Color.white, false, "Junction, or a path between runs", ""));
                _legendBody.Add(LegendAreaRow("Lift terminal, 100 ft around"));
                // Game finding 1: "Reachable", its swatch the colours things light in: green, blue, black runs and red lifts.
                _legendBody.Add(LegendStripeRow(new[] { Hex(0x1f8a4c), Hex(0x2f6bb0), Hex(0x1f1c19), Hex(0xe0483a) }, "Reachable"));
                _legendBody.Add(Note("Click a lift, a stretch of trail or a node to light everything you can ski to from it. Esc or a click on empty ground clears it."));
            }
            else
            {
                _shownLegend = null;   // the info layer's own legend comes back
                _shownContours = !_legendContours;
                SetLegend(_legendInfo, _legendContours);
                return;
            }
            ShowLegend();
        }

        /// <summary>NT15: a station's area, its dark red disc in a light red ring.</summary>
        static VisualElement LegendAreaRow(string word)
        {
            var row = LegendRow(Hex(0x4a2622), false, word, "");
            var swatch = row.Q(className: "lr__sw");
            swatch.AddToClassList("lr__sw--area");
            return row;
        }

        /// <summary>A legend row whose swatch is touching squares of <paramref name="colours"/>, left to right.</summary>
        static VisualElement LegendStripeRow(Color[] colours, string word)
        {
            var row = LegendRow(Color.clear, false, word, "");
            var swatch = row.Q(className: "lr__sw");
            swatch.AddToClassList("lr__sw--stripes");
            foreach (var c in colours)
            {
                var part = new VisualElement { pickingMode = PickingMode.Ignore };
                part.AddToClassList("lr__part");
                part.style.backgroundColor = c;
                swatch.Add(part);
            }
            return row;
        }

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

        /// <summary>A card opened or closed: the legend folds to its title while one is open (▸ unfolds it).</summary>
        void CardsChanged(bool closed)
        {
            FoldLegend();
            if (closed) CardClosed?.Invoke();
        }

        void FoldLegend()
        {
            if (_legend == null || _legendFold == null) return;
            bool card = _trailCardId != 0 || _liftCardId != 0;
            bool folded = card && !_legendPeek;
            if (!card) _legendPeek = false;
            _legend.EnableInClassList("legend--folded", folded);
            _legendFold.EnableInClassList("hidden", !card);
            _legendFold.text = folded ? "▸" : "▾";
            _legendFold.tooltip = folded ? "Show the key" : "Fold the key";
        }
    }
}
