using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Netlane {
    public sealed class AppRule {
        public string Executable { get; set; }
        // Bare image name (e.g. "UIS Operator Workplace.exe"), matched in any directory like Clash PROCESS-NAME.
        public string Name { get; set; }
        public string Action { get; set; }
        public override string ToString() {
            if (Name != null) return (Action == "vpn" ? "VPN" : "Напрямую") + " · по имени · " + Name;
            return (Action == "vpn" ? "VPN" : "Напрямую") + " · " + Path.GetFileName(Executable) + " · " + Executable;
        }
    }
    public sealed class Routing {
        public string Mode { get; set; }
        public List<string> Destinations { get; set; }
        public List<AppRule> Applications { get; set; }
        public Routing() { Mode = "general"; Destinations = new List<string>(); Applications = new List<AppRule>(); }
        public void Validate() {
            if (Mode != "general" && Mode != "selective") throw new FormatException("Неизвестный режим маршрутизации.");
            if (Destinations == null || Destinations.Count > 256 || Applications == null || Applications.Count > 100) throw new FormatException("Слишком много правил: максимум 256 адресов и 100 приложений.");
            HashSet<string> destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Destinations.Count; i++) { string value = NormalizeDestination(Destinations[i]); if (!destinations.Add(value)) throw new FormatException("Правило адреса уже существует."); Destinations[i] = value; }
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Applications.Count; i++) {
                AppRule app = Applications[i];
                if (app == null || (app.Action != "vpn" && app.Action != "direct")) throw new FormatException("Нужен маршрут VPN/напрямую для правила приложения.");
                bool byName = app.Name != null;
                if (byName == (app.Executable != null)) throw new FormatException("У правила укажите либо полный путь .exe, либо имя процесса .exe — не оба.");
                if (!byName) {
                    app.Name = null;
                    if (app.Executable == null || app.Executable.Length > 1024 || !Regex.IsMatch(app.Executable, @"\A[A-Za-z]:\\[^\r\n""<>|?*]+\.exe\z", RegexOptions.IgnoreCase) || app.Executable.Contains("\\..\\")) throw new FormatException("Нужен полный локальный путь к .exe.");
                    app.Executable = Path.GetFullPath(app.Executable);
                    if (!paths.Add(app.Executable)) throw new FormatException("Для приложения уже есть правило.");
                } else {
                    app.Executable = null;
                    if (app.Name == null || app.Name.Length < 5 || app.Name.Length > 255 || app.Name.IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '\r', '\n' }) >= 0 || !app.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Укажите имя файла .exe, например `UIS Operator Workplace.exe`, без пути.");
                    if (!names.Add(app.Name)) throw new FormatException("Для имени процесса уже есть правило.");
                }
                Applications[i] = app;
            }
        }
        public static string NormalizeDestination(string value) {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 260 || value.Any(Char.IsWhiteSpace)) throw new FormatException("Укажите домен, IP или подсеть CIDR без пробелов и URL.");
            value = value.Trim().ToLowerInvariant();
            string[] parts = value.Split('/'); IPAddress ip;
            if (parts.Length > 2) throw new FormatException("Неверная подсеть CIDR.");
            if (IPAddress.TryParse(parts[0], out ip)) {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) throw new FormatException("В этом профиле IPv6 пока выключен. Используйте IPv4 или домен.");
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !Regex.IsMatch(parts[0], @"\A\d{1,3}(\.\d{1,3}){3}\z")) throw new FormatException("IPv4 должен содержать четыре числа через точку.");
                if (parts[0].Split('.').Any(part => part.Length > 1 && part[0] == '0')) throw new FormatException("В IPv4 не должно быть ведущих нулей.");
                if (parts.Length == 1) return ip.ToString();
                int prefix; int max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                if (!Int32.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max) throw new FormatException("Неверная длина префикса CIDR.");
                return ip.ToString() + "/" + prefix;
            }
            if (parts.Length != 1) throw new FormatException("CIDR применяется только к IP-адресам.");
            if (Regex.IsMatch(value, @"\A[0-9.]+\z")) throw new FormatException("Неверный IPv4-адрес.");
            string domain = value.StartsWith("*.") ? value.Substring(2) : value;
            if (domain.Length > 253 || !domain.Contains(".") || !Regex.IsMatch(domain, @"\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+\z")) throw new FormatException("Неверный домен. Используйте example.com или *.example.com; для IDN — punycode.");
            foreach (string label in domain.Split('.')) if (label.Length > 63) throw new FormatException("Слишком длинная метка домена.");
            return value;
        }
        public Routing Copy() { return new JavaScriptSerializer().Deserialize<Routing>(new JavaScriptSerializer().Serialize(this)); }
        public string Summary { get { return Destinations.Count == 0 && Applications.Count == 0 ? (Mode == "general" ? "Весь трафик через VPN" : "Весь трафик напрямую") : "Адреса: " + Destinations.Count + " · Приложения: " + Applications.Count; } }
        public string EngineConfig() {
            Validate(); JavaScriptSerializer json = new JavaScriptSerializer();
            string text = "vpn_mode = " + json.Serialize(Mode) + "\nexclusions = [" + String.Join(", ", Destinations.Select(d => json.Serialize(d))) + "]\n";
            foreach (AppRule app in Applications) text += "[[application_rules]]\n" + (app.Name != null
                ? "process_name = " + json.Serialize(app.Name)
                : "executable = " + json.Serialize(app.Executable)) + "\naction = " + json.Serialize(app.Action) + "\n";
            return text;
        }
    }
    // Portable rules file: deliberately contains no credentials, hostname or password.
    public static class RulesFile {
        public const string Format = "netlane-rules";
        // Files exported under the former product name (*.wttrules) stay importable.
        public const string LegacyFormat = "wintrusttunnel-rules";
        public const int Version = 1;
        sealed class Envelope {
            public string Format { get; set; }
            public int Version { get; set; }
            public Routing Routing { get; set; }
        }
        public static string Export(Routing routing) {
            routing.Validate();
            return new JavaScriptSerializer { MaxJsonLength = 65536 }.Serialize(new Envelope { Format = Format, Version = Version, Routing = routing });
        }
        public static Routing Import(string text) {
            if (String.IsNullOrWhiteSpace(text) || text.Length > 65536) throw new FormatException("Пустой или слишком большой файл правил.");
            Envelope envelope;
            try { envelope = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<Envelope>(text.Trim().TrimStart('\uFEFF')); }
            catch (Exception) { throw new FormatException("Файл не является файлом правил Netlane."); }
            if (envelope == null || (envelope.Format != Format && envelope.Format != LegacyFormat)) throw new FormatException("Файл не является файлом правил Netlane.");
            if (envelope.Version != Version) throw new FormatException("Версия файла правил не поддерживается: " + envelope.Version + ".");
            if (envelope.Routing == null) throw new FormatException("В файле нет правил маршрутизации.");
            envelope.Routing.Validate();
            return envelope.Routing;
        }
    }
}
