using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace FlyAI
{
    // The main menu: starts the fly brain, picks the level (a LiDAR file from this computer, or one uploaded from
    // a phone over the network), and starts the game. It also owns shutting the helper servers down on quit.
    // Set FLYAI_SKIP_MENU=1 to skip it and load the latest scan straight away, as before.
    public class MainMenu : MonoBehaviour
    {
        enum Mode { Main, Upload, Loading }

        public static bool Active { get; private set; } = true;

        public static bool Disabled =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLYAI_SKIP_MENU"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLYAI_RENDER_OUT"));

        static MainMenu instance;

        Mode mode = Mode.Main;
        RoomLoader loader;
        string levelId = "";
        string levelLabel = "";
        string notice = "";
        bool busy;
        Task<string> ingestTask;
        float nextPoll;
        float loadStarted;
        float brainCheckAt = -1f;
        float nextRelocate;

        // Phone upload screen.
        string uploadStartId = "";
        string uploadError;
        UploadInfo uploadInfo;
        Texture2D qr;
        float columnWidth;
        string wifiName = "";
        Task<string> wifiTask;

        GUIStyle titleStyle;
        GUIStyle bodyStyle;
        GUIStyle buttonStyle;
        GUIStyle playStyle;
        Texture2D solid;
        Texture2D buttonTexture;
        Texture2D buttonHover;
        Texture2D playTexture;
        Texture2D playHover;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = true;
            instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Disabled)
            {
                Active = false;
                return;
            }
            if (instance != null) return;
            var go = new GameObject("MainMenu");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MainMenu>();
        }

        // Back to the menu from a finished round.
        public static void Open()
        {
            if (instance != null) instance.Show();
        }

        void Awake()
        {
            Application.quitting += GameServices.Shutdown;
            brainCheckAt = Time.unscaledTime + (BackendClient.Enabled ? 3.5f : 0f); // The Pi's brain is used if it answers.
            GameServices.StartImu();
            RoomLoader.RoomLoaded += OnRoomLoaded;
            RoomLoader.LoadFailed += OnLoadFailed;
            RefreshLevel();
            FreeCursor();
            SetPaused(true);
            MenuArt.Build();
        }

        void OnDestroy()
        {
            RoomLoader.RoomLoaded -= OnRoomLoaded;
            RoomLoader.LoadFailed -= OnLoadFailed;
            MenuArt.Release();
            if (qr != null) Destroy(qr);
        }

        // While the menu is up, a level may be loading behind it. It stays frozen and silent until Play.
        static void SetPaused(bool paused)
        {
            Time.timeScale = paused ? 0f : 1f;
            AudioListener.pause = paused;
        }

        void StartGame()
        {
            GameServices.StopUploadServer();
            mode = Mode.Main;
            MenuCursor.Reset();
            Active = false;
            SetPaused(false);
        }

        void FreeCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Show()
        {
            Active = true;
            MenuCursor.Reset();
            mode = Mode.Main;
            notice = "";
            if (loader != null) loader.Unload(); // The room for the next round then loads again behind the menu.
            RefreshLevel();
            FreeCursor();
            SetPaused(true);
        }

        void OnRoomLoaded()
        {
            if (mode == Mode.Loading && loader != null && loader.LoadedId == levelId) StartGame();
        }

        void OnLoadFailed(string message)
        {
            if (mode == Mode.Loading) mode = Mode.Main;
            notice = "Could not load that level: " + message;
        }

        // The current level is whatever data/latest.json points at.
        void RefreshLevel()
        {
            try
            {
                var path = Path.Combine(GameServices.DataDirectory, "latest.json");
                if (!File.Exists(path))
                {
                    levelId = levelLabel = "";
                    return;
                }
                var latest = JsonUtility.FromJson<LatestScan>(File.ReadAllText(path));
                levelId = latest != null && latest.id != null ? latest.id : "";
                levelLabel = latest != null && !string.IsNullOrEmpty(latest.name) ? latest.name : levelId;
            }
            catch (Exception) { /* The file may be mid-write; try again next time. */ }
        }

        void Update()
        {
            if (!Active)
            {
                ImuReceiver.TakePress(); // The stick's press only means something in the menu.
                return;
            }
            if (Cursor.lockState != CursorLockMode.None) FreeCursor();
            MenuCursor.Tick();
            UpdateRunLocation();
            if (Time.timeScale != 0f) Time.timeScale = 0f;
            if (loader == null) loader = FindFirstObjectByType<RoomLoader>();

            if (ingestTask != null && ingestTask.IsCompleted)
            {
                notice = ingestTask.Result ?? "";
                ingestTask = null;
                busy = false;
                RefreshLevel();
            }
            if (wifiTask != null && wifiTask.IsCompleted)
            {
                wifiName = wifiTask.Result ?? "";
                wifiTask = null;
            }
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + 0.5f;
                RefreshLevel();
                if (mode == Mode.Upload) PollUpload();
            }
            if (mode == Mode.Loading && Time.unscaledTime - loadStarted > 90f) OnLoadFailed("it took too long");
        }

        // Runs the fly on the Pi whenever it is reachable, and on this laptop when it is not.
        void UpdateRunLocation()
        {
            if (brainCheckAt >= 0f && Time.unscaledTime >= brainCheckAt)
            {
                brainCheckAt = -1f;
                if (!BackendClient.Connected) GameServices.StartBrain();
            }
            var fly = FlyAgent.Current;
            if (fly == null || loader == null || loader.IsLoading || fly.IsRemote == BackendClient.Connected) return;
            if (Time.unscaledTime < nextRelocate) return;
            nextRelocate = Time.unscaledTime + 5f;
            if (!BackendClient.Connected) GameServices.StartBrain();
            loader.Unload(); // The level is rebuilt behind the menu, with the fly in the right place.
            RefreshLevel();
        }

        void PollUpload()
        {
            if (uploadInfo == null)
            {
                uploadInfo = GameServices.ReadUploadInfo();
                if (uploadInfo != null) BuildQr(uploadInfo.matrix);
            }
            if (uploadInfo == null && GameServices.UploadServerExited)
                uploadError = "The upload server stopped. Check that the project's Python (.venv) is set up.";
        }

        void BuildQr(string[] rows)
        {
            if (qr != null) Destroy(qr);
            var size = rows.Length;
            qr = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Upload QR" };
            var pixels = new Color32[size * size];
            for (var row = 0; row < size; row++)
                for (var column = 0; column < size; column++)
                {
                    var dark = column < rows[row].Length && rows[row][column] == '1';
                    pixels[(size - 1 - row) * size + column] = dark ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
                }
            qr.SetPixels32(pixels);
            qr.Apply();
        }

        // --- Actions ------------------------------------------------------------------------------------

        void ChooseFile()
        {
            var path = FilePicker.Pick(GameServices.UploadsDirectory);
            if (string.IsNullOrEmpty(path)) return;
            GameServices.CancelIngest(); // Swapping worlds drops the one that was being processed.
            busy = true;
            notice = $"Processing {Path.GetFileName(path)}... this can take a minute.";
            ingestTask = GameServices.IngestAsync(path);
        }

        void OpenUpload()
        {
            mode = Mode.Upload;
            notice = "";
            uploadError = null;
            uploadInfo = null;
            uploadStartId = levelId;
            wifiName = "";
            wifiTask = GameServices.WifiNameAsync();
            if (!GameServices.StartUploadServer()) uploadError = "Could not start the upload server. Check that the project's Python (.venv) is set up.";
        }

        void CloseUpload()
        {
            GameServices.StopUploadServer();
            mode = Mode.Main;
        }

        void Play()
        {
            if (loader == null || string.IsNullOrEmpty(levelId) || busy) return;
            notice = "";
            // Usually the level is already loaded behind the menu, so this starts straight away.
            if (loader.LoadedId == levelId && !loader.IsLoading)
            {
                StartGame();
                return;
            }
            GameServices.StopUploadServer();
            mode = Mode.Loading;
            loadStarted = Time.unscaledTime;
            if (!loader.IsLoading) loader.ReloadScan();
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // --- Screen -------------------------------------------------------------------------------------

        Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        static void SetColor(GUIStyle style, Color color)
        {
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = color;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = color;
        }

        void EnsureStyles()
        {
            if (titleStyle != null) return;
            solid = Solid(new Color(0.035f, 0.04f, 0.05f));
            buttonTexture = Solid(new Color(0.14f, 0.16f, 0.19f));
            buttonHover = Solid(new Color(0.2f, 0.25f, 0.3f));
            playTexture = Solid(new Color(0.16f, 0.5f, 0.24f));
            playHover = Solid(new Color(0.22f, 0.64f, 0.31f));

            titleStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = true, font = MenuStyle.UiFont };
            bodyStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = true, font = MenuStyle.UiFont, fontStyle = FontStyle.Bold };
            // Text sits on a soft rounded panel so it reads over the picture.
            MenuStyle.Round(bodyStyle, MenuStyle.Rounded(new Color(1f, 0.98f, 0.94f, 0.82f), 12), null, 12);
            bodyStyle.padding = new RectOffset(14, 14, 5, 5);
            buttonStyle = MenuStyle.Primary;
            playStyle = MenuStyle.Go;
            SetColor(titleStyle, Color.white);
            SetColor(bodyStyle, new Color(0.12f, 0.1f, 0.08f));
        }

        static GUIStyle MakeButton(Texture2D normal, Texture2D hover)
        {
            var style = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            style.normal.background = style.focused.background = normal;
            style.hover.background = style.active.background = hover;
            style.onNormal.background = style.onFocused.background = normal;
            style.onHover.background = style.onActive.background = hover;
            SetColor(style, Color.white);
            return style;
        }

        void OnGUI()
        {
            GUI.depth = -1000;
            if (!Active) return;
            EnsureStyles();
            var width = Screen.width;
            var height = Screen.height;
            var unit = Mathf.Max(14f, height * 0.024f);
            titleStyle.fontSize = Mathf.RoundToInt(Mathf.Min(height * 0.11f, 84f));
            bodyStyle.fontSize = Mathf.RoundToInt(unit * 0.9f);
            buttonStyle.fontSize = playStyle.fontSize = Mathf.RoundToInt(unit * 1.05f);

            columnWidth = Mathf.Min(640f, width - 40f);
            var area = new Rect((width - columnWidth) * 0.5f, height * 0.07f, columnWidth, height * 0.88f);
            var backgroundOnly = MenuCursor.Begin(Event.current, (int)mode); // The thumbstick steps through the items.
            GUI.DrawTexture(new Rect(0f, 0f, width, height), solid);
            DrawBackground(width, height);
            if (backgroundOnly) return;
            GUILayout.BeginArea(area);
            switch (mode)
            {
                case Mode.Main: DrawMain(unit); break;
                case Mode.Upload: DrawUpload(unit, columnWidth); break;
                default: DrawLoading(unit); break;
            }
            GUILayout.EndArea();
            if (mode == Mode.Main) LocalScores.Draw(width, unit, (width - columnWidth) * 0.5f - 20f);
            if (mode == Mode.Main && BackendClient.Current != null)
            {
                var strip = new Rect(14f, height - 40f, Mathf.Min(560f, width - 28f), 28f);
                MenuStyle.Box(strip, new Color(0.05f, 0.06f, 0.08f, 0.82f), 10);
                MenuStyle.Text(new Rect(strip.x + 12f, strip.y, strip.width - 20f, strip.height), BackendClient.StatusLine(), unit * 0.72f, MenuStyle.Light, TextAnchor.MiddleLeft, true);
            }
            MenuCursor.End();
        }

        // A button that is smaller than its slot but sits in the middle of it, so the layout does not move.
        bool SlotButton(string text, GUIStyle style, float slotHeight, float widthFraction)
        {
            GUILayout.BeginVertical(GUILayout.Height(slotHeight));
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var clicked = GUILayout.Button(text, style, GUILayout.Width(columnWidth * widthFraction), GUILayout.Height(slotHeight * 0.8f));
            clicked |= MenuCursor.Hit(GUILayoutUtility.GetLastRect(), style == playStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return clicked;
        }

        bool StickButton(string text, float unit)
        {
            var clicked = GUILayout.Button(text, buttonStyle, GUILayout.Height(unit * 3f));
            return MenuCursor.Hit(GUILayoutUtility.GetLastRect()) || clicked;
        }

        // The picture: a hallway, a person chasing the fly with a swatter. The fly (left) bobs and flutters, and the
        // arm swings.
        void DrawBackground(float width, float height)
        {
            if (!MenuArt.Ready) return;
            var scale = Mathf.Max(width / MenuArt.W, height / MenuArt.H);
            var origin = new Vector2((width - MenuArt.W * scale) * 0.5f, (height - MenuArt.H * scale) * 0.5f);
            Vector2 ToScreen(float x, float y) => origin + new Vector2(x, y) * scale;
            var time = Time.unscaledTime;
            GUI.DrawTexture(new Rect(origin.x, origin.y, MenuArt.W * scale, MenuArt.H * scale), MenuArt.Hall, ScaleMode.StretchToFill);

            // The fly, zipping about, with speed lines behind it.
            var fly = ToScreen(330f + Mathf.Sin(time * 1.7f) * 28f, 380f + Mathf.Sin(time * 2.6f) * 20f);
            var size = 215f * scale;
            GUI.color = new Color(1f, 1f, 1f, 0.5f);
            for (var i = 0; i < 4; i++)
                GUI.DrawTexture(new Rect(fly.x + size * (0.42f + i * 0.2f), fly.y - size * 0.18f + i * size * 0.12f, size * (0.34f - i * 0.05f), Mathf.Max(3f, 5f * scale)), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Sin(time * 1.7f) * 10f - 6f, fly);
            var frame = (int)(time * 18f) % 2 == 0 ? MenuArt.FlyWingsUp : MenuArt.FlyWingsDown;
            GUI.DrawTexture(new Rect(fly.x - size * 0.5f, fly.y - size * 0.5f, size, size), frame);
            GUI.matrix = matrix;

            // The raised arm and swatter swing from the shoulder.
            var shoulder = ToScreen(MenuArt.Shoulder.x, MenuArt.Shoulder.y);
            GUIUtility.RotateAroundPivot(Mathf.Sin(time * 2.3f) * 6f + Mathf.Sin(time * 5.1f) * 1.5f, shoulder);
            var arm = MenuArt.ArmRect;
            GUI.DrawTexture(new Rect(origin.x + arm.x * scale, origin.y + arm.y * scale, arm.width * scale, arm.height * scale), MenuArt.ArmAndSwatter);
            GUI.matrix = matrix;
        }

        // The title, in yellow with a dark outline so it reads over the picture.
        void DrawLogo()
        {
            const string text = "WHACK A FLY";
            var content = new GUIContent(text);
            var height = titleStyle.CalcHeight(content, columnWidth);
            var rect = GUILayoutUtility.GetRect(columnWidth, height);
            var outline = Mathf.Max(3f, height * 0.04f);
            SetColor(titleStyle, new Color(0.14f, 0.09f, 0.05f));
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) GUI.Label(new Rect(rect.x + dx * outline, rect.y + dy * outline, rect.width, rect.height), text, titleStyle);
            SetColor(titleStyle, new Color(1f, 0.82f, 0.2f));
            GUI.Label(rect, text, titleStyle);
            SetColor(titleStyle, Color.white);
        }

        // A short line of text on its own rounded panel, centred.
        void Pill(string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, bodyStyle, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        void DrawHeader(string subtitle)
        {
            DrawLogo();
            if (subtitle.Length > 0) Pill(subtitle);
            GUILayout.Space(14f);
        }

        void DrawMain(float unit)
        {
            DrawHeader("");

            var online = GameServices.BrainOnline || BackendClient.Connected;
            Pill("Current World");
            GUILayout.Space(unit * 0.2f);
            var levelText = busy ? "Processing...  (click to choose another)" : levelLabel.Length > 0 ? levelLabel : "Choose LiDAR file...";
            if (SlotButton(levelText, buttonStyle, unit * 2.4f, 0.78f)) ChooseFile();
            if (!busy && levelId.Length > 0 && loader != null)
            {
                var ready = loader.LoadedId == levelId && !loader.IsLoading;
                Pill(ready ? "<color=#1B7F3A>Level ready</color>" : "<color=#9A6200>Getting the level ready in the background...</color>");
            }
            GUILayout.Space(unit * 0.4f);
            if (SlotButton("Upload from phone", buttonStyle, unit * 2.4f, 0.78f)) OpenUpload();
            GUILayout.Space(unit * 0.2f);

            var theme = loader != null ? loader.Theme : ThemeMenu.SavedTheme();
            if (SlotButton($"Theme: {theme.name}", buttonStyle, unit * 2.4f, 0.78f) && loader != null)
            {
                var next = ThemeMenu.Next(theme);
                ThemeMenu.Save(next);
                loader.SetTheme(next); // The level behind the menu is rebuilt in the new theme.
            }
            GUILayout.Space(unit * 0.2f);
            DrawDifficulty(unit);
            GUILayout.Space(unit * 0.2f);
            if (SlotButton(LocalScores.Visible ? "Leaderboard: shown" : "Leaderboard", buttonStyle, unit * 2.4f, 0.78f)) LocalScores.Visible = !LocalScores.Visible;
            GUILayout.Space(unit * 0.4f);

            GUI.enabled = !busy && levelId.Length > 0 && loader != null;
            if (SlotButton("PLAY", playStyle, unit * 3.3f, 0.78f)) Play();
            GUI.enabled = true;

            GUILayout.Space(unit * 0.3f);
            if (notice.Length > 0) Pill($"<color=#B04A08>{notice}</color>");
            else if (levelId.Length == 0) Pill("Choose a LiDAR file or upload one from your phone to play.");
            else if (!online) Pill("The fly brain is not running yet, so the fly will not react until it is.");

            GUILayout.FlexibleSpace();
            if (SlotButton("Quit", buttonStyle, unit * 1.8f, 0.4f)) Quit();
        }

        // Easy / Medium / Hard slider, under the theme button.
        void DrawDifficulty(float unit)
        {
            var level = Difficulty.Level;
            GUILayout.BeginVertical(GUILayout.Height(unit * 2.4f));
            GUILayout.FlexibleSpace();
            Pill($"Difficulty:  {Difficulty.Names[level]}");
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var picked = Mathf.RoundToInt(GUILayout.HorizontalSlider(level, 0f, 2f, GUILayout.Width(columnWidth * 0.6f), GUILayout.Height(unit * 0.9f)));
            if (MenuCursor.Hit(GUILayoutUtility.GetLastRect())) picked = (level + 1) % 3; // A press steps Easy, Medium, Hard.
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            if (picked != level) Difficulty.Level = picked;
        }

        void DrawUpload(float unit, float columnWidth)
        {
            DrawHeader("Upload a scan from your phone");

            if (uploadError != null)
            {
                GUILayout.Label($"<color=#B02A20>{uploadError}</color>", bodyStyle);
            }
            else if (qr == null)
            {
                GUILayout.Label("Starting the upload server...", bodyStyle);
            }
            else
            {
                var size = Mathf.Min(columnWidth * 0.62f, Screen.height * 0.42f);
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                rect.x = (columnWidth - size) * 0.5f;
                var margin = size * 0.04f;
                GUI.DrawTexture(new Rect(rect.x - margin, rect.y - margin, size + margin * 2f, size + margin * 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(rect, qr, ScaleMode.StretchToFill);
                GUILayout.Space(margin * 2f);
                GUILayout.Label("Scan this with your phone's camera, then upload the Polycam export (.zip or .glb).", bodyStyle);
                GUILayout.Label(uploadInfo.url, bodyStyle);
            }

            GUILayout.Space(unit * 0.5f);
            GUILayout.Label(wifiName.Length > 0
                ? $"This computer is on the Wi-Fi network <b>{wifiName}</b>. Connect your phone to the same one."
                : "Your phone and this computer must be on the same Wi-Fi network.", bodyStyle);
            GUILayout.Space(unit * 0.6f);

            var received = levelId.Length > 0 && levelId != uploadStartId;
            GUILayout.Label(received ? $"<color=#1B7F3A>Received: {levelLabel}   -   ready to play</color>" : "Waiting for an upload...", bodyStyle);

            GUILayout.FlexibleSpace();
            if (received)
            {
                // Another phone (or another scan) can use the same QR code without leaving this screen.
                GUILayout.BeginHorizontal();
                if (StickButton("Upload another", unit)) uploadStartId = levelId;
                if (StickButton("Return to main menu", unit)) CloseUpload();
                GUILayout.EndHorizontal();
            }
            else if (SlotButton("Return to main menu", buttonStyle, unit * 3f, 0.7f)) CloseUpload();
        }

        void DrawLoading(float unit)
        {
            GUILayout.Space(Screen.height * 0.3f);
            DrawHeader("Loading the level...");
            GUILayout.FlexibleSpace();
            if (SlotButton("Back to menu", buttonStyle, unit * 3f, 0.5f)) mode = Mode.Main;
        }
    }
}
