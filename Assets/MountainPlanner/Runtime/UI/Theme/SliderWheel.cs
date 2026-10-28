using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Every slider's wheel and hover value (owner BR20, 2026-10-28): over a slider the wheel moves it a step a notch (up
    /// is more), and never scrolls the panel or zooms the camera; a small bubble over the thumb shows the slider's value
    /// while the pointer is on it, while it's dragged and just after a notch. The value goes through the slider, so its own
    /// change callback does the rest (the brush's diameter, a setting saved).
    /// </summary>
    public static class SliderWheel
    {
        public const string TipClass = "mp-slider-tip";

        /// <summary>How long the bubble stays after a notch with the pointer gone, ms.</summary>
        const long LingerMs = 800;

        /// <summary>An int slider: <paramref name="wheelStep"/> a notch; <paramref name="caption"/> is what the bubble says.</summary>
        public static Label Attach(BaseSlider<int> slider, int wheelStep, Func<string> caption) =>
            Wire(slider, dir => slider.value = Math.Max(slider.lowValue, Math.Min(slider.highValue, slider.value + dir * wheelStep)), caption);

        /// <summary>A float slider: <paramref name="wheelStep"/> a notch, the value kept on that step's grid from the low end.</summary>
        public static Label Attach(BaseSlider<float> slider, float wheelStep, Func<string> caption) =>
            Wire(slider, dir =>
            {
                float lo = Mathf.Min(slider.lowValue, slider.highValue), hi = Mathf.Max(slider.lowValue, slider.highValue);
                float steps = Mathf.Round((slider.value - lo) / wheelStep) + dir;
                slider.value = Mathf.Clamp(lo + steps * wheelStep, lo, hi);
            }, caption);

        static Label Wire(VisualElement slider, Action<int> notch, Func<string> caption)
        {
            var tip = new Label { pickingMode = PickingMode.Ignore };
            tip.AddToClassList(TipClass);
            tip.AddToClassList("mono");
            tip.style.display = DisplayStyle.None;
            var thumb = slider.Q(className: "unity-base-slider__dragger");
            (thumb ?? slider).Add(tip);
            bool over = false, dragging = false;
            IVisualElementScheduledItem hide = null;
            void Show(bool on)
            {
                if (on) tip.text = caption?.Invoke() ?? "";
                tip.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            }
            void Refresh() => Show(over || dragging);
            slider.RegisterCallback<PointerEnterEvent>(_ => { over = true; Refresh(); });
            slider.RegisterCallback<PointerLeaveEvent>(_ => { over = false; if (hide == null || !hide.isActive) Refresh(); });
            slider.RegisterCallback<PointerDownEvent>(_ => { dragging = true; Refresh(); }, TrickleDown.TrickleDown);
            slider.RegisterCallback<PointerUpEvent>(_ => { dragging = false; Refresh(); }, TrickleDown.TrickleDown);
            slider.RegisterCallback<PointerCaptureOutEvent>(_ => { dragging = false; Refresh(); });
            // The caption follows the value: read it once the slider's own callback has set it.
            slider.RegisterCallback<ChangeEvent<int>>(_ => { if (tip.style.display.value == DisplayStyle.Flex) slider.schedule.Execute(() => tip.text = caption?.Invoke() ?? ""); });
            slider.RegisterCallback<ChangeEvent<float>>(_ => { if (tip.style.display.value == DisplayStyle.Flex) slider.schedule.Execute(() => tip.text = caption?.Invoke() ?? ""); });
            slider.RegisterCallback<WheelEvent>(e =>
            {
                if (!slider.enabledInHierarchy) return;
                int dir = e.delta.y < 0 ? 1 : e.delta.y > 0 ? -1 : 0;   // wheel up is more
                e.StopPropagation();   // the panel doesn't scroll and the camera doesn't zoom
                if (dir == 0) return;
                notch(dir);
                Show(true);
                slider.schedule.Execute(() => tip.text = caption?.Invoke() ?? "");
                hide?.Pause();
                hide = slider.schedule.Execute(() => { hide = null; Refresh(); }).StartingIn(LingerMs);
            });
            return tip;
        }
    }
}
