using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Lifts;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// One tower editor for New lift's towers and Edit lift (task 09, F1, F2, F4; LT37, LT38). Over a draft of the line it:
    /// - selects what the pointer is on in 3D (the draft's tower or terminal, through the lift pick, LT23), or steps through
    ///   the parts with [ and ]; the selected one is lit white (UI-15);
    /// - changes the selected tower: Page Up / Page Down 5 ft, , and . slide it 5 ft (Shift 50 ft), drag it, Delete;
    /// - changes the selected terminal's level (Page Up / Page Down, 1 ft) where the host allows;
    /// - adds a tower: a click on the line where no tower is, Insert or the window's Add tower puts one in hand there, on the
    ///   5 ft grid between its neighbours, shown as its own see-through model with the head the game picks (green where it
    ///   can stand, red where it can't); a click or Enter places it.
    /// The host owns the draft (its towers, levels, undo and analysis) and gets every change through its hooks.
    /// </summary>
    public sealed class LiftDraftEditor
    {
        public const int None = int.MinValue, Bottom = LiftView.PartBottom, Top = LiftView.PartTop;
        const double Slot = 1.524;   // 5 ft: TW-12's grid
        const float PickPixels = 12;

        readonly ToolContext _ctx;

        // ---------- what the host sets ----------

        /// <summary>The line, in local metres (x, z), and its type and stance.</summary>
        public Vector2 BottomXz, TopXz;
        public LiftType Type;
        public TowerStance Stance;
        /// <summary>The draft's towers, in order up the line.</summary>
        public IReadOnlyList<Tower> Towers = Array.Empty<Tower>();
        /// <summary>The analysed draft's rope (heads and loads for the window), or null while it's worked out.</summary>
        public LiftRope Rope;
        /// <summary>A change to the towers: one step of the host's undo, analysed again; a tower changed by hand makes the
        /// lift By hand.</summary>
        public Action<Action<List<Tower>>> Change;
        /// <summary>A drag: begun (the host keeps the draft as it was for its undo), moved (applied, not analysed), ended (one
        /// step, analysed; none if nothing moved).</summary>
        public Action DragBegan, DragEnded;
        public Action<Action<List<Tower>>> Dragged;
        /// <summary>The selected terminal's level, 1 ft at a time (true for the bottom); null where levels aren't edited here.</summary>
        public Action<bool, int> Level;
        /// <summary>The tower in hand's judge over the towers below it (TW-13), or null while the ground isn't ready.</summary>
        public Func<IReadOnlyList<Tower>, TowerInHand> HandFor;
        /// <summary>After a placement the next tower is in hand at once (New lift's towers by hand).</summary>
        public bool KeepAdding;
        /// <summary>Whether the terminals can be selected (Edit lift: to set their level or drag them).</summary>
        public bool Terminals = true;
        /// <summary>Delete with nothing selected (New lift's towers by hand: the last tower back); null for nothing.</summary>
        public Action TakeBack;

        // ---------- its state ----------

        public int Selected { get; private set; } = None;
        public bool Adding { get; private set; }
        public double HandS { get; private set; } = -1;
        public int HandMm { get; private set; } = DesignUnits.FeetToMm(35);
        public TowerInHand.Status HandStatus { get; private set; }
        /// <summary>A drag of the selected tower in progress (the host takes it as one step when it ends).</summary>
        public bool Dragging { get; private set; }
        /// <summary>Changes whenever what the window says about the editor would (the host refreshes on it, not per frame).</summary>
        public long StateKey => Adding
            ? ((long)Math.Round(HandS * 1000) * 1000 + HandMm / DesignUnits.FiveFeetMm) * 4 + (HandStatus == null ? 0 : HandStatus.Ok ? 1 : 2) + 1_000_000_000_000L
            : Selected;

        TowerInHand _hand;
        int _handBelow = -1;   // how many towers stand below the tower in hand (_hand was made over them)
        long _statusKey = long.MinValue;
        Vector2 _lastPointer = new Vector2(float.NaN, float.NaN);
        int _lit = None;

        public LiftDraftEditor(ToolContext ctx) { _ctx = ctx; }

        Vector2 Dir => (TopXz - BottomXz).normalized;
        public float Length => Vector2.Distance(BottomXz, TopXz);
        Vector2 At(double s) => BottomXz + Dir * (float)s;
        static double Gap => DesignUnits.TowerGapMm / 1000.0;

        /// <summary>The towers changed under it (an undo, a re-place): a selection past the end goes, the hand is judged again.</summary>
        public void TowersChanged()
        {
            if (Selected >= Towers.Count) Select(None);
            _hand = null;
            _statusKey = long.MinValue;
        }

        public void Select(int part)
        {
            Selected = part;
            Dragging = false;
            if (part != None) StopAdding();
            Claims();
            Light();
        }

        /// <summary>Puts a tower in hand at <paramref name="s"/> metres up the line (or the middle of the widest gap).</summary>
        public void StartAdding(double s = double.NaN)
        {
            Selected = None;
            Adding = true;
            HandS = double.IsNaN(s) ? WidestGapMiddle() : s;
            _lastPointer = new Vector2(float.NaN, float.NaN);
            _hand = null;
            _statusKey = long.MinValue;
            Claims();
            Light();
        }

        public void StopAdding()
        {
            if (!Adding) return;
            Adding = false;
            HandStatus = null;
            _ctx.TowerGhost?.Invoke()?.Hide();
            Claims();
        }

        /// <summary>Esc's step: the hand put down, then the selection; false when there was neither.</summary>
        public bool Escape()
        {
            if (Adding) { StopAdding(); return true; }
            if (Selected != None) { Select(None); return true; }
            return false;
        }

        /// <summary>Lets go of everything the editor holds (the tool closes or leaves its step).</summary>
        public void Release()
        {
            StopAdding();
            Selected = None;
            Dragging = false;
            _ctx.HighlightDraftPart?.Invoke(None);
            _lit = None;
            ClaimAll(false);
        }

        void Claims()
        {
            bool tower = Selected >= 0 || Adding, terminal = Selected == Bottom || Selected == Top;
            _ctx.Claim(GameAction.TowerUp, tower);
            _ctx.Claim(GameAction.TowerDown, tower);
            _ctx.Claim(GameAction.TowerSlideBack, tower);
            _ctx.Claim(GameAction.TowerSlideOn, tower);
            _ctx.Claim(GameAction.DeleteSelected, Selected >= 0 || TakeBack != null);
            _ctx.Claim(GameAction.TerminalLevelUp, terminal && Level != null);
            _ctx.Claim(GameAction.TerminalLevelDown, terminal && Level != null);
            _ctx.Claim(GameAction.TowerAdd, true);
            _ctx.Claim(GameAction.BrushNarrower, true);   // [ and ]: the previous and next part
            _ctx.Claim(GameAction.BrushWider, true);
        }

        void ClaimAll(bool on)
        {
            foreach (var a in new[] { GameAction.TowerUp, GameAction.TowerDown, GameAction.TowerSlideBack, GameAction.TowerSlideOn, GameAction.DeleteSelected,
                                      GameAction.TerminalLevelUp, GameAction.TerminalLevelDown, GameAction.TowerAdd, GameAction.BrushNarrower, GameAction.BrushWider })
                _ctx.Claim(a, on);
        }

        /// <summary>Claims the editor's keys (the host's step opens it).</summary>
        public void Open() { Claims(); Light(); }

        void Light()
        {
            int part = Adding ? None : Selected;
            if (part == _lit) return;
            _lit = part;
            _ctx.HighlightDraftPart?.Invoke(part);
        }

        /// <summary>Lights the selection again after the host redrew the draft (its pieces are new).</summary>
        public void Relight()
        {
            _lit = int.MaxValue;
            Light();
        }

        /// <summary>
        /// One frame's input. Returns true when the editor used the click (the host leaves it alone). Keys the editor
        /// owns are read here; the host reads its own (Enter on to the review, Ctrl+Z, Esc through <see cref="Escape"/>).
        /// </summary>
        public bool Tick(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.BrushWider)) Step(+1);
            if (input.Pressed(ToolKeys.BrushNarrower)) Step(-1);
            if (input.Pressed(ToolKeys.AddTower) && !Adding) StartAdding(input.HasGround ? Project(input) : double.NaN);
            if (Adding)
            {
                if (input.Pressed(ToolKeys.Delete) && TakeBack != null) { TakeBack(); _hand = null; }
                return TickHand(input);
            }
            bool used = false;
            if (input.Click && !input.OverPanel)
            {
                int part = _ctx.DraftPartUnderPointer?.Invoke() ?? None;
                // Finding E5: the rope (or a terminal while they can't be picked) under the pointer still gives way to a
                // tower within a few pixels of it, so a click near a tower selects it rather than adding another.
                bool notTower = part == None || part == LiftView.PartRope || (!Terminals && (part == Bottom || part == Top));
                if (notTower && input.HasGround)
                {
                    int near = Nearest(new Vector2(input.Ground.x, input.Ground.z), Mathf.Max(4f, PickPixels * input.MetresPerPixel));
                    if (near != None) part = near;
                }
                if (part >= 0 || (Terminals && (part == Bottom || part == Top)))
                {
                    Select(part);
                    Dragging = part >= 0 && part < Towers.Count;
                    if (Dragging) DragBegan?.Invoke();
                    used = true;
                }
                else if (input.HasGround && OnLine(input, out double s)) { StartAdding(s); used = true; }
                else if (Selected != None) { Select(None); used = true; }
            }
            if (Dragging && input.Drag && input.HasGround && Selected >= 0) SetAlong(Selected, Math.Round(Project(input) / Slot) * Slot, record: false);
            if (input.Release && Dragging)
            {
                Dragging = false;
                DragEnded?.Invoke();   // one step for the whole move
            }
            if (Selected >= 0 && Selected < Towers.Count)
            {
                double step = input.Shift ? 10 * Slot : Slot;
                if (input.Pressed(ToolKeys.SlideOn)) SetAlong(Selected, Towers[Selected].AlongMm / 1000.0 + step, record: true);
                if (input.Pressed(ToolKeys.SlideBack)) SetAlong(Selected, Towers[Selected].AlongMm / 1000.0 - step, record: true);
                if (input.Pressed(ToolKeys.TowerUp)) Height(Selected, +1);
                if (input.Pressed(ToolKeys.TowerDown)) Height(Selected, -1);
                if (input.Pressed(ToolKeys.Delete)) Remove(Selected);
            }
            else if ((Selected == Bottom || Selected == Top) && Level != null)
            {
                if (input.Pressed(ToolKeys.LevelUp)) Level(Selected == Bottom, +1);
                if (input.Pressed(ToolKeys.LevelDown)) Level(Selected == Bottom, -1);
            }
            else if (Selected == None && input.Pressed(ToolKeys.Delete)) TakeBack?.Invoke();
            return used;
        }

        // The tower in hand: it follows the pointer along the line between its neighbours, Page Up / Page Down set its
        // height, a click or Enter places it where it's green.
        bool TickHand(in ToolInput input)
        {
            int max = LiftCatalogIds.TowerMaxMm(Type.CatalogId);
            if (input.Pressed(ToolKeys.TowerUp)) HandMm = Mathf.Min(max, HandMm + DesignUnits.FiveFeetMm);
            if (input.Pressed(ToolKeys.TowerDown)) HandMm = Mathf.Max(DesignUnits.StationTowerMinMm, HandMm - DesignUnits.FiveFeetMm);
            double step = input.Shift ? 10 * Slot : Slot;
            if (input.Pressed(ToolKeys.SlideOn)) HandS += step;
            if (input.Pressed(ToolKeys.SlideBack)) HandS -= step;
            if (input.HasGround && !input.OverPanel)
            {
                var p = new Vector2(input.Ground.x, input.Ground.z);
                if (float.IsNaN(_lastPointer.x) || (p - _lastPointer).sqrMagnitude > 0.25f)
                {
                    _lastPointer = p;
                    HandS = Project(input);
                }
            }
            HandS = Math.Round(HandS / Slot) * Slot;
            int below = Below(HandS);
            double lo = below > 0 ? Towers[below - 1].AlongMm / 1000.0 + Gap : FirstSlot();
            double hi = below < Towers.Count ? Towers[below].AlongMm / 1000.0 - Gap : Length - Gap;
            HandS = hi >= lo ? Math.Max(lo, Math.Min(hi, HandS)) : lo;
            if (_hand == null || _handBelow != below)
            {
                var under = new List<Tower>(below);
                for (int i = 0; i < below; i++) under.Add(Towers[i]);
                _hand = HandFor?.Invoke(under);
                _handBelow = below;
                _statusKey = long.MinValue;
            }
            long key = (long)Math.Round(HandS * 1000) * 1000 + HandMm / DesignUnits.FiveFeetMm * 7 + below;
            if (key != _statusKey && _hand != null)
            {
                _statusKey = key;
                HandStatus = hi >= lo ? _hand.Evaluate(HandS, HandMm) : null;
                if (HandStatus != null && HandStatus.Problem == null && hi < lo) HandStatus = null;
            }
            ShowGhost();
            bool used = false;
            bool ok = HandStatus != null && HandStatus.Ok;
            if ((input.Click && !input.OverPanel || input.Pressed(ToolKeys.Next)) && ok)
            {
                Place(below);
                used = true;
            }
            return used || input.Click && !input.OverPanel;   // a click on red keeps the tower in hand, and is the editor's
        }

        void ShowGhost()
        {
            var ghost = _ctx.TowerGhost?.Invoke();
            if (ghost == null) return;
            if (HandStatus == null) { ghost.Hide(); return; }
            var xz = At(HandS);
            float y = HandStatus.BaseY != 0 ? (float)HandStatus.BaseY : _ctx.HeightAt(xz);
            var up = new Vector3(Dir.x, 0, Dir.y);
            ghost.Show(HandStatus.Head, new Vector3(xz.x, y, xz.y), up, (float)HandStatus.Lean, HandMm / 1000f, HandStatus.Ok ? GhostTint.Ok : GhostTint.Bad, Type.TowerKit);
        }

        void Place(int below)
        {
            int along = (int)Math.Round(HandS * 1000 / DesignUnits.FiveFeetMm) * DesignUnits.FiveFeetMm, h = HandMm;
            Change?.Invoke(list => list.Insert(Math.Min(below, list.Count), new Tower(default, along, h)));
            _hand = null;
            _statusKey = long.MinValue;
            if (KeepAdding)
            {
                _lastPointer = new Vector2(float.NaN, float.NaN);   // the next one starts past it, wherever the pointer is
                HandS = along / 1000.0 + Gap;
            }
            else
            {
                StopAdding();
                Select(below);
            }
        }

        // ---------- changes to a tower ----------

        void SetAlong(int i, double s, bool record)
        {
            double lo = i > 0 ? Towers[i - 1].AlongMm / 1000.0 + Gap : Gap;
            double hi = i < Towers.Count - 1 ? Towers[i + 1].AlongMm / 1000.0 - Gap : Length - Gap;
            s = Math.Max(lo, Math.Min(hi, s));
            int mm = (int)Math.Round(s * 1000 / DesignUnits.FiveFeetMm) * DesignUnits.FiveFeetMm;
            if (mm == Towers[i].AlongMm) return;
            Action<List<Tower>> set = list => list[i] = new Tower(list[i].Id, mm, list[i].HeightMm);
            if (record) Change?.Invoke(set);
            else Dragged?.Invoke(set);
        }

        void Height(int i, int steps)
        {
            int max = LiftCatalogIds.TowerMaxMm(Type.CatalogId);
            int h = Mathf.Clamp(Towers[i].HeightMm + steps * DesignUnits.FiveFeetMm, DesignUnits.StationTowerMinMm, max);
            if (h == Towers[i].HeightMm) return;
            Change?.Invoke(list => list[i] = new Tower(list[i].Id, list[i].AlongMm, h));
        }

        void Remove(int i)
        {
            Change?.Invoke(list => list.RemoveAt(i));
            Select(None);
        }

        // ---------- where things are ----------

        double Project(in ToolInput input) => Vector2.Dot(new Vector2(input.Ground.x, input.Ground.z) - BottomXz, Dir);

        // A click on the line itself (within a few pixels of it, between the terminals' fronts): where a tower would go.
        bool OnLine(in ToolInput input, out double s)
        {
            var p = new Vector2(input.Ground.x, input.Ground.z);
            s = Vector2.Dot(p - BottomXz, Dir);
            float reach = Mathf.Max(4f, PickPixels * input.MetresPerPixel);
            float side = Mathf.Abs(Vector2.Dot(p - BottomXz, new Vector2(-Dir.y, Dir.x)));
            return side <= reach && s > FirstSlot() - Slot && s < Length - Gap + Slot;
        }

        int Nearest(Vector2 p, float reach)
        {
            int best = None;
            float bestD = reach;
            if (Terminals)
            {
                if (Vector2.Distance(p, BottomXz) < bestD) { bestD = Vector2.Distance(p, BottomXz); best = Bottom; }
                if (Vector2.Distance(p, TopXz) < bestD) { bestD = Vector2.Distance(p, TopXz); best = Top; }
            }
            for (int i = 0; i < Towers.Count; i++)
            {
                float d = Vector2.Distance(p, At(Towers[i].AlongMm / 1000.0));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        int Below(double s)
        {
            int n = 0;
            while (n < Towers.Count && Towers[n].AlongMm / 1000.0 < s) n++;
            return n;
        }

        // The first slot a tower may stand on: past the bottom terminal's front, as TW-03 v2's station tower.
        double FirstSlot() => Math.Ceiling((Type.Bottom.Ahead + DesignUnits.StationPastFrontMm / 1000.0) / Slot - 1e-9) * Slot;

        double WidestGapMiddle()
        {
            double from = FirstSlot() - Gap, best = -1, at = Length / 2;
            for (int i = 0; i <= Towers.Count; i++)
            {
                double to = i < Towers.Count ? Towers[i].AlongMm / 1000.0 : Length;
                if (to - from > best) { best = to - from; at = (from + to) / 2; }
                from = i < Towers.Count ? Towers[i].AlongMm / 1000.0 : from;
            }
            return Math.Round(at / Slot) * Slot;
        }

        // [ and ]: from the bottom terminal through the towers to the top terminal and round.
        void Step(int by)
        {
            int n = Towers.Count + (Terminals ? 2 : 0);
            if (n == 0) return;
            int off = Terminals ? 1 : 0;
            int at = Selected == Bottom ? 0 : Selected >= 0 ? Selected + off : Selected == Top ? n - 1 : (by > 0 ? -1 : n);
            at = ((at + by) % n + n) % n;
            Select(Terminals && at == 0 ? Bottom : Terminals && at == n - 1 ? Top : at - off);
        }

        // ---------- the window ----------

        /// <summary>The window's line about the selection or the tower in hand (the host shows it as its hint).</summary>
        public string Describe()
        {
            if (Adding)
            {
                string h = ToolFormat.Feet(Mathf.RoundToInt(HandMm / 304.8f));
                string where = $"Tower in hand: {h} at {ToolFormat.Length(HandS)} up the line. Click (or Enter) to place it where it's green; Esc puts it down.";
                if (HandStatus != null && HandStatus.Ok)
                    where += $"\n{HeadName(HandStatus.Head)} for now · lowest chairs {ToolFormat.Length(HandStatus.LowestClearance)}";
                return where;
            }
            if (Selected >= 0 && Selected < Towers.Count)
            {
                var t = Towers[Selected];
                string head = Rope != null && Rope.Towers.Count == Towers.Count ? " · " + HeadName(Rope.Towers[Selected].Head) : "";
                return $"Tower {Selected + 1}: ‹ {ToolFormat.Feet(Mathf.RoundToInt(t.HeightMm / 304.8f))} › at {ToolFormat.Length(t.AlongMm / 1000.0)} up the line{head}. " +
                       "Page Up / Page Down change its height; drag or slide it; Delete takes it out.";
            }
            if (Selected == Bottom || Selected == Top)
                return $"{(Selected == Bottom ? "Bottom" : "Top")} terminal{(Level != null ? ": Page Up / Page Down set its level." : ".")}";
            return null;
        }

        /// <summary>The selected tower's (or the hand's) loads for the window's gauge (TW-11), or null.</summary>
        public TowerForces.Loads? Loads =>
            Adding ? HandStatus?.Loads
            : Selected >= 0 && Rope != null && Rope.Towers.Count == Towers.Count && Selected < Towers.Count ? Rope.Towers[Selected].Loads : (TowerForces.Loads?)null;

        /// <summary>The red reason or amber warning for the tower in hand, or null.</summary>
        public LiftProblem? HandProblem => Adding && HandStatus != null ? HandStatus.Problem ?? HandStatus.Warning : null;

        public static string HeadName(LiftRope.Head head) => head switch
        {
            LiftRope.Head.S4 => "Support, 4 sheaves",
            LiftRope.Head.S6 => "Support, 6 sheaves",
            LiftRope.Head.B8 => "Breakover, 8 sheaves",
            LiftRope.Head.D8 => "Hold-down, 8 sheaves",
            LiftRope.Head.C8 => "Combination, 4 + 4 sheaves",
            LiftRope.Head.S10 => "Support, 10 sheaves",
            LiftRope.Head.B10 => "Breakover, 10 sheaves",
            LiftRope.Head.D10 => "Hold-down, 10 sheaves",
            _ => "Too sharp for one head",
        };
    }
}
