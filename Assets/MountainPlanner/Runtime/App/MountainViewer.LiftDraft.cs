using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Lifts;
using UnityEngine;

namespace MountainPlanner.App
{
    // Task 09 (F4, LT38): the lift being built or edited, drawn in 3D as it stands now: its terminals, the towers placed so
    // far and the rope strung through them, without chairs (they come at Break ground); the tower in hand as its own
    // see-through model; and Edit lift's draft in place of the built lift, which hides while it's open.
    public sealed partial class MountainViewer
    {
        LiftView _draftView;
        TowerGhost _towerGhost;
        ulong _hiddenLiftId;
        int _draftPart = int.MinValue;   // the draft's part under the pointer (F1), int.MinValue for none
        bool _draftLit, _draftLitShown;  // a part of the draft lit (the selected tower or terminal, UI-15)
        int _draftLitPart = int.MinValue;
        Lift _draftLift;   // the draft shown now (NT16's catchment rings round its terminals), or null

        // The selected part lit white (int.MinValue for none), as a hovered lift is.
        void HighlightDraftPart(int part)
        {
            _draftLitPart = part;
            _draftLit = _draftView != null && _draftView.Root.activeSelf && part != int.MinValue;
            _draftView?.HighlightPart(_draftLit ? part : int.MinValue);
        }

        /// <summary>The draft's view, or null (tests and captures).</summary>
        public LiftView DraftView => _draftView != null && _draftView.Root.activeSelf ? _draftView : null;

        TowerGhost TowerGhostFor() =>
            _towerGhost ??= Lifts != null && _resort != null && _liftMaterials != null ? new TowerGhost(_resort.Root.transform, Lifts, _liftMaterials) : null;

        // Redrawn when the tool's draft changes (a placement, an edit), never per frame.
        void ShowDraftLift(Lift lift)
        {
            _draftLift = null;
            if (lift == null || lift.Towers.Count == 0 || _resort == null || _edits == null || Lifts == null || _liftMaterials == null)
            {
                if (_draftView != null) _draftView.Root.SetActive(false);
                _draftPart = int.MinValue;
                _draftLit = false;
                return;
            }
            _draftView ??= new LiftView(_resort.Root.transform, Lifts, _liftMaterials, _cableMaterial, "Lift draft");
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _draftView.Build(lift, _edits.Pipeline.Heights, _resort.Frame, LiftLivery.For(lift.CatalogId));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _draftView.Root.SetActive(false);
                return;
            }
            _draftView.Root.SetActive(_liftsShown);
            _draftLift = lift;
            _draftLit = _draftLitPart != int.MinValue;
            _draftView.HighlightPart(_draftLitPart);
            DraftMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>How long the draft's last redraw took (the benchmark's target: under 50 ms).</summary>
        public double DraftMilliseconds { get; private set; }

        // Edit lift hides the lift it has open; 0 shows it again.
        void HideBuiltLift(ulong id)
        {
            if (id == _hiddenLiftId) return;
            _hiddenLiftId = id;
            ShowLifts(_liftsShown, true);
        }

        bool LiftHidden(LiftId id) => _hiddenLiftId != 0 && id.Value == _hiddenLiftId;

        // The draft's part under the pointer, picked as the built lifts are (LT23's pick, with the part, F1).
        void PickDraft(Ray ray, float ground, float slack)
        {
            _draftPart = int.MinValue;
            if (_draftView == null || !_draftView.Root.activeSelf) return;
            if (_draftView.Pick(ray, ground + 2, slack, out _, out int part)) _draftPart = part;
        }

        void DisposeLiftDraft()
        {
            _draftView?.Dispose();
            _draftView = null;
            _towerGhost?.Dispose();
            _towerGhost = null;
            _hiddenLiftId = 0;
            _draftPart = int.MinValue;
            _draftLift = null;
        }
    }
}
