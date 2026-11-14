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
        internal string Version, Url, Sha256, Notes, Mirror, SignatureUrl;
    }

    // The owner's release key (ECDSA P-256, SHA-256). Its private half is not on GitHub: an update is
    // installed only when Netlane-windows-x64.zip.sig verifies with this key, so a stolen GitHub account
    // cannot ship code to installed apps. The same key signs the server and the macOS client.
    internal static class ReleaseSignature {
        static readonly byte[] X = Hex("b5ca1516f75fb306721650eabc73d1e194bbc3140babb8b0347b6ffa7c741817");
        static readonly byte[] Y = Hex("4cb1f46a37124412e3a6250857f0af3b9f9f5f27cc792bb48554f4f711b3d676");

        internal static bool Verify(byte[] data, byte[] der) {
            byte[] raw = Raw(der);
            if (raw == null || data == null) return false;
            // BCRYPT_ECCPUBLIC_BLOB: "ECS1", key length 32, then X and Y.
            byte[] blob = new byte[8 + 64];
            BitConverter.GetBytes(0x31534345).CopyTo(blob, 0);
            BitConverter.GetBytes(32).CopyTo(blob, 4);
            X.CopyTo(blob, 8); Y.CopyTo(blob, 40);
            try {
                using (System.Security.Cryptography.CngKey key = System.Security.Cryptography.CngKey.Import(blob, System.Security.Cryptography.CngKeyBlobFormat.EccPublicBlob))
                using (System.Security.Cryptography.ECDsaCng ecdsa = new System.Security.Cryptography.ECDsaCng(key)) {
                    ecdsa.HashAlgorithm = System.Security.Cryptography.CngAlgorithm.Sha256;
                    return ecdsa.VerifyData(data, raw);
                }
            } catch (System.Security.Cryptography.CryptographicException) { return false; }
        }

        // openssl writes the signature as DER SEQUENCE { INTEGER r, INTEGER s }; CNG wants r || s, 32 bytes each.
        internal static byte[] Raw(byte[] der) {
            if (der == null || der.Length < 8 || der.Length > 72 || der[0] != 0x30 || der[1] != der.Length - 2) return null;
            byte[] raw = new byte[64];
            int at = 2;
            for (int part = 0; part < 2; part++) {
                if (at + 2 > der.Length || der[at] != 0x02) return null;
                int length = der[at + 1];
                at += 2;
                if (length < 1 || length > 33 || at + length > der.Length) return null;
                int start = at, count = length;
                while (count > 0 && der[start] == 0) { start++; count--; }
                if (count > 32) return null;
                Buffer.BlockCopy(der, start, raw, part * 32 + 32 - count, count);
                at += length;
            }
            return at == der.Length ? raw : null;
        }

        static byte[] Hex(string text) {
            byte[] bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return bytes;
        }
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
        // Updates come from one place: the latest published release of the public repository (drafts and
        // pre-releases are never "latest"). The archive is its asset, checked by the SHA-256 digest GitHub computes.
        internal const string Repository = "divezh/netlane-windows";
        internal const string ReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
        internal const string AssetName = "Netlane-windows-x64.zip";

        internal static async Task<UpdateManifest> FetchAsync() {
            try { return ParseRelease(await GetAsync(ReleaseUrl, "application/vnd.github+json")); }
            catch (WebException error) { throw new FormatException("Не удалось узнать последний релиз: " + error.Message); }
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
            // An unsigned release is never installed, so it is not offered either.
            Dictionary<string, object> signature = assets.OfType<Dictionary<string, object>>().FirstOrDefault(a => Text(a, "name") == AssetName + ".sig");
            string signatureUrl = signature == null ? null : Text(signature, "browser_download_url");
            if (!IsHttps(signatureUrl)) throw new FormatException("Последний релиз не подписан владельцем.");
            UpdateManifest manifest = Validate(tag, Text(asset, "browser_download_url"), sha, ReleaseNotes(Text(root, "body")), null);
            manifest.SignatureUrl = signatureUrl;
            return manifest;
        }

        // The dialog shows one short line: the points of the release notes (the CHANGELOG section), joined.
        internal static string ReleaseNotes(string body) {
            if (body == null) return null;
            List<string> points = new List<string>();
            foreach (string raw in body.Replace("\r", "").Split('\n')) {
                string line = raw.Trim();
                if (line.StartsWith("- ") || line.StartsWith("* ")) points.Add(line.Substring(2).Replace("`", "").Replace("**", "").Trim());
            }
            string notes = String.Join(" ", points).Trim();
            if (notes.Length > 240) notes = notes.Substring(0, 240).TrimEnd() + "…";
            return notes.Length == 0 ? null : notes;
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
                throw new FormatException("В последнем релизе нет версии.");
            if (!IsHttps(url))
                throw new FormatException("В последнем релизе нет ссылки на архив.");
            if (sha == null || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{64}\z"))
                throw new FormatException("У архива в последнем релизе нет SHA-256.");
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
