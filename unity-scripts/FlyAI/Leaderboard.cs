using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

namespace FlyAI
{
    [Serializable]
    public class LeaderboardEntry
    {
        public string name;
        public int damage;
        public float seconds;
        public string theme;
        public string at;
    }

    [Serializable]
    class LeaderboardResponse
    {
        public int rank;
        public LeaderboardEntry[] scores;
    }

    [Serializable]
    class LeaderboardSubmission
    {
        public string round_id;
        public string name;
        public int damage;
        public float seconds;
        public string theme;
    }

    // Times each round (room load or R reload until the fly is swatted), then asks for a name, sends the run to
    // leaderboard/server.py and shows the top 10. It also logs game events (round start, swings, fly escapes,
    // the swat) for a spectator dashboard; the server stores them in Tiger Cloud (Tiger Data).
    // The game never holds database credentials: it only talks HTTP to that server.
    // Everything here is optional: with the server, Tiger Cloud or the internet down, the game plays on.
    // Lowest damage bill wins; equal bills go to the faster swat.
    // FLYAI_LEADERBOARD sets the server URL; "off" (or empty) turns the leaderboard and the event log off.
    public class Leaderboard : MonoBehaviour
    {
        public const string DefaultUrl = "http://127.0.0.1:8768"; // not 8766: the game backend uses that
        const int MaxName = 20;
        const int TimeoutSeconds = 8;
        const float FlushInterval = 1f;
        const float OfflineBackoff = 15f;
        const int MaxBatch = 200;
        const int MaxQueued = 1000;
        const string NameKey = "FlyAI.LeaderboardName";

        // True while the player is typing a name. Other scripts skip their key bindings while it is set.
        public static bool TypingName => instance != null && instance.state == State.EnterName;

        enum State { Idle, Playing, EnterName, Sending, Board }

        struct QueuedEvent
        {
            public string roundId;
            public string json;
        }

        static Leaderboard instance;
        string url;
        State state = State.Idle;
        string roundId = "";
        float runStartedAt;
        int run;
        int damage;
        float seconds;
        string theme = "";
        string playerName = "";
        string error = "";
        int rank;
        LeaderboardEntry[] scores = new LeaderboardEntry[0];
        Keyboard listening;
        PlayerController watchedPlayer;
        readonly List<QueuedEvent> queue = new List<QueuedEvent>();
        bool flushing;
        float nextFlush;
        bool warnedOffline;
        GUIStyle style;
        GUIStyle titleStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (instance == null) new GameObject("Leaderboard").AddComponent<Leaderboard>();
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            url = ResolveUrl();
            playerName = PlayerPrefs.GetString(NameKey, "");
            Debug.Log(url == null ? "[FlyAI] Leaderboard off" : $"[FlyAI] Leaderboard at {url}");
        }

        void OnDestroy()
        {
            StopListening();
            if (watchedPlayer != null) watchedPlayer.Swung -= OnSwung;
            if (instance == this) instance = null;
        }

        static string ResolveUrl()
        {
            var value = Environment.GetEnvironmentVariable("FLYAI_LEADERBOARD");
            if (value == null) return null; // Off unless FLYAI_LEADERBOARD names a server.
            value = value.Trim().TrimEnd('/');
            return value.Length == 0 || value.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : value;
        }

        // --- Rounds ---------------------------------------------------------------------------------------

        // Called whenever the player is (re)spawned in a room: a new round starts.
        public static void StartRun(PlayerController player)
        {
            if (instance == null) return;
            var self = instance;
            self.StopListening();
            self.run++;
            self.roundId = Guid.NewGuid().ToString();
            self.state = State.Playing;
            self.runStartedAt = Time.unscaledTime;
            self.error = "";
            if (player != null && player != self.watchedPlayer)
            {
                if (self.watchedPlayer != null) self.watchedPlayer.Swung -= self.OnSwung;
                self.watchedPlayer = player;
                player.Swung += self.OnSwung;
            }
            self.theme = CurrentTheme();
            Log("round_start", null, theme: self.theme);
        }

        static string CurrentTheme()
        {
            var loader = FindFirstObjectByType<RoomLoader>();
            return loader != null ? loader.ThemeName : "";
        }

        // Called once when the fly dies. Damage after this point does not count.
        public static void FlySwatted(int damageBill, float? roundSeconds = null)
        {
            if (instance == null || instance.state != State.Playing) return;
            var self = instance;
            self.damage = damageBill;
            self.seconds = roundSeconds ?? Time.unscaledTime - self.runStartedAt;
            self.theme = CurrentTheme();
            Log("fly_swatted", null, damageBill, self.seconds, self.theme);
            if (self.url == null)
            {
                self.state = State.Idle;
                return;
            }
            self.state = State.EnterName;
            self.StartListening();
        }

        void OnSwung(SwingResult swing)
        {
            var p = swing.point;
            Log("swing", Data("hit", swing.hit, "target", swing.target, "part", swing.hit ? swing.kind.ToString() : "",
                "strength", swing.strength, "x", p.x, "y", p.y, "z", p.z,
                "bill", watchedPlayer != null ? watchedPlayer.DamageBill : 0), swing.damage);
        }

        // --- Event log --------------------------------------------------------------------------------------

        // Queues one game event for the dashboard log. data is a JSON object from Data(...), or null.
        // Never blocks and never throws: events are sent in batches, and dropped if the server is unreachable.
        public static void Log(string kind, string data, int? damage = null, float? seconds = null, string theme = null)
        {
            var self = instance;
            if (self == null || self.url == null || self.roundId.Length == 0) return;
            var json = new StringBuilder(96);
            json.Append("{\"kind\":").Append(Quote(kind));
            json.Append(",\"t\":").Append(Num(Mathf.Max(0f, Time.unscaledTime - self.runStartedAt)));
            if (damage.HasValue) json.Append(",\"damage\":").Append(Mathf.Max(0, damage.Value).ToString(CultureInfo.InvariantCulture));
            if (seconds.HasValue) json.Append(",\"seconds\":").Append(Num(Mathf.Max(0f, seconds.Value)));
            if (!string.IsNullOrEmpty(theme)) json.Append(",\"theme\":").Append(Quote(theme));
            if (!string.IsNullOrEmpty(data)) json.Append(",\"data\":").Append(data);
            json.Append('}');
            if (self.queue.Count >= MaxQueued) self.queue.RemoveAt(0);
            self.queue.Add(new QueuedEvent { roundId = self.roundId, json = json.ToString() });
        }

        // Builds a flat JSON object from key, value pairs: Data("hit", true, "target", "tv", "strength", 0.8f).
        public static string Data(params object[] pairs)
        {
            var json = new StringBuilder("{");
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (i > 0) json.Append(',');
                json.Append(Quote(Convert.ToString(pairs[i], CultureInfo.InvariantCulture))).Append(':');
                var value = pairs[i + 1];
                switch (value)
                {
                    case null: json.Append("null"); break;
                    case bool b: json.Append(b ? "true" : "false"); break;
                    case float f: json.Append(Num(f)); break;
                    case double d: json.Append(Num((float)d)); break;
                    case int n: json.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                    default: json.Append(Quote(Convert.ToString(value, CultureInfo.InvariantCulture))); break;
                }
            }
            return json.Append('}').ToString();
        }

        static string Num(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? "0" : value.ToString("0.###", CultureInfo.InvariantCulture);

        static string Quote(string value)
        {
            var json = new StringBuilder("\"");
            foreach (var c in value ?? "")
            {
                if (c == '"' || c == '\\') json.Append('\\').Append(c);
                else if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4"));
                else json.Append(c);
            }
            return json.Append('"').ToString();
        }

        void FlushEvents()
        {
            if (flushing || queue.Count == 0 || Time.unscaledTime < nextFlush) return;
            nextFlush = Time.unscaledTime + FlushInterval;
            // One request per round: take the leading events that share the oldest event's round.
            var batchRound = queue[0].roundId;
            var count = 0;
            while (count < queue.Count && count < MaxBatch && queue[count].roundId == batchRound) count++;
            var body = new StringBuilder("{\"round_id\":").Append(Quote(batchRound)).Append(",\"events\":[");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append(queue[i].json);
            }
            body.Append("]}");
            queue.RemoveRange(0, count);
            StartCoroutine(SendEvents(body.ToString()));
        }

        IEnumerator SendEvents(string body)
        {
            flushing = true;
            using (var request = Post("/events", body))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    warnedOffline = false;
                }
                else
                {
                    // Fail quietly: drop this batch, say so once, and wait before trying again.
                    nextFlush = Time.unscaledTime + OfflineBackoff;
                    if (!warnedOffline) Debug.Log($"[FlyAI] Event log unavailable ({request.error}); game events are being dropped until it is back.");
                    warnedOffline = true;
                }
            }
            flushing = false;
        }

        UnityWebRequest Post(string path, string body)
        {
            var request = new UnityWebRequest(url + path, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = TimeoutSeconds;
            return request;
        }

        // --- Name entry and the board ---------------------------------------------------------------------

        void StartListening()
        {
            StopListening();
            listening = Keyboard.current;
            if (listening != null) listening.onTextInput += OnText;
        }

        void StopListening()
        {
            if (listening != null) listening.onTextInput -= OnText;
            listening = null;
        }

        void OnText(char c)
        {
            if (state != State.EnterName || char.IsControl(c) || playerName.Length >= MaxName) return;
            playerName += c;
        }

        void Update()
        {
            if (url != null) FlushEvents();
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (state == State.EnterName)
            {
                if (keyboard.backspaceKey.wasPressedThisFrame && playerName.Length > 0)
                    playerName = playerName.Substring(0, playerName.Length - 1);
                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    StopListening();
                    state = State.Idle;
                }
                else if ((keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) && playerName.Trim().Length > 0)
                {
                    StopListening();
                    playerName = playerName.Trim();
                    PlayerPrefs.SetString(NameKey, playerName);
                    state = State.Sending;
                    StartCoroutine(Submit(run));
                }
            }
            else if (state == State.Board && keyboard.escapeKey.wasPressedThisFrame)
            {
                state = State.Idle;
            }
        }

        IEnumerator Submit(int forRun)
        {
            var body = JsonUtility.ToJson(new LeaderboardSubmission { round_id = roundId, name = playerName, damage = damage, seconds = seconds, theme = theme });
            using (var request = Post("/scores", body))
            {
                yield return request.SendWebRequest();
                if (forRun != run) yield break; // The room was reloaded while this was in flight.

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var response = JsonUtility.FromJson<LeaderboardResponse>(request.downloadHandler.text);
                    rank = response != null ? response.rank : 0;
                    scores = response?.scores ?? new LeaderboardEntry[0];
                    error = "";
                }
                else
                {
                    error = request.responseCode >= 400 ? $"Leaderboard error {request.responseCode}" : "Leaderboard offline - start leaderboard/server.py";
                    Debug.LogWarning($"[FlyAI] Leaderboard submit failed: {request.error} {request.downloadHandler?.text}");
                }
                state = State.Board;
            }
        }

        static string FormatTime(float value) => value >= 60f ? $"{(int)(value / 60f)}:{value % 60f:00.0}" : $"{value:0.0}s";

        void OnGUI()
        {
            if (state != State.EnterName && state != State.Sending && state != State.Board) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, wordWrap = false, normal = { textColor = new Color(0.86f, 0.9f, 0.86f) } };
                titleStyle = new GUIStyle(style) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }

            var summary = $"Damage ${damage}   Time {FormatTime(seconds)}" + (string.IsNullOrEmpty(theme) ? "" : $"   {theme}");
            string[] lines;
            if (state == State.EnterName)
            {
                var caret = Mathf.Repeat(Time.unscaledTime, 1f) < 0.5f ? "_" : " ";
                lines = new[]
                {
                    summary,
                    "",
                    $"Name:  <b>{Escape(playerName)}</b>{caret}",
                    "",
                    "<color=#9AA39A>Type your name, Enter to submit, Esc to skip</color>",
                };
            }
            else if (state == State.Sending)
            {
                lines = new[] { summary, "", "Sending to the leaderboard..." };
            }
            else
            {
                lines = BoardLines(summary);
            }

            const float lineHeight = 22f;
            var width = 560f;
            var height = 52f + lines.Length * lineHeight + 14f;
            var panel = new Rect((Screen.width - width) / 2f, Mathf.Max(70f, (Screen.height - height) / 2f), width, height);
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(new Rect(panel.x, panel.y + 10f, width, 34f), state == State.Board ? "LEADERBOARD" : "YOU GOT THE FLY!", titleStyle);
            for (var i = 0; i < lines.Length; i++)
                DrawLine(new Rect(panel.x + 18f, panel.y + 52f + i * lineHeight, width - 36f, lineHeight), lines[i]);
        }

        // Board rows use tabs between cells; OnGUI draws each cell in its own column.
        static readonly float[] Columns = { 0f, 40f, 250f, 340f, 420f };

        string[] BoardLines(string summary)
        {
            var count = Mathf.Min(scores.Length, 10);
            var lines = new string[count + 5];
            var n = 0;
            lines[n++] = error.Length > 0 ? $"<color=#FF7C7C>{error}</color>" : rank > 0 ? $"{Escape(playerName)}: rank #{rank}.  {summary}" : summary;
            lines[n++] = "<color=#9AA39A>#\tName\tDamage\tTime\tTheme</color>";
            var marked = false;
            for (var i = 0; i < count; i++)
            {
                var entry = scores[i];
                var mine = !marked && error.Length == 0 && entry.name == playerName && entry.damage == damage && Mathf.Abs(entry.seconds - seconds) < 0.006f;
                marked |= mine;
                var cells = new[] { $"{i + 1}", Escape(entry.name), $"${entry.damage}", FormatTime(entry.seconds), Escape(entry.theme) };
                if (mine) for (var c = 0; c < cells.Length; c++) cells[c] = $"<color=#7CFF9B><b>{cells[c]}</b></color>";
                lines[n++] = string.Join("\t", cells);
            }
            if (count == 0) lines[n++] = error.Length > 0 ? "" : "No scores yet.";
            lines[n++] = "";
            lines[n++] = "<color=#9AA39A>Esc to hide</color>";
            Array.Resize(ref lines, n);
            return lines;
        }

        void DrawLine(Rect row, string line)
        {
            if (line.IndexOf('\t') < 0)
            {
                GUI.Label(row, line, style);
                return;
            }
            // A header line wraps all its cells in one colour tag; split it so each cell keeps the colour.
            var wrapped = line.StartsWith("<color=") && line.EndsWith("</color>") && line.IndexOf("</color>") == line.Length - 8;
            var open = wrapped ? line.Substring(0, line.IndexOf('>') + 1) : "";
            var body = wrapped ? line.Substring(open.Length, line.Length - open.Length - 8) : line;
            var cells = body.Split('\t');
            for (var c = 0; c < cells.Length && c < Columns.Length; c++)
            {
                var right = c + 1 < Columns.Length ? Columns[c + 1] : row.width;
                var text = wrapped ? open + cells[c] + "</color>" : cells[c];
                GUI.Label(new Rect(row.x + Columns[c], row.y, right - Columns[c] - 6f, row.height), text, style);
            }
        }

        // Names are shown with rich text on, so angle brackets must not become tags.
        static string Escape(string value) => (value ?? "").Replace("<", "\u2039").Replace(">", "\u203A");
    }
}
