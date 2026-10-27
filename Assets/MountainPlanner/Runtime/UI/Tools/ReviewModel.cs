using System.Collections.Generic;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// A tool's review (CF-02 step 4, P3-06): figure rows with the rule each comes from, warnings (some of which block
    /// Break ground), and which commit buttons are live. The tool rebuilds it when its analysis changes, never per
    /// frame; the window redraws when <see cref="Version"/> moves.
    /// </summary>
    public sealed class ReviewModel
    {
        public readonly struct Row
        {
            public readonly string Label, Value, Rule, Slot;
            public readonly bool Pending;
            public Row(string label, string value, string rule, bool pending, string slot)
            {
                Label = label; Value = value; Rule = rule; Pending = pending; Slot = slot;
            }
        }

        public readonly struct Warning
        {
            public readonly string Text, Rule;
            /// <summary>Blocks Break ground (a red ✕); otherwise a caution (amber !).</summary>
            public readonly bool Blocks;
            public Warning(string text, string rule, bool blocks) { Text = text; Rule = rule; Blocks = blocks; }
        }

        /// <summary>
        /// Reserved rows other tasks fill (P3-06 §2): Terrain is task 05/08's cut, fill, balance and trees cleared;
        /// Network is task 10's NW-12 warnings. Until then the window shows each greyed with a dash.
        /// </summary>
        public const string TerrainSlot = "terrain", NetworkSlot = "network";

        readonly List<Row> _rows = new List<Row>();
        readonly List<Warning> _warnings = new List<Warning>();

        public IReadOnlyList<Row> Rows => _rows;
        public IReadOnlyList<Warning> Warnings => _warnings;
        public bool CanBreakGround { get; private set; }
        public bool CanSavePlan { get; private set; }
        public int Version { get; private set; }

        public ReviewModel Clear()
        {
            _rows.Clear();
            _warnings.Clear();
            CanBreakGround = CanSavePlan = false;
            Version++;
            return this;
        }

        public ReviewModel Add(string label, string value, string rule = null)
        {
            _rows.Add(new Row(label, value, rule, false, null));
            Version++;
            return this;
        }

        /// <summary>A reserved row: its label, the rule that will fill it, and the slot name a later task fills.</summary>
        public ReviewModel Reserved(string label, string rule, string slot)
        {
            _rows.Add(new Row(label, "—", rule, true, slot));
            Version++;
            return this;
        }

        /// <summary>Fills a reserved row's value (task P3-10 fills the Network slot); nothing when no row has the slot.</summary>
        public ReviewModel Fill(string slot, string value)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Slot != slot) continue;
                _rows[i] = new Row(_rows[i].Label, value, _rows[i].Rule, false, slot);
                Version++;
            }
            return this;
        }

        public ReviewModel Warn(string text, string rule = null, bool blocks = false)
        {
            _warnings.Add(new Warning(text, rule, blocks));
            Version++;
            return this;
        }

        /// <summary>Sets which commit buttons are live. A blocking warning always turns Break ground off.</summary>
        public ReviewModel Ready(bool breakGround, bool savePlan)
        {
            bool blocked = false;
            foreach (var w in _warnings) blocked |= w.Blocks;
            CanBreakGround = breakGround && !blocked;
            CanSavePlan = savePlan;
            Version++;
            return this;
        }
    }
}
