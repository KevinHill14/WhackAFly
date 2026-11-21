using UnityEngine;

namespace FlyAI
{
    // Remembers the chosen room theme. The picker itself is the Theme button on the main menu.
    public class ThemeMenu : MonoBehaviour
    {
        const string PrefKey = "FlyAI.Theme";

        // Kept for the player's input check: the old in-game picker is gone, so this is always false.
        public static bool IsOpen => false;

        // The saved theme, or the house if nothing (or an unknown name) is saved.
        public static RoomTheme SavedTheme() => RoomThemes.ByName(PlayerPrefs.GetString(PrefKey, RoomThemes.Main.name));

        public static void Save(RoomTheme theme)
        {
            PlayerPrefs.SetString(PrefKey, theme.name);
            PlayerPrefs.Save();
        }

        // The theme after this one, wrapping round.
        public static RoomTheme Next(RoomTheme current)
        {
            var all = RoomThemes.All;
            var index = System.Array.IndexOf(all, current);
            return all[(index + 1) % all.Length];
        }
    }
}
