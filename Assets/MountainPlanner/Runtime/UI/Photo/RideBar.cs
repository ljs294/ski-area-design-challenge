using System;
using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Photo
{
    /// <summary>
    /// Ride a lift's bar (task 4.11 PR B; the approved mockup's #demo=ride): the lift's plate and name, its Status, the time
    /// to the top and a progress bar, the keys, then Hide and Leave, docked to the bottom while the HUD is away. It only
    /// shows state and raises events; the viewer owns the ride. Its own UI document shares the HUD's panel, as the photo
    /// bar's does. Setters change an element only when what it shows changes, so a steady ride allocates nothing.
    /// </summary>
    public sealed class RideBar : MonoBehaviour
    {
        public const string Resource = "MountainPlannerPhoto/Ride";

        public event Action HideChosen, ExitChosen;

        UIDocument _document;
        VisualElement _root, _bar, _lamp, _progress, _top;
        Label _plate, _name, _word, _eta;
        string _shownWord;
        int _shownEta = int.MinValue, _shownPermille = -1, _shownLamp = -1;

        static readonly string[] EtaText = new string[3600];

        public static RideBar Create(Transform parent, PanelSettings panel)
        {
            var go = new GameObject("Ride bar");
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.sortingOrder = 10;
            document.visualTreeAsset = Resources.Load<VisualTreeAsset>(Resource);
            var bar = go.AddComponent<RideBar>();
            bar._document = document;
            go.SetActive(true);
            return bar;
        }

        void OnEnable()
        {
            if (_document == null) _document = GetComponent<UIDocument>();
            UiPanels.Adopt(_document);
            _root = _document.rootVisualElement;
            _bar = _root.Q("ride-bar");
            _bar.Query(className: "sep").ForEach(cell => { var rule = new VisualElement { pickingMode = PickingMode.Ignore }; rule.AddToClassList("psep"); cell.Insert(0, rule); });
            _plate = _root.Q<Label>("ride-plate");
            _name = _root.Q<Label>("ride-name");
            _lamp = _root.Q("ride-lamp");
            _word = _root.Q<Label>("ride-word");
            _eta = _root.Q<Label>("ride-eta");
            _progress = _root.Q("ride-progress");
            _top = _root.Q("ride-top");
            _root.Q<Button>("ride-hide").clicked += () => HideChosen?.Invoke();
            _root.Q<Button>("ride-exit").clicked += () => ExitChosen?.Invoke();
            _root.RegisterCallback<PointerUpEvent>(_ => _root.schedule.Execute(LetGo), TrickleDown.TrickleDown);
            _root.schedule.Execute(HudIcon.RetintAll).Every(250);
            SetVisible(false);
        }

        void LetGo()
        {
            if (_root?.panel?.focusController?.focusedElement is Focusable f && _root.Contains(f as VisualElement)) f.Blur();
        }

        public void SetVisible(bool visible)
        {
            if (_root != null) _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void SetBarHidden(bool hidden) => _bar?.EnableInClassList("hidden", hidden);
        public bool BarHidden => _bar != null && _bar.ClassListContains("hidden");

        /// <summary>The lift being ridden (once, when the ride starts).</summary>
        public void SetLift(string plate, string name)
        {
            if (_root == null) return;
            _plate.text = plate ?? "";
            _plate.EnableInClassList("hidden", string.IsNullOrEmpty(plate));
            _name.text = name ?? "Lift";
            _shownWord = null;
            _shownEta = int.MinValue;
            _shownPermille = -1;
            _shownLamp = -1;
        }

        /// <summary>
        /// The Status word ("Open", "On hold · wind over 41 mph", "Closed · parked at the bottom"), its lamp (0 running, 1 a
        /// warning, 2 stopped), the real seconds to the top (negative: not moving, the time hidden) and how far up, 0–1.
        /// The caller makes the word once per change, not per frame.
        /// </summary>
        public void SetState(string word, int lamp, int secondsToTop, float progress)
        {
            if (_root == null) return;
            if (!ReferenceEquals(word, _shownWord) && word != _shownWord)
            {
                _shownWord = word;
                _word.text = word;
            }
            if (lamp != _shownLamp)
            {
                _shownLamp = lamp;
                _lamp.EnableInClassList("rlamp--warn", lamp == 1);
                _lamp.EnableInClassList("rlamp--off", lamp == 2);
                _word.EnableInClassList("rword--warn", lamp == 1);
            }
            int eta = secondsToTop < 0 ? -1 : Mathf.Min(secondsToTop, EtaText.Length - 1);
            if (eta != _shownEta)
            {
                _shownEta = eta;
                _eta.text = eta < 0 ? "Stopped" : EtaText[eta] ??= $"Top in {eta / 60}:{eta % 60:D2}";
            }
            int permille = Mathf.Clamp(Mathf.RoundToInt(progress * 1000), 0, 1000);
            if (permille != _shownPermille)
            {
                _shownPermille = permille;
                _progress.style.width = Length.Percent(permille / 10f);
            }
        }

        /// <summary>True when the pointer is on the bar itself (it shares the HUD's panel, so a pick elsewhere doesn't count).</summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (_root?.panel == null || _root.resolvedStyle.display != DisplayStyle.Flex || BarHidden) return false;
            var p = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screen.x, Screen.height - screen.y));
            var picked = _root.panel.Pick(p);
            return picked != null && (picked == _bar || _bar.Contains(picked));
        }
        public VisualElement Root => _root;
        internal VisualElement TopCell => _top;
    }
}
