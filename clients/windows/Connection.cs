using System;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Netlane {
    public sealed class StartRequest { public Profile Profile { get; set; } public int SocksPort { get; set; } }
    // What the main window needs from the engine connection; tests use a fake.
    public interface IConnection : IDisposable {
        bool Active { get; }
        event Action<string> Changed;
        Task StartAsync(Profile profile, bool elevate, int socksPort);
        Task<bool> StopAsync();
    }
    public sealed class Connection : IConnection {
        NamedPipeServerStream pipe;
        StreamWriter writer;
        Process worker;
        public event Action<string> Changed;
        public bool Active { get { try { return worker != null && !worker.HasExited; } catch { return false; } } }
        void Notify(string value) { Action<string> handler = Changed; if (handler != null) { try { handler(value); } catch (Exception error) { EngineLog.Write("event handler failed: " + error); } } }
        public async Task StartAsync(Profile profile, bool elevate, int socksPort) {
            if (Active) throw new InvalidOperationException("VPN уже запущен.");
            profile.Validate();
            EngineSpec.CheckAvailability();
            Dispose();
            string name = "Netlane-" + Guid.NewGuid().ToString("N");
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
            Task accepting = pipe.WaitForConnectionAsync();
            try {
                ProcessStartInfo start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--worker " + name + " " + Process.GetCurrentProcess().Id) { UseShellExecute = elevate, CreateNoWindow = !elevate, WindowStyle = ProcessWindowStyle.Hidden };
                if (elevate) start.Verb = "runas";
                worker = Process.Start(start);
                if (worker == null) throw new IOException("Не удалось запустить VPN.");
                if (await Task.WhenAny(accepting, Task.Delay(20000)) != accepting) throw new IOException("Не получен ответ от VPN-процесса.");
                await accepting;
                uint peer;
                if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out peer) || peer != worker.Id) throw new IOException("Не удалось проверить VPN-процесс.");
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await writer.WriteLineAsync(new JavaScriptSerializer().Serialize(new StartRequest { Profile = profile, SocksPort = socksPort }));
                ReadEvents();
            } catch { if (pipe != null) pipe.Dispose(); pipe = null; throw; }
        }
        async void ReadEvents() {
            NamedPipeServerStream current = pipe;
            try {
                using (StreamReader reader = new StreamReader(current, Encoding.UTF8, false, 4096, true)) {
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null) if (line.Length < 200 && line != "STOPPED" && current == pipe) Notify(line);
                }
            } catch (IOException) {} catch (ObjectDisposedException) {}
            if (current == pipe) {
                Process ending = worker;
                bool stopped = ending == null || await Task.Run(delegate { try { return ending.WaitForExit(5000); } catch { return true; } });
                if (current == pipe) Notify(stopped ? "STOPPED" : "STOP_FAILED");
            }
        }
        public async Task<bool> StopAsync() {
            if (!Active) return true;
            try { if (writer != null) await writer.WriteLineAsync("STOP"); } catch (IOException) {} catch (ObjectDisposedException) {}
            Process current = worker;
            return await Task.Run(delegate { try { return current.WaitForExit(18000); } catch { return true; } });
        }
        // Closing the app must never fail: when the engine already exited, the pipe is broken and
        // flushing the writer throws «Канал нарушен», which used to block closing the window.
        public void Dispose() {
            StreamWriter w = writer; writer = null;
            NamedPipeServerStream p = pipe; pipe = null;
            Process e = worker; worker = null;
            Quietly(w); Quietly(p); Quietly(e);
        }
        static void Quietly(IDisposable item) {
            if (item == null) return;
            try { item.Dispose(); } catch (IOException) {} catch (ObjectDisposedException) {} catch (InvalidOperationException) {}
        }
    }
    internal static class EngineLog {
        internal static readonly string Path = System.IO.Path.Combine(ProfileStore.DataDirectory, "engine.log");
        static readonly object Gate = new object();
        internal static void Start(string header) {
            lock (Gate) {
                try {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    if (File.Exists(Path) && new FileInfo(Path).Length > 262144) File.Delete(Path);
                    File.AppendAllText(Path, "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + header + "\n");
                } catch (IOException) {} catch (UnauthorizedAccessException) {}
            }
        }
        internal static void Write(string line) {
            lock (Gate) { try { File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\n"); } catch (IOException) {} catch (UnauthorizedAccessException) {} }
        }
        internal static string Mask(string config) {
            string[] lines = config.Split('\n');
            for (int i = 0; i < lines.Length; i++) { int equals = lines[i].IndexOf('='); if (equals > 0 && lines[i].Substring(0, equals).Trim() == "password") lines[i] = "password = \"***\""; }
            return String.Join("\n", lines);
        }
    }
    internal static class Native {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("iphlpapi.dll")] internal static extern int GetBestInterface(uint destination, out uint bestIndex);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern bool AllocConsole();
        [DllImport("kernel32.dll")] internal static extern bool FreeConsole();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("kernel32.dll")] internal static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GenerateConsoleCtrlEvent(uint control, uint group);
        internal static bool IsAdmin() { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    internal static class Worker {
        static readonly Regex LogLevel = new Regex(@"\A\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}\.\d+ (TRACE|DEBUG|INFO|WARN|ERROR)\s");
        // Engine log lines carry a level. Only ERROR lines can mean a failed connection: INFO and WARN
        // lines also say "Failed to" for routine events, e.g. DNS sent directly in selective mode.
        // Lines without a level (CLI messages) keep the plain checks.
        internal static bool IsEngineFailure(string line) {
            Match level = LogLevel.Match(line);
            if (level.Success && level.Groups[1].Value != "ERROR") return false;
            return line.Contains("Error:") || line.Contains("Failed to") || line.Contains("Cannot create route");
        }
        // The engine reports "Waiting recovery: to next=<ms> error=..." and reconnects on its own.
        internal static int RecoverySeconds(string line) {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line, @"to next=(\d+)ms");
            int milliseconds;
            if (match.Success && Int32.TryParse(match.Groups[1].Value, out milliseconds) && milliseconds > 0) return Math.Max(1, milliseconds / 1000);
            return 5;
        }
        internal static int Run(string name, int parentId) {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\ANetlane-[0-9a-f]{32}\z")) return 2;
            Native.FreeConsole();
            if (!Native.AllocConsole()) return 2;
            Native.ShowWindow(Native.GetConsoleWindow(), 0);
            string directory = null;
            Process engine = null;
            bool started = false;
            bool signaled = false;
            object outputLock = new object();
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                StreamWriter output = null;
                Action<string> send = delegate(string value) { lock (outputLock) { try { if (output != null) output.WriteLine(value); } catch (IOException) {} catch (ObjectDisposedException) {} } };
                try {
                    pipe.Connect(15000);
                    uint peer;
                    if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out peer) || peer != parentId) return 2;
                    using (Process parent = Process.GetProcessById(parentId)) {
                        if (parent.Handle == IntPtr.Zero) return 2;
                        output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                        StreamReader input = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                        Task<string> requestLine = input.ReadLineAsync();
                        if (!requestLine.Wait(10000) || requestLine.Result == null || requestLine.Result.Length > 32768) return 2;
                        StartRequest request = new JavaScriptSerializer { MaxJsonLength = 32768 }.Deserialize<StartRequest>(requestLine.Result);
                        if (request == null || request.Profile == null) return 2;
                        request.Profile.Validate();
                        EngineLog.Start("connect protocol=" + request.Profile.Protocol + " socks=" + request.SocksPort + " admin=" + Native.IsAdmin() + " engine=" + EngineSpec.Version);
                        if (request.SocksPort == 0 && !Native.IsAdmin()) { EngineLog.Write("worker: not elevated, refusing TUN"); send("ADMIN_REQUIRED"); return 2; }
                        EngineLog.Write("worker: verifying pinned engine hashes");
                        EngineSpec.Verify();
                        EngineLog.Write("worker: engine hashes ok");
                        DirectorySecurity acl = new DirectorySecurity();
                        acl.SetAccessRuleProtection(true, false);
                        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                        directory = Path.Combine(Path.GetTempPath(), "Netlane-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(directory, acl);
                        string config = Path.Combine(directory, "session.toml");
                        string configText = request.Profile.EngineConfig(request.SocksPort);
                        File.WriteAllText(config, configText, new UTF8Encoding(false));
                        EngineLog.Write("session.toml (password masked):\n" + EngineLog.Mask(configText));
                        // The session must be able to reach its endpoint even when another VPN owns
                        // the default route; the exception is removed when the engine exits.
                        RouteException.Add(request.Profile);
                        engine = new Process { StartInfo = new ProcessStartInfo(EngineSpec.Executable, "--config \"" + config + "\"") { WorkingDirectory = EngineSpec.DirectoryPath, UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true } };
                        DataReceivedEventHandler observe = delegate(object sender, DataReceivedEventArgs args) {
                            string line = args.Data;
                            if (line == null) return;
                            EngineLog.Write("engine: " + line);
                            if (line.Contains("Successfully connected to endpoint")) send("CONNECTED");
                            else if (line.Contains("Waiting recovery:")) send("RECONNECTING:" + RecoverySeconds(line));
                            else if (line.Contains("Kill switch active; awaiting explicit stop")) send("KILL_SWITCH_ACTIVE");
                            else if (IsEngineFailure(line)) send("CONNECTION_ERROR");
                        };
                        engine.OutputDataReceived += observe; engine.ErrorDataReceived += observe;
                        engine.Start(); started = true;
                        Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
                        engine.BeginOutputReadLine(); engine.BeginErrorReadLine();
                        send("STARTED");
                        Task<string> command = input.ReadLineAsync();
                        bool stopping = false, warned = false;
                        DateTime stopAt = DateTime.MaxValue;
                        while (!engine.WaitForExit(100)) {
                            bool parentGone;
                            try { parentGone = parent.HasExited; } catch { parentGone = true; }
                            if (!stopping && (parentGone || command.IsCompleted)) {
                                stopping = true; stopAt = DateTime.UtcNow;
                                send("STOPPING");
                                signaled = true;
                                if (!Native.GenerateConsoleCtrlEvent(0, 0)) send("STOP_FAILED");
                            }
                            if (stopping && !warned && (DateTime.UtcNow - stopAt).TotalSeconds > 12) { warned = true; send("STOP_FAILED"); }
                        }
                        engine.WaitForExit();
                        EngineLog.Write("engine exit code " + engine.ExitCode);
                        send(stopping || engine.ExitCode == 0 ? "STOPPED" : "CONNECTION_ERROR");
                    }
                    return 0;
                } catch (Exception error) { EngineLog.Write("worker error: " + error); send("WORKER_ERROR"); return 1; }
                finally {
                    RouteException.Clear();
                    if (started && !engine.HasExited) {
                        if (!signaled) Native.GenerateConsoleCtrlEvent(0, 0);
                        while (!engine.WaitForExit(1000)) send("STOP_FAILED");
                    }
                    if (!started || engine.HasExited) {
                        if (directory != null) { try { File.Delete(Path.Combine(directory, "session.toml")); Directory.Delete(directory); } catch (IOException) {} }
                    }
                    if (engine != null) engine.Dispose();
                    Native.FreeConsole();
                }
            }
        }
    }
}
