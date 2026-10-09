using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// The loading screen (task P2-03.1; the cover since task P2-03): the title's own picture over the whole screen while
    /// a mountain opens, so its tiles are never seen filling in, and one box at the HUD panels' opacity with the game's
    /// logo, "Opening …", the bar, a Details list of the stages (click only, remembered) and the studio. It fades over the
    /// finished mountain. While it's up the flow's screens take no keys.
    /// </summary>
    public sealed partial class FlowScreens
    {
        /// <summary>The title's picture in Resources (captured with -titlestill: AppFlow.Capture).</summary>
        public const string TitleStillResource = "MountainPlannerFlow/TitleStill";
        public const float CoverFadeSeconds = 0.7f;

        static readonly string[] RowNames = { "ld-package", "ld-preparing", "ld-terrain", "ld-groundcover", "ld-snowpack", "ld-cliffs", "ld-forest" };

        VisualElement _cover, _coverBox, _coverFill, _coverPlate, _coverDetails, _coverStill;
        Label _coverText, _coverPercent, _coverElapsed;
        Button _coverToggle;
        readonly VisualElement[] _ldRows = new VisualElement[LoadProgress.StageCount];
        readonly Label[] _ldTicks = new Label[LoadProgress.StageCount], _ldCounts = new Label[LoadProgress.StageCount], _ldTimes = new Label[LoadProgress.StageCount];
        float _coverGoneAt, _coverShown = -1;
        int _ldRowsVersion = -1;

        /// <summary>The loading screen is up: a mountain is opening under it, and the flow's screens take no keys.</summary>
        public bool CoverUp { get; private set; }
        /// <summary>The title's picture was found (it is captured separately; without it the box sits on warm near-black).</summary>
        public bool CoverHasStill { get; private set; }

        void BindCover()
        {
            _cover = _root.Q("cover");
            if (_cover == null) return;
            _coverBox = _root.Q("cover-box");
            _coverFill = _root.Q("cover-fill");
            _coverText = _root.Q<Label>("cover-text");
            _coverPlate = _root.Q("cover-plate");
            _coverDetails = _root.Q("cover-details");
            _coverStill = _root.Q("cover-still");
            _coverPercent = _root.Q<Label>("cover-percent");
            _coverElapsed = _root.Q<Label>("cover-elapsed");
            _coverToggle = _root.Q<Button>("cover-details-toggle");
            for (int i = 0; i < RowNames.Length; i++)
            {
                var row = _root.Q(RowNames[i]);
                _ldRows[i] = row;
                _ldTicks[i] = row?.Q<Label>(className: "ld-tick");
                _ldCounts[i] = row?.Q<Label>(className: "ld-count");
                _ldTimes[i] = row?.Q<Label>(className: "ld-time");
            }
            var still = Resources.Load<Texture2D>(TitleStillResource);
            CoverHasStill = still != null;
            if (still != null) _coverStill.style.backgroundImage = new StyleBackground(still);
            if (_coverToggle != null)
            {
                _coverToggle.focusable = false;   // click only (owner): the keys stay with nothing while the box is up
                _coverToggle.clicked += () => LoadingPreferences.SetDetails(!LoadingPreferences.Details);
            }
            LoadingPreferences.Changed += ApplyDetails;
            HudPreferences.Changed += ApplyCoverOpacity;
            ApplyDetails();
            ApplyCoverOpacity();
            // Nothing underneath takes a key while the box is up: Enter can't open an area that isn't in yet.
            _root.RegisterCallback<KeyDownEvent>(e => { if (CoverUp) e.StopImmediatePropagation(); }, TrickleDown.TrickleDown);
            _root.RegisterCallback<NavigationSubmitEvent>(e => { if (CoverUp) e.StopImmediatePropagation(); }, TrickleDown.TrickleDown);
            _root.RegisterCallback<NavigationMoveEvent>(e => { if (CoverUp) e.StopImmediatePropagation(); }, TrickleDown.TrickleDown);
        }

        void UnbindCover()
        {
            LoadingPreferences.Changed -= ApplyDetails;
            HudPreferences.Changed -= ApplyCoverOpacity;
        }

        void ApplyDetails()
        {
            bool on = LoadingPreferences.Details;
            Show(_coverDetails, on);
            Show(_coverElapsed, on);
            if (_coverToggle != null) _coverToggle.text = on ? "Hide details" : "Show details";
        }

        /// <summary>The box's colour at the HUD panels' opacity, as the HUD draws its own (MountainHud.Glass).</summary>
        void ApplyCoverOpacity()
        {
            if (_coverPlate != null) _coverPlate.style.opacity = HudPreferences.PanelOpacity / 100f;
        }

        void UpdateCover()
        {
            if (_coverGoneAt > 0 && Time.unscaledTime > _coverGoneAt)
            {
                _coverGoneAt = 0;
                Show(_cover, false);   // faded out: out of the layout and the pointer's way
            }
        }

        /// <summary>Puts the loading screen up, from its first stage: "Opening Jackson Hole".</summary>
        public void ShowCover(string text)
        {
            if (_cover == null) return;
            CoverUp = true;
            _coverGoneAt = 0;
            _cover.RemoveFromClassList("cover--fading");
            SetCoverPicking(PickingMode.Position);   // nothing underneath takes a click
            Show(_cover, true);
            _coverText.text = text ?? "";
            _coverShown = -1;
            _ldRowsVersion = -1;
            SetCoverProgress(0);
        }

        /// <summary>The bar's fill, 0–1 (only restyled when it moves a visible step).</summary>
        public void SetCoverProgress(float fraction)
        {
            float f = Mathf.Round(Mathf.Clamp01(fraction) * 200) / 200;
            if (_coverFill == null || f == _coverShown) return;
            _coverShown = f;
            _coverFill.style.width = Length.Percent(f * 100);
        }

        /// <summary>The bar, the percentage, the time so far and the Details rows; texts change only when the view model's do.</summary>
        public void RenderLoading(LoadingViewModel vm)
        {
            if (_cover == null || vm == null) return;
            SetCoverProgress(vm.Fraction);
            SetText(_coverPercent, vm.Percent);
            SetText(_coverElapsed, vm.Elapsed);
            if (vm.RowsVersion == _ldRowsVersion) return;
            _ldRowsVersion = vm.RowsVersion;
            for (int i = 0; i < vm.Rows.Length; i++)
            {
                var r = vm.Rows[i];
                var row = _ldRows[i];
                if (row == null) continue;
                Show(row, r.Shown);
                row.EnableInClassList("ld-row--done", r.State == StageState.Done);
                row.EnableInClassList("ld-row--current", r.State == StageState.Current);
                SetText(_ldTicks[i], r.State == StageState.Done ? "✓" : r.State == StageState.Current ? "▸" : "");
                SetText(_ldCounts[i], r.Count);
                SetText(_ldTimes[i], r.Time);
            }
        }

        /// <summary>The screen, the box and its button take the pointer, or let it through (a child's picking doesn't follow its parent's).</summary>
        void SetCoverPicking(PickingMode mode)
        {
            _cover.pickingMode = mode;
            if (_coverBox != null) _coverBox.pickingMode = mode;
            if (_coverToggle != null) _coverToggle.pickingMode = mode;
        }

        /// <summary>Fades the loading screen out over the finished mountain; the title's first sign takes the keyboard.</summary>
        public void HideCover()
        {
            if (!CoverUp) return;
            CoverUp = false;
            SetCoverProgress(1);
            _cover.AddToClassList("cover--fading");
            SetCoverPicking(PickingMode.Ignore);   // the mountain takes the pointer while the box fades
            _coverGoneAt = Time.unscaledTime + CoverFadeSeconds;
            if (IsShown(_title)) UiFocus.FocusSoon(_title);
        }
    }
}
