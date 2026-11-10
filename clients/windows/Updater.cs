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
            await DownloadAsync(update.Url, zip, progress);
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

        internal static void LaunchInstaller(string unpacked, string version) {
            string target = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string folder = Path.Combine(Root, version);
            string helper = Path.Combine(folder, "apply-update.ps1");
            File.WriteAllText(helper, Script, new UTF8Encoding(false));
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            string arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + helper + "\" -ParentPid " + Process.GetCurrentProcess().Id
                + " -Source \"" + unpacked + "\" -Target \"" + target + "\" -Exe \"Netlane.exe\"";
            Process.Start(new ProcessStartInfo(powershell, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        // Waits for the running app to exit, replaces the files with robocopy and starts it again.
        const string Script = @"param([int]$ParentPid,[string]$Source,[string]$Target,[string]$Exe)
$log = Join-Path $env:LOCALAPPDATA 'Netlane\update.log'
function Write-Log([string]$Text) { try { Add-Content -LiteralPath $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $Text) } catch { } }
try {
  try { Wait-Process -Id $ParentPid -Timeout 60 -ErrorAction Stop } catch { }
  Get-Process -Name Netlane,trusttunnel_client -ErrorAction SilentlyContinue | ForEach-Object { try { Wait-Process -Id $_.Id -Timeout 30 -ErrorAction Stop } catch { } }
  Start-Sleep -Seconds 1
  for ($i = 0; $i -lt 15; $i++) {
    & robocopy.exe $Source $Target /E /IS /IT /R:2 /W:1 | Out-Null
    if ($LASTEXITCODE -lt 8) { Write-Log ('copied ' + $Source); break }
    Start-Sleep -Seconds 1
  }
  if ($LASTEXITCODE -ge 8) { Write-Log ('copy failed ' + $LASTEXITCODE) }
} catch { Write-Log ('error: ' + $_) }
Start-Process -FilePath (Join-Path $Target $Exe)
";
    }
}
