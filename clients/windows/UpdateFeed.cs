using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Netlane {
    // The public update channel: a small latest.json in a public repository, no token in the client.
    // Url is the main download; Mirror, when set, is tried if the main one fails.
    internal sealed class UpdateManifest {
        internal string Version, Url, Sha256, Notes, Mirror;
    }

    // Built with csc.exe directly, the assembly carries no TargetFramework, so .NET Framework offers only
    // TLS 1.0 by default. GitHub and most subscription servers accept TLS 1.2+ only. Every HTTPS request
    // goes through this before it is made, whatever runs first: the update check or a subscription.
    internal static class Tls {
        internal static void Enable() {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        }
    }

    internal static class UpdateFeed {
        // The channel is the "updates" branch of the public repository: the release workflow writes
        // latest.json and the archive there when a release is published. raw.githubusercontent.com is
        // what works through restrictive proxies; release-asset downloads redirect to another host that
        // some of them refuse, so the release asset is only the mirror.
        internal const string Repository = "divezh/netlane-windows";
        internal const string ManifestUrl = "https://raw.githubusercontent.com/" + Repository + "/updates/latest.json";
        // Fallback when the channel cannot be read: the latest published release through the GitHub API.
        internal const string ReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
        internal const string AssetName = "Netlane-windows-x64.zip";

        internal static async Task<UpdateManifest> FetchAsync() {
            string channelError;
            try { return Parse(await GetAsync(ManifestUrl, "application/json")); }
            catch (FormatException error) { channelError = error.Message; }
            catch (WebException error) { channelError = error.Message; }
            try { return ParseRelease(await GetAsync(ReleaseUrl, "application/vnd.github+json")); }
            catch (WebException error) { throw new FormatException("Канал обновлений недоступен: " + channelError + " / " + error.Message); }
        }

        static async Task<string> GetAsync(string url, string accept) {
            Tls.Enable();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "Netlane/" + Program.Version;
            request.Accept = accept;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = true;
            using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
            using (Stream body = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(body, Encoding.UTF8))
                return await reader.ReadToEndAsync();
        }

        // GitHub's "latest release": tag vX.Y.Z, the archive asset and the SHA-256 digest GitHub computes for it.
        internal static UpdateManifest ParseRelease(string json) {
            Dictionary<string, object> root = Deserialize(json, 1 << 21);
            if (root == null) throw new FormatException("GitHub вернул неожиданный ответ о релизе.");
            object flag;
            if ((root.TryGetValue("draft", out flag) && flag is bool && (bool)flag) || (root.TryGetValue("prerelease", out flag) && flag is bool && (bool)flag))
                throw new FormatException("Последний релиз — предварительный.");
            string tag = Text(root, "tag_name");
            if (tag != null && tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(1);
            object assetsValue;
            object[] assets = root.TryGetValue("assets", out assetsValue) ? assetsValue as object[] : null;
            if (assets == null) { System.Collections.ArrayList list = assetsValue as System.Collections.ArrayList; if (list != null) assets = list.ToArray(); }
            Dictionary<string, object> asset = assets == null ? null : assets.OfType<Dictionary<string, object>>().FirstOrDefault(a => Text(a, "name") == AssetName);
            if (asset == null) throw new FormatException("В последнем релизе нет " + AssetName + ".");
            string digest = Text(asset, "digest");
            string sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : null;
            return Validate(tag, Text(asset, "browser_download_url"), sha, null, null);
        }

        internal static UpdateManifest Parse(string json) {
            Dictionary<string, object> root = Deserialize(json, 1 << 18);
            if (root == null) throw new FormatException("Канал обновлений вернул неожиданный ответ.");
            return Validate(Text(root, "version"), Text(root, "url"), Text(root, "sha256"), Text(root, "notes"), Text(root, "mirror"));
        }

        static Dictionary<string, object> Deserialize(string json, int limit) {
            try { return new JavaScriptSerializer { MaxJsonLength = limit }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        static bool IsHttps(string url) {
            Uri parsed;
            return url != null && url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out parsed) && parsed.Scheme == Uri.UriSchemeHttps;
        }

        static UpdateManifest Validate(string version, string url, string sha, string notes, string mirror) {
            Version parsed;
            if (version == null || version.Length > 32 || !Version.TryParse(version, out parsed))
                throw new FormatException("Канал обновлений не указал версию.");
            if (!IsHttps(url))
                throw new FormatException("Канал обновлений не указал ссылку на архив.");
            if (sha == null || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{64}\z"))
                throw new FormatException("Канал обновлений не указал SHA-256 архива.");
            if (notes != null) {
                notes = notes.Trim();
                if (notes.Length == 0 || notes.Length > 500 || notes.Any(Char.IsControl)) notes = null;
            }
            if (!IsHttps(mirror) || mirror == url) mirror = null;
            return new UpdateManifest { Version = version, Url = url, Sha256 = sha.ToLowerInvariant(), Notes = notes, Mirror = mirror };
        }

        static string Text(Dictionary<string, object> map, string key) {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value as string : null;
        }
    }
}
