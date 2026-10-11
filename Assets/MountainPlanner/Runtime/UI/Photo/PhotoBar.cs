using System;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Photo
{
    /// <summary>
    /// S10's thin photo bar (task P2-07; the accepted mockup's #demo=p2,photo): Time (the sun), Date, Lens, Focus, Grade
    /// and Size, then Hide, Capture and Exit, docked to the bottom while the HUD is away. It only shows state and raises
    /// events; the viewer owns photo mode and wires them. Its own UI document shares the HUD's panel, so it takes the
    /// theme, UI scale and focus ring with it. Setters change an element only when the shown value changes, from text
    /// made once and kept, so a steady frame allocates nothing.
    /// </summary>
    public sealed class PhotoBar : MonoBehaviour
    {
        public const string Resource = "MountainPlannerPhoto/Photo";
        /// <summary>The time slider's step: five minutes.</summary>
        public const int TimeStep = 300;

        public event Action<int> TimeChosen;
        public event Action<int> DayStepped;
        public event Action<int> LensChosen;
        public event Action<PhotoFocusMode> FocusChosen;
        public event Action<int> GradeStepped;
        public event Action<int> SizeChosen;
        public event Action HideChosen, CaptureChosen, ExitChosen;

        UIDocument _document;
        VisualElement _root, _bar, _flash;
        Slider _time, _lens;
        Label _timeValue, _dateValue, _lensValue, _gradeValue, _captureKey;
        Button _dateBack, _dateOn, _gradeBack, _gradeOn, _focusOff, _focusNatural, _focusMini, _size1, _size2;
        int _shownSecond = -1, _shownLens = -1, _shownScale;
        PhotoFocusMode _shownFocus = (PhotoFocusMode)(-1);
        bool _quiet;

        static readonly string[] TimeText = new string[86400 / TimeStep];
        static readonly string[] LensText = new string[181];

        /// <summary>Makes the bar's document on the HUD's panel settings (shared panel, drawn above the HUD).</summary>
        public static PhotoBar Create(Transform parent, PanelSettings panel)
        {
            var go = new GameObject("Photo bar");
            go.SetActive(false);   // set up before the document builds its tree
            go.transform.SetParent(parent, false);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.sortingOrder = 10;
            document.visualTreeAsset = Resources.Load<VisualTreeAsset>(Resource);
            var bar = go.AddComponent<PhotoBar>();
            bar._document = document;
            go.SetActive(true);
            return bar;
        }

        void OnEnable()
        {
            if (_document == null) _document = GetComponent<UIDocument>();
            UiPanels.Adopt(_document);
            _root = _document.rootVisualElement;
            _bar = _root.Q("photo-bar");
            _flash = _root.Q("photo-flash");
            // The mockup's .sep::before: a short rule on the left of each separated cell.
            _bar.Query(className: "sep").ForEach(cell => { var rule = new VisualElement { pickingMode = PickingMode.Ignore }; rule.AddToClassList("psep"); cell.Insert(0, rule); });
            _time = _root.Q<Slider>("photo-time-slider");
            _lens = _root.Q<Slider>("photo-lens-slider");
            _timeValue = _root.Q<Label>("photo-time-value");
            _dateValue = _root.Q<Label>("photo-date-value");
            _lensValue = _root.Q<Label>("photo-lens-value");
            _gradeValue = _root.Q<Label>("photo-grade-value");
            _captureKey = _root.Q<Label>("photo-capture-key");
            _dateBack = _root.Q<Button>("photo-date-back");
            _dateOn = _root.Q<Button>("photo-date-on");
            _gradeBack = _root.Q<Button>("photo-grade-back");
            _gradeOn = _root.Q<Button>("photo-grade-on");
            _focusOff = _root.Q<Button>("photo-dof-off");
            _focusNatural = _root.Q<Button>("photo-dof-natural");
            _focusMini = _root.Q<Button>("photo-dof-mini");
            _size1 = _root.Q<Button>("photo-size-1");
            _size2 = _root.Q<Button>("photo-size-2");

            _time.RegisterValueChangedCallback(e => { if (!_quiet) TimeChosen?.Invoke(Mathf.RoundToInt(e.newValue) * TimeStep); });
            _lens.RegisterValueChangedCallback(e => { if (!_quiet) LensChosen?.Invoke(Mathf.RoundToInt(e.newValue)); });
            _dateBack.clicked += () => DayStepped?.Invoke(-1);
            _dateOn.clicked += () => DayStepped?.Invoke(1);
            _gradeBack.clicked += () => GradeStepped?.Invoke(-1);
            _gradeOn.clicked += () => GradeStepped?.Invoke(1);
            _focusOff.clicked += () => FocusChosen?.Invoke(PhotoFocusMode.Off);
            _focusNatural.clicked += () => FocusChosen?.Invoke(PhotoFocusMode.Natural);
            _focusMini.clicked += () => FocusChosen?.Invoke(PhotoFocusMode.Miniature);
            _size1.clicked += () => SizeChosen?.Invoke(1);
            _size2.clicked += () => SizeChosen?.Invoke(2);
            _root.Q<Button>("photo-hide").clicked += () => HideChosen?.Invoke();
            _root.Q<Button>("photo-capture").clicked += () => CaptureChosen?.Invoke();
            _root.Q<Button>("photo-exit").clicked += () => ExitChosen?.Invoke();
            // A click leaves no focus behind, so the camera keys keep working after one (as the HUD does).
            _root.RegisterCallback<PointerUpEvent>(_ => _root.schedule.Execute(LetGo), TrickleDown.TrickleDown);
            _root.schedule.Execute(HudIcon.RetintAll).Every(250);
            KeyBindings.Changed += ShowKeys;
            ShowKeys();
            SetVisible(false);
        }

        void OnDisable() => KeyBindings.Changed -= ShowKeys;

        void LetGo()
        {
            if (_root?.panel?.focusController?.focusedElement is Focusable f && _root.Contains(f as VisualElement) && !UiFocus.KeyboardActive(_root)) f.Blur();
        }

        void ShowKeys()
        {
            if (_root == null) return;
            string capture = KeyBindings.Caption(GameAction.PhotoCapture, 1);
            if (capture.Length == 0) capture = KeyBindings.Caption(GameAction.PhotoCapture);
            _captureKey.text = capture;
            _captureKey.EnableInClassList("hidden", capture.Length == 0);
            string hide = KeyBindings.Caption(GameAction.PhotoHideBar);
            _root.Q("photo-hide").tooltip = hide.Length > 0 ? $"Hide the bar ({hide}); {hide} again shows it" : "Hide the bar";
            string earlier = KeyBindings.Caption(GameAction.PhotoEarlier), later = KeyBindings.Caption(GameAction.PhotoLater);
            _root.Q("photo-time").tooltip = earlier.Length > 0 || later.Length > 0 ? $"Time of day: moves the sun ({earlier} {later})" : "Time of day: moves the sun";
            string back = KeyBindings.Caption(GameAction.PhotoDayBack), on = KeyBindings.Caption(GameAction.PhotoDayOn);
            _root.Q("photo-date").tooltip = back.Length > 0 || on.Length > 0 ? $"Date: the sun's path through the year ({back}, {on})" : "Date: the sun's path through the year";
            string exit = KeyBindings.Caption(GameAction.PhotoMode);
            _root.Q("photo-exit").tooltip = exit.Length > 0 ? $"Leave photo mode (Esc or {exit})" : "Leave photo mode (Esc)";
        }

        /// <summary>Photo mode on or off: the whole document.</summary>
        public void SetVisible(bool visible)
        {
            if (_root != null) _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>H: the bar goes, the picture stays (the flash still shows a capture).</summary>
        public void SetBarHidden(bool hidden) => _bar?.EnableInClassList("hidden", hidden);

        public bool BarShown => _root != null && _root.resolvedStyle.display == DisplayStyle.Flex && !_bar.ClassListContains("hidden");

        public void SetTime(int secondOfDay)
        {
            int step = Mathf.Clamp(secondOfDay / TimeStep, 0, TimeText.Length - 1);
            if (step == _shownSecond) return;
            _shownSecond = step;
            _quiet = true;
            _time.value = step;
            _quiet = false;
            _timeValue.text = TimeText[step] ?? (TimeText[step] = Clock(step * TimeStep));
        }

        /// <summary>"10:30 AM", as the mockup's bar.</summary>
        static string Clock(int second)
        {
            int h = second / 3600, m = second / 60 % 60;
            return $"{(h + 11) % 12 + 1}:{m:D2} {(h < 12 ? "AM" : "PM")}";
        }

        public void SetDate(string date)
        {
            if (_dateValue.text != date) _dateValue.text = date;
        }

        public void SetLens(int degrees)
        {
            if (degrees == _shownLens) return;
            _shownLens = degrees;
            _quiet = true;
            _lens.value = degrees;
            _quiet = false;
            int i = Mathf.Clamp(degrees, 0, LensText.Length - 1);
            _lensValue.text = LensText[i] ?? (LensText[i] = i + "°");
        }

        public void SetFocus(PhotoFocusMode mode, bool available = true)
        {
            if (mode == _shownFocus) return;
            _shownFocus = mode;
            _focusOff.EnableInClassList("mp-seg__opt--on", mode == PhotoFocusMode.Off);
            _focusNatural.EnableInClassList("mp-seg__opt--on", mode == PhotoFocusMode.Natural);
            _focusMini.EnableInClassList("mp-seg__opt--on", mode == PhotoFocusMode.Miniature);
            _focusNatural.SetEnabled(available);
            _focusMini.SetEnabled(available);
        }

        public void SetGrade(string name, bool canBack, bool canOn)
        {
            if (_gradeValue.text != name) _gradeValue.text = name;
            _gradeBack.SetEnabled(canBack);
            _gradeOn.SetEnabled(canOn);
        }

        /// <summary>Screen or 2×; the switch's tooltip gives the photo's pixel size.</summary>
        public void SetSize(int scale, string screenSize, string doubleSize)
        {
            if (scale != _shownScale)
            {
                _shownScale = scale;
                _size1.EnableInClassList("mp-seg__opt--on", scale == 1);
                _size2.EnableInClassList("mp-seg__opt--on", scale == 2);
            }
            if (_size1.tooltip != screenSize) _size1.tooltip = screenSize;
            if (_size2.tooltip != doubleSize) _size2.tooltip = doubleSize;
        }

        /// <summary>The capture's flash over the screen (it never reaches the photo, which renders off screen).</summary>
        public void Flash()
        {
            if (_flash == null) return;
            _flash.AddToClassList("pflash--on");
            _flash.schedule.Execute(() => _flash.RemoveFromClassList("pflash--on")).StartingIn(50);
        }

        IVisualElementScheduledItem _toastHide;

        /// <summary>A line at the top centre for a few seconds ("Saved …"); <paramref name="error"/> gives it the warning stripe.</summary>
        public void Toast(string text, bool error = false)
        {
            var toast = _root?.Q("photo-toast");
            if (toast == null) return;
            toast.Q<Label>("photo-toast-text").text = text;
            toast.EnableInClassList("ptoast--error", error);
            toast.RemoveFromClassList("hidden");
            _toastHide?.Pause();
            _toastHide = toast.schedule.Execute(() => toast.AddToClassList("hidden")).StartingIn(3600);
        }

        public bool IsPointerOver(Vector2 screen) => _root != null && _root.resolvedStyle.display == DisplayStyle.Flex && PanelPointer.IsOver(_bar, screen);

        /// <summary>True while a bar control has the keyboard (reached with the arrows), so the camera leaves the keys alone.</summary>
        public bool HasKeyboard
        {
            get
            {
                var focused = _root?.panel?.focusController?.focusedElement as VisualElement;
                return focused != null && _root.Contains(focused) && UiFocus.KeyboardActive(_root);
            }
        }

        /// <summary>For tests and captures: the root of the bar's tree.</summary>
        public VisualElement Root => _root;
    }
}
