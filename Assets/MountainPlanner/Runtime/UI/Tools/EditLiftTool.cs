using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Toolbox › Lifts › Edit (task 09, LT2, LT9; the approved mockup, #92). Point at a built lift (it lights) and click: it
    /// opens as a draft in the lift window, drawn in 3D in the built lift's place (F4, LT38). Click a tower or a terminal
    /// (what the pointer is on in 3D, F1), or step through them with [ and ] (<see cref="LiftDraftEditor"/>, LT37):
    /// - a tower slides along the line (, and ., 5 ft, Shift 50 ft; or drag it), changes height (Page Up / Page Down,
    ///   5 ft), or goes (Delete); a click on the line where no tower is, Insert or Add tower adds one (F2);
    /// - a terminal moves anywhere (drag it: the line re-aims through the other one) or changes level (Page Up / Page Down,
    ///   1 ft, SK14);
    /// - the type, the tower mode and the stance change in the menu; Revert to auto places every tower again (F3, asking
    ///   first); the name in the title renames it (F5).
    /// In Automatic, a change to the type, a terminal or a level re-places the towers; By hand keeps them, each its distance
    /// from the bottom terminal (LF-05 v2). Every change is a step of the tool's own undo (Ctrl+Z); Apply (Ctrl+Enter)
    /// commits all of it as one <see cref="EditLift"/> with its earthwork rebuilt, so one Ctrl+Z later undoes the whole edit.
    /// A red lift can't be applied, as it can't break ground (LF-04). Cancel (or Esc) drops the draft, asking first.
    /// </summary>
    public sealed class EditLiftTool : IDesignTool, IPicksLifts
    {
        public const string ToolId = "edit-lift";
        const double Slot = 1.524;   // 5 ft
        const float PickPixels = 12;
        const int SlotBase = 8, SelectedSlot = 98, BottomRing = 0, TopRing = 3;
        const int LevelMinFt = -10, LevelMaxFt = 30;

        // The draft: everything Apply would write. A snapshot of it is one step of the tool's undo.
        sealed class Draft
        {
            public string CatalogId, Name;
            public PointMm Bottom, Top;
            public TowerMode Mode;
            public TowerStance Stance;
            public List<Tower> Towers = new List<Tower>();   // an added tower's id is 0
            public LiftLevels? Levels;
            public bool Replace;                              // Automatic: the towers are placed again at the next analysis
            public Draft Copy() => new Draft
            {
                CatalogId = CatalogId, Name = Name, Bottom = Bottom, Top = Top, Mode = Mode, Stance = Stance, Towers = new List<Tower>(Towers),
                Levels = Levels, Replace = Replace,
            };
        }

        ToolContext _ctx;
        Lift _lift;                 // the lift as it stands in the design
        Draft _draft;
        readonly List<Draft> _undo = new List<Draft>();
        LiftDraftEditor _editor;   // F1, F2: the towers and terminals (LT37)
        long _editorKey = long.MinValue;
        bool _terminalDrag, _armCancel, _armReplace;
        LiftTypeMenu _typeMenu;
        ToolDropdown _modeDd, _stanceDd;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _line = new Vector2[2];
        int _drawnTowers;

        // The analysis: the ground without this lift (worked out once per opening), then the draft built on it.
        EditedHeights _without;
        bool _groundWorking;
        LiftBuild.Result _built;
        TerrainEdits _applyEdits;
        bool _building;
        int _analysed = -1, _version;
        string _problem;

        public string Id => ToolId;
        public string Name => "Edit lift";
        public bool HasWork => _undo.Count > 0;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            var w = context.Window;
            _editor = new LiftDraftEditor(context)
            {
                Change = change => Change(d => { change(d.Towers); ByHand(d); }),
                DragBegan = () => _undo.Add(_draft.Copy()),
                Dragged = change => { change(_draft.Towers); ByHand(_draft); Refresh(); },
                DragEnded = DragEnded,
                Level = Level,
                HandFor = HandFor,
            };
            _typeMenu = new LiftTypeMenu(w, LiftCatalogIds.SesselliftFgq4, () => Change(d => { d.CatalogId = _typeMenu.CatalogId; d.Replace = d.Mode == TowerMode.Automatic; }),
                                         "SetLiftType: in Automatic the towers re-place for the new type");
            (_modeDd, _stanceDd) = w.AddDropdownPair("towers", "Towers", "mode", PickMode, "stance", PickStance,
                "LF-01: to By hand keeps the towers; to Automatic re-places them, asking first if you placed any", "TW-14: plumb, or leaned square to the rope (15° at most)");
            _modeDd.Set(new[] { "Automatic", "By hand" }, 0);
            _stanceDd.Set(new[] { "Plumb", "Leaned" }, 0);
            _typeMenu.AddNote();
            w.SetSteps(System.Array.Empty<string>());
            w.SetCommitText("Apply", "Cancel");
            w.SetPlanAlwaysLive(true);
            Picking();
        }

        // ---------- picking a lift ----------

        void Picking()
        {
            _lift = null;
            _draft = null;
            _undo.Clear();
            _editor.Release();
            _terminalDrag = false;
            _ctx.ShowDraftLift?.Invoke(null);
            _ctx.HideBuiltLift?.Invoke(0);
            _armCancel = _armReplace = false;
            _without = null;
            _built = null;
            _applyEdits = null;
            _rebuild = null;
            _ctx.PendingTerrain = null;
            _ctx.HoldLift?.Invoke(0);
            _ctx.Preview.HideFrom(0);
            _drawnTowers = 0;
            var w = _ctx.Window;
            w.SetName(null);
            w.SetRenamable(null);
            w.SetAsking(null);
            w.SetHint("Point at a lift to light it, then click it to edit its towers, terminals and type.");
            w.SetMessage(null);
            w.SetKeys();
            w.SetButtons();
            w.SetNext(false, false);
            w.ShowReview(null);
            w.SetCommit(false, false);
            w.HideStrip();
            w.ShowGauge(null);
        }

        void Open(Lift lift)
        {
            _lift = lift;
            _draft = new Draft
            {
                CatalogId = lift.CatalogId, Name = lift.Name, Bottom = lift.Bottom, Top = lift.Top, Mode = lift.TowerMode, Stance = lift.Stance,
                Towers = new List<Tower>(lift.Towers), Levels = lift.Levels,
            };
            _undo.Clear();
            _editor.Release();
            _ctx.HoldLift?.Invoke(lift.Id.Value);
            _ctx.HideBuiltLift?.Invoke(lift.Id.Value);   // F4: its draft stands in its place until Apply or Cancel
            _typeMenu.Set(_draft.CatalogId);
            _modeDd.Set(new[] { "Automatic", "By hand" }, _draft.Mode == TowerMode.Automatic ? 0 : 1);
            _stanceDd.Set(new[] { "Plumb", "Leaned" }, _draft.Stance == TowerStance.Plumb ? 0 : 1);
            _ctx.Window.SetName(lift.Name);
            // F5: renaming is a change of the draft, applied with the rest in Apply's one step.
            _ctx.Window.SetRenamable(n =>
            {
                if (DesignRules.NameProblem(n) is string bad) return bad;
                if (n != _draft?.Name) Change(d => d.Name = n);
                return null;
            });
            _ctx.Claim(GameAction.UndoStroke, true);      // Ctrl+Z: the edit's own undo
            _ctx.Window.ShowStrip(null, "Working out the lift…");
            SyncEditor();
            _editor.Open();
            WorkOutGround();
            Changed();
        }

        // The editor over the draft as it stands.
        void SyncEditor()
        {
            if (_draft == null) return;
            _editor.BottomXz = BottomXz;
            _editor.TopXz = TopXz;
            _editor.Type = LiftType.Of(_draft.CatalogId);
            _editor.Stance = _draft.Stance;
            _editor.Towers = _draft.Towers;
            _editor.Rope = _built != null && _built.Towers.Count == _draft.Towers.Count ? _built.Rope : null;
        }

        // The tower in hand's judge (TW-13) over the towers below it, on the ground without this lift.
        TowerInHand HandFor(IReadOnlyList<Tower> below)
        {
            if (_draft == null || _without == null) return null;
            var type = LiftType.Of(_draft.CatalogId);
            var b = _ctx.ToGround(_draft.Bottom);
            var t = _ctx.ToGround(_draft.Top);
            var levels = _draft.Levels ?? LiftBuild.GroundLevels(type, _without, b.X, b.Y, t.X, t.Y);
            return new TowerInHand(type, _without, b.X, b.Y, t.X, t.Y, levels, below, _draft.Stance);
        }

        void DragEnded()
        {
            if (_undo.Count > 0 && Same(_undo[_undo.Count - 1], _draft)) _undo.RemoveAt(_undo.Count - 1);   // a click, no move
            else Changed();
        }

        // The design's earthwork fold (TL3, P3-08's), when the area's session has one; null in tests without it.
        DesignEarthwork Fold => _ctx.Earthwork != null && _ctx.Earthwork.Ready ? _ctx.Earthwork.Earthwork : null;
        DesignEarthwork.Result _rebuild;   // Apply's whole earthwork, accepted by the fold once the command is in

        // The ground without this lift: the design's earthwork folded without it (trails and the other lifts kept, TL3), or
        // with no fold every other built lift replayed (LT24): what the draft builds on.
        void WorkOutGround()
        {
            var ground = _ctx.EditedGround?.Invoke();
            if (ground == null || _ctx.ToGround == null) { _without = null; return; }
            _groundWorking = true;
            var doc = _ctx.History.Document;
            var committed = _ctx.History.Terrain;
            var id = _lift.Id;
            var toGround = _ctx.ToGround;
            var fold = Fold;
            long revision = _ctx.History.Revision;
            _ctx.Analyse(() =>
            {
                if (fold == null) return new EditedHeights(ground.Source, ground.Lattice, LiftReplay.Rebuild(ground.Source, ground.Lattice, doc, p => toGround(p), id));
                var gone = new DeleteLift(revision, id).Apply(doc);
                return new EditedHeights(ground.Source, ground.Lattice, fold.Rebuild(gone.Document, committed).Edits);
            }, g =>
            {
                _groundWorking = false;
                if (_lift == null || _lift.Id.Value != id.Value) return;
                _without = g;
                _analysed = -1;
                Analyse();
            });
        }

        // ---------- changes ----------

        void Change(System.Action<Draft> change)
        {
            if (_draft == null) return;
            _undo.Add(_draft.Copy());
            if (_undo.Count > 100) _undo.RemoveAt(0);
            change(_draft);
            _armCancel = _armReplace = false;
            _ctx.Window.SetAsking(null);
            Changed();
        }

        void Changed()
        {
            _version++;
            _built = null;
            _applyEdits = null;
            _rebuild = null;
            _ctx.PendingTerrain = null;
            Analyse();
            Refresh();
        }

        void Undo()
        {
            if (_undo.Count == 0) return;
            _draft = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _ctx.Window.SetName(_draft.Name);
            _typeMenu.Set(_draft.CatalogId);
            _modeDd.Set(new[] { "Automatic", "By hand" }, _draft.Mode == TowerMode.Automatic ? 0 : 1);
            _stanceDd.Set(new[] { "Plumb", "Leaned" }, _draft.Stance == TowerStance.Plumb ? 0 : 1);
            SyncEditor();
            _editor.TowersChanged();
            Changed();
        }

        void PickMode(int i)
        {
            var mode = i == 0 ? TowerMode.Automatic : TowerMode.ByHand;
            if (_draft == null || mode == _draft.Mode) { _modeDd.Set(new[] { "Automatic", "By hand" }, i); return; }
            Change(d => { d.Mode = mode; d.Replace = mode == TowerMode.Automatic; });
            _modeDd.Set(new[] { "Automatic", "By hand" }, i);
        }

        void PickStance(int i)
        {
            var stance = i == 0 ? TowerStance.Plumb : TowerStance.Leaned;
            _stanceDd.Set(new[] { "Plumb", "Leaned" }, i);
            if (_draft == null || stance == _draft.Stance) return;
            Change(d => d.Stance = stance);
        }

        // F3: Revert to auto places every tower automatically again and sets the lift back to Automatic, asking first where
        // towers were placed or changed by hand (LF-01); one step of the tool's undo.
        void RevertToAuto()
        {
            if (_draft == null) return;
            if (_draft.Mode == TowerMode.ByHand && !_armReplace) { _armReplace = true; Refresh(); return; }
            _editor.Release();
            _editor.Open();
            Change(d => { d.Mode = TowerMode.Automatic; d.Replace = true; });
            _modeDd.Set(new[] { "Automatic", "By hand" }, 0);
        }

        // ---------- the analysis ----------

        void Analyse()
        {
            if (_draft == null || _analysed == _version) return;
            if (_without == null) { if (!_groundWorking) WorkOutGround(); return; }
            _analysed = _version;
            _building = true;
            var d = _draft.Copy();
            var ground = _without;
            var b = _ctx.ToGround(d.Bottom);
            var t = _ctx.ToGround(d.Top);
            var type = LiftType.Of(d.CatalogId);
            var doc = _ctx.History.Document;
            var committed = _ctx.History.Terrain;
            long revision = _ctx.History.Revision;
            var fold = Fold;
            var lift = _lift;
            var toGround = _ctx.ToGround;
            int version = _version;
            _ctx.Analyse(() =>
            {
                // Automatic with Replace: TW-12's search, the terminals where they were set (as offsets over this ground).
                LiftBuild.Result r;
                if (d.Replace || d.Towers.Count == 0)
                {
                    var start = LiftBuild.GroundLevels(type, ground, b.X, b.Y, t.X, t.Y);
                    double bh = d.Levels is LiftLevels lv ? lv.BottomMetres - start.BottomMetres : 0, th = d.Levels is LiftLevels lv2 ? lv2.TopMetres - start.TopMetres : 0;
                    r = LiftBuild.Build(type, ground, b.X, b.Y, t.X, t.Y, d.Stance, null, bh, th);
                }
                else
                {
                    var specs = new List<TowerSpec>(d.Towers.Count);
                    foreach (var tw in d.Towers) specs.Add(new TowerSpec(tw.AlongMm, tw.HeightMm));
                    var levels = d.Levels ?? LiftBuild.GroundLevels(type, ground, b.X, b.Y, t.X, t.Y);
                    r = LiftBuild.BuildWith(type, ground, b.X, b.Y, t.X, t.Y, specs, d.Stance, levels);
                }
                // Apply's whole earthwork: the fold with the edited lift in its place (trails, then lifts, TL3), or with no
                // fold the built lifts replayed (LT24).
                if (fold != null)
                {
                    bool replaced = d.Replace || d.Towers.Count == 0;
                    var cmd = Command(revision, lift, d, replaced ? null : d.Towers, replaced ? r.Towers : null, r.Levels);
                    var after = cmd.Apply(doc);
                    if (after.Accepted)
                    {
                        var rebuilt = fold.Rebuild(after.Document, committed);
                        return (r, rebuilt.Edits, rebuilt);
                    }
                }
                var edited = new Lift(lift.Id, lift.Name, lift.Stage, d.CatalogId, d.Bottom, d.Top, d.Mode, d.Stance, Stored(d, r), r.Levels);
                var edits = LiftReplay.Rebuild(ground.Source, ground.Lattice, doc, p => toGround(p), default, edited);
                return (r, edits, (DesignEarthwork.Result)null);
            }, done =>
            {
                _building = false;
                if (_draft == null || version != _version) return;
                _built = done.r;
                _applyEdits = done.Item2;
                _rebuild = done.Item3;
                // A re-place's towers become the draft's (new ids), and its levels; By hand they were the draft's already.
                if (_draft.Replace || _draft.Towers.Count == 0)
                {
                    _draft.Towers.Clear();
                    foreach (var tw in done.r.Towers) _draft.Towers.Add(new Tower(default, tw.AlongMm, tw.HeightMm));
                    _draft.Replace = false;
                }
                _draft.Levels = done.r.Levels;
                _ctx.PendingTerrain = _applyEdits;
                // F4: the draft in 3D, as Apply would build it.
                _ctx.ShowDraftLift?.Invoke(new Lift(_lift.Id, _draft.Name, WorkStage.Built, _draft.CatalogId, _draft.Bottom, _draft.Top, _draft.Mode, _draft.Stance,
                                                    Stored(_draft, done.r), done.r.Levels));
                SyncEditor();
                _editor.TowersChanged();
                _editor.Relight();
                var b = _ctx.ToGround(_draft.Bottom);
                var t = _ctx.ToGround(_draft.Top);
                _ctx.Window.ShowStrip(LiftProfile.From(done.r.Rope, new EditedHeights(_without.Source, _without.Lattice, done.r.Edits), b.X, b.Y, t.X, t.Y, done.r.Levels));
                Refresh();
            });
        }

        // The towers as the command keeps them: the draft's, or a re-place's (all new).
        static List<Tower> Stored(Draft d, LiftBuild.Result r)
        {
            var list = new List<Tower>();
            if (d.Replace || d.Towers.Count == 0) for (int i = 0; i < r.Towers.Count; i++) list.Add(new Tower(TowerId.Make(7, i), r.Towers[i].AlongMm, r.Towers[i].HeightMm));
            else for (int i = 0; i < d.Towers.Count; i++) list.Add(new Tower(d.Towers[i].Id.Value != 0 ? d.Towers[i].Id : TowerId.Make(7, i), d.Towers[i].AlongMm, d.Towers[i].HeightMm));
            return list;
        }

        // ---------- each frame ----------

        public void Tick(in ToolInput input)
        {
            var doc = _ctx.History.Document;
            if (_lift != null && doc.FindLift(_lift.Id) == null) { Picking(); return; }   // undone or removed meanwhile
            if (_draft == null)
            {
                if (input.Click && !input.OverPanel)
                {
                    ulong under = _ctx.LiftUnderPointer?.Invoke() ?? 0;
                    var lift = under != 0 ? doc.FindLift(new LiftId(under)) : null;
                    if (lift != null && lift.Stage == WorkStage.Built && lift.Towers.Count > 0) Open(lift);
                }
                return;
            }
            if (input.Pressed(ToolKeys.UndoStroke)) Undo();
            SyncEditor();
            _editor.Tick(input);
            // A terminal moves anywhere: pressed on, then dragged (the press is the step).
            bool terminal = _editor.Selected == LiftDraftEditor.Bottom || _editor.Selected == LiftDraftEditor.Top;
            if (input.Click && !input.OverPanel && terminal)
            {
                _terminalDrag = true;
                _undo.Add(_draft.Copy());
            }
            else if (input.Drag && _terminalDrag && input.HasGround)
                MoveTerminal(_editor.Selected == LiftDraftEditor.Bottom, input.Point, new Vector2(input.Ground.x, input.Ground.z));
            if (input.Release && _terminalDrag)
            {
                _terminalDrag = false;
                DragEnded();
            }
            if (_editor.StateKey != _editorKey) { _editorKey = _editor.StateKey; Refresh(); }
            Draw();
        }

        static bool Same(Draft a, Draft b)
        {
            if (a.Name != b.Name || a.Bottom.X != b.Bottom.X || a.Bottom.Y != b.Bottom.Y || a.Top.X != b.Top.X || a.Top.Y != b.Top.Y || a.Towers.Count != b.Towers.Count) return false;
            for (int i = 0; i < a.Towers.Count; i++) if (a.Towers[i].AlongMm != b.Towers[i].AlongMm || a.Towers[i].HeightMm != b.Towers[i].HeightMm) return false;
            return true;
        }

        Vector2 BottomXz => DesignFrame.ToLocal(_draft.Bottom);
        Vector2 TopXz => DesignFrame.ToLocal(_draft.Top);
        Vector2 Dir => (TopXz - BottomXz).normalized;
        float Length => Vector2.Distance(BottomXz, TopXz);
        Vector2 TowerXz(int i) => BottomXz + Dir * (_draft.Towers[i].AlongMm / 1000f);
        Vector2 AlongXz(double s) => BottomXz + Dir * (float)s;

        void ByHand(Draft d)
        {
            if (d.Mode == TowerMode.ByHand) return;
            d.Mode = TowerMode.ByHand;
            _modeDd.Set(new[] { "Automatic", "By hand" }, 1);
        }

        // SK14: the selected terminal's level, 1 ft at a time, within −10 to +30 ft of the ground at its origin.
        void Level(bool bottom, int feet)
        {
            if (_draft == null || !(_draft.Levels is LiftLevels lv) || _without == null) return;
            var b = _ctx.ToGround(_draft.Bottom);
            var t = _ctx.ToGround(_draft.Top);
            var ground = LiftBuild.GroundLevels(LiftType.Of(_draft.CatalogId), _without, b.X, b.Y, t.X, t.Y);
            int stepMm = DesignUnits.FeetToMm(1) * feet;
            int now = bottom ? lv.BottomMm : lv.TopMm;
            int groundMm = bottom ? ground.BottomMm : ground.TopMm;
            int next = Mathf.Clamp(now + stepMm, groundMm + DesignUnits.FeetToMm(LevelMinFt), groundMm + DesignUnits.FeetToMm(LevelMaxFt));
            if (next == now) return;
            Change(d =>
            {
                d.Levels = bottom ? new LiftLevels(next, lv.TopMm) : new LiftLevels(lv.BottomMm, next);
                d.Replace = d.Mode == TowerMode.Automatic;
            });
        }

        // LF-05 v2: a terminal moves anywhere; the line re-aims through the other one. By hand, towers keep their distance
        // from the bottom terminal and any past the new top go (with a message); Automatic re-places them.
        void MoveTerminal(bool bottom, PointMm point, Vector2 at)
        {
            var other = bottom ? TopXz : BottomXz;
            string bad = !_ctx.InCore(at) ? "A terminal must stand inside the area"
                : Vector2.Distance(at, other) < 91.44f ? "The terminals stand at least 300 ft apart"
                : (bottom ? _ctx.HeightAt(at) >= _ctx.HeightAt(other) : _ctx.HeightAt(at) <= _ctx.HeightAt(other)) ? "The top terminal must be higher" : null;
            _problem = bad;
            if (bad != null) { _ctx.Window.SetMessage(bad); return; }
            if (bottom) _draft.Bottom = point; else _draft.Top = point;
            _draft.Levels = null;   // the terminals stand at their new ground (their levels are worked out again)
            if (_draft.Mode == TowerMode.Automatic) _draft.Replace = true;
            else
            {
                double last = Length - DesignUnits.TowerGapMm / 1000.0;
                int dropped = _draft.Towers.RemoveAll(tw => tw.AlongMm / 1000.0 > last);
                if (dropped > 0) _ctx.Window.SetMessage($"{dropped} tower{(dropped > 1 ? "s" : "")} past the top terminal went", true);
            }
            Refresh();
        }

        // ---------- the window and the map ----------

        void Refresh()
        {
            if (_draft == null) return;
            var w = _ctx.Window;
            int changes = _undo.Count;
            bool blocked = _built != null && !_built.Ok;
            string line = _editor.Describe();
            w.ShowGauge(_editor.Loads, provisional: _editor.Adding);
            if (_editor.Adding)
            {
                w.SetHint(line);
                w.SetKeys((GameAction.TowerUp, "taller"), (GameAction.TowerDown, "shorter"), (GameAction.TowerSlideBack, "slide"),
                          (GameAction.TowerSlideOn, "slide"), (GameAction.ToolNext, "place"));
            }
            else if (_editor.Selected >= 0)
            {
                w.SetHint(line);
                w.SetKeys((GameAction.TowerSlideBack, "slide"), (GameAction.TowerSlideOn, "slide"), (GameAction.TowerUp, "taller"),
                          (GameAction.TowerDown, "shorter"), (GameAction.DeleteSelected, "remove"), (GameAction.BrushWider, "next"));
            }
            else if (_editor.Selected == LiftDraftEditor.Bottom || _editor.Selected == LiftDraftEditor.Top)
            {
                bool bottom = _editor.Selected == LiftDraftEditor.Bottom;
                string level = _draft.Levels is LiftLevels lv ? ToolFormat.Length(bottom ? lv.BottomMetres : lv.TopMetres) : "at its ground";
                w.SetHint($"{(bottom ? "Bottom" : "Top")} terminal: level {level}. Drag it anywhere: the line re-aims through the other one.");
                w.SetKeys((GameAction.TerminalLevelUp, "higher"), (GameAction.TerminalLevelDown, "lower"), (GameAction.BrushWider, "next"));
            }
            else
            {
                w.SetHint("Click a tower or a terminal, or step through them with [ and ]. A click on the line where there's no tower adds one.");
                w.SetKeys((GameAction.BrushNarrower, "previous"), (GameAction.BrushWider, "next"), (GameAction.TowerAdd, "add a tower"), (GameAction.UndoStroke, "undo"));
            }
            if (_editor.HandProblem is LiftProblem hp) { w.SetMessage(hp.Message, !hp.Blocks); BuildReview(changes, blocked); w.ShowReview(_review); Buttons(changes); return; }
            if (_problem == null) w.SetMessage(null);
            BuildReview(changes, blocked);
            w.ShowReview(_review);
            Buttons(changes);
        }

        void Buttons(int changes)
        {
            var w = _ctx.Window;
            bool revertable = _draft.Mode == TowerMode.ByHand;
            w.SetButtons(
                ("add", "Add tower", _editor.Adding ? null : (System.Action)(() => { _editor.StartAdding(); Refresh(); })),
                ("revert", _armReplace ? "Again: revert yours" : "Revert to auto", revertable ? (System.Action)RevertToAuto : null),
                ("undo", "Undo", changes > 0 ? (System.Action)Undo : null));
            w.SetAsking(_armCancel ? "Discard the changes? Cancel again" : _armReplace ? "Your towers go: Revert to auto again" : null);
        }

        void BuildReview(int changes, bool blocked)
        {
            _review.Clear();
            float run = Length;
            _review.Add("Changes", changes == 0 ? "none yet" : $"{changes}, not applied yet", "LT2: a draft inside the tool until Apply");
            if (_built == null)
            {
                _review.Add("Towers", _building || _groundWorking ? "working out…" : $"{_draft.Towers.Count}", "TW-12").Ready(false, true);
                return;
            }
            var rope = _built.Rope;
            _review.Add("Length", $"{ToolFormat.Length(run)} across", "LF-06")
                .Add("Towers", $"{_built.Towers.Count}", "TW-02, TW-12")
                .Add("Lowest chairs", $"{ToolFormat.Length(rope.LowestClearance)} over the ground", "TW-01: 15 ft at least");
            foreach (var p in _built.Problems) _review.Warn(p.Message, p.Rule, p.Blocks);
            _review.Ready(changes > 0 && !blocked && _applyEdits != null, true);
        }

        void Draw()
        {
            var p = _ctx.Preview;
            _line[0] = BottomXz;
            _line[1] = TopXz;
            p.Line(2, _line, 2, PreviewInk.Line);
            p.Ring(BottomRing, BottomXz, 8, _editor.Selected == LiftDraftEditor.Bottom ? PreviewInk.Ok : PreviewInk.Line);
            p.Dot(1, BottomXz, PreviewInk.Line);
            p.Ring(TopRing, TopXz, 8, _editor.Selected == LiftDraftEditor.Top ? PreviewInk.Ok : PreviewInk.Line);
            p.Dot(4, TopXz, PreviewInk.Line);
            int n = _draft.Towers.Count;
            for (int i = 0; i < n; i++) p.Dot(SlotBase + i, TowerXz(i), PreviewInk.Lift);
            for (int i = n; i < _drawnTowers; i++) p.Hide(SlotBase + i);
            _drawnTowers = n;
            if (_editor.Selected >= 0 && _editor.Selected < n) p.Ring(SelectedSlot, TowerXz(_editor.Selected), 6, PreviewInk.Ok);
            else if (_editor.Adding && _editor.HandStatus != null) p.Ring(SelectedSlot, AlongXz(_editor.HandS), 6, _editor.HandStatus.Ok ? PreviewInk.Ok : PreviewInk.Bad);
            else p.Hide(SelectedSlot);
        }

        // ---------- commit ----------

        public ToolBackOut Escape()
        {
            if (_draft == null) return ToolBackOut.Close;
            if (_editor.Escape()) { Refresh(); return ToolBackOut.Stepped; }
            Cancel();
            return ToolBackOut.Stepped;
        }

        // Cancel asks once when there are changes, then drops the draft; the tool stays out to pick another lift.
        void Cancel()
        {
            if (_undo.Count > 0 && !_armCancel) { _armCancel = true; Refresh(); return; }
            if (_undo.Count > 0) _ctx.Notice?.Invoke($"Changes to {_lift.Name} discarded");
            Picking();
        }

        /// <summary>Apply (Break ground's button and key, Ctrl+Enter); the second button is Cancel.</summary>
        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (_draft == null) return null;
            if (stage == WorkStage.Plan) { Cancel(); return null; }
            if (_undo.Count == 0 || _built == null || !_built.Ok || _applyEdits == null) return null;
            var kept = new List<Tower>();
            var added = new List<TowerSpec>();
            foreach (var tw in _draft.Towers)
                if (tw.Id.Value != 0 && _lift.IndexOfTower(tw.Id) >= 0) kept.Add(tw);
                else added.Add(new TowerSpec(tw.AlongMm, tw.HeightMm));
            bool moved = _draft.Bottom.X != _lift.Bottom.X || _draft.Bottom.Y != _lift.Bottom.Y || _draft.Top.X != _lift.Top.X || _draft.Top.Y != _lift.Top.Y;
            var kind = moved ? LiftEdit.MoveTerminal : _draft.CatalogId != _lift.CatalogId ? LiftEdit.ChangeType : LiftEdit.Edit;
            return new EditLift(baseRevision, _lift.Id, _draft.CatalogId, _draft.Bottom, _draft.Top, _draft.Mode, _draft.Stance, kept, added, _draft.Levels, kind,
                                _draft.Name != _lift.Name ? _draft.Name : null);
        }

        // The command a draft makes: towers that are the lift's own keep their IDs; the rest (or a re-place's) are new.
        static EditLift Command(long revision, Lift lift, Draft d, List<Tower> towers, List<TowerSpec> placed, LiftLevels levels)
        {
            var kept = new List<Tower>();
            var added = new List<TowerSpec>();
            if (towers != null)
                foreach (var tw in towers)
                    if (tw.Id.Value != 0 && lift.IndexOfTower(tw.Id) >= 0) kept.Add(tw);
                    else added.Add(new TowerSpec(tw.AlongMm, tw.HeightMm));
            if (placed != null) added.AddRange(placed);
            return new EditLift(revision, lift.Id, d.CatalogId, d.Bottom, d.Top, d.Mode, d.Stance, kept, added, levels, LiftEdit.Edit, d.Name != lift.Name ? d.Name : null);
        }

        public void Committed(CommandResult result)
        {
            // The fold takes Apply's earthwork as its own once it's the history's (as the trail tools do).
            if (_rebuild != null && _ctx.History.Terrain == _rebuild.Edits) _ctx.Earthwork?.Earthwork?.Accept(_rebuild);
            _rebuild = null;
            _ctx.Notice?.Invoke($"{_draft?.Name ?? _lift.Name}: {_undo.Count} change{(_undo.Count == 1 ? "" : "s")} applied · Ctrl+Z undoes the edit");
            Picking();
        }

        public void Disarm()
        {
            _editor?.Release();
            _ctx?.ShowDraftLift?.Invoke(null);
            _ctx?.HideBuiltLift?.Invoke(0);
            _ctx?.HoldLift?.Invoke(0);
            _ctx = null;
        }

        // For tests and captures.
        public bool Open(ulong liftId)
        {
            var lift = _ctx?.History.Document.FindLift(new LiftId(liftId));
            if (lift == null || lift.Stage != WorkStage.Built || lift.Towers.Count == 0) return false;
            Open(lift);
            return true;
        }
        public bool Editing => _draft != null;
        public int Changes => _undo.Count;
        public IReadOnlyList<Tower> DraftTowers => _draft?.Towers;
        public LiftBuild.Result Built => _built;
        public string Selected => _editor.Selected >= 0 ? "tower " + _editor.Selected
            : _editor.Selected == LiftDraftEditor.Bottom ? "bottom" : _editor.Selected == LiftDraftEditor.Top ? "top" : "none";
        public void SelectTower(int i) { SyncEditor(); _editor.Select(i); Refresh(); }
        public void SelectTerminal(bool bottom) { SyncEditor(); _editor.Select(bottom ? LiftDraftEditor.Bottom : LiftDraftEditor.Top); Refresh(); }
        public LiftDraftEditor Editor => _editor;
        public LiftTypeMenu TypeMenu => _typeMenu;
    }
}
