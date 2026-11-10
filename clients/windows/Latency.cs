using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Netlane {
    public sealed class LatencyResult {
        public string Method { get; set; }
        public long Milliseconds { get; set; }
        public string Detail { get; set; }
        public bool Success { get; set; }
        public string Display { get { return Success ? Method + ": " + Milliseconds + " мс" : "Нет ответа"; } }
    }
    public static class Latency {
        // A task we stopped waiting for (timeout) may still fail later; observe it so the
        // failure is not reported as an unobserved task exception.
        internal static void Observe(Task task) {
            task.ContinueWith(delegate(Task t) { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        public static async Task<LatencyResult> Measure(Profile profile) {
            profile.Validate(); Uri address = new Uri("https://" + profile.Address);
            IPAddress[] resolved;
            try {
                Task<IPAddress[]> resolving = Dns.GetHostAddressesAsync(address.DnsSafeHost);
                if (await Task.WhenAny(resolving, Task.Delay(3000)) != resolving) { Observe(resolving); return Failure("Не получен ответ DNS за 3 секунды."); }
                resolved = await resolving;
            } catch { return Failure("Не удалось определить адрес сервера."); }
            if (resolved.Length == 0) return Failure("У сервера нет доступного IP-адреса.");
            IPAddress target = resolved.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? resolved[0];
            long[] samples = new long[3]; int count = 0;
            for (int i = 0; i < 3; i++) {
                try { using (Ping ping = new Ping()) { PingReply reply = await ping.SendPingAsync(target, 1000); if (reply.Status == IPStatus.Success) samples[count++] = reply.RoundtripTime; } } catch (Exception) {}
            }
            if (count > 0) { Array.Sort(samples, 0, count); return new LatencyResult { Success = true, Method = "Ping", Milliseconds = samples[count / 2], Detail = "ICMP: медиана успешных ответов из трёх запросов. Замер с этого компьютера по текущему маршруту сети." }; }
            try {
                Stopwatch watch = Stopwatch.StartNew();
                using (TcpClient tcp = new TcpClient(target.AddressFamily)) {
                    Task connecting = tcp.ConnectAsync(target, address.Port);
                    if (await Task.WhenAny(connecting, Task.Delay(2500)) != connecting) { Observe(connecting); return Failure("ICMP не ответил; TCP-подключение также превысило таймаут."); }
                    await connecting;
                    using (SslStream tls = new SslStream(tcp.GetStream(), false)) {
                        Task handshake = tls.AuthenticateAsClientAsync(profile.Hostname, null, SslProtocols.Tls12, true);
                        if (await Task.WhenAny(handshake, Task.Delay(2500)) != handshake) { Observe(handshake); return Failure("ICMP не ответил; TLS-сервер не завершил рукопожатие за 2,5 секунды."); }
                        await handshake; watch.Stop();
                        return new LatencyResult { Success = true, Method = "TLS", Milliseconds = watch.ElapsedMilliseconds, Detail = "ICMP недоступен. Показано время TCP + TLS с проверкой сертификата, а не обычный ping." };
                    }
                }
            } catch (AuthenticationException) { return Failure("ICMP не ответил; проверка сертификата TLS не пройдена."); }
            catch { return Failure("ICMP и TCP/TLS не ответили. Это не доказывает, что VPN недоступен: проверьте сеть и транспорт."); }
        }
        static LatencyResult Failure(string text) { return new LatencyResult { Success = false, Detail = text }; }
    }
}
