using ServerLib.Controllers;
using ServerLib.Utilities;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Debug = ServerLib.Utilities.Debug;

namespace ServerLib.Web
{
    /// <summary>
    /// Printed once at start: which addresses players use to reach this server, what has to be open for players on
    /// other PCs (TCP backend port, UDP match ports), and whether Windows Firewall has rules for them.
    /// </summary>
    public static class NetworkReport
    {
        /// <summary>Rule names used by "Open firewall ports.bat" (and checked here).</summary>
        public const string RuleTcp = "EFT Alpha Server (backend TCP)";
        public const string RuleUdp = "EFT Alpha Server (raids UDP)";

        public static void Print(IPAddress bind, int port)
        {
            var mm = ConfigController.Configs.Server?.Matchmaking ?? new Json.MatchmakingConfig();
            string udp = mm.PortStart == mm.PortEnd ? mm.PortStart.ToString() : $"{mm.PortStart}-{mm.PortEnd}";
            // the default host port too when outside the range (same list as the launcher's Firewall.UdpPorts)
            if (mm.HostDefaultPort > 0 && (mm.HostDefaultPort < mm.PortStart || mm.HostDefaultPort > mm.PortEnd)) udp += "," + mm.HostDefaultPort;
            try
            {
                if (IPAddress.IsLoopback(bind))
                {
                    Debug.PrintWarn($"Listening on {bind}:{port} only: players on other PCs (LAN, internet, VPS) cannot connect. " +
                        "Set \"ip\" to \"0.0.0.0\" in Files/configs/server.json to let them in.", "Network");
                    return;
                }
                var lan = NetAddress.LocalIPv4();
                bool any = bind.Equals(IPAddress.Any);
                var shown = any ? lan : new List<IPAddress> { bind };
                Debug.PrintInfo("Players connect with one of these addresses in client.config.json / the server list:", "Network");
                Debug.PrintInfo($"  this PC:          http://127.0.0.1:{port}" + (any ? "" : " (not available: \"ip\" is a single address)"), "Network");
                foreach (var a in shown)
                    Debug.PrintInfo($"  {(NetAddress.IsPrivate(a.ToString()) ? "LAN / VPN:   " : "public / VPN:")}     http://{a}:{port}", "Network");
                Debug.PrintInfo($"  internet:         http://<your public IP or domain>:{port} (router: forward TCP {port} and UDP {udp} to this PC)", "Network");
                Debug.PrintInfo($"Raids use UDP {udp}; each player gets the game server address that fits where they connect from.", "Network");
                if (!string.IsNullOrEmpty(mm.MatchIp))
                    Debug.PrintInfo($"Players from the internet get matchIp {mm.MatchIp} for raids.", "Network");
                CheckFirewall(port, udp);
            }
            catch (Exception ex)
            {
                Debug.PrintWarn("Network report failed: " + ex.Message, "Network");
            }
        }

        static bool IsWindows => OperatingSystem.IsWindows();

        const string Marker = "Files/configs/firewall_setup.txt";

        /// <summary>
        /// Checks both rules (with the current ports). On the server's first start (no Files/configs/firewall_setup.txt;
        /// the launcher's first START SERVER writes the same file) missing rules are added after asking for administrator
        /// rights once; later starts only warn.
        /// </summary>
        static void CheckFirewall(int port, string udp)
        {
            if (!IsWindows) return;
            int tcpCode = Netsh($"advfirewall firewall show rule name=\"{RuleTcp}\"", out string tcpOut);
            int udpCode = Netsh($"advfirewall firewall show rule name=\"{RuleUdp}\"", out string udpOut);
            if (tcpCode < 0 || udpCode < 0) return; // cannot tell; say nothing
            bool present = tcpCode == 0 && udpCode == 0;
            // labels are in the Windows language, port numbers are not
            bool current = present && HasToken(tcpOut, port.ToString()) && HasToken(udpOut, udp);
            if (current)
            {
                Debug.PrintInfo($"Windows Firewall: TCP {port} and UDP {udp} are allowed.", "Network");
                return;
            }
            string what = present ? $"the firewall rules are for other ports than TCP {port} / UDP {udp}" : $"no rule allows TCP {port} and UDP {udp}";
            if (!File.Exists(Marker))
            {
                Debug.PrintInfo($"Windows Firewall: {what}. First start: asking for administrator rights to add them...", "Network");
                string result = AddRules(port, udp);
                try { File.WriteAllText(Marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " server first start: " + result + "\r\n"); } catch (Exception) { }
                if (result == "added")
                {
                    Debug.PrintInfo($"Windows Firewall now allows TCP {port} and UDP {udp}.", "Network");
                    return;
                }
                Debug.PrintWarn("Firewall rules not added: " + result + ".", "Network");
            }
            Debug.PrintWarn($"Windows Firewall: {what}. Players on other PCs may be blocked (a VPS also needs the ports allowed in its provider's firewall). " +
                "Use OPEN PORTS in the launcher's server CONFIG, or run \"Open firewall ports.bat\" in the server folder.", "Network");
        }

        static bool HasToken(string output, string token) =>
            Regex.IsMatch(output ?? "", @"(^|\s)" + Regex.Escape(token) + @"(\s|$)", RegexOptions.Multiline);

        /// <summary>Replaces both rules through an elevated cmd (UAC prompt). "added" or why not.</summary>
        static string AddRules(int port, string udp)
        {
            string del = "netsh advfirewall firewall delete rule name=\"{0}\" >nul 2>&1";
            string add = "netsh advfirewall firewall add rule name=\"{0}\" dir=in action=allow protocol={1} localport={2} profile=any >nul";
            string command = string.Format(del, RuleTcp) + " & " + string.Format(del, RuleUdp) + " & " +
                string.Format(add, RuleTcp, "TCP", port) + " && " + string.Format(add, RuleUdp, "UDP", udp);
            try
            {
                using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + command + "\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (p == null) return "the command did not start";
                if (!p.WaitForExit(30000)) return "the command did not finish";
                return p.ExitCode == 0 ? "added" : $"netsh failed (exit code {p.ExitCode})";
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? "administrator rights were not given" : ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        static int Netsh(string args, out string output)
        {
            output = "";
            try
            {
                var info = new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(info);
                if (p == null) return -1;
                output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000)) return -1;
                return p.ExitCode;
            }
            catch (Exception)
            {
                return -1;
            }
        }    }
}
