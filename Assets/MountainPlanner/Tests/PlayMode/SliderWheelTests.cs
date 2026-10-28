using System.Collections;
using MountainPlanner.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Owner BR20: the wheel over a slider moves it a step a notch (up is more) without scrolling the panel round it, and a
    /// bubble over the thumb shows its value while the pointer is on it. On a real panel, so the events dispatch as in the game.
    /// </summary>
    public sealed class SliderWheelTests
    {
        GameObject _host;
        PanelSettings _settings;
        ScrollView _scroll;
        SliderInt _slider;
        Label _caption, _tip;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _host = new GameObject("SliderWheelTests");
            var doc = _host.AddComponent<UIDocument>();
            doc.panelSettings = _settings;
            yield return null;
            var root = doc.rootVisualElement;
            _scroll = new ScrollView { style = { height = 80 } };
            for (int i = 0; i < 20; i++) _scroll.Add(new Label("row " + i) { style = { height = 30 } });
            _slider = new SliderInt(10, 400) { value = 60, style = { width = 200 } };
            _caption = new Label();
            _slider.RegisterValueChangedCallback(e => _caption.text = e.newValue + " ft");
            _caption.text = "60 ft";
            _scroll.Insert(0, _slider);
            root.Add(_scroll);
            _tip = SliderWheel.Attach(_slider, 5, () => _caption.text);
            yield return null;
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host);
            Object.DestroyImmediate(_settings);
        }

        void Wheel(float notches)
        {
            using var e = WheelEvent.GetPooled(new Event { type = EventType.ScrollWheel, delta = new Vector2(0, notches), mousePosition = _slider.worldBound.center });
            e.target = _slider;
            _slider.SendEvent(e);
        }

        [UnityTest]
        public IEnumerator BR20_TheWheelMovesTheSliderAStepANotchAndNotThePanel()
        {
            Wheel(-1);   // up
            yield return null;
            Assert.That(_slider.value, Is.EqualTo(65), "wheel up is more, one step a notch");
            Wheel(1);
            Wheel(1);
            yield return null;
            Assert.That(_slider.value, Is.EqualTo(55));
            Assert.That(_caption.text, Is.EqualTo("55 ft"), "through the slider's own callback");
            Assert.That(_scroll.scrollOffset.y, Is.EqualTo(0), "the panel round it doesn't scroll");
            for (int i = 0; i < 100; i++) Wheel(-1);
            yield return null;
            Assert.That(_slider.value, Is.EqualTo(400), "clamped at the top");
        }

        [UnityTest]
        public IEnumerator BR20_TheBubbleShowsTheValueWhileHovered()
        {
            Assert.That(_tip.resolvedStyle.display, Is.EqualTo(DisplayStyle.None), "hidden at rest");
            using (var enter = PointerEnterEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = _slider.worldBound.center }))
            {
                enter.target = _slider;
                _slider.SendEvent(enter);
            }
            yield return null;
            Assert.That(_tip.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(_tip.text, Is.EqualTo("60 ft"));
            Wheel(-1);
            yield return null;
            yield return null;
            Assert.That(_tip.text, Is.EqualTo("65 ft"), "the bubble follows the value");
            using (var leave = PointerLeaveEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = Vector2.zero }))
            {
                leave.target = _slider;
                _slider.SendEvent(leave);
            }
            float until = Time.realtimeSinceStartup + 2;
            while (_tip.style.display.value == DisplayStyle.Flex && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(_tip.style.display.value, Is.EqualTo(DisplayStyle.None), "gone once the pointer leaves and the notch's moment passes");
        }
    }
}
