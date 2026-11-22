using System.Collections.Generic;
using UnityEngine;

namespace FlyAI
{
    // Shared look for the menu: one clean font, a small colour palette, rounded rectangles.
    public static class MenuStyle
    {
        public static readonly Color Card = new Color(0.045f, 0.055f, 0.075f, 0.9f);
        public static readonly Color Edge = new Color(1f, 1f, 1f, 0.09f);
        public static readonly Color Button = new Color(0.16f, 0.185f, 0.24f, 1f);
        public static readonly Color ButtonHover = new Color(0.23f, 0.27f, 0.35f, 1f);
        public static readonly Color Accent = new Color(1f, 0.78f, 0.22f, 1f);
        public static readonly Color AccentHover = new Color(1f, 0.86f, 0.42f, 1f);
        public static readonly Color Green = new Color(0.2f, 0.62f, 0.32f, 1f);
        public static readonly Color GreenHover = new Color(0.27f, 0.76f, 0.4f, 1f);
        public static readonly Color Muted = new Color(0.62f, 0.68f, 0.77f, 1f);
        public static readonly Color Light = new Color(0.93f, 0.95f, 0.98f, 1f);
        public static readonly Color Dark = new Color(0.09f, 0.07f, 0.02f, 1f);

        static Font font;
        static GUIStyle text;
        static readonly Dictionary<string, GUIStyle> cache = new Dictionary<string, GUIStyle>();

        // A system font that looks clean (Segoe UI on Windows). Falls back to Unity's built-in font.
        public static Font UiFont
        {
            get
            {
                if (font != null) return font;
                try { font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Helvetica Neue", "Helvetica", "Arial" }, 16); }
                catch (System.Exception) { }
                return font;
            }
        }

        // A rounded rectangle with soft edges, for use as a 9-sliced background (see Round).
        public static Texture2D Rounded(Color fill, int radius = 14)
        {
            var size = radius * 2 + 4;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var half = size * 0.5f;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - (half - radius), 0f);
                    var dy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - (half - radius), 0f);
                    var edge = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    texture.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * Mathf.Clamp01(0.5f - edge)));
                }
            texture.Apply();
            return texture;
        }

        public static void Round(GUIStyle style, Texture2D normal, Texture2D hover, int radius = 14)
        {
            style.border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1);
            style.normal.background = style.focused.background = style.onNormal.background = style.onFocused.background = normal;
            style.hover.background = style.active.background = style.onHover.background = style.onActive.background = hover ?? normal;
        }

        static void TextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = color;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = color;
        }

        // A button style (cached by its colours).
        public static GUIStyle MakeButton(Color fill, Color hover, Color? textColor = null, int radius = 14)
        {
            var key = $"b{fill}{hover}{textColor}{radius}";
            if (cache.TryGetValue(key, out var found)) return found;
            var style = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, font = UiFont };
            Round(style, Rounded(fill, radius), Rounded(hover, radius), radius);
            style.margin = new RectOffset(0, 0, 0, 0);
            style.padding = new RectOffset(14, 14, 4, 4);
            TextColor(style, textColor ?? Color.white);
            cache[key] = style;
            return style;
        }

        public static GUIStyle Primary => MakeButton(Button, ButtonHover);
        public static GUIStyle Go => MakeButton(Green, GreenHover, null, 18);
        public static GUIStyle Ghost => MakeButton(new Color(1f, 1f, 1f, 0f), new Color(1f, 1f, 1f, 0.09f), Muted);
        public static GUIStyle SegOff => MakeButton(Button, ButtonHover, Light);
        public static GUIStyle SegOn => MakeButton(Accent, AccentHover, Dark);

        // A filled rounded box (cards, chips, table rows).
        public static void Box(Rect rect, Color fill, int radius = 16)
        {
            var key = $"x{fill}{radius}";
            if (!cache.TryGetValue(key, out var style))
            {
                style = new GUIStyle();
                Round(style, Rounded(fill, radius), null, radius);
                cache[key] = style;
            }
            GUI.Box(rect, GUIContent.none, style);
        }

        // A card with a faint edge.
        public static void CardBox(Rect rect, int radius = 28)
        {
            Box(new Rect(rect.x - 1.5f, rect.y - 1.5f, rect.width + 3f, rect.height + 3f), Edge, radius + 1);
            Box(rect, Card, radius);
        }

        // A label style for layout code: small caps-like section headers and chips.
        public static GUIStyle Label(float size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, bool bold = true)
        {
            var key = $"l{Mathf.RoundToInt(size)}{color}{anchor}{bold}";
            if (cache.TryGetValue(key, out var found)) return found;
            var style = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, font = UiFont, fontSize = Mathf.RoundToInt(size), alignment = anchor, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal };
            TextColor(style, color);
            cache[key] = style;
            return style;
        }

        public static GUIStyle Chip(Color fill, Color textColor, float size)
        {
            var key = $"c{fill}{textColor}{Mathf.RoundToInt(size)}";
            if (cache.TryGetValue(key, out var found)) return found;
            var style = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false, font = UiFont, fontSize = Mathf.RoundToInt(size), alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            Round(style, Rounded(fill, 12), null, 12);
            style.padding = new RectOffset(14, 14, 5, 5);
            TextColor(style, textColor);
            cache[key] = style;
            return style;
        }

        // A label drawn in a rectangle (for hand-placed layouts).
        public static void Text(Rect rect, string content, float size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, bool bold = false)
        {
            if (text == null) text = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false, font = UiFont };
            text.fontSize = Mathf.RoundToInt(size);
            text.alignment = anchor;
            text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            TextColor(text, color);
            GUI.Label(rect, content, text);
        }
    }
}
