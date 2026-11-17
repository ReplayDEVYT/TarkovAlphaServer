using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ServerLib.Utilities
{
    /// <summary>
    /// Address helpers for handing out game-server addresses and for the start-up network report.
    /// A player on the server's own PC, on its LAN (or a VPN) and on the internet each need a different address for
    /// the same game server; see MatchController.AddressFor.
    /// </summary>
    public static class NetAddress
    {
        /// <summary>"::ffff:1.2.3.4" -> "1.2.3.4"; null/blank -> "".</summary>
        public static string Normalize(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return "";
            ip = ip.Trim();
            if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) ip = ip.Substring(7);
            return ip;
        }

        public static bool IsLoopback(string? ip)
        {
            ip = Normalize(ip);
            if (ip == "" ) return false;
            if (ip.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(ip, out var a) && IPAddress.IsLoopback(a);
        }

        public static bool IsIPv4(string? ip) =>
            IPAddress.TryParse(Normalize(ip), out var a) && a.AddressFamily == AddressFamily.InterNetwork;

        /// <summary>
        /// Addresses that are not reachable from the internet: RFC 1918 LAN ranges, link-local, and 100.64.0.0/10
        /// (carrier-grade NAT, also used by VPNs such as Tailscale). IPv6 unique-local/link-local too.
        /// Not an IP address (a host name) = false.
        /// </summary>
        public static bool IsPrivate(string? ip)
        {
            if (!IPAddress.TryParse(Normalize(ip), out var a)) return false;
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = a.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        /// <summary>This PC's IPv4 addresses on network interfaces that are up (no loopback).</summary>
        public static List<IPAddress> LocalIPv4()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var u in nic.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address) && !list.Contains(u.Address))
                            list.Add(u.Address);
                }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>The address belongs to this PC (loopback, 0.0.0.0 or one of its interfaces).</summary>
        public static bool IsThisMachine(string? ip)
        {
            ip = Normalize(ip);
            if (ip == "") return true;
            if (IsLoopback(ip)) return true;
            if (!IPAddress.TryParse(ip, out var a)) return false;
            if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var u in nic.GetIPProperties().UnicastAddresses)
                        if (u.Address.Equals(a)) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>Host part of an HTTP Host header: "1.2.3.4:6969" -> "1.2.3.4", "[::1]:6969" -> "::1", "name" -> "name".</summary>
        public static string HostOf(string? hostHeader)
        {
            if (string.IsNullOrWhiteSpace(hostHeader)) return "";
            string h = hostHeader.Trim();
            if (h.StartsWith("["))
            {
                int end = h.IndexOf(']');
                return end > 0 ? h.Substring(1, end - 1) : h.Trim('[', ']');
            }
            int colon = h.IndexOf(':');
            // more than one colon = a bare IPv6 address without a port
            if (colon >= 0 && h.IndexOf(':', colon + 1) < 0) h = h.Substring(0, colon);
            return h;
        }

        /// <summary>Null when this UDP port can be used here, else why not (another game server or program has it).</summary>
        public static string? UdpPortProblem(int port)
        {
            if (port <= 0 || port > 65535) return $"{port} is not a valid port";
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.ExclusiveAddressUse = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                return null;
            }
            catch (SocketException ex)
            {
                return ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"UDP port {port} is already in use on the server PC (another match or program)"
                    : $"UDP port {port} cannot be opened on the server PC ({ex.SocketErrorCode})";
            }
        }
    }
}
