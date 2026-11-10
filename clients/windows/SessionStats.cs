using System;
using System.Globalization;
using System.Net.NetworkInformation;

namespace Netlane {
    // Session line under the connect ring: "00:42:17 · ↓ 1,8 ГБ · ↑ 210,0 МБ".
    internal static class SessionStats {
        internal static string FormatDuration(TimeSpan span) {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            long hours = (long)Math.Floor(span.TotalHours);
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":" + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        // Time always; traffic only when the adapter counters could be read.
        internal static string SessionLine(TimeSpan duration, bool hasTraffic, long down, long up) {
            string line = FormatDuration(duration);
            return hasTraffic ? line + " · ↓ " + FormatBytes(down) + " · ↑ " + FormatBytes(up) : line;
        }

        internal static string FormatRate(double bytesPerSecond) { return FormatBytes((long)Math.Round(bytesPerSecond)) + "/с"; }
        internal static string SpeedLine(double down, double up) { return "↓ " + FormatRate(down) + " · ↑ " + FormatRate(up); }

        // Binary units with one decimal and a Russian decimal comma.
        internal static string FormatBytes(long bytes) {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " Б";
            string[] units = { "КБ", "МБ", "ГБ", "ТБ" };
            double value = bytes / 1024.0;
            int unit = 0;
            // Compare the value as it will be printed, so 1023,96 КБ becomes 1,0 МБ rather than 1024,0 КБ.
            while (Math.Round(value, 1) >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " " + units[unit];
        }
    }

    internal interface ITrafficSource { bool TryRead(out long received, out long sent); }

    // Byte counters of the Wintun adapter the engine creates (device_name = "Netlane").
    // Adapters left by the former product name are still recognised.
    // The adapter is found once and kept: enumerating every adapter each second is slow on busy machines.
    internal sealed class AdapterTraffic : ITrafficSource {
        NetworkInterface adapter;
        DateTime nextSearch = DateTime.MinValue;
        static bool Named(string value, string name) { return (value ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0; }
        internal static bool Matches(string name, string description, OperationalStatus status) {
            return status == OperationalStatus.Up && (Named(name, "Netlane") || Named(description, "Netlane") || Named(name, "WinTrustTunnel") || Named(description, "WinTrustTunnel"));
        }
        public bool TryRead(out long received, out long sent) {
            received = 0; sent = 0;
            try {
                if (adapter != null && adapter.OperationalStatus != OperationalStatus.Up) adapter = null;
                if (adapter == null && DateTime.UtcNow >= nextSearch) {
                    nextSearch = DateTime.UtcNow.AddSeconds(5);
                    foreach (NetworkInterface candidate in NetworkInterface.GetAllNetworkInterfaces())
                        if (Matches(candidate.Name, candidate.Description, candidate.OperationalStatus)) { adapter = candidate; break; }
                }
                if (adapter == null) return false;
                IPInterfaceStatistics statistics = adapter.GetIPStatistics();
                received = statistics.BytesReceived; sent = statistics.BytesSent;
                return true;
            } catch (Exception) { adapter = null; return false; }
        }
    }

    // Session totals from adapter counters. A counter that goes down means the adapter was
    // recreated (reconnect): the total so far is kept and the new adapter is counted from zero.
    internal sealed class TrafficMeter {
        readonly ITrafficSource source;
        long baseDown, baseUp, lastDown, lastUp, doneDown, doneUp;
        internal TrafficMeter(ITrafficSource source) { this.source = source; }
        internal void Start() {
            long down, up;
            if (!source.TryRead(out down, out up)) { down = 0; up = 0; }
            baseDown = lastDown = down; baseUp = lastUp = up; doneDown = doneUp = 0;
        }
        internal bool TryGet(out long down, out long up) {
            down = 0; up = 0;
            long rx, tx;
            if (!source.TryRead(out rx, out tx)) return false;
            down = Advance(rx, ref baseDown, ref lastDown, ref doneDown);
            up = Advance(tx, ref baseUp, ref lastUp, ref doneUp);
            return true;
        }
        DateTime rateAt = DateTime.MinValue;
        long rateDown, rateUp;
        double lastDownRate, lastUpRate;
        // Speed between this reading and the previous one; readings under 0.5 s apart keep the
        // previous rate so a fast timer does not show noise. Session totals never go down.
        internal bool TryGetRate(DateTime now, out double downPerSecond, out double upPerSecond) {
            downPerSecond = lastDownRate; upPerSecond = lastUpRate;
            if (rateAt != DateTime.MinValue && (now - rateAt).TotalSeconds < 0.5) return true;
            long down, up;
            if (!TryGet(out down, out up)) return false;
            if (rateAt != DateTime.MinValue) {
                double seconds = (now - rateAt).TotalSeconds;
                lastDownRate = Math.Max(0, (down - rateDown) / seconds);
                lastUpRate = Math.Max(0, (up - rateUp) / seconds);
            }
            rateAt = now; rateDown = down; rateUp = up;
            downPerSecond = lastDownRate; upPerSecond = lastUpRate;
            return true;
        }
        static long Advance(long value, ref long origin, ref long last, ref long done) {
            // A recreated adapter starts again from zero, so everything it has counted belongs to the session.
            if (value < last) { done += last - origin; origin = 0; }
            last = value;
            return done + (value - origin);
        }
    }
}
