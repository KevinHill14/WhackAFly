using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FlyAI
{
    [Serializable]
    public class UploadInfo
    {
        public string url;
        public string[] matrix; // QR code rows, '1' is a dark module.
    }

    // Starts and stops the Python side from inside the game, so nobody has to run Python by hand:
    // the fly brain server (when the game launches), the phone upload server (on demand) and scan ingest.
    // Everything it starts is shut down when the game closes.
    public static class GameServices
    {
        public const int UploadPort = 8080;

        static Process brain;
        static Process upload;
        static Process ingest;
        static DateTime ingestStarted;
        static readonly object IngestLock = new object();
        static CancellationTokenSource cancel;
        static string brainHost = "127.0.0.1";
        static int brainPort = 8765;
        static bool brainWanted;
        static bool brainLaunchedByUs;
        static string brainNote = "";

        public static volatile bool BrainOnline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            brain = null;
            upload = null;
            cancel = null;
            BrainOnline = false;
        }

        // The repository folder: the closest parent of the game that contains brain/ and lidar/.
        // FLYAI_ROOT overrides it.
        public static string Root
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable("FLYAI_ROOT");
                if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
                for (var folder = new DirectoryInfo(Application.dataPath); folder != null; folder = folder.Parent)
                    if (File.Exists(Path.Combine(folder.FullName, "brain", "server.py")) && File.Exists(Path.Combine(folder.FullName, "lidar", "server.py")))
                        return folder.FullName;
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            }
        }

        public static string DataDirectory
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable("FLYAI_DATA");
                return string.IsNullOrEmpty(configured) ? Path.Combine(Root, "data") : Path.GetFullPath(configured);
            }
        }

        // Where phone uploads are kept, and where the file picker opens.
        public static string UploadsDirectory => Path.Combine(DataDirectory, "uploads");

        // The project's own Python if it has one, otherwise whatever "python" is on the PATH.
        static string Python
        {
            get
            {
                var windows = Path.Combine(Root, ".venv", "Scripts", "python.exe");
                if (File.Exists(windows)) return windows;
                var unix = Path.Combine(Root, ".venv", "bin", "python");
                return File.Exists(unix) ? unix : "python";
            }
        }

        // --- Fly brain ------------------------------------------------------------------------------------

        // "Running", "Starting...", or the reason it is not running.
        public static string BrainStatus()
        {
            if (!brainWanted) return brainNote;
            if (BrainOnline) return brainLaunchedByUs ? "Running" : "Running (already started)";
            if (brain != null && brain.HasExited) return $"Stopped (exit code {SafeExitCode(brain)}). Is numpy installed in .venv?";
            return string.IsNullOrEmpty(brainNote) ? "Starting..." : brainNote;
        }

        public static bool BrainReady => BrainOnline || !brainWanted;

        static int SafeExitCode(Process process)
        {
            try { return process.ExitCode; } catch (Exception) { return -1; }
        }

        public static void StartBrain()
        {
            if (cancel != null) return;
            var url = FlyBrainClient.ResolveUrl();
            if (url == null)
            {
                brainNote = "Switched off (FLYAI_BRAIN=off): the fly will not react";
                return;
            }
            brainWanted = true;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                brainHost = uri.Host;
                brainPort = uri.Port;
            }

            cancel = new CancellationTokenSource();
            var token = cancel.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    BrainOnline = PortOpen(brainHost, brainPort);
                    try { await Task.Delay(1000, token); } catch (OperationCanceledException) { }
                }
            });

            var local = brainHost == "127.0.0.1" || brainHost == "localhost";
            if (!local)
            {
                brainNote = $"Using the brain at {brainHost}:{brainPort}";
                return;
            }
            if (PortOpen(brainHost, brainPort))
            {
                BrainOnline = true;
                return;
            }
            brain = Launch(Python, Quote(Path.Combine(Root, "brain", "server.py")), false);
            brainLaunchedByUs = brain != null;
            if (brain == null) brainNote = "Could not start Python. Is the project's .venv set up?";
        }

        static bool PortOpen(string host, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.BeginConnect(host, port, null, null);
                    var connected = connect.AsyncWaitHandle.WaitOne(250) && client.Connected;
                    if (connected) client.EndConnect(connect);
                    return connected;
                }
            }
            catch (Exception) { return false; }
        }

        // The Wi-Fi network this computer is on, so the menu can tell the player which one the phone must join.
        // Nothing is stored, and no password is read. Empty if it cannot be found.
        public static Task<string> WifiNameAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var windows = Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer;
                    var info = new ProcessStartInfo(windows ? "netsh" : "iwgetid", windows ? "wlan show interfaces" : "-r")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var process = Process.Start(info))
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(4000);
                        if (!windows) return output.Trim();
                        var match = Regex.Match(output, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
                        return match.Success ? match.Groups[1].Value : "";
                    }
                }
                catch (Exception) { return ""; }
            });
        }

        // --- Phone upload server ------------------------------------------------------------------------

        static string UploadInfoPath => Path.Combine(Application.temporaryCachePath, "flyai-upload.json");

        public static bool StartUploadServer()
        {
            if (upload != null && !upload.HasExited) return true;
            try { File.Delete(UploadInfoPath); } catch (Exception) { }
            Directory.CreateDirectory(Application.temporaryCachePath);
            upload = Launch(Python, $"{Quote(Path.Combine(Root, "lidar", "server.py"))} --port {UploadPort} --info {Quote(UploadInfoPath)}", false);
            return upload != null;
        }

        public static bool UploadServerExited => upload != null && upload.HasExited;

        public static UploadInfo ReadUploadInfo()
        {
            try
            {
                if (!File.Exists(UploadInfoPath)) return null;
                var info = JsonUtility.FromJson<UploadInfo>(File.ReadAllText(UploadInfoPath));
                return info != null && !string.IsNullOrEmpty(info.url) && info.matrix != null && info.matrix.Length > 0 ? info : null;
            }
            catch (Exception) { return null; }
        }

        public static void StopUploadServer()
        {
            KillTree(upload);
            upload = null;
        }

        // --- Scan ingest ----------------------------------------------------------------------------------

        // Processes a LiDAR file (a .zip or .glb) and makes it the current level. Returns an error message, or null.
        public static Task<string> IngestAsync(string file)
        {
            var python = Python;
            var script = Path.Combine(Root, "lidar", "ingest.py");
            var root = Root;
            return Task.Run(() =>
            {
                try
                {
                    var info = new ProcessStartInfo(python, $"{Quote(script)} {Quote(file)}")
                    {
                        WorkingDirectory = root,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };
                    info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    using (var process = Process.Start(info))
                    {
                        lock (IngestLock)
                        {
                            ingest = process;
                            ingestStarted = DateTime.UtcNow;
                        }
                        var errors = process.StandardError.ReadToEndAsync();
                        process.StandardOutput.ReadToEnd();
                        if (!process.WaitForExit(300000))
                        {
                            KillTree(process);
                            return "Processing took too long.";
                        }
                        lock (IngestLock)
                        {
                            if (ingest == process) ingest = null;
                        }
                        if (process.ExitCode == 0) return null;
                        var message = errors.Result.Trim();
                        var lines = message.Split('\n');
                        return lines.Length > 0 && lines[lines.Length - 1].Trim().Length > 0 ? lines[lines.Length - 1].Trim() : "Processing failed.";
                    }
                }
                catch (Exception error)
                {
                    return "Could not run Python: " + error.Message;
                }
            });
        }

        // Stops the scan that is being processed (the player chose another level) and removes what it had started.
        public static void CancelIngest()
        {
            Process running;
            DateTime started;
            lock (IngestLock)
            {
                running = ingest;
                started = ingestStarted;
                ingest = null;
            }
            if (running == null) return;
            KillTree(running);
            try
            {
                var scans = Path.Combine(DataDirectory, "scans");
                if (!Directory.Exists(scans)) return;
                var latest = File.ReadAllText(Path.Combine(DataDirectory, "latest.json"));
                foreach (var directory in new DirectoryInfo(scans).GetDirectories())
                    if (directory.CreationTimeUtc >= started.AddSeconds(-1) && !latest.Contains(directory.Name)) directory.Delete(true);
            }
            catch (Exception) { /* Leftovers are harmless. */ }
        }

        // --- Shutdown -------------------------------------------------------------------------------------

        public static void Shutdown()
        {
            cancel?.Cancel();
            cancel = null;
            CancelIngest();
            KillTree(upload);
            upload = null;
            if (brainLaunchedByUs) KillTree(brain);
            brain = null;
            brainLaunchedByUs = false;
            brainWanted = false;
            BrainOnline = false;
        }

        // --- Processes ------------------------------------------------------------------------------------

        static string Quote(string value) => "\"" + value + "\"";

        static Process Launch(string file, string arguments, bool redirect)
        {
            try
            {
                var info = new ProcessStartInfo(file, arguments)
                {
                    WorkingDirectory = Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = redirect,
                    RedirectStandardError = redirect,
                };
                info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                var process = Process.Start(info);
                Debug.Log($"[FlyAI] Started {Path.GetFileName(file)} {arguments}");
                return process;
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[FlyAI] Could not start {file}: {error.Message}");
                return null;
            }
        }

        // A Windows virtual-environment python.exe starts the real interpreter as a child, so the whole tree must go.
        static void KillTree(Process process)
        {
            if (process == null) return;
            try
            {
                if (process.HasExited) return;
                if (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer)
                {
                    var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F") { CreateNoWindow = true, UseShellExecute = false });
                    kill?.WaitForExit(3000);
                }
                else process.Kill();
            }
            catch (Exception) { /* Already gone. */ }
        }
    }
}
