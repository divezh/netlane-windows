using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Netlane {
    internal static class Updater {
        internal static string Root {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Netlane", "updates"); }
        }

        internal static async Task<string> DownloadAndUnpackAsync(UpdateManifest update, IProgress<int> progress) {
            string folder = Path.Combine(Root, update.Version);
            Directory.CreateDirectory(folder);
            string zip = Path.Combine(folder, "Netlane-windows-x64.zip");
            bool useMirror = false;
            try { await DownloadAsync(update.Url, zip, progress); }
            catch (WebException error) {
                if (update.Mirror == null) throw;
                EngineLog.Write("updates: " + update.Url + ": " + error.Message + "; trying the mirror");
                useMirror = true;
            }
            if (useMirror) await DownloadAsync(update.Mirror, zip, progress);
            string actual;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(zip))
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (actual != update.Sha256.ToLowerInvariant()) {
                try { File.Delete(zip); } catch (IOException) { }
                throw new FormatException("Контрольная сумма обновления не совпала.");
            }
            string unpacked = Path.Combine(folder, "unpacked");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            ZipFile.ExtractToDirectory(zip, unpacked);
            if (!File.Exists(Path.Combine(unpacked, "Netlane.exe")))
                throw new FormatException("В архиве обновления нет Netlane.exe.");
            return unpacked;
        }

        static async Task DownloadAsync(string url, string path, IProgress<int> progress) {
            Tls.Enable();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "Netlane/" + Program.Version;
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            request.AllowAutoRedirect = true;
            using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
            using (Stream body = response.GetResponseStream())
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
                long total = response.ContentLength, read = 0;
                byte[] buffer = new byte[65536];
                int chunk, last = -1;
                while ((chunk = await body.ReadAsync(buffer, 0, buffer.Length)) > 0) {
                    await file.WriteAsync(buffer, 0, chunk);
                    read += chunk;
                    if (total > 0) {
                        int percent = (int)Math.Min(100, read * 100 / total);
                        if (percent != last) { last = percent; if (progress != null) progress.Report(percent); }
                    }
                }
            }
            if (progress != null) progress.Report(100);
        }

        // The downloaded Netlane.exe installs itself: started as «Netlane.exe --apply-update <target> <pid>»
        // from the unpacked folder, it waits for this process to exit, copies its own folder over the
        // installed one and starts the installed copy. No script and no shell are involved.
        internal static void LaunchInstaller(string unpacked, string version) {
            string target = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string arguments = "--apply-update \"" + target + "\" " + Process.GetCurrentProcess().Id;
            Process.Start(new ProcessStartInfo(Path.Combine(unpacked, "Netlane.exe"), arguments) { UseShellExecute = false, WorkingDirectory = unpacked });
        }

        static string Log { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Netlane", "update.log"); } }
        static void Note(string text) { try { File.AppendAllText(Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine); } catch (Exception) { } }

        internal static int Apply(string target, int parentPid) {
            string source = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try {
                if (String.IsNullOrEmpty(target) || !File.Exists(Path.Combine(target, "Netlane.exe"))) { Note("apply: no Netlane.exe in " + target); return 2; }
                if (String.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) { Note("apply: source is the target"); return 2; }
                // Gone already, or not ours to wait for (access denied): either way go on.
                try { using (Process parent = Process.GetProcessById(parentPid)) parent.WaitForExit(60000); } catch (Exception) { }
                WaitForExit("Netlane", 30000); WaitForExit("trusttunnel_client", 30000);
                Exception last = null;
                for (int attempt = 0; attempt < 15; attempt++) {
                    try { CopyTree(source, target); last = null; break; }
                    catch (IOException error) { last = error; } catch (UnauthorizedAccessException error) { last = error; }
                    System.Threading.Thread.Sleep(1000);
                }
                Note(last == null ? "apply: copied " + source + " to " + target : "apply: copy failed: " + last.Message);
            } catch (Exception error) { Note("apply: " + error.Message); }
            try { Process.Start(new ProcessStartInfo(Path.Combine(target, "Netlane.exe")) { UseShellExecute = true, WorkingDirectory = target }); }
            catch (Exception error) { Note("apply: start failed: " + error.Message); return 1; }
            return 0;
        }
        // Other copies of the app and the engine still hold files in the target; this one is excluded.
        static void WaitForExit(string name, int milliseconds) {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcessesByName(name)) {
                using (process) { if (process.Id == self) continue; try { process.WaitForExit(milliseconds); } catch (Exception) { } }
            }
        }
        internal static void CopyTree(string source, string target) {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (string folder in Directory.GetDirectories(source)) CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }
}
