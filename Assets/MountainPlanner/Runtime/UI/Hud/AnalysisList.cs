using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// An Analysis tab's list (task 08, owner TL9; task 09 reuses it for Lifts), ported from the mockup's listTab: a count
    /// and a Filter button; sortable columns; with Filter on, a ▾ on each filterable column opens a spreadsheet-style menu
    /// (sort, the values to show, Clear filter); rows that light their thing on the map while hovered and open its card
    /// when chosen (owner, #87). Rows are rebuilt only when the rows or the filters change, never per frame.
    /// </summary>
    public sealed class AnalysisList
    {
        public sealed class Column
        {
            public string Title;
            public bool Numeric;
            public float Grow = 1;
            public Func<Row, string> Cell;
            /// <summary>Sorts by it when set (the mockup's Length and Vertical).</summary>
            public Func<Row, double> SortKey;
            /// <summary>Filters by it when set: each row's value, and the values with their labels, in menu order.</summary>
            public Func<Row, string> Bin;
            public (string Value, string Label)[] Bins;
        }

        public sealed class Row
        {
            public ulong Id;
            public object Data;
        }

        public VisualElement Root { get; }
        /// <summary>A row under the pointer (0 when none): the thing lights on the map.</summary>
        public event Action<ulong> Hovered;
        /// <summary>A row clicked or chosen with Enter: its focus card opens.</summary>
        public event Action<ulong> Chosen;

        readonly string _noun, _id;
        readonly Column[] _cols;
        readonly Label _count, _foot;
        readonly Button _filter, _clear;
        readonly VisualElement _head, _rows, _menu;
        readonly List<Row> _all = new List<Row>();
        readonly Dictionary<int, HashSet<string>> _hidden = new Dictionary<int, HashSet<string>>();
        bool _filterOn;
        int _sortCol = -1;
        bool _sortUp;
        int _menuCol = -1;
        ulong _focused;
        string _footText;
        readonly Dictionary<ulong, Button> _rowButtons = new Dictionary<ulong, Button>();

        /// <param name="id">The elements' name part when two lists share a noun (task P3-10's Network tab); the noun by default.</param>
        public AnalysisList(string noun, Column[] columns, string id = null)
        {
            _noun = noun;
            _id = id ?? noun;
            _cols = columns;
            Root = new VisualElement { name = "an-list-" + _id };
            Root.AddToClassList("an-list");
            var tools = new VisualElement();
            tools.AddToClassList("an-tools");
            _count = new Label { name = "an-count-" + _id };
            _count.AddToClassList("an-tools__count");
            tools.Add(_count);
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            tools.Add(gap);
            _clear = new Button(ClearFilters) { name = "an-clear-" + _id, text = "Clear filters" };
            _clear.AddToClassList("an-tb");
            tools.Add(_clear);
            _filter = new Button(() => { _filterOn = !_filterOn; _menuCol = -1; Redraw(); }) { name = "an-filter-" + _id, text = "Filter" };
            _filter.AddToClassList("an-tb");
            tools.Add(_filter);
            Root.Add(tools);
            _head = new VisualElement();
            _head.AddToClassList("an-row");
            _head.AddToClassList("an-row--head");
            Root.Add(_head);
            _rows = new ScrollView(ScrollViewMode.Vertical);
            _rows.AddToClassList("an-rows");
            Root.Add(_rows);
            _foot = new Label();
            _foot.AddToClassList("an-foot");
            Root.Add(_foot);
            _menu = new VisualElement { name = "an-menu-" + _id };
            _menu.AddToClassList("an-menu");
            Root.Add(_menu);
            Redraw();
        }

        /// <summary>The list's rows (in the order given) and its foot note.</summary>
        public void SetRows(IReadOnlyList<Row> rows, string foot)
        {
            _all.Clear();
            _all.AddRange(rows);
            _footText = foot;
            Redraw();
        }

        /// <summary>The row whose card is open, marked.</summary>
        public void SetFocused(ulong id)
        {
            _focused = id;
            foreach (var kv in _rowButtons) kv.Value.EnableInClassList("an-row--on", kv.Key == id);
        }

        public int Shown { get; private set; }

        void ClearFilters()
        {
            _hidden.Clear();
            _menuCol = -1;
            Redraw();
        }

        bool Passes(Row r)
        {
            foreach (var kv in _hidden)
                if (kv.Value.Count > 0 && kv.Value.Contains(_cols[kv.Key].Bin(r))) return false;
            return true;
        }

        void Redraw()
        {
            // The header: each title (with the sort arrow), and while filtering a ▾ on the filterable and sortable ones.
            _head.Clear();
            for (int c = 0; c < _cols.Length; c++)
            {
                var col = _cols[c];
                var cell = new VisualElement();
                cell.AddToClassList("an-cell");
                if (col.Numeric) cell.AddToClassList("an-cell--n");
                cell.style.flexGrow = col.Grow;
                cell.style.flexBasis = 0;
                string arrow = _sortCol == c ? (_sortUp ? " ↑" : " ↓") : "";
                var title = new Label(col.Title + arrow);
                title.AddToClassList("an-head");
                cell.Add(title);
                if (_filterOn && (col.Bin != null || col.SortKey != null))
                {
                    int at = c;
                    var dd = new Button(() => { _menuCol = _menuCol == at ? -1 : at; Redraw(); }) { name = $"an-dd-{_id}-{c}", text = "▾", tooltip = "Filter and sort" };
                    dd.AddToClassList("an-dd");
                    dd.EnableInClassList("an-dd--act", _sortCol == c || (_hidden.TryGetValue(c, out var h) && h.Count > 0));
                    cell.Add(dd);
                }
                _head.Add(cell);
            }

            // The rows that pass the filters, sorted if asked.
            var shown = new List<Row>();
            foreach (var r in _all) if (Passes(r)) shown.Add(r);
            if (_sortCol >= 0 && _cols[_sortCol].SortKey != null)
            {
                var key = _cols[_sortCol].SortKey;
                bool up = _sortUp;
                shown.Sort((a, b) => up ? key(a).CompareTo(key(b)) : key(b).CompareTo(key(a)));
            }
            _rows.Clear();
            _rowButtons.Clear();
            foreach (var r in shown)
            {
                var row = r;
                var b = new Button(() => Chosen?.Invoke(row.Id)) { name = $"an-row-{_id}-{row.Id:x}" };
                b.AddToClassList("an-row");
                b.AddToClassList("an-row--item");
                b.EnableInClassList("an-row--on", row.Id == _focused);
                b.RegisterCallback<PointerEnterEvent>(_ => Hovered?.Invoke(row.Id));
                b.RegisterCallback<PointerLeaveEvent>(_ => Hovered?.Invoke(0));
                b.RegisterCallback<FocusInEvent>(_ => Hovered?.Invoke(row.Id));
                for (int c = 0; c < _cols.Length; c++)
                {
                    var l = new Label(_cols[c].Cell(row)) { pickingMode = PickingMode.Ignore };
                    l.AddToClassList("an-cell");
                    if (_cols[c].Numeric) { l.AddToClassList("an-cell--n"); l.AddToClassList("mono"); }
                    l.style.flexGrow = _cols[c].Grow;
                    l.style.flexBasis = 0;
                    b.Add(l);
                }
                _rows.Add(b);
                _rowButtons[row.Id] = b;
            }
            Shown = shown.Count;
            string noun = _all.Count == 1 && _noun.EndsWith("s") ? _noun.Substring(0, _noun.Length - 1) : _noun;
            _count.text = shown.Count == _all.Count ? $"{_all.Count} {noun} on the map" : $"{shown.Count} of {_all.Count} {_noun} on the map";
            int active = 0;
            foreach (var h in _hidden.Values) active += h.Count;
            _clear.EnableInClassList("hidden", active == 0);
            _filter.EnableInClassList("an-tb--on", _filterOn);
            _foot.text = shown.Count > 0 ? _footText + " Hover a row to find it on the mountain; click it for its card." : $"No {_noun} match the filters.";

            // The open column's menu (the mockup's amenu).
            _menu.Clear();
            _menu.EnableInClassList("hidden", _menuCol < 0);
            if (_menuCol < 0) return;
            var mc = _cols[_menuCol];
            int ci = _menuCol;
            if (mc.SortKey != null)
            {
                MenuItem("Sort smallest to largest", () => { _sortCol = ci; _sortUp = true; _menuCol = -1; Redraw(); });
                MenuItem("Sort largest to smallest", () => { _sortCol = ci; _sortUp = false; _menuCol = -1; Redraw(); });
            }
            if (mc.Bin != null && mc.Bins != null)
            {
                if (!_hidden.TryGetValue(ci, out var hide)) _hidden[ci] = hide = new HashSet<string>();
                foreach (var (value, label) in mc.Bins)
                {
                    int n = 0;
                    foreach (var r in _all) if (mc.Bin(r) == value) n++;
                    string v = value;
                    MenuItem($"{(hide.Contains(v) ? "☐" : "☑")}  {label}  ({n})", () =>
                    {
                        if (!hide.Remove(v)) hide.Add(v);
                        Redraw();
                    });
                }
                var clr = MenuItem("Clear filter", () => { hide.Clear(); Redraw(); });
                clr.SetEnabled(hide.Count > 0);
            }
            // Under its column, as the mockup's menu hangs from its ▾.
            float left = 0;
            for (int c = 0; c < ci; c++) left += _cols[c].Grow;
            float total = 0;
            foreach (var c in _cols) total += c.Grow;
            _menu.style.left = new Length(100f * left / total, LengthUnit.Percent);
        }

        Button MenuItem(string text, Action act)
        {
            var b = new Button(act) { text = text };
            b.AddToClassList("an-mi");
            _menu.Add(b);
            return b;
        }
    }
}
