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
    internal sealed class UpdateManifest {
        internal string Version, Url, Sha256, Notes;
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
        // raw.githubusercontent.com is the channel that works through restrictive proxies; the
        // release-asset URL redirects to objects.githubusercontent.com and returns 502 there.
        internal const string ManifestUrl = "https://raw.githubusercontent.com/divezh/netlane-updates/main/latest.json";

        internal static async Task<UpdateManifest> FetchAsync() {
            Tls.Enable();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ManifestUrl);
            request.Method = "GET";
            request.UserAgent = "Netlane/" + Program.Version;
            request.Accept = "application/json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = true;
            try {
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (Stream body = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(body, Encoding.UTF8))
                    return Parse(await reader.ReadToEndAsync());
            } catch (WebException error) {
                throw new FormatException("Канал обновлений недоступен: " + error.Message);
            }
        }

        internal static UpdateManifest Parse(string json) {
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer { MaxJsonLength = 1 << 18 }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { root = null; }
            catch (InvalidOperationException) { root = null; }
            if (root == null) throw new FormatException("Канал обновлений вернул неожиданный ответ.");
            string version = Text(root, "version"), url = Text(root, "url"), sha = Text(root, "sha256"), notes = Text(root, "notes");
            Version parsed;
            if (version == null || version.Length > 32 || !Version.TryParse(version, out parsed))
                throw new FormatException("Канал обновлений не указал версию.");
            if (url == null || url.Length > 2048 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Канал обновлений не указал ссылку на архив.");
            if (sha == null || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{64}\z"))
                throw new FormatException("Канал обновлений не указал SHA-256 архива.");
            if (notes != null) {
                notes = notes.Trim();
                if (notes.Length > 500 || notes.Any(Char.IsControl)) notes = null;
            }
            return new UpdateManifest { Version = version, Url = url, Sha256 = sha.ToLowerInvariant(), Notes = notes };
        }

        static string Text(Dictionary<string, object> map, string key) {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value as string : null;
        }
    }
}
