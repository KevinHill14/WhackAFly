using System.Collections.Generic;
using UnityEngine;

namespace FlyAI
{
    // Screen rectangles of clickable on-screen buttons, so a click on one is not also a swing or a cursor grab.
    public static class UiHit
    {
        static readonly Dictionary<string, Rect> Rects = new Dictionary<string, Rect>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Rects.Clear();

        public static void Set(string key, Rect rect) => Rects[key] = rect;
        public static void Clear(string key) => Rects.Remove(key);

        // screenPosition is the mouse position with the origin at the bottom left.
        public static bool Over(Vector2 screenPosition)
        {
            var point = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            foreach (var rect in Rects.Values)
                if (rect.Contains(point)) return true;
            return false;
        }
    }

    // One round: a 3-2-1 countdown, a 60 second hunt, then a win (the fly is smushed) or a loss (time up).
    // Reloading the room (R or the Play again button) starts a new round with a new fly.
    public class GameRound : MonoBehaviour
    {
        public enum Phase { Countdown, Playing, Won, Lost }

        public const float RoundSeconds = 60f;
        const float StepSeconds = 1f;
        const float HuntBannerSeconds = 1f;

        public static GameRound Current { get; private set; }

        public Phase CurrentPhase { get; private set; } = Phase.Countdown;
        public bool CanSwing => CurrentPhase == Phase.Playing;
        public bool CanMove => CurrentPhase != Phase.Countdown;
        public bool FlyActive => CurrentPhase == Phase.Playing;
        public bool Finished => CurrentPhase == Phase.Won || CurrentPhase == Phase.Lost;

        FlyAgent fly;
        PlayerController player;
        AudioSource voice;
        float phaseStart;
        float timeLeft = RoundSeconds;
        float timeTaken;
        int lastStep = -1;
        int swings;
        bool saveRun;
        GUIStyle bigStyle;
        GUIStyle midStyle;
        GUIStyle smallStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public static GameRound Begin(Transform parent, FlyAgent agent)
        {
            var go = new GameObject("Round");
            go.transform.SetParent(parent, false);
            var round = go.AddComponent<GameRound>();
            round.fly = agent;
            round.phaseStart = Time.time;
            round.voice = go.AddComponent<AudioSource>();
            round.voice.playOnAwake = false;
            round.voice.spatialBlend = 0f;
            Current = round;
            return round;
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            if (player != null) player.Swung -= OnSwung;
            UiHit.Clear("again");
            UiHit.Clear("menu");
        }

        void OnSwung(SwingResult result) => swings++;

        void SaveRun()
        {
            saveRun = false;
            var bill = player != null ? player.DamageBill : 0;
            LocalScores.Add(timeTaken, Difficulty.Level, bill, swings, FindFirstObjectByType<RoomLoader>()?.ThemeName);
            Leaderboard.FlySwatted(bill, timeTaken);
        }

        void Update()
        {
            if (player == null)
            {
                player = FindFirstObjectByType<PlayerController>();
                if (player != null) player.Swung += OnSwung;
            }
            if (saveRun) SaveRun();

            switch (CurrentPhase)
            {
                case Phase.Countdown:
                    var t = Time.time - phaseStart;
                    var step = Mathf.FloorToInt(t / StepSeconds);
                    if (step != lastStep && step < 3)
                    {
                        lastStep = step;
                        Say("announce_" + (3 - step));
                    }
                    if (t >= 3f * StepSeconds) StartHunt();
                    break;
                case Phase.Playing:
                    timeLeft -= Time.deltaTime;
                    if (timeLeft <= 0f)
                    {
                        timeLeft = 0f;
                        End(Phase.Lost);
                        Play("round_lost", 0.9f);
                    }
                    break;
            }
        }

        void StartHunt()
        {
            CurrentPhase = Phase.Playing;
            phaseStart = Time.time;
            Play("whistle", 1f);
            Say("announce_go"); // silent unless a recording exists
            if (fly != null) fly.OnRoundStart();
        }

        // Called by the fly when it is swatted.
        public void Win(Vector3 point)
        {
            if (CurrentPhase != Phase.Playing) return;
            timeTaken = RoundSeconds - timeLeft;
            saveRun = true; // Next frame, so the swing that killed the fly is counted too.
            End(Phase.Won);
            Say("announce_smushed");
            if (player != null) player.AddPopup(point, "SMUSHED!", new Color(0.3f, 1f, 0.35f), 40);
            Invoke(nameof(PlayWinJingle), 0.9f);
        }

        void PlayWinJingle() => Play("round_won", 0.8f);

        void End(Phase phase)
        {
            CurrentPhase = phase;
            phaseStart = Time.time;
            // Free the cursor so the Play again button can be clicked.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // An announcer line: a recording in Resources/FlyAISfx if there is one, otherwise the generated voice.
        void Say(string name)
        {
            var clip = FlySounds.Get(name);
            if (clip != null) voice.PlayOneShot(clip, 1f);
        }

        void Play(string name, float volume)
        {
            var clip = FlySounds.Get(name);
            if (clip != null) voice.PlayOneShot(clip, volume);
        }

        void OpenMenu() => MainMenu.Open();

        void PlayAgain()
        {
            var loader = FindFirstObjectByType<RoomLoader>();
            if (loader != null) loader.ReloadScan();
        }

        // --- Screen -------------------------------------------------------------------------------------

        // Every state gets the same colour, so nothing changes (or doubles up) when the mouse is over the text.
        static void SetColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }

        // Text with a soft offset shadow, for the countdown and timer over the 3D scene.
        static void Shadowed(Rect rect, string text, GUIStyle style, Color color)
        {
            SetColor(style, new Color(0f, 0f, 0f, color.a * 0.85f));
            GUI.Label(new Rect(rect.x + 3f, rect.y + 3f, rect.width, rect.height), text, style);
            SetColor(style, color);
            GUI.Label(rect, text, style);
        }

        // Plain text: drawn once, no shadow.
        static void Plain(Rect rect, string text, GUIStyle style, Color color)
        {
            SetColor(style, color);
            GUI.Label(rect, text, style);
        }

        void OnGUI()
        {
            if (bigStyle == null)
            {
                bigStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                midStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                smallStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            }
            var width = Screen.width;
            var height = Screen.height;
            var centre = new Rect(0f, height * 0.18f, width, height * 0.3f);

            switch (CurrentPhase)
            {
                case Phase.Countdown:
                    var t = Time.time - phaseStart;
                    var step = Mathf.Clamp(Mathf.FloorToInt(t / StepSeconds), 0, 2);
                    var frac = t / StepSeconds - Mathf.Floor(t / StepSeconds);
                    bigStyle.fontSize = Mathf.RoundToInt(height * Mathf.Lerp(0.26f, 0.2f, frac));
                    Shadowed(centre, (3 - step).ToString(), bigStyle, new Color(1f, 0.9f, 0.4f, 1f - frac * frac));
                    midStyle.fontSize = Mathf.RoundToInt(height * 0.035f);
                    Shadowed(new Rect(0f, height * 0.5f, width, height * 0.06f), "Get ready to swat the fly", midStyle, new Color(1f, 1f, 1f, 0.8f));
                    break;

                case Phase.Playing:
                    DrawTimer(width, height);
                    var since = Time.time - phaseStart;
                    if (since < HuntBannerSeconds)
                    {
                        var fade = 1f - Mathf.Clamp01((since - 0.5f) / (HuntBannerSeconds - 0.5f));
                        bigStyle.fontSize = Mathf.RoundToInt(height * 0.2f);
                        Shadowed(centre, "HUNT!", bigStyle, new Color(0.45f, 1f, 0.5f, fade));
                    }
                    break;

                default:
                    DrawTimer(width, height);
                    DrawResult(width, height);
                    break;
            }
        }

        void DrawTimer(float width, float height)
        {
            var seconds = Mathf.Max(0f, timeLeft);
            var low = seconds <= 10f && CurrentPhase == Phase.Playing;
            var pulse = low ? 1f + 0.12f * Mathf.Sin(Time.time * 8f) : 1f;
            midStyle.fontSize = Mathf.RoundToInt(height * 0.05f * pulse);
            var color = low ? new Color(1f, 0.3f, 0.25f) : new Color(1f, 1f, 1f, 0.92f);
            var text = $"{Mathf.FloorToInt(seconds / 60f)}:{Mathf.FloorToInt(seconds % 60f):00}.{Mathf.FloorToInt(seconds * 10f % 10f)}";
            Shadowed(new Rect(width * 0.5f - 150f, 10f, 300f, height * 0.07f), text, midStyle, color);
        }

        void DrawResult(float width, float height)
        {
            var won = CurrentPhase == Phase.Won;
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, width, height), Texture2D.whiteTexture);
            GUI.color = previous;

            var panel = new Rect(width * 0.5f - 270f, height * 0.5f - 200f, 540f, 400f);
            bigStyle.fontSize = Mathf.RoundToInt(Mathf.Min(height * 0.12f, 92f));
            Plain(new Rect(panel.x, panel.y, panel.width, 110f), won ? "YOU WIN!" : "TIME'S UP", bigStyle,
                won ? new Color(0.4f, 1f, 0.45f) : new Color(1f, 0.35f, 0.3f));

            midStyle.fontSize = 22;
            var lines = new List<string>();
            if (won) lines.Add($"The fly was smushed in {timeTaken:0.0} s");
            else lines.Add("The fly got away");
            lines.Add($"Fly: {(fly != null ? fly.PersonalityName : "unknown")}   ({Difficulty.Name})");
            lines.Add($"Damage bill: ${(player != null ? player.DamageBill : 0)}");
            lines.Add($"Swings: {swings}");
            if (player != null && player.SwatterScale > 1.001f) lines.Add($"Swatter grew to {Mathf.RoundToInt(player.SwatterScale * 100f)}%");
            for (var i = 0; i < lines.Count; i++)
                Plain(new Rect(panel.x, panel.y + 120f + i * 34f, panel.width, 32f), lines[i], midStyle, new Color(1f, 1f, 1f, 0.92f));

            var again = new Rect(panel.x + 20f, panel.yMax - 74f, 240f, 48f);
            var menu = new Rect(panel.xMax - 260f, panel.yMax - 74f, 240f, 48f);
            UiHit.Set("again", again);
            UiHit.Set("menu", menu);
            if (GUI.Button(again, "Play again   [R]")) PlayAgain();
            if (GUI.Button(menu, "Main menu")) OpenMenu();
        }
    }
}
