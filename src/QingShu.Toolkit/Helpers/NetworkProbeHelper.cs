using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Helpers;

/// <summary>网卡快照（诊断引擎共享上下文使用）。</summary>
public sealed record AdapterSnapshot(
    string Name, string Id, string Description, NetworkInterfaceType Type,
    bool IsUp, long SpeedBps, bool IsWireless,
    string? IpV4, string? MaskV4, string? Gateway, List<string> DnsServers,
    bool IsDhcpEnabled, OperationalStatus Status);

/// <summary>Ping 统计结果。</summary>
public sealed record PingStats(int Sent, int Received, double LossPct, double AvgMs, double JitterMs, double MinMs, double MaxMs, string Error)
{
    public bool AllLost => Received == 0 && Sent > 0;
}

/// <summary>HTTP 探测结果。</summary>
public sealed record HttpProbeResult(
    bool Ok, int StatusCode, string FinalUrl, string Location, string BodySnippet,
    string ContentType, DateTime? ServerDateUtc, long ElapsedMs, string Error)
{
    public bool IsRedirect => StatusCode is 301 or 302 or 303 or 307 or 308;
    public bool LooksLikeHtml => ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);
}

/// <summary>TCP 探测结果。</summary>
public sealed record TcpProbeResult(bool Ok, long ElapsedMs, string Error);

public sealed record WifiSnapshot(string State, string Ssid, int SignalPct);

/// <summary>
/// 网络探测工具箱：ping 统计 / HTTP 探测 / TCP 探测 / 主网卡快照 / Wi-Fi 状态 / 命令解析。
/// </summary>
public static class NetworkProbeHelper
{
    private static readonly HttpClient RedirectClient = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly HttpClient DirectClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly string[] PublicDnsProbe = { "223.5.5.5", "119.29.29.29", "114.114.114.114" };
    public static IReadOnlyList<string> PublicDnsServers => PublicDnsProbe;

    #region Ping

    public static async Task<PingStats> PingAsync(string host, int count = 4, int timeoutMs = 1500, int size = 32, bool dontFragment = false)
    {
        int sent = 0, received = 0;
        var times = new List<double>();
        string error = "";
        for (int i = 0; i < count; i++)
        {
            try
            {
                using var ping = new Ping();
                var opts = new PingOptions(64, dontFragment);
                var reply = await ping.SendPingAsync(host, timeoutMs, size, opts).ConfigureAwait(false);
                sent++;
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    times.Add(reply.RoundtripTime);
                }
                else error = reply.Status.ToString();
            }
            catch (Exception ex) { sent++; error = ex.Message; }
            if (i < count - 1) await Task.Delay(120).ConfigureAwait(false);
        }
        if (times.Count == 0) return new PingStats(sent, received, sent == 0 ? 100 : 100.0 * (sent - received) / sent, 0, 0, 0, 0, error);
        double avg = times.Average();
        double jitter = times.Count > 1 ? Math.Sqrt(times.Sum(t => (t - avg) * (t - avg)) / times.Count) : 0;
        return new PingStats(sent, received, 100.0 * (sent - received) / sent, avg, jitter, times.Min(), times.Max(), error);
    }

    #endregion

    #region HTTP

    public static async Task<HttpProbeResult> HttpAsync(string url, bool followRedirect, int timeoutMs = 8000, bool readBody = true)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await (followRedirect ? RedirectClient : DirectClient).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            string body = "";
            if (readBody)
            {
                try
                {
                    var buf = new byte[4096];
                    var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                    body = Encoding.UTF8.GetString(buf, 0, n);
                }
                catch { /* 正文可选 */ }
            }
            DateTime? date = null;
            if (resp.Headers.Date.HasValue) date = resp.Headers.Date.Value.UtcDateTime;
            return new HttpProbeResult(true, (int)resp.StatusCode, resp.RequestMessage?.RequestUri?.ToString() ?? url,
                resp.Headers.Location?.ToString() ?? "", body, resp.Content?.Headers.ContentType?.ToString() ?? "", date, sw.ElapsedMilliseconds, "");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HttpProbeResult(false, 0, url, "", "", "", null, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    #endregion

    #region TCP

    public static async Task<TcpProbeResult> TcpAsync(string host, int port, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
            sw.Stop();
            if (done != connectTask) return new TcpProbeResult(false, sw.ElapsedMilliseconds, "连接超时");
            await connectTask.ConfigureAwait(false);
            return new TcpProbeResult(true, sw.ElapsedMilliseconds, "");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TcpProbeResult(false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    #endregion

    #region 网卡快照

    /// <summary>选择主用网卡：已连接、非回环/隧道、拥有 IPv4 与网关者优先。</summary>
    public static AdapterSnapshot? GetPrimaryAdapter()
    {
        var all = GetAllAdapters();
        return all.FirstOrDefault(a => a.IsUp && a.Gateway != null)
            ?? all.FirstOrDefault(a => a.IsUp && a.IpV4 != null)
            ?? all.FirstOrDefault();
    }

    public static List<AdapterSnapshot> GetAllAdapters()
    {
        var list = new List<AdapterSnapshot>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var ipProps = nic.GetIPProperties();
                var v4 = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                string? gw = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address?.ToString();
                var dns = ipProps.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                list.Add(new AdapterSnapshot(
                    nic.Name, nic.Id, nic.Description, nic.NetworkInterfaceType,
                    nic.OperationalStatus == OperationalStatus.Up, nic.Speed,
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    v4?.Address.ToString(), v4?.IPv4Mask?.ToString(),
                    gw, dns, v4 != null && v4.PrefixOrigin == PrefixOrigin.Dhcp, nic.OperationalStatus));
            }
        }
        catch { }
        return list;
    }

    #endregion

    #region Wi-Fi

    /// <summary>解析 netsh wlan show interfaces（中英文系统均可）。</summary>
    public static async Task<WifiSnapshot?> GetWifiInfoAsync()
    {
        var r = await ProcessRunner.RunAsync("netsh wlan show interfaces").ConfigureAwait(false);
        if (r.ExitCode != 0) return null;
        string state = "", ssid = "";
        int signal = -1;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (state.Length == 0 && (line.StartsWith("状态", StringComparison.Ordinal) || line.StartsWith("State", StringComparison.OrdinalIgnoreCase)))
                state = ExtractValue(line);
            else if (ssid.Length == 0 && line.StartsWith("SSID", StringComparison.Ordinal) && !line.Contains("BSSID", StringComparison.Ordinal))
                ssid = line.Substring(line.IndexOf(':', StringComparison.Ordinal) + 1).Trim();
            else if (signal < 0 && (line.StartsWith("信号", StringComparison.Ordinal) || line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase)))
            {
                var m = Regex.Match(line, @"(\d+)%");
                if (m.Success) signal = int.Parse(m.Groups[1].Value);
            }
        }
        if (state.Length == 0 && ssid.Length == 0 && signal < 0) return null;
        return new WifiSnapshot(state, ssid, signal);
    }

    private static string ExtractValue(string line)
    {
        int i = line.IndexOf(':', StringComparison.Ordinal);
        return i >= 0 ? line[(i + 1)..].Trim() : "";
    }

    #endregion

    #region 工具

    /// <summary>是否为私网地址（10/172.16-31/192.168/169.254）。</summary>
    public static bool IsPrivateIp(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return false;
        var bytes = a.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    public static bool IsLoopbackOrInvalid(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return true;
        return System.Net.IPAddress.IsLoopback(a) || ip.StartsWith("0.", StringComparison.Ordinal);
    }

    public static bool IsApipa(string? ip) => ip != null && ip.StartsWith("169.254.", StringComparison.Ordinal);

    #endregion
}
