using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Netlane {
    public sealed class Profile {
        public string Hostname { get; set; }
        public string Address { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Protocol { get; set; }
        public bool AntiDpi { get; set; }
        public List<string> DnsServers { get; set; }
        public bool KillSwitch { get; set; }
        public Routing Routing { get; set; }
        // Display name from the tt:// link (tag 12); not used by the engine.
        public string Name { get; set; }
        public Profile() { Protocol = "http2"; KillSwitch = true; Routing = new Routing(); }
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 65536 };
        public void Validate() {
            if (String.IsNullOrEmpty(Hostname) || Hostname.Length > 253 || !Regex.IsMatch(Hostname, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\z") || Hostname.Contains("..")) throw new FormatException("Неверное имя сервера.");
            Uri uri;
            if (String.IsNullOrEmpty(Address) || Address.Length > 300 || !Uri.TryCreate("https://" + Address, UriKind.Absolute, out uri) || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.Host.Length == 0 || uri.Port < 1 || Address.Any(Char.IsWhiteSpace) || Address.Contains("\\") || Address.Contains("/") || Address.Contains("@")) throw new FormatException("Неверный адрес VPN-сервера.");
            if (String.IsNullOrEmpty(Username) || !Regex.IsMatch(Username, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\z")) throw new FormatException("Неверное имя пользователя.");
            if (Password == null || Utf8.GetByteCount(Password) < 12 || Utf8.GetByteCount(Password) > 1024) throw new FormatException("Неверная длина пароля в профиле.");
            if (Protocol != "http2" && Protocol != "http3") throw new FormatException("Поддерживаются только HTTP/2 и HTTP/3.");
            if (Routing == null) Routing = new Routing();
            Routing.Validate();
            DnsServers = DnsSettings.Normalize(DnsServers);
        }
        public static Profile Import(string input) {
            if (input == null || input.Length > 32768) throw new FormatException("Профиль слишком большой.");
            input = input.Trim().TrimStart('\uFEFF');
            Profile profile = input.StartsWith("tt://?", StringComparison.Ordinal) ? FromLink(input) : FromToml(input);
            profile.Validate(); return profile;
        }
        static Profile FromLink(string input) {
            string payload = input.Substring(6);
            if (!Regex.IsMatch(payload, @"\A[A-Za-z0-9_-]+\z") || payload.Length % 4 == 1) throw new FormatException("Повреждённая ссылка tt://.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/') + new string('=', (4 - payload.Length % 4) % 4)); }
            catch (FormatException) { throw new FormatException("Повреждённая ссылка tt://."); }
            Dictionary<ulong, byte[]> fields = new Dictionary<ulong, byte[]>();
            int offset = 0;
            while (offset < bytes.Length) {
                ulong tag = Integer(bytes, ref offset), length = Integer(bytes, ref offset);
                if (length > (ulong)(bytes.Length - offset) || fields.ContainsKey(tag)) throw new FormatException("Повреждённые или повторяющиеся поля профиля.");
                if (!new ulong[] { 0, 1, 2, 3, 4, 5, 6, 9, 10, 12 }.Contains(tag)) throw new FormatException("Это расширенный профиль. Экспортируйте профиль из нашей панели.");
                fields.Add(tag, bytes.Skip(offset).Take((int)length).ToArray()); offset += (int)length;
            }
            if (!fields.ContainsKey(0) || fields[0].Length != 1 || fields[0][0] != 1) throw new FormatException("Нужна ссылка tt:// версии 1 из панели.");
            if (Bool(fields, 4)) throw new FormatException("Профили с отключённой проверкой TLS запрещены.");
            Bool(fields, 3);
            Profile p = new Profile { Hostname = Text(fields, 1), Address = Text(fields, 2), Username = Text(fields, 5), Password = Text(fields, 6), AntiDpi = Bool(fields, 10) };
            byte[] protocol;
            if (fields.TryGetValue(9, out protocol)) {
                if (protocol.Length != 1 || (protocol[0] != 1 && protocol[0] != 2)) throw new FormatException("Неизвестный транспорт.");
                p.Protocol = protocol[0] == 1 ? "http2" : "http3";
            }
            byte[] name;
            if (fields.TryGetValue(12, out name)) { try { p.Name = new UTF8Encoding(false, true).GetString(name); } catch (ArgumentException) { throw new FormatException("Повреждённое имя профиля."); } }
            return p;
        }
        static ulong Integer(byte[] bytes, ref int offset) {
            if (offset >= bytes.Length) throw new FormatException("Обрезанный профиль.");
            int length = 1 << (bytes[offset] >> 6);
            if (bytes.Length - offset < length) throw new FormatException("Обрезанный профиль.");
            ulong result = (ulong)(bytes[offset++] & 63);
            for (int i = 1; i < length; i++) result = (result << 8) | bytes[offset++];
            return result;
        }
        static string Text(Dictionary<ulong, byte[]> fields, ulong tag) {
            byte[] bytes;
            if (!fields.TryGetValue(tag, out bytes)) throw new FormatException("В профиле не хватает обязательных полей.");
            try { return Utf8.GetString(bytes); } catch (DecoderFallbackException) { throw new FormatException("Неверная кодировка профиля."); }
        }
        static bool Bool(Dictionary<ulong, byte[]> fields, ulong tag) {
            byte[] bytes;
            if (!fields.TryGetValue(tag, out bytes)) return false;
            if (bytes.Length != 1 || bytes[0] > 1) throw new FormatException("Повреждённый флаг профиля.");
            return bytes[0] == 1;
        }
        static Profile FromToml(string input) {
            Dictionary<string, string> values = new Dictionary<string, string>();
            foreach (string original in input.Replace("\r", "").Split('\n')) {
                string line = original.Trim();
                if (line == "" || line.StartsWith("#")) continue;
                int split = line.IndexOf('=');
                if (split < 1) throw new FormatException("Нужен TOML-профиль, экспортированный панелью.");
                string key = line.Substring(0, split).Trim(), value = line.Substring(split + 1).Trim();
                if (!new string[] { "hostname", "addresses", "has_ipv6", "username", "password", "skip_verification", "upstream_protocol", "anti_dpi" }.Contains(key) || values.ContainsKey(key)) throw new FormatException("Неизвестное или повторное поле TOML. Экспортируйте профиль из панели.");
                values.Add(key, value);
            }
            if (Boolean(values, "skip_verification", false)) throw new FormatException("Профили с отключённой проверкой TLS запрещены.");
            Boolean(values, "has_ipv6", false);
            string addresses = Required(values, "addresses");
            if (!addresses.StartsWith("[") || !addresses.EndsWith("]")) throw new FormatException("Неверный адрес в профиле.");
            return new Profile { Hostname = StringValue(Required(values, "hostname")), Address = StringValue(addresses.Substring(1, addresses.Length - 2).Trim()), Username = StringValue(Required(values, "username")), Password = StringValue(Required(values, "password")), Protocol = values.ContainsKey("upstream_protocol") ? StringValue(values["upstream_protocol"]) : "http2", AntiDpi = Boolean(values, "anti_dpi", false) };
        }
        static string Required(Dictionary<string, string> values, string name) { string value; if (!values.TryGetValue(name, out value)) throw new FormatException("Не хватает поля " + name + "."); return value; }
        static bool Boolean(Dictionary<string, string> values, string name, bool fallback) { string value; if (!values.TryGetValue(name, out value)) return fallback; if (value != "true" && value != "false") throw new FormatException("Неверный флаг " + name + "."); return value == "true"; }
        static string StringValue(string value) {
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') throw new FormatException("Ожидается строка TOML из панели.");
            try { string result = Json.Deserialize<string>(value); if (result == null) throw new FormatException(); return result; }
            catch (Exception) { throw new FormatException("Повреждённая строка TOML."); }
        }
        public string EngineConfig(int socksPort) {
            Validate();
            if (socksPort != 0 && (socksPort < 1024 || socksPort > 65535)) throw new FormatException("Неверный тестовый порт.");
            string endpoint = "[endpoint]\nhostname = " + Quote(Hostname) + "\naddresses = [" + Quote(Address) + "]\nhas_ipv6 = false\nusername = " + Quote(Username) + "\npassword = " + Quote(Password) + "\nskip_verification = false\nupstream_protocol = " + Quote(Protocol) + "\nanti_dpi = " + (AntiDpi ? "true" : "false") + "\n";
            if(DnsServers.Count>0)endpoint+="dns_upstreams = ["+String.Join(", ",DnsServers.Select(d=>Quote(d)))+"]\n";
            string listener = socksPort == 0 ? "[listener.tun]\ndevice_name = \"Netlane\"\nchange_system_dns = true\nincluded_routes = [\"0.0.0.0/0\"]\nexcluded_routes = [\"0.0.0.0/8\", \"10.0.0.0/8\", \"169.254.0.0/16\", \"172.16.0.0/12\", \"192.168.0.0/16\", \"224.0.0.0/3\"]\n" : "[listener.socks]\naddress = \"127.0.0.1:" + socksPort + "\"\n";
            if (Routing.Applications.Count > 0 && !EngineSpec.SupportsApplications) throw new FormatException("Для правил приложений нужна сборка Netlane с расширенным движком.");
            if (Routing.Applications.Any(a => a.Name != null) && !EngineSpec.SupportsApplicationNames) throw new FormatException("Правила по имени процесса .exe поддерживаются только в последней сборке Netlane. Обновите полный комплект или используйте выбор приложения по пути.");
            // With DNS servers set (by the user or the panel) the patched engine sends every query to them through the
            // VPN; otherwise names outside the rule set, applications' lookups included, go to the home resolver.
            string dnsEverywhere = DnsServers.Count > 0 && EngineSpec.SupportsApplications ? "dns_all_through_vpn = true\n" : "";
            return "loglevel = \"info\"\nkillswitch_enabled = " + (socksPort == 0 && KillSwitch ? "true" : "false") + "\n" + dnsEverywhere + Routing.EngineConfig() + endpoint + listener;
        }
        static string Quote(string value) { return Json.Serialize(value); }
        public string Serialize() { Validate(); string value=Json.Serialize(this);if(Utf8.GetByteCount(value)>30000)throw new FormatException("Список правил слишком большой для профиля. Сократите длинные домены или пути приложений.");return value; }
        public static Profile Deserialize(string value) { Profile p = Json.Deserialize<Profile>(value); if (p == null) throw new FormatException("Пустой профиль."); p.Validate(); return p; }
    }
    public static class ProfileStore {
        internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Netlane.profile.v1");
        // Profiles saved before the rename: read once, then re-encrypted with the current entropy.
        internal static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("WinTrustTunnel.profile.v1");
        internal const string DataFolder = "Netlane";
        internal const string LegacyDataFolder = "WinTrustTunnel";
        internal static string DataDirectory { get { return MigrateDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); } }
        // Moves the profile and log folder of a former app name once; keeps using it if the move is impossible.
        internal static string MigrateDataDirectory(string root) {
            string current = Path.Combine(root, DataFolder);
            if (Directory.Exists(current)) return current;
            string legacy = Path.Combine(root, LegacyDataFolder);
            if (Directory.Exists(legacy)) {
                try { Directory.Move(legacy, current); return current; }
                catch (IOException) { return legacy; }
                catch (UnauthorizedAccessException) { return legacy; }
            }
            return current;
        }
        public static void Save(string path, Profile profile) {
            byte[] plain = Encoding.UTF8.GetBytes(profile.Serialize());
            byte[] sealedBytes;
            try { sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); } finally { Array.Clear(plain, 0, plain.Length); }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(sealedBytes, 0, sealedBytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static Profile Load(string path) {
            if (new FileInfo(path).Length > 65536) throw new FormatException("Повреждённый файл профиля.");
            byte[] sealedBytes = File.ReadAllBytes(path), plain; bool legacy = false;
            try { plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) {
                legacy = true;
                plain = ProtectedData.Unprotect(sealedBytes, LegacyEntropy, DataProtectionScope.CurrentUser);
            }
            Profile profile;
            try { profile = Profile.Deserialize(Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain, 0, plain.Length); }
            if (legacy) { try { Save(path, profile); } catch (IOException) {} catch (UnauthorizedAccessException) {} }
            return profile;
        }
    }
}
