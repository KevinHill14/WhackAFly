using UnityEngine;
using UnityEngine.InputSystem;

namespace FlyAI
{
    // Theme picker. Opens when the game starts; O opens or closes it, 1/2/3 pick a theme.
    // The choice is saved and the room is rebuilt through RoomLoader's normal reload.
    public class ThemeMenu : MonoBehaviour
    {
        const string PrefKey = "FlyAI.Theme";

        // While open, the player does not walk or swing (see PlayerController.Update).
        public static bool IsOpen { get; private set; }

        RoomLoader loader;
        GUIStyle titleStyle;
        GUIStyle lineStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => IsOpen = false;

        // The saved theme, or the house if nothing (or an unknown name) is saved.
        public static RoomTheme SavedTheme() => RoomThemes.ByName(PlayerPrefs.GetString(PrefKey, RoomThemes.Main.name));

        void Awake()
        {
            loader = GetComponent<RoomLoader>();
            IsOpen = true;
        }

        void OnDestroy() => IsOpen = false;

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.oKey.wasPressedThisFrame)
            {
                IsOpen = !IsOpen;
                return;
            }
            if (!IsOpen) return;

            var count = Mathf.Min(RoomThemes.All.Length, 9);
            for (var i = 0; i < count; i++)
                if (keyboard[Key.Digit1 + i].wasPressedThisFrame || keyboard[Key.Numpad1 + i].wasPressedThisFrame)
                {
                    Choose(RoomThemes.All[i]);
                    return;
                }
        }

        void Choose(RoomTheme theme)
        {
            PlayerPrefs.SetString(PrefKey, theme.name);
            PlayerPrefs.Save();
            IsOpen = false;
            if (loader != null) loader.SetTheme(theme);
        }

        void OnGUI()
        {
            if (!IsOpen) return;
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.86f, 0.9f, 0.86f) } };
            }

            var current = loader != null ? loader.Theme : null;
            var count = Mathf.Min(RoomThemes.All.Length, 9);
            const float lineHeight = 26f;
            const float width = 360f;
            var height = 60f + (count + 2) * lineHeight;
            var panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = previous;

            GUI.Label(new Rect(panel.x, panel.y + 10f, width, 32f), "Choose a room theme", titleStyle);
            for (var i = 0; i < count; i++)
            {
                var theme = RoomThemes.All[i];
                var marker = theme == current ? "   (current)" : "";
                GUI.Label(new Rect(panel.x + 40f, panel.y + 52f + i * lineHeight, width - 60f, lineHeight), $"[{i + 1}]  {theme.name}{marker}", lineStyle);
            }
            GUI.Label(new Rect(panel.x + 40f, panel.y + 60f + (count + 0.6f) * lineHeight, width - 60f, lineHeight), "[O]  close  (O reopens this menu)", lineStyle);
        }
    }
}
