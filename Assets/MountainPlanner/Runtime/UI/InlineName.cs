using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// A name renamed in place (task P3-10, sections finding 8): the text field takes the name's spot, font, margins and
    /// width, in the hovered name's colour, with no box (HudTools.uss <c>.mp-inline-name</c>), so typing looks like editing the name itself. Used by
    /// the tool window's title and the lift, trail and Area cards.
    /// </summary>
    public static class InlineName
    {
        public const string FieldClass = "mp-inline-name";

        /// <summary>Puts <paramref name="field"/> where <paramref name="name"/> is, dressed as it, and hides the name.</summary>
        public static void Open(Label name, TextField field)
        {
            field.AddToClassList(FieldClass);
            var s = name.resolvedStyle;
            field.style.marginLeft = s.marginLeft;
            field.style.marginRight = s.marginRight;
            field.style.marginTop = s.marginTop;
            field.style.marginBottom = s.marginBottom;
            if (name.layout.width > 0) field.style.minWidth = name.layout.width;
            var input = field.Q(className: TextField.inputUssClassName);
            if (input != null)
            {
                input.style.fontSize = s.fontSize;
                input.style.unityFontDefinition = s.unityFontDefinition;
                input.style.unityFontStyleAndWeight = s.unityFontStyleAndWeight;
                input.style.unityTextOutlineWidth = s.unityTextOutlineWidth;
                input.style.paddingLeft = s.paddingLeft;
                input.style.paddingRight = s.paddingRight;
                input.style.paddingTop = s.paddingTop;
                input.style.paddingBottom = s.paddingBottom;
                input.style.height = name.layout.height > 0 ? name.layout.height : StyleKeyword.Auto;
            }
            name.parent.Insert(name.parent.IndexOf(name) + 1, field);
            name.AddToClassList("hidden");
        }
    }
}
