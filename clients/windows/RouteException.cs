using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Netlane {
    // The engine binds its outbound socket to the physical adapter, so that the tunnel never
    // captures its own connection. When another VPN (L2TP, WireGuard, another client) owns the
    // default route, that binding has no path to the endpoint and the connection dies with
    // "Failed to ping location" and then "Number of connection attempts exceeded".
    //
    // A /32 route to the endpoint through the physical gateway gives the engine the path it
    // assumes exists. It lives only for the session, so the other VPN keeps the default route
    // and both work together: Netlane only takes the destinations its rules select.
    //
    // The route is added with route.exe rather than CreateIpForwardEntry: the deprecated IP
    // helper call rejected the same arguments with ERROR_BAD_ARGUMENTS (160), and route.exe is
    // both the documented tool and the command a user would type by hand.
    internal static class RouteException {
        const string HostMask = "255.255.255.255";

        static readonly List<string> Installed = new List<string>();

        // IPv4 addresses of the endpoint, without duplicates and without loopback.
        internal static List<IPAddress> Resolve(string hostname) {
            List<IPAddress> addresses = new List<IPAddress>();
            if (String.IsNullOrEmpty(hostname)) return addresses;
            try {
                foreach (IPAddress address in Dns.GetHostAddresses(hostname)) {
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)) continue;
                    bool seen = false;
                    foreach (IPAddress known in addresses) if (known.Equals(address)) seen = true;
                    if (!seen) addresses.Add(address);
                }
            } catch (SocketException error) {
                EngineLog.Write("route: cannot resolve " + hostname + ": " + error.Message);
            }
            return addresses;
        }

        // The physical adapter that carries the real internet: an Ethernet or Wi-Fi link, up,
        // with an IPv4 default gateway. Virtual switches and tunnels have no gateway here.
        internal static NetworkInterface PhysicalAdapter() {
            NetworkInterface chosen = null;
            int chosenIndex = Int32.MaxValue;
            foreach (NetworkInterface candidate in NetworkInterface.GetAllNetworkInterfaces()) {
                if (candidate.OperationalStatus != OperationalStatus.Up) continue;
                NetworkInterfaceType type = candidate.NetworkInterfaceType;
                if (type != NetworkInterfaceType.Ethernet && type != NetworkInterfaceType.Wireless80211 && type != NetworkInterfaceType.GigabitEthernet) continue;
                if (IPv4Gateway(candidate) == null) continue;
                IPv4InterfaceProperties properties;
                try { properties = candidate.GetIPProperties().GetIPv4Properties(); } catch (NetworkInformationException) { continue; }
                if (properties == null) continue;
                if (properties.Index < chosenIndex) { chosen = candidate; chosenIndex = properties.Index; }
            }
            return chosen;
        }

        internal static IPAddress IPv4Gateway(NetworkInterface adapter) {
            foreach (GatewayIPAddressInformation gateway in adapter.GetIPProperties().GatewayAddresses) {
                IPAddress address = gateway.Address;
                if (address != null && address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any)) return address;
            }
            return null;
        }

        // Little-endian DWORD whose bytes are the address, which is what the IP helper API expects.
        internal static uint ToUInt(IPAddress address) {
            return BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        }

        internal static string AddArguments(IPAddress endpoint, IPAddress gateway, int interfaceIndex) {
            return "add " + endpoint + " mask " + HostMask + " " + gateway + " metric 1 if " + interfaceIndex;
        }

        internal static string DeleteArguments(IPAddress endpoint) {
            return "delete " + endpoint;
        }

        static bool Run(string arguments) {
            try {
                ProcessStartInfo start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "route.exe"), arguments) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process route = Process.Start(start)) {
                    if (route == null) return false;
                    route.StandardOutput.ReadToEnd();
                    route.StandardError.ReadToEnd();
                    if (!route.WaitForExit(10000)) { try { route.Kill(); } catch (InvalidOperationException) { } return false; }
                    return route.ExitCode == 0;
                }
            } catch (Exception error) {
                EngineLog.Write("route: route.exe failed: " + error.Message);
                return false;
            }
        }

        // Returns the addresses that received a route. An empty list means nothing was needed
        // or nothing could be done, and the session simply runs the way it did before.
        internal static List<string> Add(Profile profile) {
            List<string> routed = new List<string>();
            if (profile == null) return routed;
            NetworkInterface adapter = PhysicalAdapter();
            if (adapter == null) {
                EngineLog.Write("route: no physical adapter with an IPv4 gateway; leaving routes alone");
                return routed;
            }
            IPAddress gateway = IPv4Gateway(adapter);
            IPv4InterfaceProperties properties = adapter.GetIPProperties().GetIPv4Properties();
            if (gateway == null || properties == null) return routed;
            int index = properties.Index;
            foreach (IPAddress endpoint in Resolve(profile.Hostname)) {
                uint best;
                if (Native.GetBestInterface(ToUInt(endpoint), out best) == 0 && best == (uint)index) continue;
                string arguments = AddArguments(endpoint, gateway, index);
                if (Run(arguments)) {
                    Installed.Add(endpoint.ToString());
                    routed.Add(endpoint.ToString());
                    EngineLog.Write("route: " + endpoint + " via " + gateway + " on " + adapter.Name + " (another VPN owns the default route)");
                } else {
                    EngineLog.Write("route: could not route " + endpoint + " with: route " + arguments);
                }
            }
            return routed;
        }

        internal static void Clear() {
            foreach (string address in Installed.ToArray()) {
                IPAddress parsed;
                if (!IPAddress.TryParse(address, out parsed)) continue;
                if (!Run(DeleteArguments(parsed))) EngineLog.Write("route: could not remove route to " + address);
            }
            Installed.Clear();
        }
    }
}
