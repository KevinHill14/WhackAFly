using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FlyAI
{
    [Serializable]
    public class ScoreEntry
    {
        public float seconds;
        public int difficulty;
        public int damage;
        public int swings;
        public string theme;
        public string at;
    }

    [Serializable]
    class ScoreFile
    {
        public List<ScoreEntry> runs = new List<ScoreEntry>();
    }

    // Your best runs, kept on this computer. Each run: time, difficulty, damage to the apartment and swings.
    // The main menu shows them (top right: pick what to sort by, and reverse the order).
    public static class LocalScores
    {
        public enum SortKey { Time, Difficulty, Damage }

        const int Keep = 200;
        const int Shown = 10;
        static List<ScoreEntry> runs;
        public static SortKey Key = SortKey.Time;
        public static bool Reversed;
        public static bool Visible; // Shown with the menu's Leaderboard button.

        static string FilePath => Environment.GetEnvironmentVariable("FLYAI_SCORES") ?? Path.Combine(Application.persistentDataPath, "FlyAI-scores.json");

        static List<ScoreEntry> Runs
        {
            get
            {
                if (runs != null) return runs;
                runs = new List<ScoreEntry>();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var file = JsonUtility.FromJson<ScoreFile>(File.ReadAllText(FilePath));
                        if (file?.runs != null) runs = file.runs;
                    }
                }
                catch (Exception) { }
                return runs;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => runs = null;

        public static void Add(float seconds, int difficulty, int damage, int swings, string theme)
        {
            Runs.Add(new ScoreEntry { seconds = seconds, difficulty = difficulty, damage = damage, swings = swings, theme = theme ?? "", at = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
            var best = Sorted(SortKey.Time, false);
            if (best.Count > Keep) best.RemoveRange(Keep, best.Count - Keep);
            runs = best;
            try { File.WriteAllText(FilePath, JsonUtility.ToJson(new ScoreFile { runs = runs })); }
            catch (Exception e) { Debug.LogWarning("[FlyAI] Could not save the scores: " + e.Message); }
        }

        public static int Count => Runs.Count;

        public static void NextKey() => Key = (SortKey)(((int)Key + 1) % 3);

        // The chosen key first, then time, difficulty, damage and swings break ties, so the order is always the same.
        public static int Compare(ScoreEntry a, ScoreEntry b, SortKey key)
        {
            var c = 0;
            if (key == SortKey.Time) c = a.seconds.CompareTo(b.seconds);
            else if (key == SortKey.Difficulty) c = a.difficulty.CompareTo(b.difficulty);
            else c = a.damage.CompareTo(b.damage);
            if (c == 0) c = a.seconds.CompareTo(b.seconds);
            if (c == 0) c = a.difficulty.CompareTo(b.difficulty);
            if (c == 0) c = a.damage.CompareTo(b.damage);
            if (c == 0) c = a.swings.CompareTo(b.swings);
            return c;
        }

        public static List<ScoreEntry> Sorted(SortKey key, bool reversed)
        {
            var list = new List<ScoreEntry>(Runs);
            list.Sort((a, b) => reversed ? Compare(b, a, key) : Compare(a, b, key));
            return list;
        }

        // --- Menu drawing -------------------------------------------------------------------------------

        static GUIStyle label, header, button;

        static void Styles(float unit)
        {
            if (label == null)
            {
                label = new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = false };
                header = new GUIStyle(label) { fontStyle = FontStyle.Bold };
                button = MenuStyle.Primary;
                label.font = header.font = MenuStyle.UiFont;
                label.normal.textColor = new Color(0.9f, 0.93f, 0.9f);
                header.normal.textColor = new Color(0.65f, 0.72f, 0.66f);
            }
            label.fontSize = header.fontSize = Mathf.RoundToInt(unit * 0.8f);
            button.fontSize = Mathf.RoundToInt(unit * 0.85f);
        }

        static Texture2D Solid(Color color)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, color);
            t.Apply();
            return t;
        }

        static string FormatTime(float v) => v >= 60f ? $"{(int)(v / 60f)}:{v % 60f:00.0}" : $"{v:0.0}s";

        // The two buttons sit in the top right corner; the table is below them.
        public static void Draw(float screenWidth, float unit, float freeWidth)
        {
            if (!Visible || freeWidth < 260f) return; // No room beside the menu column.
            Styles(unit);
            var width = Mathf.Min(freeWidth, unit * 24f);
            var right = screenWidth - 14f;
            var buttonHeight = unit * 1.7f;
            var keyName = Key.ToString();
            var reverseWidth = unit * 8f;
            var sortWidth = unit * 8.5f;
            var reverseRect = new Rect(right - reverseWidth, 12f, reverseWidth, buttonHeight);
            var sortRect = new Rect(reverseRect.x - 8f - sortWidth, 12f, sortWidth, buttonHeight);
            if (GUI.Button(sortRect, "Filter: " + keyName, button)) NextKey();
            if (GUI.Button(reverseRect, Reversed ? "Reverse ▲" : "Reverse ▼", button)) Reversed = !Reversed;

            var rowHeight = unit * 1.3f;
            var list = Sorted(Key, Reversed);
            var rows = Mathf.Min(Shown, list.Count);
            var panel = new Rect(right - width, 12f + buttonHeight + 10f, width, rowHeight * (rows + 3) + 16f);
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = previous;

            var order = Key == SortKey.Difficulty ? (Reversed ? "hard to easy" : "easy to hard") : (Reversed ? "high to low" : "low to high");
            GUI.Label(new Rect(panel.x + 12f, panel.y + 4f, panel.width - 24f, rowHeight), $"<b>BEST RUNS</b>  <color=#9AA39A>{keyName.ToLower()}, {order}</color>", label);
            // Columns: rank, then time, difficulty, damage, swings (the tie-break order).
            var cols = new[] { 0f, 0.1f, 0.34f, 0.62f, 0.84f };
            var heads = new[] { "#", "Time", "Difficulty", "Damage", "Swings" };
            var y = panel.y + 4f + rowHeight;
            for (var c = 0; c < heads.Length; c++)
                GUI.Label(new Rect(panel.x + 12f + cols[c] * (panel.width - 24f), y, panel.width, rowHeight), heads[c], header);
            if (rows == 0)
                GUI.Label(new Rect(panel.x + 12f, y + rowHeight, panel.width - 24f, rowHeight), "No runs yet. Swat the fly!", label);
            for (var i = 0; i < rows; i++)
            {
                var e = list[i];
                y += rowHeight;
                var cells = new[] { (i + 1).ToString(), FormatTime(e.seconds), Difficulty.Names[Mathf.Clamp(e.difficulty, 0, 2)], "$" + e.damage, e.swings.ToString() };
                for (var c = 0; c < cells.Length; c++)
                    GUI.Label(new Rect(panel.x + 12f + cols[c] * (panel.width - 24f), y, panel.width, rowHeight), cells[c], label);
            }
        }
    }
}
