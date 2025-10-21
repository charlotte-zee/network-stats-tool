using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;

class NetworkStatsTool
{
    const string PingTarget = "8.8.8.8";
    const int PingCount = 2;
    const int RefreshMs = 500;
    static readonly TimeSpan PublicIpTimeout = TimeSpan.FromSeconds(3);

    static HttpClient httpClient = new HttpClient() { Timeout = PublicIpTimeout };
    static NetworkInterface cachedRealAdapter = null;

    static void SafeConsoleResize(int width, int height)
    {
        try
        {
            Console.SetWindowSize(Math.Min(width, Console.LargestWindowWidth),
                                  Math.Min(height, Console.LargestWindowHeight));
            Console.SetBufferSize(Math.Max(width, Console.BufferWidth), Math.Max(height, Console.BufferHeight));
        }
        catch { }
    }

    static string RunCmd(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/C " + command)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                return outp ?? "";
            }
        }
        catch
        {
            return "";
        }
    }

    static NetworkInterface GetBestNetworkInterface()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();

        Func<NetworkInterface, bool> isVirtualOrUnwanted = ni =>
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return true;

            string n = (ni.Name + " " + ni.Description).ToLowerInvariant();

            return n.Contains("vmware") || n.Contains("vmnet") || n.Contains("virtualbox") ||
                   n.Contains("vbox") || n.Contains("tap") || n.Contains("loopback") ||
                   n.Contains("hyper-v") || n.Contains("virtual") || n.Contains("vpn") ||
                   n.Contains("tunnel") || n.Contains("pseudo");
        };

        var primary = all
            .Where(ni => !isVirtualOrUnwanted(ni))
            .Where(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Where(ni => ni.GetIPProperties().UnicastAddresses.Any(u =>
                u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !u.Address.ToString().StartsWith("127.") &&
                !u.Address.ToString().StartsWith("169.254.")))
            .OrderByDescending(ni => ni.Speed)
            .FirstOrDefault();

        if (primary != null)
        {
            cachedRealAdapter = primary;
            return primary;
        }

        primary = all
            .Where(ni => !isVirtualOrUnwanted(ni))
            .Where(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .OrderByDescending(ni => ni.Speed)
            .FirstOrDefault();

        if (primary != null)
        {
            cachedRealAdapter = primary;
            return primary;
        }

        primary = all
            .Where(ni => !isVirtualOrUnwanted(ni))
            .Where(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderByDescending(ni => ni.Speed)
            .FirstOrDefault();

        if (primary != null)
        {
            cachedRealAdapter = primary;
            return primary;
        }

        return cachedRealAdapter;
    }

    static string GetIPv4Address(NetworkInterface ni)
    {
        if (ni == null) return "N/A";
        try
        {
            var current = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == ni.Id);
            if (current == null) return "N/A";

            var ip = current.GetIPProperties()
                       .UnicastAddresses
                       .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                       .Select(u => u.Address.ToString())
                       .FirstOrDefault();
            return ip ?? "N/A";
        }
        catch { return "N/A"; }
    }

    static string GetGateway(NetworkInterface ni)
    {
        if (ni == null) return "N/A";
        try
        {
            var current = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == ni.Id);
            if (current == null) return "N/A";

            var gw = current.GetIPProperties()
                       .GatewayAddresses
                       .Where(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                       .Select(g => g.Address.ToString())
                       .FirstOrDefault();
            return gw ?? "N/A";
        }
        catch { return "N/A"; }
    }

    static string GetDns(NetworkInterface ni)
    {
        if (ni == null) return "N/A";
        try
        {
            var current = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == ni.Id);
            if (current == null) return "N/A";

            var dns = current.GetIPProperties()
                        .DnsAddresses
                        .Where(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(d => d.ToString())
                        .ToArray();
            return dns.Length == 0 ? "N/A" : string.Join(", ", dns);
        }
        catch { return "N/A"; }
    }

    static string GetMac(NetworkInterface ni)
    {
        if (ni == null) return "N/A";
        try
        {
            var bytes = ni.GetPhysicalAddress()?.GetAddressBytes();
            return (bytes == null || bytes.Length == 0) ? "N/A" : BitConverter.ToString(bytes);
        }
        catch { return "N/A"; }
    }

    static string GetInterfaceType(NetworkInterface ni)
    {
        if (ni == null) return "N/A";
        switch (ni.NetworkInterfaceType)
        {
            case NetworkInterfaceType.Wireless80211: return "Wireless";
            case NetworkInterfaceType.Ethernet: return "Ethernet";
            default: return ni.NetworkInterfaceType.ToString();
        }
    }

    static string GetInterfaceState(NetworkInterface ni)
    {
        if (ni == null) return "Disconnected";

        var current = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Id == ni.Id);

        if (current == null) return "Disconnected";

        return current.OperationalStatus == OperationalStatus.Up ? "Connected" : "Disconnected";
    }

    static async System.Threading.Tasks.Task<string> GetPublicIpAsync()
    {
        try
        {
            var resp = await httpClient.GetStringAsync("https://api.ipify.org");
            return resp?.Trim() ?? "N/A";
        }
        catch
        {
            return "N/A";
        }
    }

    static (int avgMs, int lostPercent) GetPingStats(string host, int count = 2, int timeout = 1000)
    {
        try
        {
            var ping = new Ping();
            int success = 0;
            long totalMs = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reply = ping.Send(host, timeout);
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        success++;
                        totalMs += reply.RoundtripTime;
                    }
                }
                catch { }
            }
            int lost = count - success;
            int lostPercent = (int)((double)lost / count * 100);
            int avg = success > 0 ? (int)(totalMs / success) : -1;
            return (avg, lostPercent);
        }
        catch
        {
            return (-1, 100);
        }
    }

    static (long rxBytes, long txBytes) GetInterfaceBytes(NetworkInterface ni)
    {
        if (ni == null) return (0, 0);
        try
        {
            var current = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == ni.Id);
            if (current == null) return (0, 0);

            var stats = current.GetIPStatistics();
            long rx = stats.BytesReceived;
            long tx = stats.BytesSent;
            return (rx, tx);
        }
        catch
        {
            return (0, 0);
        }
    }

    static string GetWifiSsid()
    {
        try
        {
            string outp = RunCmd("netsh wlan show interfaces");
            if (string.IsNullOrWhiteSpace(outp)) return "N/A";

            var ssidMatch = Regex.Match(outp, @"^\s*SSID\s*: (.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (ssidMatch.Success) return ssidMatch.Groups[1].Value.Trim();

            ssidMatch = Regex.Match(outp, @"SSID\s*: (\S+)", RegexOptions.IgnoreCase);
            if (ssidMatch.Success) return ssidMatch.Groups[1].Value.Trim();
        }
        catch { }
        return "N/A";
    }

    static string GetWifiSignal()
    {
        try
        {
            string outp = RunCmd("netsh wlan show interfaces");
            if (string.IsNullOrWhiteSpace(outp)) return "N/A";

            var match = Regex.Match(outp, @"^\s*Signal\s*: (.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();

            match = Regex.Match(outp, @"Signal\s*: (\d+)%", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim() + "%";
        }
        catch { }
        return "N/A";
    }

    static void PrintColored(string label, string value, ConsoleColor labelColor = ConsoleColor.Gray, ConsoleColor valueColor = ConsoleColor.White)
    {
        Console.ForegroundColor = labelColor;
        Console.Write(label);
        Console.ForegroundColor = valueColor;
        Console.WriteLine(value);
        Console.ResetColor();
    }

    static void Main(string[] args)
    {
        SafeConsoleResize(50, 19);
        Console.Title = "Network Stats Tool";
        Console.CursorVisible = false;

        long prevRx = 0, prevTx = 0;
        DateTime prevTime = DateTime.UtcNow;

        bool running = true;
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            running = false;
        };

        NetworkInterface current = GetBestNetworkInterface();
        if (current != null)
        {
            var b = GetInterfaceBytes(current);
            prevRx = b.rxBytes;
            prevTx = b.txBytes;
            prevTime = DateTime.UtcNow;
        }

        while (running)
        {
            try
            {
                current = GetBestNetworkInterface();

                string name = current?.Description ?? "N/A";
                string type = GetInterfaceType(current);
                string state = GetInterfaceState(current);
                string ip = GetIPv4Address(current);
                string gw = GetGateway(current);
                string dns = GetDns(current);
                string mac = GetMac(current);

                string publicIp = "N/A";
                try
                {
                    var task = GetPublicIpAsync();
                    if (!task.Wait(2500)) publicIp = "N/A"; else publicIp = task.Result;
                }
                catch { publicIp = "N/A"; }

                var pingStats = GetPingStats(PingTarget, PingCount);
                string pingStr = pingStats.avgMs >= 0 ? pingStats.avgMs + "ms" : "N/A";
                string lossStr = pingStats.lostPercent + "%";

                var bytes = GetInterfaceBytes(current);
                var now = DateTime.UtcNow;
                double seconds = Math.Max(0.001, (now - prevTime).TotalSeconds);
                double rxSpeedMBs = (bytes.rxBytes - prevRx) / 1048576.0 / seconds;
                double txSpeedMBs = (bytes.txBytes - prevTx) / 1048576.0 / seconds;

                prevRx = bytes.rxBytes;
                prevTx = bytes.txBytes;
                prevTime = now;

                double rxMbTotal = bytes.rxBytes / 1048576.0;
                double txMbTotal = bytes.txBytes / 1048576.0;

                bool hasInternet = pingStats.avgMs >= 0 && pingStats.lostPercent < 100;

                // Smart label logic: only show dominant direction
                string rxLabel = "";
                string txLabel = "";

                if (rxSpeedMBs >= 0.5 || txSpeedMBs >= 0.5)
                {
                    // Download is 3x higher than upload -> only show Download
                    if (rxSpeedMBs > txSpeedMBs * 3)
                    {
                        rxLabel = " (Download)";
                    }
                    // Upload is 3x higher than download -> only show Upload
                    else if (txSpeedMBs > rxSpeedMBs * 3)
                    {
                        txLabel = " (Upload)";
                    }
                    // Both are high and similar -> show both
                    else if (rxSpeedMBs >= 0.5 && txSpeedMBs >= 0.5)
                    {
                        rxLabel = " (Download)";
                        txLabel = " (Upload)";
                    }
                    // Only download is significant
                    else if (rxSpeedMBs >= 0.5)
                    {
                        rxLabel = " (Download)";
                    }
                    // Only upload is significant
                    else if (txSpeedMBs >= 0.5)
                    {
                        txLabel = " (Upload)";
                    }
                }

                Console.Clear();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("Network Stats Tool   {0}", DateTime.Now.ToString("HH:mm:ss"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("======================================");
                Console.ResetColor();

                PrintColored("Adapter   : ", name, ConsoleColor.Gray, ConsoleColor.White);
                PrintColored("Type      : ", type, ConsoleColor.Gray, ConsoleColor.White);

                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("State     : ");
                Console.ForegroundColor = state == "Connected" ? ConsoleColor.Green : ConsoleColor.Red;
                Console.WriteLine(state);
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("Internet  : ");
                Console.ForegroundColor = hasInternet ? ConsoleColor.Green : ConsoleColor.Red;
                Console.WriteLine(hasInternet ? "Online" : "Offline");
                Console.ResetColor();

                PrintColored("IP        : ", ip, ConsoleColor.Gray, ConsoleColor.Cyan);
                PrintColored("Gateway   : ", gw, ConsoleColor.Gray, ConsoleColor.Cyan);
                PrintColored("DNS       : ", dns, ConsoleColor.Gray, ConsoleColor.Cyan);
                PrintColored("MAC       : ", mac, ConsoleColor.Gray, ConsoleColor.Yellow);
                PrintColored("Public IP : ", publicIp, ConsoleColor.Gray, ConsoleColor.Magenta);

                if (type.ToLower().Contains("wireless") || type.ToLower().Contains("wi-fi"))
                {
                    string ssid = GetWifiSsid();
                    string signal = GetWifiSignal();
                    PrintColored("SSID      : ", ssid, ConsoleColor.Gray, ConsoleColor.White);
                    PrintColored("Signal    : ", signal, ConsoleColor.Gray, ConsoleColor.Green);
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("======================================");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("Speed & Status:");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("--------------");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("Ping         : ");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(pingStr);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("      Packet Loss : ");

                int lossVal = 0;
                int.TryParse(lossStr.Replace("%", ""), out lossVal);
                if (lossVal == 0) Console.ForegroundColor = ConsoleColor.Green;
                else if (lossVal < 10) Console.ForegroundColor = ConsoleColor.Yellow;
                else Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(lossStr);
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("Received     : ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{rxMbTotal:F2} MB");
                if (rxSpeedMBs >= 0.05)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"  ({rxSpeedMBs:F2} MB/s){rxLabel}");
                }
                Console.WriteLine();
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("Sent         : ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{txMbTotal:F2} MB");
                if (txSpeedMBs >= 0.05)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"  ({txSpeedMBs:F2} MB/s){txLabel}");
                }
                Console.WriteLine();
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("--------------------------------------");
                Console.ResetColor();

                Thread.Sleep(RefreshMs);
            }
            catch (Exception ex)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: " + ex.Message);
                Console.ResetColor();
                Thread.Sleep(1000);
            }
        }

        Console.CursorVisible = true;
        Console.ResetColor();
        Console.Clear();
        Console.WriteLine("Exiting Network Stats Tool...");
    }
}
