using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Netlane {
    // How sites see this computer: the address and country Cloudflare reports for a plain HTTPS request
    // to https://www.cloudflare.com/cdn-cgi/trace. Nothing but the request itself is sent; the user can
    // turn the check off in «Настройки» (ClientState.HidePublicIp).
    internal sealed class PublicIp {
        internal const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
        internal string Address;
        internal string Country;

        internal static async Task<PublicIp> FetchAsync() {
            Tls.Enable();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(TraceUrl);
            request.Method = "GET";
            request.UserAgent = "Netlane/" + Program.Version;
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            Task<WebResponse> pending = request.GetResponseAsync();
            if (await Task.WhenAny(pending, Task.Delay(9000)) != pending) { request.Abort(); throw new TimeoutException("Проверка IP не ответила."); }
            using (WebResponse response = await pending)
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                return Parse(await reader.ReadToEndAsync());
        }

        // The trace is «key=value» lines; ip= and loc= are what is needed.
        internal static PublicIp Parse(string trace) {
            PublicIp result = new PublicIp();
            foreach (string raw in (trace ?? "").Split('\n')) {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 1).Trim();
                IPAddress parsed;
                if (key == "ip" && IPAddress.TryParse(value, out parsed)) result.Address = parsed.ToString();
                else if (key == "loc" && value.Length == 2 && Char.IsLetter(value[0]) && Char.IsLetter(value[1])) result.Country = value.ToUpperInvariant();
            }
            if (result.Address == null) throw new FormatException("Ответ проверки IP не распознан.");
            return result;
        }

        // «185.12.3.4 · Россия»; the country is left out when unknown.
        internal string Text {
            get {
                string name = CountryName(Country);
                return name == null ? Address : Address + " · " + name;
            }
        }
        internal static string CountryName(string code) {
            if (code == null || code == "XX" || code == "T1") return null;
            for (int i = 0; i < CountryNames.All.GetLength(0); i++) if (CountryNames.All[i, 0] == code) return CountryNames.All[i, 1];
            return code;
        }
    }
}
