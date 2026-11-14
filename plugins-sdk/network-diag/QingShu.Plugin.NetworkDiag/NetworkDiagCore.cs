using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace QingShu.Plugin.NetworkDiag;

// ============================================================================
// 网络诊断核心引擎（宿主 Services/Network 全量同源移植 + 隐藏依赖 Helper 一并移植）：
//   会话日志 / 网络探测工具箱 / WMI / 原生 DNS / 测速引擎 / 门户自动登录 /
//   共享上下文与检查基座 / 诊断编排器 / 45 项检查（物理·DNS 认证·系统质量·吞吐·疑难·硬件健康·IP 冲突）/
//   一键修复服务 / 低速归因分析 / 拓扑构建器
// 日志适配：宿主日志服务接口 → Action<string, string>（level = "debug"/"info"/"warn"/"error"）；
// 会话日志按宿主同口径写入 宿主数据根\logs\yyyy-MM-dd\diag-yyyyMMdd-HHmmss-NNN.{log,jsonl}
// （内置/插件共用同一日志目录口径）。设置适配：宿主设置服务 → NetworkDiagSettingsStore
// 的诊断设置只读镜像。除契约接口外不引用任何 QingShu.Toolkit.* 类型。
// ============================================================================

// ================= 会话日志（宿主 Services/Logging 诊断会话区段同源移植） =================

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>单次诊断会话日志（分日期 / 分次数 / 多文件）。</summary>
public interface ISessionLog : IDisposable
{
    string JsonPath { get; }
    string TextPath { get; }
    void Write(LogLevel level, string category, string message, object? data = null);
}

/// <summary>
/// 诊断会话日志落盘（宿主 LogService 的 SessionLog/BeginDiagSession 同源移植）：
/// logs/&lt;logRoot&gt;/yyyy-MM-dd/diag-yyyyMMdd-HHmmss-NNN.log|jsonl 双文件（第 NNN 次会话）。
/// 插件传入 logRoot = 宿主数据根 + \logs，与内置页共用同一日志目录，日志查看器两态可读。
/// </summary>
public sealed class DiagSessionLog : ISessionLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    public string JsonPath { get; }
    public string TextPath { get; }

    private DiagSessionLog(string jsonPath, string textPath)
    {
        JsonPath = jsonPath;
        TextPath = textPath;
    }

    public static DiagSessionLog Begin(string logRoot)
    {
        var now = DateTime.Now;
        var dir = Path.Combine(logRoot, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        int seq;
        try { seq = Directory.GetFiles(dir, "diag-*.jsonl").Length + 1; }
        catch { seq = 1; }
        string baseName = $"diag-{now:yyyyMMdd-HHmmss}-{seq:D3}";
        return new DiagSessionLog(Path.Combine(dir, baseName + ".jsonl"), Path.Combine(dir, baseName + ".log"));
    }

    public void Write(LogLevel level, string category, string message, object? data = null)
    {
        var now = DateTime.Now;
        lock (_lock)
        {
            try
            {
                var line = JsonSerializer.Serialize(new
                {
                    ts = now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    level = level.ToString().ToLowerInvariant(),
                    cat = category,
                    msg = message,
                    data
                }, JsonOpts);
                File.AppendAllText(JsonPath, line + "\n", Encoding.UTF8);

                var sb = new StringBuilder();
                sb.Append('[').Append(now.ToString("HH:mm:ss.fff")).Append("][")
                  .Append(level.ToString().ToUpperInvariant()).Append("][")
                  .Append(category).Append("] ").Append(message);
                if (data != null)
                {
                    var d = JsonSerializer.Serialize(data, JsonOpts);
                    if (d.Length > 600) d = d[..600] + "…";
                    sb.Append(" | ").Append(d);
                }
                File.AppendAllText(TextPath, sb.ToString() + "\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    public void Dispose() { /* 双文件均为追加式，无需刷新 */ }
}

// ================= 网络探测工具箱（宿主 Helpers/NetworkProbeHelper 同源移植） =================

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
                // SendPingAsync 的 buffer 参数是 byte[]（载荷），int 大小需转换
                var reply = await ping.SendPingAsync(host, timeoutMs, new byte[size], opts).ConfigureAwait(false);
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

    /// <summary>
    /// 是否为 IPv4 组播/广播/保留地址（首字节 ≥ 224，含 255.255.255.255）。
    /// 组播 IP → MAC 映射仅取低 23 位、天然多对一（如 224.1.1.1 与 225.1.1.1 必然同 MAC），
    /// 参与 ARP「多 IP 绑定同一 MAC」冲突判定会产生假性欺骗告警，需排除。
    /// </summary>
    public static bool IsMulticastOrBroadcastIp(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return false;
        var bytes = a.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] >= 224;
    }

    public static bool IsLoopbackOrInvalid(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return true;
        return System.Net.IPAddress.IsLoopback(a) || ip.StartsWith("0.", StringComparison.Ordinal);
    }

    public static bool IsApipa(string? ip) => ip != null && ip.StartsWith("169.254.", StringComparison.Ordinal);

    #endregion
}

// ================= WMI 查询封装（宿主 Helpers/WmiHelper 同源移植） =================

/// <summary>WMI 命名空间常量（默认 root\cimv2 之外的 provider）。</summary>
internal static class WmiScopes
{
    /// <summary>MSFT_NetAdapter / MSFT_NetAdapterPowerManagement 所在命名空间（Win8+ 自带）。</summary>
    public const string StandardCimV2 = "root\\standardcimv2";
}

/// <summary>
/// WMI 查询封装：全部在后台线程执行，避免卡 UI。
/// System.Management 程序集运行期经默认加载上下文共享宿主副本（插件 csproj ExcludeAssets=runtime）。
/// </summary>
public static class WmiHelper
{
    public static Task<List<T>> QueryAsync<T>(string wql, Func<ManagementObject, T> selector)
        => Task.Run(() =>
        {
            var list = new List<T>();
            try
            {
                using var searcher = new ManagementObjectSearcher(wql);
                foreach (var o in searcher.Get())
                {
                    if (o is ManagementObject mo)
                    {
                        try { list.Add(selector(mo)); }
                        catch { }
                        mo.Dispose();
                    }
                }
            }
            catch { }
            return list;
        });

    /// <summary>带 WMI 命名空间的查询：root\standardcimv2 的 MSFT_NetAdapter* 系列 provider
    /// （双工/速率协商、网卡节能）不在默认 root\cimv2 下。类不存在（Win7）或查询失败一律
    /// 返回空列表不抛——调用方按「信息不可得」降级，不阻断诊断。</summary>
    public static Task<List<T>> QueryInScopeAsync<T>(string scope, string wql, Func<ManagementObject, T> selector)
        => Task.Run(() =>
        {
            var list = new List<T>();
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, wql);
                foreach (var o in searcher.Get())
                {
                    if (o is ManagementObject mo)
                    {
                        try { list.Add(selector(mo)); }
                        catch { }
                        mo.Dispose();
                    }
                }
            }
            catch { }
            return list;
        });

    public static Task<string> FirstValueAsync(string wql, string property)
        => Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(wql);
                foreach (var o in searcher.Get())
                {
                    if (o is ManagementObject mo)
                    {
                        try
                        {
                            var v = mo[property]?.ToString();
                            mo.Dispose();
                            if (!string.IsNullOrWhiteSpace(v)) return v;
                        }
                        catch { mo.Dispose(); }
                    }
                }
            }
            catch { }
            return "";
        });
}

// ================= 原生 UDP DNS 查询（宿主 Helpers/DnsQueryHelper 同源移植） =================

/// <summary>DNS 探测结果。</summary>
public sealed record DnsProbeResult(bool Ok, List<string> Addresses, long ElapsedMs, string Error)
{
    public string AddressText => Addresses.Count == 0 ? "—" : string.Join(", ", Addresses.Take(3));
}

/// <summary>
/// 原生 UDP DNS A 记录查询（无第三方依赖）：
/// 直接向指定 DNS 服务器发送标准查询，用于逐台验证 DNS 服务器可用性与解析劫持。
/// </summary>
public static class DnsQueryHelper
{
    public static Task<DnsProbeResult> ResolveAsync(string dnsServer, string domain, int timeoutMs = 2000)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var udp = new UdpClient(dnsServer, 53);
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Client.SendTimeout = timeoutMs;

                ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
                var query = DnsPacket.BuildQuery(id, domain);
                udp.Send(query, query.Length);

                var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                byte[] data;
                while (true)
                {
                    try { data = udp.Receive(ref remote); }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
                    {
                        sw.Stop();
                        return new DnsProbeResult(false, new List<string>(), sw.ElapsedMilliseconds, "DNS 响应超时");
                    }
                    // 校验事务 ID，防串包
                    if (data.Length >= 2 && data[0] == (id >> 8) && data[1] == (id & 0xFF)) break;
                    if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        sw.Stop();
                        return new DnsProbeResult(false, new List<string>(), sw.ElapsedMilliseconds, "DNS 响应超时");
                    }
                }
                sw.Stop();
                var ips = DnsPacket.ParseARecords(data);
                if (ips.Count == 0)
                    return new DnsProbeResult(false, ips, sw.ElapsedMilliseconds, "DNS 应答中无 A 记录（域名不存在或被过滤）");
                return new DnsProbeResult(true, ips, sw.ElapsedMilliseconds, "");
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new DnsProbeResult(false, new List<string>(), sw.ElapsedMilliseconds, ex.Message);
            }
        });
}

/// <summary>DNS 报文构造与解析。</summary>
public static class DnsPacket
{
    public static byte[] BuildQuery(ushort id, string domain)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)(id >> 8)); w.Write((byte)(id & 0xFF));   // ID
        w.Write((byte)0x01); w.Write((byte)0x00);               // Flags: 标准递归查询
        w.Write((byte)0x00); w.Write((byte)0x01);               // QDCOUNT = 1
        w.Write((byte)0x00); w.Write((byte)0x00);               // ANCOUNT
        w.Write((byte)0x00); w.Write((byte)0x00);               // NSCOUNT
        w.Write((byte)0x00); w.Write((byte)0x00);               // ARCOUNT
        foreach (var label in domain.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            w.Write((byte)b.Length); w.Write(b);
        }
        w.Write((byte)0);
        w.Write((byte)0x00); w.Write((byte)0x01);               // Type A
        w.Write((byte)0x00); w.Write((byte)0x01);               // Class IN
        w.Flush();
        return ms.ToArray();
    }

    public static List<string> ParseARecords(byte[] data)
    {
        var result = new List<string>();
        try
        {
            int pos = 12; // 跳过头部
            bool skippedQuestion = false;
            while (pos < data.Length && data[pos] != 0)
            {
                if ((data[pos] & 0xC0) == 0xC0) { pos += 2; skippedQuestion = true; break; }
                pos += data[pos] + 1;
            }
            if (!skippedQuestion) pos += 5; // 根零字节 + Type(2) + Class(2)

            ushort anCount = (ushort)((data[6] << 8) | data[7]);
            for (int i = 0; i < anCount && pos + 12 <= data.Length; i++)
            {
                if ((data[pos] & 0xC0) == 0xC0) pos += 2;
                else { while (pos < data.Length && data[pos] != 0) pos += data[pos] + 1; pos++; }
                if (pos + 10 > data.Length) break;
                ushort type = (ushort)((data[pos] << 8) | data[pos + 1]);
                ushort rdLen = (ushort)((data[pos + 8] << 8) | data[pos + 9]);
                pos += 10;
                if (type == 1 && rdLen == 4 && pos + 4 <= data.Length)
                    result.Add($"{data[pos]}.{data[pos + 1]}.{data[pos + 2]}.{data[pos + 3]}");
                pos += rdLen;
            }
        }
        catch { /* 解析失败按无记录处理 */ }
        return result;
    }
}

// ================= 下载吞吐实测引擎（宿主 Helpers/SpeedTestHelper 同源移植） =================

/// <summary>
/// 下载吞吐实测引擎（低速专项核心）：
/// 1) 顺序探测候选大文件端点（腾讯/微软 CDN 与教育镜像优先，命中校园网白名单概率高）；
/// 2) 取可达的前 2 个端点，各以多连接 Range 限时下载（默认 8 秒、4 连接）；
/// 3) 汇总每端点平均/峰值速率，取最优端点作为代表带宽，并给出体验档位。
/// 全程只读下载、不落盘、不运行任何脚本；单点命令可逆，测速流量有总量上限。
/// </summary>
public static class SpeedTestHelper
{
    public sealed record Candidate(string Url, string Label);

    /// <summary>内置候选端点（按校园网可达概率排序；均为版本冻结、常年常驻的大文件）。</summary>
    public static readonly Candidate[] DefaultCandidates =
    {
        // 腾讯 CDN：微信安装包为多年不变的固定直链，校内几乎必然可达
        new("https://dldir1.qq.com/weixin/Windows/WeChatSetup.exe", "腾讯微信安装包"),
        // 微软更新 CDN：历史补丁文件永久保留
        new("https://download.windowsupdate.com/c/msdownload/update/software/secu/2023/10/windows10.0-kb5031354-x64_9854b36fa7b9e1cd66e942d5c9e51cfca36b0b71.msu", "微软更新 CDN"),
        // 教育网镜像：centos-vault 为冻结归档，文件名永久不变（约 10GB，仅 Range 读取前段）
        new("https://mirrors.tuna.tsinghua.edu.cn/centos-vault/7.9.2009/isos/x86_64/CentOS-7-x86_64-Everything-2009.iso", "清华 TUNA 镜像"),
        new("https://mirrors.ustc.edu.cn/centos-vault/7.9.2009/isos/x86_64/CentOS-7-x86_64-Everything-2009.iso", "中科大镜像"),
        new("https://dldir1.qq.com/qqfile/qq/PCQQ9.7.17/QQ9.7.17.29225.exe", "腾讯 QQ 安装包"),
        new("https://mirrors.aliyun.com/centos-vault/7.9.2009/isos/x86_64/CentOS-7-x86_64-Everything-2009.iso", "阿里云镜像")
    };

    private const int ProbeTimeoutMs = 6000;

    /// <summary>
    /// 执行下载测速。
    /// </summary>
    /// <param name="customUrl">自定义端点（留空用内置候选；自定义端点排最前）。</param>
    /// <param name="budgetSeconds">每端点下载限时（3-30 秒）。</param>
    /// <param name="connections">并发连接数（1-8）。</param>
    /// <param name="maxBytes">单端点下载总量上限（字节）。</param>
    /// <param name="progress">实时进度（约 400ms 一拍）。</param>
    public static async Task<SpeedTestResult> MeasureAsync(
        string? customUrl, int budgetSeconds, int connections, long maxBytes,
        IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        budgetSeconds = Math.Clamp(budgetSeconds, 3, 30);
        connections = Math.Clamp(connections, 1, 8);
        if (maxBytes < (8L << 20)) maxBytes = 8L << 20; // 下限 8MB，避免过早上限导致误判

        var candidates = new List<Candidate>();
        if (!string.IsNullOrWhiteSpace(customUrl) && Uri.TryCreate(customUrl, UriKind.Absolute, out var cu)
            && (cu.Scheme == Uri.UriSchemeHttp || cu.Scheme == Uri.UriSchemeHttps))
            candidates.Add(new Candidate(customUrl, "自定义测速节点"));
        candidates.AddRange(DefaultCandidates);

        // ---- 阶段 1：探测可达端点 ----
        progress?.Report(new SpeedProgress { Phase = "正在探测测速节点…" });
        var alive = new List<(Candidate C, bool Range, long Size)>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(c.Url).ConfigureAwait(false);
            if (probe.Ok)
            {
                alive.Add((c, probe.SupportsRange, probe.Size));
                if (alive.Count >= 2) break;
            }
        }

        var result = new SpeedTestResult();
        if (alive.Count == 0)
        {
            result.Error = "所有测速节点均不可达（可能被校园网白名单拦截、出口拥塞或当前无外网）";
            return result;
        }

        // ---- 阶段 2：逐端点多连接限时下载 ----
        long bytesAll = 0;
        var swAll = Stopwatch.StartNew();
        double peak = 0;
        foreach (var (c, range, size) in alive)
        {
            ct.ThrowIfCancellationRequested();
            var r = await MeasureEndpointAsync(c, range, size, budgetSeconds, connections, maxBytes, progress, ct)
                .ConfigureAwait(false);
            result.Endpoints.Add(r);
            if (r.Success)
            {
                bytesAll += r.BytesTotal;
                peak = Math.Max(peak, r.MbpsPeak);
                if (r.MbpsAvg > result.MbpsAvg)
                {
                    result.MbpsAvg = r.MbpsAvg;
                    result.EndpointLabel = r.Label;
                }
            }
        }
        swAll.Stop();

        if (result.MbpsAvg <= 0)
        {
            result.Error = "测速节点可达但未测得有效数据（连接被重置或限速接近于 0），请结合延迟画像判断";
            return result;
        }

        result.Success = true;
        result.MbpsPeak = peak;
        result.BytesTotal = bytesAll;
        result.DurationMs = (int)swAll.ElapsedMilliseconds;
        return result;
    }

    // ---------------- 阶段 1：端点探测 ----------------

    /// <summary>GET Range 0-65535 探测：可达性 + 是否支持断点 + 文件总大小。</summary>
    private static async Task<(bool Ok, bool SupportsRange, long Size)> ProbeAsync(string url)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.Timeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 65535);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, false, 0);
            // 206 时 ContentLength 是本段大小；文件总大小在 Content-Range 的 “/总长” 中
            long size = resp.Content.Headers.ContentRange?.Length
                        ?? resp.Content.Headers.ContentLength ?? 0;
            bool range = resp.StatusCode == HttpStatusCode.PartialContent;
            return (true, range, size);
        }
        catch { return (false, false, 0); }
    }

    // ---------------- 阶段 2：单端点多连接限时下载 ----------------

    private static async Task<SpeedEndpointResult> MeasureEndpointAsync(
        Candidate c, bool supportsRange, long fileSize, int budgetSeconds, int connections,
        long maxBytes, IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        var r = new SpeedEndpointResult { Label = c.Label, Url = c.Url, Connections = connections };
        var sw = Stopwatch.StartNew();
        try
        {
            // 不支持 Range 的大文件改单连接；文件太小时避免多连接 416
            if (!supportsRange || fileSize <= 0) connections = 1;
            else if (fileSize < connections * (1L << 20)) connections = 1;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));

            long total = 0;
            long chunk = 32L << 20; // 每连接取 32MB 段，够 8 秒×百兆出口
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseProxy = false });
            client.Timeout = Timeout.InfiniteTimeSpan;

            var tasks = Enumerable.Range(0, connections).Select(i => Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, c.Url);
                    if (supportsRange && connections > 1)
                        req.Headers.Range = new RangeHeaderValue(i * chunk, i * chunk + chunk - 1);
                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return;
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var buf = new byte[64 * 1024];
                    while (true)
                    {
                        int n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        Interlocked.Add(ref total, n);
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                catch { /* 单连接失败不影响整体统计 */ }
            })).ToArray();

            // 采样循环：进度上报 + 峰值统计；预算耗尽/总量达上限/全部完成即退出
            long lastBytes = 0;
            long lastMs = 0;
            double peak = 0;
            while (true)
            {
                if (tasks.All(t => t.IsCompleted)) break;
                long b = Interlocked.Read(ref total);
                if (b >= maxBytes) { cts.Cancel(); break; }
                try { await Task.Delay(400, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                long now = sw.ElapsedMilliseconds;
                long cur = Interlocked.Read(ref total);
                if (now - lastMs >= 300)
                {
                    double inst = (cur - lastBytes) * 8.0 / (now - lastMs) / 1000.0; // bytes*8/(ms*1000)=Mbps
                    if (inst > peak) peak = inst;
                    double avg = cur * 8.0 / Math.Max(1, now) / 1000.0;
                    progress?.Report(new SpeedProgress
                    {
                        Phase = $"正在测速：{c.Label}",
                        InstantMbps = inst,
                        AvgMbps = avg,
                        BytesRead = cur,
                        ElapsedMs = (int)now,
                        Connections = connections
                    });
                    lastBytes = cur;
                    lastMs = now;
                }
            }

            cts.Cancel(); // 无论如何终止残余连接
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* 已吞掉 */ }

            sw.Stop();
            long bytes = Interlocked.Read(ref total);
            r.BytesTotal = bytes;
            r.DurationMs = (int)sw.ElapsedMilliseconds;
            r.MbpsAvg = bytes * 8.0 / Math.Max(1, sw.ElapsedMilliseconds) / 1000.0;
            r.MbpsPeak = peak;
            r.Success = bytes >= (128L << 10) && sw.ElapsedMilliseconds >= 200; // 至少 128KB 才可信
            if (!r.Success)
                r.Error = $"仅下载 {FormatHelper.Bytes(bytes)}，样本不足";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            r.Error = ex.Message;
        }
        return r;
    }
}

#if !STABLE_BUILD
// ================= 城域网门户自动登录（宿主 Helpers/PortalAutoLoginHelper 同源移植，正式版剔除） =================

/// <summary>
/// 城域网认证门户“自动登录”（实验性）：
///  1. 拉取门户页面（跟随重定向 / 自动识别 iframe 内嵌登录页）
///  2. 解析 HTML 表单：用户名输入框（text/tel/email/number）、密码输入框（password）、隐藏字段原样保留
///  3. 按表单 method/action 以 urlencoded 方式提交账号密码（不执行任何 JS —— 纯 JS 渲染的门户无法支持，会明确返回原因）
///  4. 提交后用 NCSI 探测复核真实连通性（唯一可信判据）
/// 安全说明：账号密码仅从宿主设置镜像读取；日志与返回消息不含密码。
/// 门户形态千差万别，本助手是“尽力而为”：失败一律回退为打开浏览器人工登录。
/// </summary>
public static class PortalAutoLoginHelper
{
    public sealed record AutoLoginResult(bool Submitted, bool VerifiedOnline, string Message);

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string NcsiExpected = "Microsoft Connect Test";

    public static async Task<AutoLoginResult> TryLoginAsync(string portalUrl, string username, string password, CancellationToken ct)
    {
        try
        {
            // 1) 拉取门户页面
            using var req = new HttpRequestMessage(HttpMethod.Get, portalUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) QingShuToolkit");
            using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var baseUrl = resp.RequestMessage?.RequestUri?.ToString() ?? portalUrl;

            // 2) 解析表单（主页面没有密码框时，尝试第一个 iframe）
            var form = ParseForm(html);
            if (form.PasswordField.Length == 0)
            {
                var iframe = Regex.Match(html, @"<iframe\b[^>]*src\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase);
                if (iframe.Success)
                {
                    var iframeUrl = ResolveUrl(baseUrl, iframe.Groups[1].Value);
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                    using var resp2 = await Client.SendAsync(req2, ct).ConfigureAwait(false);
                    var html2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var form2 = ParseForm(html2);
                    if (form2.PasswordField.Length > 0) form = form2;
                    baseUrl = resp2.RequestMessage?.RequestUri?.ToString() ?? iframeUrl;
                }
            }

            if (form.PasswordField.Length == 0 || form.UserField.Length == 0)
                return new AutoLoginResult(false, false,
                    "自动登录失败：门户页面未找到标准登录表单（可能为纯 JS 渲染或扫码登录），已回退为浏览器人工登录。");

            // 3) 组装并提交
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var (name, value) in form.Hidden)
                fields.Add(new KeyValuePair<string, string>(name, value));
            fields.Add(new KeyValuePair<string, string>(form.UserField, username));
            fields.Add(new KeyValuePair<string, string>(form.PasswordField, password));
            // 部分门户要求 submit 按钮名值对，补一个通用值
            if (form.SubmitField.Length > 0)
                fields.Add(new KeyValuePair<string, string>(form.SubmitField, form.SubmitValue));

            var actionUrl = ResolveUrl(baseUrl, form.Action.Length > 0 ? form.Action : baseUrl);
            HttpResponseMessage post;
            if (string.Equals(form.Method, "get", StringComparison.OrdinalIgnoreCase))
            {
                var query = string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
                post = await Client.GetAsync(actionUrl + (actionUrl.Contains('?') ? "&" : "?") + query, ct).ConfigureAwait(false);
            }
            else
            {
                using var content = new FormUrlEncodedContent(fields);
                post = await Client.PostAsync(actionUrl, content, ct).ConfigureAwait(false);
            }
            _ = await post.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 4) NCSI 复核（唯一可信判据：真的能拿到预期内容才算登录成功）
            await Task.Delay(800, ct).ConfigureAwait(false); // 给门户服务端一点入账时间
            var verify = await NetworkProbeHelper.HttpAsync(NcsiUrl, followRedirect: false, readBody: true).ConfigureAwait(false);
            if (verify.Ok && verify.StatusCode == 200 && verify.BodySnippet.Contains(NcsiExpected, StringComparison.Ordinal))
                return new AutoLoginResult(true, true, $"自动登录成功：已提交认证并通过连通性复核（{verify.ElapsedMs} ms）。");
            return new AutoLoginResult(true, false,
                $"自动登录已提交（门户响应 HTTP {(int)post.StatusCode}），但连通性复核未通过：账号可能输错、已在线别处登录或门户需要验证码。已回退为浏览器人工登录。");
        }
        catch (OperationCanceledException)
        {
            return new AutoLoginResult(false, false, "自动登录超时，已回退为浏览器人工登录。");
        }
        catch (Exception ex)
        {
            return new AutoLoginResult(false, false, $"自动登录异常（{ex.Message}），已回退为浏览器人工登录。");
        }
    }

    private sealed record PortalForm(string Action, string Method, string UserField, string PasswordField,
        string SubmitField, string SubmitValue, List<(string Name, string Value)> Hidden);

    private static PortalForm ParseForm(string html)
    {
        var formTag = Regex.Match(html, @"<form\b[^>]*>", RegexOptions.IgnoreCase);
        string action = "", method = "post";
        if (formTag.Success)
        {
            action = Attr(formTag.Value, "action");
            var m = Attr(formTag.Value, "method");
            if (m.Length > 0) method = m;
        }

        string user = "", pass = "", submit = "", submitValue = "";
        var hidden = new List<(string, string)>();
        foreach (Match im in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = im.Value;
            var name = Attr(tag, "name");
            var type = Attr(tag, "type").ToLowerInvariant();
            var value = Attr(tag, "value");
            if (name.Length == 0) continue;
            switch (type)
            {
                case "password" when pass.Length == 0: pass = name; break;
                case "hidden": hidden.Add((name, value)); break;
                case "submit" when submit.Length == 0: submit = name; submitValue = value; break;
                case "text" or "tel" or "email" or "number" or "" or null:
                    if (user.Length == 0) user = name; // 兜底：第一个文本框
                    break;
            }
            // 用户名字段名启发式优先
            if (user.Length > 0 && user != name &&
                Regex.IsMatch(name, @"user|account|uname|loginname|login_name|uid|auth", RegexOptions.IgnoreCase))
                user = name;
        }
        return new PortalForm(action, method, user, pass, submit, submitValue, hidden);
    }

    private static string Attr(string tag, string attr)
    {
        var m = Regex.Match(tag, $@"{attr}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();
        m = Regex.Match(tag, $@"{attr}\s*=\s*([^\s""'>]+)", RegexOptions.IgnoreCase); // 无引号写法
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string ResolveUrl(string baseUrl, string url)
    {
        if (url.Length == 0) return baseUrl;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        try { return new Uri(new Uri(baseUrl), url).ToString(); }
        catch { return url; }
    }
}
#endif

// ================= 诊断共享上下文与检查基座（宿主 Services/Network/CheckContext.cs 同源移植） =================

/// <summary>
/// 诊断共享上下文：一次会话内的网卡快照 / Wi-Fi 信息 / 设置 / 会话日志 /
/// 以及检查项之间共享的结论（如“外网是否可达”供认证过期判定联动）。
/// </summary>
public sealed class CheckContext
{
    public required DiagSettings Settings { get; init; }
    public required ISessionLog Log { get; init; }
    public CancellationToken Ct { get; init; }

    public AdapterSnapshot? Primary { get; set; }
    public List<AdapterSnapshot> Adapters { get; set; } = new();
    public WifiSnapshot? Wifi { get; set; }

    /// <summary>低速专项：测速实时进度上报（由页面 VM 订阅）。</summary>
    public IProgress<SpeedProgress>? SpeedProgress { get; set; }
    /// <summary>低速专项：下载吞吐实测结果（speed.download 写入）。</summary>
    public SpeedTestResult? SpeedResult { get; set; }

    public int PingCount => Math.Clamp(Settings.PingCount, 1, 10);
    public int TimeoutMs => Math.Clamp(Settings.PingTimeoutMs, 500, 8000);

    private readonly Dictionary<string, object> _shared = new();

    public void SetShared<T>(string key, T value) => _shared[key] = value!;
    public T? GetShared<T>(string key) => _shared.TryGetValue(key, out var v) && v is T t ? t : default;
    public bool SharedBool(string key) => GetShared<bool>(key);
}

/// <summary>单项检查接口。所有检查实现均为无状态，可安全复用。</summary>
public interface ICheck
{
    string Id { get; }
    string Name { get; }
    CheckCategory Category { get; }
    /// <summary>是否纳入“快速体检”（约 30-60 秒档）。</summary>
    bool InQuickMode { get; }
    Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct);
}

/// <summary>检查实现基类（日志辅助）。</summary>
public abstract class CheckBase : ICheck
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract CheckCategory Category { get; }
    public virtual bool InQuickMode => false;
    public abstract Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct);

    protected void Log(CheckContext ctx, string message, object? data = null)
        => ctx.Log.Write(LogLevel.Debug, $"check.{Id}", message, data);
}

// ================= 诊断编排器（宿主 Services/Network/NetworkDiagnosticService.cs 同源移植） =================

public interface INetworkDiagnosticService
{
    /// <summary>生成一次运行的检查计划（全新 CheckItem 实例）。</summary>
    List<CheckItem> CreatePlan(RunMode mode);
    /// <summary>执行计划：逐项更新 CheckItem 状态（ObservableObject，UI 自动刷新），并写入会话日志。</summary>
    Task<DiagnosticSessionReport> ExecuteAsync(List<CheckItem> plan, RunMode mode, IProgress<CheckItem>? progress, CancellationToken ct,
        IProgress<SpeedProgress>? speedProgress = null);
    CheckContext? LastContext { get; }
}

/// <summary>诊断编排器：按“物理链路 → 硬件 → IP → 网关 → DNS → 互联网 → 内网 → 认证 → 时钟 → 代理 → 系统栈 → 质量 → 测速吞吐”分层执行。</summary>
public sealed class NetworkDiagnosticService : INetworkDiagnosticService
{
    private readonly NetworkDiagSettingsStore _settings;
    private readonly Action<string, string> _log; // (level, message) → 插件日志适配（宿主日志服务同职责）
    private readonly string _logRoot;             // 会话日志根（宿主数据根 + \logs，与内置页共用）

    public CheckContext? LastContext { get; private set; }

    private static readonly ICheck[] AllChecks =
    {
        new NetworkChecks.PhysicalLinkCheck(),
        new NetworkChecks.NicHardwareCheck(),
        new NetworkChecks.NetworkHardwareProbeCheck(),
        new NetworkChecks.WifiSignalCheck(),
        new NetworkChecks.IpConfigCheck(),
        new NetworkChecks.IpConflictCheck(),
        new NetworkChecks.DhcpLeaseCheck(),
        new NetworkChecks.GatewayReachableCheck(),
        new NetworkChecks.ArpCheck(),
        new NetworkChecks.RouteCheck(),
        new NetworkChecks.DnsConfigCheck(),
        new NetworkChecks.DnsResolveCheck(),
        new NetworkChecks.DnsServerProbeCheck(),
        new NetworkChecks.HostsCheck(),
        new NetworkChecks.InternetIcmpCheck(),
        new NetworkChecks.InternetHttpCheck(),
        new NetworkChecks.InternetHttpsCheck(),
        new NetworkChecks.IntranetCheck(),
        new NetworkChecks.PortalDetectCheck(),
        new NetworkChecks.PortalExpiryCheck(),
        new NetworkChecks.ClockSkewCheck(),
        new NetworkChecks.ProxyCheck(),
        new NetworkChecks.WinHttpProxyCheck(),
        new NetworkChecks.VpnAdaptersCheck(),
        new NetworkChecks.ServicesCheck(),
        new NetworkChecks.FirewallCheck(),
        new NetworkChecks.WinsockCheck(),
        new NetworkChecks.LatencyQualityCheck(),
        new NetworkChecks.MtuCheck(),
        new NetworkChecks.NicRateCheck(),
        new NetworkChecks.WifiPhyCheck(),
        new NetworkChecks.DnsPerfCheck(),
        new NetworkChecks.PathProfileCheck(),
        new NetworkChecks.DownloadSpeedCheck(),
        new NetworkChecks.BackgroundUsageCheck(),
        new NetworkChecks.TcpTuningCheck(),
        new NetworkChecks.DelivOptCheck(),
        // 疑难排查层（罕见/边缘问题，仅全面诊断运行）
        new NetworkChecks.Ipv6Check(),
        new NetworkChecks.DnsHijackCheck(),
        new NetworkChecks.GatewayLossCheck(),
        new NetworkChecks.RouteConflictCheck(),
        new NetworkChecks.ProxyDiffCheck(),
        new NetworkChecks.Port53Check(),
        new NetworkChecks.AuthClientCheck(),
        new NetworkChecks.StackSelfTestCheck(),
        new NetworkChecks.TlsCertCheck()
    };

    /// <summary>逐项硬超时（秒）：坏网环境下慢超时检查不再拖住整个诊断列表（防「待检查」滞留）。</summary>
    private static readonly Dictionary<string, int> TimeoutOverrides = new()
    {
        ["speed.download"] = 90, ["portal.detect"] = 40, ["portal.expiry"] = 40,
        ["quality.mtu"] = 30, ["rare.gatewayloss"] = 30, ["hardware.nic_probe"] = 40
    };

    private static int TimeoutFor(string id) => TimeoutOverrides.TryGetValue(id, out var s) ? s : 25;

    /// <summary>低速专项计划：8 项专项检查 + 与低速强相关的既有检查（避免重复实现，复用既有证据）。</summary>
    private static readonly string[] SpeedPlanIds =
    {
        "speed.nic_rate", "speed.wifi_phy", "speed.dns_perf", "speed.path_profile",
        "speed.download", "speed.background", "speed.tcp_tuning", "speed.delivopt",
        "physical.link", "wifi.signal", "gateway.reachable",
        "dns.config", "dns.resolve", "dns.servers",
        "proxy.system", "proxy.winhttp", "vpn.adapters",
        "quality.latency", "quality.mtu",
        "internet.http", "internet.https"
    };

    private static readonly ICheck[] OrderedChecks =
        AllChecks.OrderBy(c => (int)c.Category).ToArray();

    public NetworkDiagnosticService(NetworkDiagSettingsStore settings, Action<string, string> log, string logRoot)
    {
        _settings = settings;
        _log = log;
        _logRoot = logRoot;
    }

    public List<CheckItem> CreatePlan(RunMode mode)
    {
        var src = mode switch
        {
            RunMode.Full => OrderedChecks,
            RunMode.Quick => OrderedChecks.Where(c => c.InQuickMode),
            RunMode.Speed => OrderedChecks.Where(c => SpeedPlanIds.Contains(c.Id)),
            _ => OrderedChecks
        };
        return src.Select(c => new CheckItem(c.Id, c.Name, c.Category)).ToList();
    }

    public async Task<DiagnosticSessionReport> ExecuteAsync(List<CheckItem> plan, RunMode mode, IProgress<CheckItem>? progress, CancellationToken ct,
        IProgress<SpeedProgress>? speedProgress = null)
    {
        var session = DiagSessionLog.Begin(_logRoot);
        var report = new DiagnosticSessionReport { StartedAt = DateTime.Now, Mode = mode };

        // 构建共享上下文
        var adapters = NetworkProbeHelper.GetAllAdapters();
        var primary = NetworkProbeHelper.GetPrimaryAdapter();
        var wifi = primary is { IsWireless: true } ? await NetworkProbeHelper.GetWifiInfoAsync().ConfigureAwait(true) : null;
        var ctx = new CheckContext
        {
            Settings = _settings.Current,
            Log = session,
            Ct = ct,
            Adapters = adapters,
            Primary = primary,
            Wifi = wifi,
            SpeedProgress = speedProgress
        };
        LastContext = ctx;

        session.Write(LogLevel.Info, "session", $"开始诊断：{report.ModeText}，共 {plan.Count} 项", new
        {
            host = Environment.MachineName,
            adapter = primary?.Name,
            ip = primary?.IpV4,
            gateway = primary?.Gateway
        });

        // 检查实现索引
        var impl = OrderedChecks.ToDictionary(c => c.Id, c => c);

        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = plan[i];
                if (!impl.TryGetValue(item.Id, out var check))
                {
                    item.Status = CheckStatus.Skipped;
                    continue;
                }

                item.Status = CheckStatus.Running;
                item.Summary = "正在检查…";
                progress?.Report(item);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                CheckOutcome outcome;
                try
                {
                    // 逐项硬超时：HttpClient 默认 100s / ping 长等待会把坏网环境的诊断拖住几分钟，
                    // 这里按项限时强制收尾，超时项给出结论后继续下一项（遗留任务随取消令自然终止）。
                    var timeoutSec = TimeoutFor(item.Id);
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                    var runTask = check.RunAsync(ctx, linked.Token);
                    var done = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec + 3))).ConfigureAwait(true);
                    if (done == runTask)
                        outcome = await runTask.ConfigureAwait(true); // 正常完成；异常由此抛出统一接住
                    else
                        outcome = CheckOutcome.Fail($"检查超时（超过 {timeoutSec} 秒已强制中止）",
                            "该检查等待响应过久（多为网络无应答），已中止此项以保证其余检查不被卡住；请结合网关/DNS/外网连通项结论定位。",
                            $"超时阈值：{timeoutSec} 秒");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    outcome = CheckOutcome.Fail("检查超时（已强制中止）", "该检查内部无响应被强制中止；其余检查不受影响。");
                }
                catch (Exception ex)
                {
                    outcome = CheckOutcome.Fail($"检查执行异常：{ex.Message}", "该检查项内部出现未预期错误，已跳过；不影响其他检查项。");
                    session.Write(LogLevel.Error, $"check.{item.Id}", "检查异常", new { ex = ex.ToString() });
                }
                sw.Stop();

                item.DurationMs = sw.ElapsedMilliseconds;
                item.Status = outcome.Status;
                item.Summary = outcome.Summary;
                item.Suggestion = outcome.Suggestion;
                item.CanAutoFix = outcome.CanAutoFix;
                item.Details = outcome.Details;

                session.Write(LogLevel.Info, $"check.{item.Id}", $"[{outcome.Status}] {outcome.Summary}",
                    new { ms = sw.ElapsedMilliseconds, suggestion = outcome.Suggestion, details = outcome.Details });
                progress?.Report(item);
                report.Results.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var item in plan.Where(p => p.Status is CheckStatus.Pending or CheckStatus.Running))
            {
                item.Status = CheckStatus.Skipped;
                item.Summary = "已取消";
            }
            session.Write(LogLevel.Warn, "session", "诊断被用户取消");
        }

        report.FinishedAt = DateTime.Now;

        // 低速专项：归并证据 → 嫌疑原因清单
        if (mode == RunMode.Speed && !plan.All(p => p.Status == CheckStatus.Skipped))
        {
            try
            {
                report.SlowCauses = SlowSpeedAnalyzer.Analyze(ctx, report.Results, ctx.SpeedResult);
                session.Write(LogLevel.Info, "slow",
                    $"低速原因分析完成：识别 {report.SlowCauses.Count} 项嫌疑",
                    report.SlowCauses.Select(c => $"[{c.Level}] {c.Title}").ToArray());
            }
            catch (Exception ex)
            {
                session.Write(LogLevel.Error, "slow", "低速原因分析异常", new { ex = ex.ToString() });
            }
        }

        session.Write(LogLevel.Info, "session",
            $"诊断完成：通过 {report.PassCount} / 警告 {report.WarnCount} / 未通过 {report.FailCount} / 跳过 {report.SkipCount}");
        return report;
    }
}
// ================= 检查实现：第一层（宿主 Services/Network/Checks.Physical.cs 同源移植） =================

/// <summary>
/// 第一层：物理链路 / 硬件驱动 / IP 配置 / 网关路由（8 项）。
/// </summary>
public static partial class NetworkChecks
{
    // ---------------- 物理连接状态 ----------------
    public sealed class PhysicalLinkCheck : CheckBase
    {
        public override string Id => "physical.link";
        public override string Name => "物理连接状态";
        public override CheckCategory Category => CheckCategory.PhysicalLink;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null)
                return CheckOutcome.Fail("未找到任何可用网络适配器", "请在设备管理器确认网卡是否存在，或联系机房管理员检查硬件。", false,
                    "NetworkInterface.GetAllNetworkInterfaces() 未返回可用的非回环适配器");

            var details = new List<string>
            {
                $"适配器: {p.Name} ({p.Description})",
                $"类型: {(p.IsWireless ? "无线 Wi-Fi" : p.Type.ToString())}",
                $"状态: {p.Status}",
                $"链路速率: {p.SpeedBps / 1_000_000} Mbps"
            };

            if (p.Status != OperationalStatus.Up)
                return CheckOutcome.Fail($"适配器“{p.Name}”未连接（{p.Status}）",
                    p.IsWireless ? "请检查 Wi-Fi 是否已开启并连接到校园无线网络。" : "请检查网线是否插好、墙上端口与交换机指示灯是否正常。",
                    canFix: true, details.ToArray());

            if (p.SpeedBps is > 0 and < 100_000_000)
                details.Add($"警告: 链路速率仅 {p.SpeedBps / 1_000_000} Mbps，可能为劣质网线或老旧端口");

            if (p.IsWireless)
            {
                ctx.Wifi = await NetworkProbeHelper.GetWifiInfoAsync().ConfigureAwait(false);
                if (ctx.Wifi != null)
                {
                    details.Add($"Wi-Fi 状态: {ctx.Wifi.State}  SSID: {ctx.Wifi.Ssid}  信号: {ctx.Wifi.SignalPct}%");
                    ctx.SetShared("wifi.ssid", ctx.Wifi.Ssid);
                }
            }

            ctx.SetShared("physical.ok", true);
            return CheckOutcome.Pass($"物理链路正常（{p.Name}）", details.ToArray());
        }
    }

    // ---------------- 网卡硬件与驱动 ----------------
    public sealed class NicHardwareCheck : CheckBase
    {
        public override string Id => "hardware.nic_pnp";
        public override string Name => "网卡硬件与驱动状态";
        public override CheckCategory Category => CheckCategory.HardwareDriver;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var adapters = await WmiHelper.QueryAsync(
                "SELECT Name, NetConnectionStatus, NetEnabled, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=true",
                mo => new
                {
                    Name = mo["Name"]?.ToString() ?? "",
                    Status = Convert.ToInt32(mo["NetConnectionStatus"] ?? 0),
                    Enabled = mo["NetEnabled"]?.ToString(),
                    Pnp = mo["PNPDeviceID"]?.ToString() ?? ""
                }).ConfigureAwait(false);

            if (adapters.Count == 0)
                return CheckOutcome.Warn("未通过 WMI 查询到物理网卡信息", "可忽略此项；如网卡确实存在异常，请到设备管理器检查。");

            var details = new List<string>();
            string? problem = null;
            foreach (var a in adapters)
            {
                var statusText = a.Status switch
                {
                    0 => "已断开", 1 => "正在连接", 2 => "已连接", 3 => "正在断开",
                    4 => "硬件不存在", 5 => "硬件已禁用", 6 => "硬件故障", 7 => "媒体已断开",
                    8 => "正在认证", 9 => "认证失败", 10 => "地址无效", 11 => "需要授权",
                    _ => $"状态码 {a.Status}"
                };
                details.Add($"{a.Name} → {statusText}");
                if (a.Status is 4 or 6 or 9 or 10 or 11)
                    problem ??= $"{a.Name}: {statusText}";
                if (a.Status == 5 && (ctx.Primary == null || ctx.Primary.Description.Contains(a.Name.Split(' ')[0])))
                    problem ??= $"{a.Name}: 网卡已被禁用";

                // PnP 错误码 + 驱动信息
                if (!string.IsNullOrEmpty(a.Pnp))
                {
                    var escaped = a.Pnp.Replace("\\", "\\\\");
                    var err = await WmiHelper.QueryAsync(
                        $"SELECT ConfigManagerErrorCode, Name FROM Win32_PnPEntity WHERE PNPDeviceID='{escaped}'",
                        mo => Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0)).ConfigureAwait(false);
                    if (err.Count > 0 && err[0] != 0)
                    {
                        var errText = err[0] switch
                        {
                            1 => "设备未正确配置", 10 => "设备无法启动", 22 => "设备被禁用",
                            28 => "驱动未安装", 31 => "设备驱动异常", 43 => "设备因故障停止",
                            _ => $"配置管理器错误码 {err[0]}"
                        };
                        details.Add($"⚠ {a.Name} 设备异常: {errText}");
                        problem ??= $"{a.Name}: {errText}";
                    }
                    else if (err.Count > 0)
                    {
                        var drv = await WmiHelper.QueryAsync(
                            $"SELECT DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceID='{escaped}'",
                            mo => $"{mo["DriverVersion"]} ({mo["DriverDate"]})").ConfigureAwait(false);
                        if (drv.Count > 0) details.Add($"{a.Name} 驱动: {drv[0]}");
                    }
                }
            }

            if (problem != null)
                return CheckOutcome.Fail($"网卡硬件/驱动存在异常：{problem}",
                    "建议先在设备管理器中卸载并重装网卡驱动（勾选保留驱动文件），重启后再诊断；仍异常则可能是硬件故障需报修。",
                    canFix: false, details.ToArray());

            return CheckOutcome.Pass($"物理网卡与驱动正常（{adapters.Count} 个适配器）", details.ToArray());
        }
    }

    // ---------------- 网络硬件探查（网络类 PnP 设备 + 双工/速率协商 + 收发错误计数 + 网卡节能，插件版新增） ----------------
    // 与 hardware.nic_pnp 互补不重复：nic_pnp 从 Win32_NetworkAdapter 侧细查物理网卡的连接状态
    // 与逐卡 PnP 错误；本项补三个硬件证据面——① PnP 侧网络类设备全量（含 WAN Miniport/蓝牙 PAN/
    // 虚拟交换机等非物理卡）；② MSFT_NetAdapter 的双工与速率协商（交换网络里正常协商应为全双工，
    // 半双工 = 网线线序/水晶头/端口协商故障的强信号）与驱动日期；③ 主适配器自开机累计的收发错误/
    // 丢包计数（链路/驱动/硬件问题的量化证据）与网卡节能状态（休眠后断网的常见线索）。
    public sealed class NetworkHardwareProbeCheck : CheckBase
    {
        public override string Id => "hardware.nic_probe";
        public override string Name => "网络硬件探查";
        public override CheckCategory Category => CheckCategory.HardwareDriver;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            var problems = new List<string>(); // 实锤故障（FAIL 证据）
            var warns = new List<string>();    // 可用但劣化（WARN 证据）

            // 1) PnP 侧网络类设备全量扫描（PNPClass='Net'，含非物理网络设备）。
            //    错误码 24/45（设备不存在/历史幽灵设备）只记明细不计故障——防教学机 USB 网卡/
            //    换槽位残留误报，与 physical.link/nic_pnp 的现行连接状态判断互不越界。
            var bad = await WmiHelper.QueryAsync(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='Net' AND ConfigManagerErrorCode != 0",
                mo => new
                {
                    Name = mo["Name"]?.ToString() ?? "",
                    Err = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0)
                }).ConfigureAwait(false);

            foreach (var d in bad)
            {
                var text = d.Err switch
                {
                    1 => "设备未正确配置", 10 => "设备无法启动", 11 => "设备异常（将失败）", 12 => "系统资源不足",
                    14 => "需重启后重新配置", 18 => "需重装驱动", 22 => "设备被禁用", 28 => "驱动未安装",
                    29 => "被固件禁用", 31 => "设备驱动异常", 43 => "设备因故障停止",
                    24 => "设备当前不存在（历史记录）", 45 => "未连接（历史幽灵设备）",
                    _ => $"错误码 {d.Err}"
                };
                if (d.Err is 24 or 45)
                    details.Add($"○ {d.Name}: {text}（不计入故障）");
                else
                {
                    details.Add($"⚠ {d.Name}: {text}");
                    problems.Add($"{d.Name}（{text}）");
                }
            }
            if (bad.Count == 0) details.Add("网络类 PnP 设备：无异常（含 WAN Miniport/蓝牙/虚拟网卡）");

            // 2) 双工与速率协商 + 驱动日期（MSFT_NetAdapter，Win8+ 自带；查询失败按信息不可得降级。
            //    Hidden 过滤对齐 Get-NetAdapter 语义，去掉 WAN Miniport 等隐藏适配器的噪音行）
            var nics = await WmiHelper.QueryInScopeAsync(
                WmiScopes.StandardCimV2,
                "SELECT Name, InterfaceDescription, FullDuplex, Speed, DriverDate, DriverVersionString, Hidden FROM MSFT_NetAdapter",
                mo => new
                {
                    Name = mo["Name"]?.ToString() ?? "",
                    Desc = mo["InterfaceDescription"]?.ToString() ?? "",
                    FullDuplex = mo["FullDuplex"] is bool b ? b : (bool?)null,
                    Speed = Convert.ToUInt64(mo["Speed"] ?? (object)0UL),
                    DriverDate = mo["DriverDate"]?.ToString() ?? "",
                    DriverVer = mo["DriverVersionString"]?.ToString() ?? "",
                    Hidden = mo["Hidden"] is bool h && h
                }).ConfigureAwait(false);

            int nicRows = 0;
            foreach (var n in nics)
            {
                // 隐藏适配器与未连接且信息不可得的适配器（速率 0、无双工与驱动信息）不出行，防列表噪音
                if (n.Hidden) continue;
                if (n.Speed == 0 && n.FullDuplex == null && n.DriverVer.Length == 0) continue;
                nicRows++;
                var mbps = n.Speed / 1_000_000;
                var duplexText = n.FullDuplex == null ? "双工未知" : n.FullDuplex == true ? "全双工" : "半双工";
                var dateText = ParseDriverDate(n.DriverDate);
                var verText = dateText != null ? $"{n.DriverVer}（{dateText}）" : n.DriverVer;
                details.Add($"{n.Name}（{n.Desc}）: {duplexText} · {mbps} Mbps · 驱动 {verText}");
                if (n.FullDuplex == false && n.Speed > 0)
                {
                    details.Add($"⚠ {n.Name} 协商为半双工：交换网络下正常应为全双工，多为网线线序/水晶头/端口协商故障");
                    warns.Add($"{n.Name} 半双工（{mbps} Mbps）");
                }
                else if (n.Speed > 0 && n.Speed < 100_000_000)
                    details.Add($"○ {n.Name} 链路速率仅 {mbps} Mbps：劣质/老化网线或老旧端口的典型表现");
                if (dateText != null && int.TryParse(dateText.AsSpan(0, 4), out var year) && year <= DateTime.Now.Year - 8)
                    details.Add($"○ {n.Name} 驱动发布于 {dateText}（超过 8 年）：能用但偏旧，遇兼容性问题可到设备厂商官网更新");
            }

            // 3) 主适配器自开机累计的收发错误/丢包计数（IPv4InterfaceStatistics，量化硬件证据）
            var primary = ctx.Primary;
            if (primary != null)
            {
                try
                {
                    var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Id == primary.Id);
                    if (nic != null)
                    {
                        var s = nic.GetIPv4Statistics();
                        var ioErr = s.IncomingPacketsWithErrors + s.OutgoingPacketsWithErrors;
                        var discard = s.IncomingPacketsDiscarded + s.OutgoingPacketsDiscarded;
                        var errText = $"收 {s.IncomingPacketsWithErrors} / 发 {s.OutgoingPacketsWithErrors}";
                        var disText = $"收 {s.IncomingPacketsDiscarded} / 发 {s.OutgoingPacketsDiscarded}";
                        details.Add($"主适配器错误包计数（{primary.Name}，开机累计）: 错误 {errText}，丢弃 {disText}");
                        if (ioErr >= 50)
                        {
                            details.Add($"⚠ 收发错误包累计 {ioErr} 个：链路质量差/驱动异常/硬件故障的量化证据");
                            warns.Add($"主适配器收发错误包累计 {ioErr} 个");
                        }
                        else if (ioErr > 0)
                            details.Add("○ 错误包计数偏低（少于 50 个），一般为瞬时抖动，可忽略");
                        if (discard >= 500)
                            details.Add($"○ 收发丢弃包累计 {discard} 个：多为突发流量下缓冲区瞬时不足，持续增长才需关注");
                    }
                }
                catch { /* 计数不可得不影响其他证据 */ }
            }

            // 4) 网卡节能状态（MSFT_NetAdapterPowerManagementSettingData；值域为微软文档实锤：
            //    0=Unsupported / 1=Disabled / 2=Enabled——「允许计算机关闭此设备以节约电源」
            //    即 Enabled(2)。开启是休眠/长时间空闲后掉线的常见线索，与一键修复
            //    「关闭网卡节能」联动，只记线索不计故障）
            var pm = await WmiHelper.QueryInScopeAsync(
                WmiScopes.StandardCimV2,
                "SELECT Name, AllowComputerToTurnOffDevice FROM MSFT_NetAdapterPowerManagementSettingData",
                mo => new
                {
                    Name = mo["Name"]?.ToString() ?? "",
                    Allow = mo["AllowComputerToTurnOffDevice"]?.ToString() ?? ""
                }).ConfigureAwait(false);
            var powerOff = pm.Where(x => x.Allow == "2" && x.Name.Length > 0).Select(x => x.Name).ToList();
            if (powerOff.Count > 0)
                details.Add("○ 网卡节能（允许计算机关闭此设备以节约电源）已开启: " + string.Join("、", powerOff.Take(3)) +
                            "——休眠/空闲后断网的常见线索，可用一键修复「关闭网卡节能」处理");

            if (problems.Count > 0)
                return CheckOutcome.Fail($"网络硬件存在 {problems.Count} 处异常：{string.Join("；", problems.Take(3))}",
                    "网络类设备报错通常需重装驱动或报修：先在设备管理器卸载该设备（勾选删除驱动程序）后重启复检，仍异常则报修换件。",
                    details.ToArray());

            if (warns.Count > 0)
                return CheckOutcome.Warn($"网络硬件存在 {warns.Count} 处劣化：{string.Join("；", warns.Take(2))}",
                    "半双工与错误包累积多为网线线序、水晶头氧化或端口协商问题：先更换网线或换交换机端口，仍异常则报修。",
                    details.ToArray());

            return CheckOutcome.Pass($"网络硬件正常（{nicRows} 个适配器双工/速率协商正常，网络类 PnP 设备无异常）",
                details.ToArray());
        }

        /// <summary>WMI DMTF 时间（yyyyMMddHHmmss.ffffff+zzz）取日期段，格式异常返回 null。</summary>
        private static string? ParseDriverDate(string dmtf)
        {
            if (dmtf.Length < 8) return null;
            var head = dmtf[..8];
            return head.All(char.IsDigit) ? $"{head[..4]}-{head[4..6]}-{head[6..8]}" : null;
        }
    }

    // ---------------- Wi-Fi 信号质量 ----------------
    public sealed class WifiSignalCheck : CheckBase
    {
        public override string Id => "wifi.signal";
        public override string Name => "Wi-Fi 信号质量";
        public override CheckCategory Category => CheckCategory.PhysicalLink;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var wifi = ctx.Wifi ?? await NetworkProbeHelper.GetWifiInfoAsync().ConfigureAwait(false);
            if (wifi == null)
                return CheckOutcome.Skip("未使用无线网络（有线连接），跳过 Wi-Fi 检查。");
            if (wifi.SignalPct < 0)
                return CheckOutcome.Pass($"Wi-Fi 已连接：{wifi.Ssid}");
            var detail = $"SSID: {wifi.Ssid}  信号强度: {wifi.SignalPct}%";
            if (wifi.SignalPct < 40)
                return CheckOutcome.Fail($"Wi-Fi 信号极弱（{wifi.SignalPct}%）", "请靠近无线 AP / 信号发射点，或反馈信息中心检查教室 AP。", detail);
            if (wifi.SignalPct < 65)
                return CheckOutcome.Warn($"Wi-Fi 信号偏弱（{wifi.SignalPct}%）", "信号弱会导致掉线与高延迟，建议靠近 AP 或由信息中心调整 AP 功率/信道。", detail);
            return CheckOutcome.Pass($"Wi-Fi 信号良好（{wifi.SignalPct}%）", detail);
        }
    }

    // ---------------- IP 地址配置 ----------------
    public sealed class IpConfigCheck : CheckBase
    {
        public override string Id => "ip.config";
        public override string Name => "IP 地址配置";
        public override CheckCategory Category => CheckCategory.IpConfig;
        public override bool InQuickMode => true;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null) return Task.FromResult(CheckOutcome.Skip("无可用适配器。"));
            var details = new List<string>
            {
                $"IPv4: {p.IpV4 ?? "无"}  掩码: {p.MaskV4 ?? "无"}",
                $"获取方式: {(p.IsDhcpEnabled ? "DHCP 自动获取" : "静态配置")}",
                $"网关: {p.Gateway ?? "无"}",
                $"DNS: {(p.DnsServers.Count > 0 ? string.Join(", ", p.DnsServers) : "无")}"
            };
            if (string.IsNullOrEmpty(p.IpV4))
                return Task.FromResult(CheckOutcome.Fail("未获取到 IPv4 地址", "网线/无线已连接但无 IP，通常是 DHCP 服务不可达，稍后会自动尝试续租修复。", canFix: true, details.ToArray()));

            if (NetworkProbeHelper.IsApipa(p.IpV4))
                return Task.FromResult(CheckOutcome.Fail($"获取到自动私有地址（APIPA {p.IpV4}），说明 DHCP 获取失败",
                    "将自动尝试“释放并重新获取 IP”；若多次失败，请联系信息中心检查校园网 DHCP 服务。", canFix: true, details.ToArray()));

            if (string.IsNullOrEmpty(p.Gateway))
                return Task.FromResult(CheckOutcome.Fail("已获取 IP 但未配置默认网关", "无法访问本网段以外的资源；将尝试续租修复，静态网络请核对网关配置。", canFix: true, details.ToArray()));

            if (p.DnsServers.Count == 0)
                return Task.FromResult(CheckOutcome.Fail("未配置 DNS 服务器", "无法解析任何域名；将尝试自动修复，也可在设置中配置首选 DNS。", canFix: true, details.ToArray()));

            ctx.SetShared("ip.ok", true);
            return Task.FromResult(CheckOutcome.Pass($"IP 配置正常（{p.IpV4}，{(p.IsDhcpEnabled ? "DHCP" : "静态")}）", details.ToArray()));
        }
    }

    // ---------------- DHCP 租约 ----------------
    public sealed class DhcpLeaseCheck : CheckBase
    {
        public override string Id => "ip.dhcp_lease";
        public override string Name => "DHCP 租约状态";
        public override CheckCategory Category => CheckCategory.IpConfig;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null) return CheckOutcome.Skip("无可用适配器。");
            if (!p.IsDhcpEnabled) return CheckOutcome.Pass("静态地址配置，不涉及 DHCP 租约。");

            var list = await WmiHelper.QueryAsync(
                "SELECT Description, DHCPEnabled, DHCPLeaseObtained, DHCPLeaseExpires, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=true",
                mo => new
                {
                    Desc = mo["Description"]?.ToString() ?? "",
                    Obtained = ManagementDateTimeConverter.ToDateTime(mo["DHCPLeaseObtained"]?.ToString() ?? ""),
                    Expires = ManagementDateTimeConverter.ToDateTime(mo["DHCPLeaseExpires"]?.ToString() ?? "")
                }).ConfigureAwait(false);

            var lease = list.FirstOrDefault(l => p.Description.StartsWith(l.Desc[..Math.Min(l.Desc.Length, 20)], StringComparison.OrdinalIgnoreCase))
                        ?? list.FirstOrDefault();
            if (lease == null || lease.Expires == default)
                return CheckOutcome.Pass("DHCP 租约信息暂不可读，但 IP 已正常获取。");

            var details = new List<string> { $"租约获得: {lease.Obtained:yyyy-MM-dd HH:mm}", $"租约到期: {lease.Expires:yyyy-MM-dd HH:mm}" };
            if (lease.Expires < DateTime.Now)
                return CheckOutcome.Fail($"DHCP 租约已过期（{lease.Expires:HH:mm}）", "将自动尝试续租；若反复过期，请联系信息中心检查 DHCP 作用域地址池是否耗尽。", canFix: true, details.ToArray());
            if (lease.Expires < DateTime.Now.AddMinutes(10))
                return CheckOutcome.Warn("DHCP 租约即将到期", "系统会自动续租，一般无需处理；如正在续租中出现断网属正常现象。", details.ToArray());
            return CheckOutcome.Pass("DHCP 租约正常", details.ToArray());
        }
    }

    // ---------------- IP 地址冲突检测（主动 ARP 探测 + 系统冲突事件佐证，插件版新增） ----------------
    public sealed class IpConflictCheck : CheckBase
    {
        public override string Id => "ip.conflict";
        public override string Name => "IP 地址冲突检测";
        public override CheckCategory Category => CheckCategory.IpConfig;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null || string.IsNullOrEmpty(p.IpV4) || !System.Net.IPAddress.TryParse(p.IpV4, out var selfIp))
                return CheckOutcome.Skip("本机无 IPv4 地址，跳过冲突探测。");
            if (NetworkProbeHelper.IsApipa(p.IpV4))
                return CheckOutcome.Skip("当前为自动私有地址（APIPA），先解决 DHCP 获取问题，冲突探测顺延。");

            // 本机全部适配器 MAC（物理+虚拟）：应答 MAC 命中其一即本机自答，不算冲突
            var selfMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var pa = nic.GetPhysicalAddress();
                if (pa == null || pa.GetAddressBytes().Length < 6) continue;
                selfMacs.Add(FormatMac(pa.GetAddressBytes()));
            }

            // 主动探测：对本机 IP 连发 3 次 ARP，间隔 300ms（多次取证防单次缓存抖动）
            var foreign = new List<string>();
            var selfAnswers = 0;
            for (int i = 0; i < 3; i++)
            {
                ct.ThrowIfCancellationRequested();
                var mac = NativeIphlpapi.ProbeMac(selfIp);
                if (mac != null)
                {
                    if (selfMacs.Contains(mac)) selfAnswers++;
                    else foreign.Add(mac);
                }
                if (i < 2) await Task.Delay(300, ct).ConfigureAwait(false);
            }

            // 历史佐证：系统事件日志 Tcpip 4199（检测到 IP 与网络上其他系统冲突），近 7 天
            var since = DateTime.Now.AddDays(-7).ToString("yyyyMMddHHmmss");
            var conflicts = await WmiHelper.QueryAsync(
                "SELECT TimeGenerated, Message FROM Win32_NTLogEvent WHERE LogFile='System' AND EventCode=4199 AND SourceName='Tcpip'",
                mo => new
                {
                    Time = mo["TimeGenerated"]?.ToString() ?? "",
                    Msg = mo["Message"]?.ToString() ?? ""
                }).ConfigureAwait(false);
            var recent = conflicts
                .Where(e => e.Time.Length >= 14 && string.CompareOrdinal(e.Time[..14], since) >= 0)
                .OrderByDescending(e => e.Time)
                .ToList();

            var details = new List<string>
            {
                $"探测目标: 本机 IP {p.IpV4}（3 次 ARP 请求）",
                $"本机应答 MAC: {(selfAnswers > 0 ? "收到（自答正常）" : "未收到（部分栈不对自探应答，不影响结论）")}"
            };
            foreach (var e in recent.Take(3))
                details.Add($"系统冲突记录 {e.Time[..14]}: {FirstLine(e.Msg)}");

            if (foreign.Count > 0)
            {
                var macText = string.Join("、", foreign.Distinct());
                return CheckOutcome.Fail($"IP 冲突实锤：{p.IpV4} 被其他设备占用（应答 MAC {macText}）",
                    "两台机器用同一 IP 会互相挤掉线。请检查本机是否手工配置了与同网段其他机器相同的地址；建议改回“自动获取 IP”后重启，或联系信息中心排查占用方。",
                    details.ToArray());
            }

            if (recent.Count > 0)
                return CheckOutcome.Warn($"当前未探测到冲突，但系统近 7 天记录过 {recent.Count} 次 IP 冲突事件",
                    "多为间歇性冲突（对方设备时开时关）。建议反馈信息中心核对同网段地址分配；本机可改回自动获取 IP 避开占用。",
                    details.ToArray());

            return CheckOutcome.Pass("未发现 IP 地址冲突（网内无其他主机冒用本机 IP）", details.ToArray());
        }

        private static string FormatMac(byte[] bytes) => string.Join("-", bytes.Select(b => b.ToString("X2")));

        private static string FirstLine(string s)
        {
            var i = s.IndexOf('\n');
            return (i > 0 ? s[..i] : s).Trim();
        }
    }

    // ---------------- 网关连通 ----------------
    public sealed class GatewayReachableCheck : CheckBase
    {
        public override string Id => "gateway.reachable";
        public override string Name => "默认网关连通（路由器/交换机）";
        public override CheckCategory Category => CheckCategory.GatewayRouting;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw))
                return CheckOutcome.Fail("没有默认网关可测试", "未配置网关意味着无法访问其他网段，请先解决 IP 配置问题。");

            var stats = await NetworkProbeHelper.PingAsync(gw, ctx.PingCount, ctx.TimeoutMs).ConfigureAwait(false);
            // 拓扑快照数据：无论结论如何都写入共享上下文供拓扑图使用
            ctx.SetShared(TopoKeys.Gateway, new TopoSegmentStats(gw, stats, stats.Received > 0));
            var details = new List<string>
            {
                $"网关地址: {gw}",
                $"丢包: {stats.LossPct:0.#}% ({stats.Received}/{stats.Sent})",
                $"延迟: 平均 {stats.AvgMs:0.#} ms  抖动 {stats.JitterMs:0.#} ms  最小/最大 {stats.MinMs:0.#}/{stats.MaxMs:0.#} ms"
            };

            if (stats.AllLost)
                return CheckOutcome.Fail($"网关 {gw} 完全不通", "本机到路由器/三层交换机之间断开：请检查网线、交换机端口，或重启教室路由器后重试。", details.ToArray());
            if (stats.LossPct > 20)
                return CheckOutcome.Fail($"网关丢包严重（{stats.LossPct:0.#}%）", "网线老化/水晶头接触不良/AP 过载的典型表现：先换一根网线或换一个端口，仍异常则报修。", details.ToArray());
            if (stats.AvgMs > ctx.Settings.LatencyFailMs)
                return CheckOutcome.Warn($"网关延迟过高（平均 {stats.AvgMs:0.#} ms）", "内网延迟异常，可能为 AP 过载或内网环路，请反馈信息中心。", details.ToArray());
            return CheckOutcome.Pass($"网关连通正常（平均 {stats.AvgMs:0.#} ms，丢包 {stats.LossPct:0.#}%）", details.ToArray());
        }
    }

    // ---------------- ARP 表项 ----------------
    public sealed class ArpCheck : CheckBase
    {
        public override string Id => "gateway.arp";
        public override string Name => "网关 ARP 表项（防欺骗/冲突）";
        public override CheckCategory Category => CheckCategory.GatewayRouting;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过。");

            var r = await ProcessRunner.RunAsync("arp -a").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 ARP 表", $"arp 命令失败：{r.Error ?? r.StdErr}");

            string? gwMac = null;
            var macMap = new Dictionary<string, string>();
            foreach (var raw in r.StdOut.Split('\n'))
            {
                var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && System.Net.IPAddress.TryParse(parts[0], out _))
                {
                    var mac = parts[1].ToUpperInvariant();
                    macMap[parts[0]] = mac;
                    if (parts[0] == gw) gwMac = mac;
                }
            }
            var details = macMap.Select(kv => $"{kv.Key} → {kv.Value}").ToList();

            if (gwMac == null)
                return CheckOutcome.Fail($"ARP 表中缺少网关 {gw} 的表项", "将自动清除 ARP 缓存并重新学习；若仍缺失，可能是二层隔离或网关故障。", canFix: true,
                    $"ARP 表共 {macMap.Count} 条");
            if (gwMac.Contains("00-00-00-00") || gwMac.StartsWith("FF"))
                return CheckOutcome.Fail($"网关 {gw} 的 ARP 表项无效（{gwMac}）", "将自动清除 ARP 缓存重试；反复出现请检查是否有人使用 ARP 欺断工具。", canFix: true);

            // 同一 MAC 出现在多个 IP → IP/MAC 冲突或欺骗
            // 排除组播/广播地址防误报：组播 MAC 仅由 IP 低 23 位构成，224.1.1.1 与 225.1.1.1
            // 这类「不同 IP」天然映射到同一 01-00-5E-01-01-01，并非欺骗。
            var dup = macMap
                .Where(kv => !NetworkProbeHelper.IsMulticastOrBroadcastIp(kv.Key))
                .GroupBy(kv => kv.Value)
                .Where(g => g.Count() > 1 && !g.Key.StartsWith("FF"))
                .ToList();
            if (dup.Count > 0)
            {
                var lines = dup.Select(g => $"MAC {g.Key} 同时对应: {string.Join(", ", g.Select(x => x.Key))}");
                return CheckOutcome.Warn("发现多个 IP 绑定同一 MAC，存在 IP 冲突或 ARP 欺骗嫌疑",
                    "请反馈信息中心做内网排查（可能有人在用限速/欺骗软件）。", details.Concat(lines).ToArray());
            }
            return CheckOutcome.Pass($"网关 ARP 表项正常（{gw} → {gwMac}）", details.ToArray());
        }
    }

    // ---------------- 默认路由 ----------------
    public sealed class RouteCheck : CheckBase
    {
        public override string Id => "gateway.route";
        public override string Name => "默认路由检查";
        public override CheckCategory Category => CheckCategory.GatewayRouting;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("route print -4").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取路由表", r.Error ?? r.StdErr);

            var defaults = new List<string>();
            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("0.0.0.0", StringComparison.Ordinal)) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                    defaults.Add($"目标 {parts[0]}/{parts[1]} → 网关 {parts[2]}（接口 {parts[3]}，跃点 {parts[4]}）");
            }
            if (defaults.Count == 0)
                return CheckOutcome.Fail("路由表中不存在默认路由（0.0.0.0/0）", "将尝试续租修复；静态网络请检查网关与跃点数配置。", canFix: true, "route print -4 中未发现默认路由");

            var details = defaults.ToList();
            if (defaults.Count > 1)
                return CheckOutcome.Warn("存在多条默认路由（多出口/多网卡并存）", "多默认路由会造成时通时断：建议断开多余网络（如同时插网线又连 Wi-Fi），或由信息中心调整跃点数。", details.ToArray());
            return CheckOutcome.Pass("默认路由正常", details.ToArray());
        }
    }
}

// ================= 检查实现：第二层（宿主 Services/Network/Checks.DnsPortal.cs 同源移植） =================

/// <summary>
/// 第二层：DNS / 城域网认证 / 校园内网 / 互联网连通（10 项）。
/// </summary>
public static partial class NetworkChecks
{
    private const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string NcsiExpected = "Microsoft Connect Test";

    // ---------------- DNS 配置 ----------------
    public sealed class DnsConfigCheck : CheckBase
    {
        public override string Id => "dns.config";
        public override string Name => "DNS 服务器配置";
        public override CheckCategory Category => CheckCategory.Dns;
        public override bool InQuickMode => true;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var dns = ctx.Primary?.DnsServers ?? new List<string>();
            var details = new List<string> { $"当前 DNS: {(dns.Count > 0 ? string.Join(", ", dns) : "无")}" };

            if (dns.Count == 0)
                return Task.FromResult(CheckOutcome.Fail("未配置任何 DNS 服务器", "将自动修复（启用自动获取 DNS）；也可在设置中手动指定首选 DNS。", canFix: true, details.ToArray()));

            var weird = dns.Where(d => NetworkProbeHelper.IsLoopbackOrInvalid(d)).ToList();
            if (weird.Count > 0)
                return Task.FromResult(CheckOutcome.Fail($"DNS 配置异常（{string.Join(", ", weird)}）", "存在无效 DNS 地址（回环/0.0.0.0），多由流氓软件或误配置导致，将尝试自动修复。", canFix: true, details.ToArray()));

            ctx.SetShared("dns.servers", dns);
            return Task.FromResult(CheckOutcome.Pass($"DNS 配置正常（{string.Join(", ", dns)}）", details.ToArray()));
        }
    }

    // ---------------- 系统域名解析 ----------------
    public sealed class DnsResolveCheck : CheckBase
    {
        public override string Id => "dns.resolve";
        public override string Name => "系统域名解析";
        public override CheckCategory Category => CheckCategory.Dns;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string[] domains = { "www.baidu.com", "www.qq.com" };
            int failed = 0;
            foreach (var d in domains)
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(d).ConfigureAwait(false);
                    var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (v4 == null || NetworkProbeHelper.IsLoopbackOrInvalid(v4.ToString()))
                    {
                        failed++;
                        details.Add($"⚠ {d} → {string.Join(", ", addrs.Select(a => a.ToString()))}（结果无效）");
                    }
                    else details.Add($"{d} → {v4}");
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add($"✗ {d} → 解析失败（{ex.Message}）");
                }
            }
            sw.Stop();
            if (failed == domains.Length)
                return CheckOutcome.Fail("所有测试域名均解析失败", "DNS 服务不可用：将自动刷新 DNS 缓存并探测 DNS 服务器；校园网认证过期也会导致此现象。", canFix: true, details.ToArray());
            if (failed > 0)
                return CheckOutcome.Warn("部分域名解析失败", $"耗时 {sw.ElapsedMilliseconds} ms；持续出现请更换 DNS（设置中可改首选/备用）。", details.ToArray());
            ctx.SetShared("dns.resolve.ok", true);
            return CheckOutcome.Pass("域名解析正常", details.ToArray());
        }
    }

    // ---------------- DNS 服务器逐台探测 ----------------
    public sealed class DnsServerProbeCheck : CheckBase
    {
        public override string Id => "dns.servers";
        public override string Name => "DNS 服务器逐台探测";
        public override CheckCategory Category => CheckCategory.Dns;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var servers = ctx.GetShared<List<string>>("dns.servers") ?? ctx.Primary?.DnsServers ?? new List<string>();
            servers = servers.Distinct().Take(3).ToList();
            if (servers.Count == 0) return CheckOutcome.Skip("无 DNS 服务器可探测。");

            var details = new List<string>();
            var probes = new List<TopoDnsProbe>(); // 拓扑快照数据：逐台结果供拓扑图 DNS 分支使用
            int okCount = 0;
            foreach (var s in servers)
            {
                var r = await DnsQueryHelper.ResolveAsync(s, "www.baidu.com", 2000).ConfigureAwait(false);
                if (r.Ok) { okCount++; details.Add($"✓ {s} → {r.AddressText}（{r.ElapsedMs} ms）"); probes.Add(new TopoDnsProbe(s, true, r.ElapsedMs)); }
                else { details.Add($"✗ {s} → {r.Error}（{r.ElapsedMs} ms）"); probes.Add(new TopoDnsProbe(s, false, r.ElapsedMs)); }
            }
            ctx.SetShared(TopoKeys.Dns, probes);

            if (okCount == 0)
            {
                // 补测公共 DNS 以判断“DNS 服务问题”还是“全网不通”
                var publicProbe = await DnsQueryHelper.ResolveAsync(ctx.Settings.DnsPreferred, "www.baidu.com", 2000).ConfigureAwait(false);
                details.Add($"公共 DNS {ctx.Settings.DnsPreferred} 对照: {(publicProbe.Ok ? "可达（说明是本机 DNS 配置问题）" : "不可达（可能是外网不通）")}");
                return CheckOutcome.Fail("配置的 DNS 服务器全部无响应", "若对照公共 DNS 可达：将自动把 DNS 修复为公共地址；若也不可达：属于外网/出口问题，请看外网连通项结论。", canFix: true, details.ToArray());
            }
            if (okCount < servers.Count)
                return CheckOutcome.Warn("部分 DNS 服务器无响应", "不影响当前使用，但会影响稳定性：建议在设置中替换失效的 DNS 地址。", details.ToArray());
            return CheckOutcome.Pass($"{okCount} 台 DNS 服务器全部响应正常", details.ToArray());
        }
    }

    // ---------------- Hosts 文件劫持 ----------------
    public sealed class HostsCheck : CheckBase
    {
        private static readonly string[] WatchDomains =
        {
            "www.baidu.com", "www.qq.com", "www.msftconnecttest.com", "dns.msftncsi.com",
            "www.msftncsi.com", "login.weixin.qq.com", "wx.qq.com", "www.taobao.com"
        };

        public override string Id => "dns.hosts";
        public override string Name => "Hosts 文件劫持检查";
        public override CheckCategory Category => CheckCategory.Dns;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            string path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            try
            {
                if (!File.Exists(path)) return Task.FromResult(CheckOutcome.Pass("Hosts 文件不存在（视为干净）。"));
                var lines = File.ReadAllLines(path);
                var active = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")).ToList();
                var details = new List<string> { $"有效条目 {active.Count} 条" };
                var hijacks = new List<string>();
                foreach (var line in active)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    var ip = parts[0].ToLowerInvariant();
                    foreach (var dom in parts.Skip(1))
                    {
                        var d = dom.ToLowerInvariant();
                        if (WatchDomains.Any(w => d == w || d.EndsWith("." + w)))
                            hijacks.Add($"{ip}  {d}");
                    }
                }
                if (hijacks.Count > 0)
                    return Task.FromResult(CheckOutcome.Fail($"Hosts 文件劫持了 {hijacks.Count} 个关键域名",
                        "多由病毒/破解工具/流氓软件写入，会导致认证或网页打不开：请用杀毒软件清理后手动清空相关行。", hijacks.ToArray()));
                // 大量条目提示
                if (active.Count > 50)
                    return Task.FromResult(CheckOutcome.Warn($"Hosts 文件存在 {active.Count} 条有效条目", "数量异常偏多，常见于广告过滤工具或批量屏蔽清单；如无印象建议排查。", details.ToArray()));
                return Task.FromResult(CheckOutcome.Pass("Hosts 文件干净", details.ToArray()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CheckOutcome.Warn("无法读取 Hosts 文件", $"读取失败：{ex.Message}"));
            }
        }
    }

    // ---------------- 城域网认证门户检测 ----------------
    public sealed class PortalDetectCheck : CheckBase
    {
        public override string Id => "portal.detect";
        public override string Name => "城域网认证门户检测";
        public override CheckCategory Category => CheckCategory.PortalAuth;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await NetworkProbeHelper.HttpAsync(NcsiUrl, followRedirect: false, readBody: true).ConfigureAwait(false);
            if (!r.Ok)
                return CheckOutcome.Fail("无法发起网络探测（HTTP 请求失败）", $"探测地址 {NcsiUrl} 不可达：{r.Error}。若前面网关/DNS 均正常，可能是出口防火墙拦截。");

            if (r.StatusCode == 200 && r.BodySnippet.Contains(NcsiExpected, StringComparison.Ordinal))
            {
                ctx.SetShared("portal.detected", false);
                ctx.SetShared("internet.http.ok", true);
                return CheckOutcome.Pass("网络直通：未检测到认证门户，城域网已放行。", $"探测 {NcsiUrl} 返回预期内容（{r.ElapsedMs} ms）");
            }

            // 被重定向或返回了门户页面 → 未认证
            string portalUrl = "";
            if (r.IsRedirect && !string.IsNullOrEmpty(r.Location))
            {
                portalUrl = r.Location;
                if (portalUrl.StartsWith("/", StringComparison.Ordinal))
                {
                    try { portalUrl = new Uri(new Uri(NcsiUrl), portalUrl).ToString(); } catch { }
                }
                var page = await NetworkProbeHelper.HttpAsync(portalUrl, followRedirect: true).ConfigureAwait(false);
                if (page.Ok && !string.IsNullOrEmpty(page.FinalUrl)) portalUrl = page.FinalUrl;
            }
            else if (r.LooksLikeHtml)
            {
                portalUrl = NcsiUrl;
            }

            var keywords = new[] { "认证", "登录", "上网", "portal", "srun", "drcom", "dr.com", "锐捷", "ruijie", "cmcc", "campus", "login", "auth" };
            var snippet = (r.BodySnippet ?? "");
            bool looksPortal = r.LooksLikeHtml && keywords.Any(k => snippet.Contains(k, StringComparison.OrdinalIgnoreCase));

            ctx.SetShared("portal.detected", true);
            // 拓扑快照数据：门户探测结果供拓扑图认证分支使用
            ctx.SetShared(TopoKeys.Portal, new TopoSegmentStats(
                portalUrl.Length > 0 && !portalUrl.StartsWith(NcsiUrl, StringComparison.Ordinal) ? portalUrl : NcsiUrl,
                null, false, r.ElapsedMs, "检测到认证门户（未通过认证）"));
            // 仅在真实捕获到门户地址（非 NCSI 兜底）时写入共享上下文，
            // 避免一键修复打开无意义的探测地址
            if (portalUrl.Length > 0 && !portalUrl.StartsWith(NcsiUrl, StringComparison.Ordinal))
                ctx.SetShared("portal.url", portalUrl);
            var detail = new List<string>
            {
                $"探测 {NcsiUrl} → HTTP {r.StatusCode}{(r.IsRedirect ? $" 重定向到 {r.Location}" : "")}"
            };
            if (portalUrl.Length > 0 && !portalUrl.StartsWith(NcsiUrl, StringComparison.Ordinal)) detail.Add($"门户地址: {portalUrl}");

            // 常见错误关键字 → 认证过期/被踢
            if (looksPortal && Regex.IsMatch(snippet, @"过期|失效|被挤|离线|超时|expire|offline|kick", RegexOptions.IgnoreCase))
                return CheckOutcome.Fail("城域网认证已过期或被踢下线", "请打开认证门户重新登录；如反复被踢，可能账号在别处登录或欠费，联系信息中心核实。", canFix: true, detail.ToArray());

            var portalHint = portalUrl.Length > 0 && !portalUrl.StartsWith(NcsiUrl, StringComparison.Ordinal)
                ? $"已定位认证门户 {portalUrl}，"
                : "";
            return CheckOutcome.Fail("检测到认证门户（当前未通过城域网认证）",
                $"{portalHint}一键修复可完成认证（设置中配置账号后可自动登录，免每次手动输入）；也可在设置中配置门户地址获得更精确的过期判定。", canFix: true, detail.ToArray());
        }
    }

    // ---------------- 认证过期精确判定 ----------------
    public sealed class PortalExpiryCheck : CheckBase
    {
        public override string Id => "portal.expiry";
        public override string Name => "认证状态精确判定";
        public override CheckCategory Category => CheckCategory.PortalAuth;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var s = ctx.Settings;
            var internetOk = ctx.SharedBool("internet.http.ok");

            // 1) 配置了状态接口 → 直接判定
            if (!string.IsNullOrWhiteSpace(s.AuthStatusUrl))
            {
                var r = await NetworkProbeHelper.HttpAsync(s.AuthStatusUrl, followRedirect: true).ConfigureAwait(false);
                if (!r.Ok) return CheckOutcome.Warn("认证状态接口不可达", $"无法访问 {s.AuthStatusUrl}：{r.Error}，请确认地址是否正确。");
                var keywords = (s.AuthOkKeywords ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
                var body = r.BodySnippet ?? "";
                if (keywords.Length > 0 && keywords.Any(k => body.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return CheckOutcome.Pass("认证状态：在线（状态接口返回正常）", $"接口 {s.AuthStatusUrl} 命中在线关键字");
                return CheckOutcome.Fail("认证状态接口显示离线/过期", "请打开认证门户重新登录账号；若登录后仍提示离线，联系教体局信息中心核查账号状态。", $"接口 {s.AuthStatusUrl} 未命中在线关键字");
            }

            // 2) 配置了门户地址 → 可达性 + 外网联动判定
            if (!string.IsNullOrWhiteSpace(s.PortalBaseUrl))
            {
                var host = new Uri(s.PortalBaseUrl).Host;
                var tcp = await NetworkProbeHelper.TcpAsync(host, s.PortalBaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80, 3000).ConfigureAwait(false);
                var details = new List<string> { $"门户 {s.PortalBaseUrl} TCP 探测: {(tcp.Ok ? $"可达（{tcp.ElapsedMs} ms）" : tcp.Error)}", $"外网连通: {(internetOk ? "正常" : "不通")}" };

                if (tcp.Ok && !internetOk)
                    return CheckOutcome.Fail("认证已过期：门户可达但外网不通", "典型的城域网认证过期状态，请立即打开门户重新认证。", canFix: true, details.ToArray());
                if (tcp.Ok && internetOk)
                    return CheckOutcome.Pass("门户可达且外网正常，认证状态良好。", details.ToArray());
                if (!tcp.Ok && internetOk)
                    return CheckOutcome.Warn("外网正常但门户不可达", "门户地址可能配置错误或服务临时下线，不影响当前上网；请在设置中核对门户地址。", details.ToArray());
                return CheckOutcome.Fail("门户与外网均不可达", "网络整体中断：请先看前面“物理链路/网关/DNS”各项结论定位断点。", details.ToArray());
            }

            // 3) 未配置
            return CheckOutcome.Skip("未配置城域网认证地址，无法做精确过期判定（可在 设置 → 网络诊断 中填写门户地址或状态接口）。");
        }
    }

    // ---------------- 校园内网 ----------------
    public sealed class IntranetCheck : CheckBase
    {
        public override string Id => "intranet.probe";
        public override string Name => "校园内网连通";
        public override CheckCategory Category => CheckCategory.Intranet;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var host = ctx.Settings.IntranetProbeHost?.Trim();
            if (string.IsNullOrEmpty(host))
                return CheckOutcome.Skip("未配置内网探测地址（设置 → 网络诊断 中可填写教体局 OA / 内网资源地址）。");

            // 支持纯 IP 与域名
            bool isIp = IPAddress.TryParse(host, out _);
            var details = new List<string>();
            if (!isIp)
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                    details.Add($"{host} → {string.Join(", ", addrs.Select(a => a.ToString()).Take(2))}");
                }
                catch (Exception)
                {
                    return CheckOutcome.Warn($"内网域名 {host} 无法解析", "内网 DNS 未能解析该地址：请确认域名与内网 DNS 配置；校外 DNS 服务器也可能无法解析内网域名。", details.ToArray());
                }
            }
            var stats = await NetworkProbeHelper.PingAsync(host, ctx.PingCount, ctx.TimeoutMs).ConfigureAwait(false);
            details.Add($"ICMP: 丢包 {stats.LossPct:0.#}%，平均 {stats.AvgMs:0.#} ms {(stats.Error.Length > 0 ? $"（{stats.Error}）" : "")}");

            if (stats.AllLost)
            {
                var tcp = await NetworkProbeHelper.TcpAsync(host, 80, 3000).ConfigureAwait(false);
                details.Add($"TCP 80 对照: {(tcp.Ok ? $"可达（{tcp.ElapsedMs} ms）" : tcp.Error)}");
                if (tcp.Ok) return CheckOutcome.Pass($"内网 {host} 可达（ICMP 被禁用，TCP 正常）", details.ToArray());
                return CheckOutcome.Fail($"内网 {host} 不可达", "内网资源不通：若城域网/外网正常，多为内网侧问题，请联系信息中心检查目标服务器与内网策略。", details.ToArray());
            }
            if (stats.LossPct > 20)
                return CheckOutcome.Warn($"内网 {host} 丢包偏高（{stats.LossPct:0.#}%）", "内网链路质量差：请联系信息中心排查沿途链路。", details.ToArray());
            return CheckOutcome.Pass($"内网 {host} 连通正常", details.ToArray());
        }
    }

    // ---------------- 外网 ICMP ----------------
    public sealed class InternetIcmpCheck : CheckBase
    {
        public override string Id => "internet.icmp";
        public override string Name => "外网 ICMP 连通";
        public override CheckCategory Category => CheckCategory.Internet;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            int reachable = 0;
            foreach (var ip in NetworkProbeHelper.PublicDnsServers)
            {
                var stats = await NetworkProbeHelper.PingAsync(ip, Math.Min(3, ctx.PingCount), ctx.TimeoutMs).ConfigureAwait(false);
                if (stats.Received > 0) reachable++;
                details.Add($"{ip}: {(stats.Received > 0 ? $"✓ 平均 {stats.AvgMs:0.#} ms" : $"✗ {stats.Error}")}");
            }
            if (reachable == 0)
                return CheckOutcome.Fail("全部公共 IP 不可达（ICMP）", "不经过域名、直接 ping 公网 IP 也不通，说明出口层中断或屏蔽 ICMP：请结合 HTTP 探测结论综合判断。", details.ToArray());
            ctx.SetShared("internet.icmp.ok", true);
            return CheckOutcome.Pass($"外网 ICMP 正常（{reachable}/{NetworkProbeHelper.PublicDnsServers.Count} 可达）", details.ToArray());
        }
    }

    // ---------------- 外网 HTTP ----------------
    public sealed class InternetHttpCheck : CheckBase
    {
        public override string Id => "internet.http";
        public override string Name => "外网 HTTP 连通";
        public override CheckCategory Category => CheckCategory.Internet;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await NetworkProbeHelper.HttpAsync(NcsiUrl, followRedirect: false).ConfigureAwait(false);
            // 拓扑快照数据：无论结论如何都写入共享上下文供拓扑图使用
            ctx.SetShared(TopoKeys.Inet, new TopoSegmentStats(NcsiUrl, null, r.Ok && !r.IsRedirect, r.ElapsedMs,
                r.IsRedirect ? "HTTP 被重定向到认证门户" : ""));
            if (!r.Ok)
                return CheckOutcome.Fail("HTTP 探测失败", "IP 或可达但 HTTP 被拦截：常见于认证过期（配合认证项）或出口防火墙策略。", $"错误: {r.Error}（{r.ElapsedMs} ms）");

            if (r.IsRedirect)
            {
                ctx.SetShared("portal.detected", true);
                ctx.SetShared(TopoKeys.Portal, new TopoSegmentStats(NcsiUrl, null, false, r.ElapsedMs, "HTTP 被重定向到认证门户"));
                return CheckOutcome.Fail("HTTP 被重定向到认证门户",
                    "城域网认证已过期，所有 HTTP 请求都被引导到登录页，请重新认证（可一键修复打开认证页面）。",
                    canFix: true, $"Location: {r.Location}");
            }
            if (r.StatusCode == 200 && r.BodySnippet.Contains(NcsiExpected, StringComparison.Ordinal))
            {
                ctx.SetShared("internet.http.ok", true);
                return CheckOutcome.Pass("外网 HTTP 正常", $"探测 {NcsiUrl} → 200（{r.ElapsedMs} ms）");
            }
            return CheckOutcome.Warn($"HTTP 响应异常（状态码 {r.StatusCode}）", "内容不符合预期，可能存在透明代理或内容过滤，请结合代理检查项结论。", $"Content-Type: {r.ContentType}");
        }
    }

    // ---------------- HTTPS/TLS ----------------
    public sealed class InternetHttpsCheck : CheckBase
    {
        public override string Id => "internet.https";
        public override string Name => "HTTPS / TLS 连通";
        public override CheckCategory Category => CheckCategory.Internet;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await NetworkProbeHelper.HttpAsync("https://www.baidu.com", followRedirect: true, readBody: false).ConfigureAwait(false);
            if (r.Ok && r.StatusCode is >= 200 and < 400)
            {
                ctx.SetShared("internet.https.ok", true);
                return CheckOutcome.Pass("HTTPS 连通正常", $"https://www.baidu.com → {r.StatusCode}（{r.ElapsedMs} ms）");
            }
            var err = r.Error;
            if (err.Contains("Ssl", StringComparison.OrdinalIgnoreCase) || err.Contains("trust", StringComparison.OrdinalIgnoreCase)
                || err.Contains("证书", StringComparison.Ordinal))
                return CheckOutcome.Fail("HTTPS 证书校验失败", "极可能是系统时间错误或被中间人过滤：请先看“系统时钟”检查项并校准时间。", $"错误: {err}");
            if (!ctx.SharedBool("internet.http.ok"))
                return CheckOutcome.Warn("HTTPS 不通（HTTP 亦不通）", "整体断网的一部分：优先解决前面的连通性问题。", $"错误: {err}");
            return CheckOutcome.Fail("HTTP 正常但 HTTPS 不通", "典型于 443 出口被防火墙拦截或 TLS 中间盒异常：请反馈信息中心检查出口策略。", $"错误: {err}");
        }
    }
}
// ================= 检查实现：第三层（宿主 Services/Network/Checks.SystemQuality.cs 同源移植） =================

/// <summary>
/// 第三层：系统时钟 / 代理与 VPN / 系统网络栈 / 连接质量（9 项）。
/// </summary>
public static partial class NetworkChecks
{
    // ---------------- 系统时钟校准 ----------------
    public sealed class ClockSkewCheck : CheckBase
    {
        public override string Id => "clock.skew";
        public override string Name => "系统时钟校准";
        public override CheckCategory Category => CheckCategory.Clock;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            // 用不重定向的 HTTP Date 头对照（不依赖 TLS，避免时间错误造成证书失败而测不了）
            var r = await NetworkProbeHelper.HttpAsync("http://www.msftconnecttest.com/connecttest.txt", followRedirect: false, readBody: false).ConfigureAwait(false);
            if (r.ServerDateUtc == null)
                return CheckOutcome.Warn("无法获取服务器时间作为对照", "网络探测失败或响应无 Date 头，跳过时间校准检查。");

            var skew = Math.Abs((DateTime.UtcNow - r.ServerDateUtc.Value).TotalSeconds);
            var details = new List<string> { $"本机 UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}", $"服务器 UTC: {r.ServerDateUtc:yyyy-MM-dd HH:mm:ss}", $"偏差: {skew:0} 秒" };
            if (skew > 300)
                return CheckOutcome.Fail($"系统时钟偏差过大（{skew:0} 秒）", "时间错误会导致 HTTPS 证书失败、城域网认证失败：请立即校准时间（一键修复可自动同步）。", canFix: true, details.ToArray());
            if (skew > 60)
                return CheckOutcome.Warn($"系统时钟偏差 {skew:0} 秒", "建议校准时间，避免部分网站证书告警与认证异常。", details.ToArray());
            return CheckOutcome.Pass($"系统时钟准确（偏差 {skew:0} 秒）", details.ToArray());
        }
    }

    // ---------------- 系统代理 ----------------
    public sealed class ProxyCheck : CheckBase
    {
        public override string Id => "proxy.system";
        public override string Name => "系统代理检查";
        public override CheckCategory Category => CheckCategory.ProxyVpn;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                int proxyEnable = Convert.ToInt32(key?.GetValue("ProxyEnable", 0) ?? 0);
                string proxyServer = key?.GetValue("ProxyServer")?.ToString() ?? "";
                string pac = key?.GetValue("AutoConfigURL")?.ToString() ?? "";
                var details = new List<string> { $"用户代理开关: {(proxyEnable == 1 ? "启用" : "关闭")}", $"代理服务器: {(string.IsNullOrEmpty(proxyServer) ? "无" : proxyServer)}", $"PAC 脚本: {(string.IsNullOrEmpty(pac) ? "无" : pac)}" };

                if (proxyEnable == 1)
                {
                    ctx.SetShared("proxy.enabled", true);
                    return Task.FromResult(CheckOutcome.Fail($"系统已启用代理（{proxyServer}）",
                        "错误代理会让“能 ping 通但打不开网页”：如无特殊需求将自动关闭代理并刷新系统设置。", canFix: true, details.ToArray()));
                }
                if (!string.IsNullOrEmpty(pac))
                    return Task.FromResult(CheckOutcome.Warn("配置了 PAC 自动代理脚本", "校园网内通常无需 PAC；若网页打不开可尝试关闭。", details.ToArray()));
                return Task.FromResult(CheckOutcome.Pass("未启用系统代理", details.ToArray()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CheckOutcome.Warn("无法读取代理配置", ex.Message));
            }
        }
    }

    // ---------------- WinHTTP 代理 ----------------
    public sealed class WinHttpProxyCheck : CheckBase
    {
        public override string Id => "proxy.winhttp";
        public override string Name => "WinHTTP 代理检查";
        public override CheckCategory Category => CheckCategory.ProxyVpn;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh winhttp show proxy").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 WinHTTP 代理", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";
            if (Regex.IsMatch(text, @"直接访问|Direct access", RegexOptions.IgnoreCase))
                return CheckOutcome.Pass("WinHTTP 未设置代理。", text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(6).ToArray());
            ctx.SetShared("proxy.winhttp.set", true);
            return CheckOutcome.Warn("WinHTTP 设置了代理", "系统服务类程序会走此代理，配置错误会导致更新/认证异常：可一键修复重置。", text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(8).ToArray());
        }
    }

    // ---------------- VPN / 多网卡 ----------------
    public sealed class VpnAdaptersCheck : CheckBase
    {
        public override string Id => "vpn.adapters";
        public override string Name => "VPN 与多网卡干扰";
        public override CheckCategory Category => CheckCategory.ProxyVpn;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var adapters = ctx.Adapters;
            var details = adapters.Select(a => $"{a.Name} [{a.Type}] {(a.IsUp ? "已连接" : "断开")} {(a.Gateway != null ? $"GW:{a.Gateway}" : "无网关")}").ToList();
            var suspicious = adapters.Where(a =>
                    a.Type == NetworkInterfaceType.Tunnel
                    || a.Name.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("TAP", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("Ppp", StringComparison.OrdinalIgnoreCase))
                .Where(a => a.IsUp).ToList();
            if (suspicious.Count > 0)
            {
                var names = string.Join(", ", suspicious.Select(a => a.Name));
                return Task.FromResult(CheckOutcome.Warn($"检测到 VPN/隧道适配器在运行（{names}）",
                    "VPN 会接管全部或部分流量，造成校内认证与内网访问异常：教学机建议关闭 VPN 后再诊断。", details.ToArray()));
            }
            var gwCount = adapters.Count(a => a.IsUp && a.Gateway != null);
            if (gwCount > 1)
                return Task.FromResult(CheckOutcome.Warn($"有 {gwCount} 个已连接网卡同时具备网关", "多网络并存会引起路由漂移：建议只保留教学所需的网络（断开 Wi-Fi 或拔掉多余网线）。", details.ToArray()));
            return Task.FromResult(CheckOutcome.Pass("无 VPN 干扰，网络环境干净。", details.ToArray()));
        }
    }

    // ---------------- 网络相关服务状态（可修复：仅启动，不改动启动类型） ----------------
    public sealed class ServicesCheck : CheckBase
    {
        private static readonly (string Name, string Display)[] Watch =
        {
            ("Dhcp", "DHCP 客户端"), ("Dnscache", "DNS 缓存"), ("NlaSvc", "网络位置感知"),
            ("netprofm", "网络列表服务"), ("iphlpsvc", "IP Helper"), ("BFE", "基础筛选引擎")
        };

        public override string Id => "stack.services";
        public override string Name => "网络相关服务状态";
        public override CheckCategory Category => CheckCategory.SystemStack;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            var stopped = new List<string>();
            try
            {
                foreach (var (name, display) in Watch)
                {
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController(name);
                        var st = sc.Status;
                        var text = st == System.ServiceProcess.ServiceControllerStatus.Running ? "运行中" : st.ToString();
                        details.Add($"{display} ({name}): {text}");
                        if (st != System.ServiceProcess.ServiceControllerStatus.Running)
                            stopped.Add($"{display} ({name})");
                    }
                    catch { details.Add($"{display} ({name}): 无法查询"); }
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(CheckOutcome.Warn("服务状态查询失败", ex.Message));
            }

            if (stopped.Count > 0)
                // 应用户需求（2026-10）：由纯只读改为可修复 —— 仅「启动」未运行的网络基础服务，
                // 不改启动类型、不停用任何运行中服务；启动仍需管理员授权（并入提权批处理）
                return Task.FromResult(CheckOutcome.Fail($"网络相关服务未运行：{string.Join("、", stopped)}",
                    "网络基础服务未运行会直接影响联网与网络识别：可一键修复尝试启动它们（需管理员授权，仅启动、不改动启动类型）；"
                    + "也可手动以管理员打开 services.msc 将上述服务启动；若服务被禁用或启动失败，请联系信息中心。",
                    canFix: true, details.ToArray()));
            return Task.FromResult(CheckOutcome.Pass("网络相关服务全部运行中", details.ToArray()));
        }
    }

    // ---------------- 防火墙状态 ----------------
    public sealed class FirewallCheck : CheckBase
    {
        public override string Id => "stack.firewall";
        public override string Name => "防火墙状态";
        public override CheckCategory Category => CheckCategory.SystemStack;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh advfirewall show allprofiles state").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取防火墙状态", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";
            var lines = text.Split('\n').Where(l => l.Contains("配置文件") || l.Contains("Profile") || l.Contains("状态") || l.Contains("State")).Select(l => l.Trim()).ToList();
            int on = Regex.Matches(text, @"启用|ON\b", RegexOptions.IgnoreCase).Count;
            int off = Regex.Matches(text, @"禁用|OFF\b", RegexOptions.IgnoreCase).Count;
            if (off > 0 && on == 0)
                return CheckOutcome.Warn("所有配置文件的防火墙均已关闭", "校园终端建议保持防火墙开启（本工具不会代开，请自行权衡或由信息中心统一处理）。", lines.ToArray());
            return CheckOutcome.Pass($"防火墙状态正常（启用 {on} 项 / 关闭 {off} 项）", lines.ToArray());
        }
    }

    // ---------------- Winsock 目录 ----------------
    public sealed class WinsockCheck : CheckBase
    {
        public override string Id => "stack.winsock";
        public override string Name => "Winsock 目录完整性";
        public override CheckCategory Category => CheckCategory.SystemStack;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh winsock show catalog").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 Winsock 目录", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";
            var providers = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var m = Regex.Match(raw, @"^(?:描述|Description)\s*[:：]\s*(.+)$");
                if (m.Success) providers.Add(m.Groups[1].Value.Trim());
            }
            var foreign = providers.Where(p => !p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                                              && !p.Contains("微软", StringComparison.Ordinal)
                                              && !p.Contains("MSAFD", StringComparison.OrdinalIgnoreCase)
                                              && !p.Contains("RSPCP", StringComparison.OrdinalIgnoreCase)
                                              && !p.Contains("TCP/IP", StringComparison.OrdinalIgnoreCase)
                                              // RSVP TCP/UDP Service Provider 为 Windows 自带（微软 QoS 组件），
                                              // 描述中不含 Microsoft/MSAFD/TCP/IP 字样，必须显式排除防误报
                                              && !p.Contains("RSVP", StringComparison.OrdinalIgnoreCase)).ToList();
            var details = providers.Select(p => p).Take(12).ToArray();
            if (foreign.Count > 0)
                return CheckOutcome.Warn($"发现 {foreign.Count} 个第三方 LSP 提供程序", "第三方 LSP 可能劫持/拖慢网络，甚至导致“能 ping 通但上不了网”：如无印象，可一键修复重置 Winsock（需重启）。", details.Concat(foreign.Select(f => "第三方: " + f)).ToArray());
            return CheckOutcome.Pass($"Winsock 目录正常（{providers.Count} 个提供程序）", details);
        }
    }

    // ---------------- 外网延迟与抖动 ----------------
    public sealed class LatencyQualityCheck : CheckBase
    {
        public override string Id => "quality.latency";
        public override string Name => "外网延迟与抖动";
        public override CheckCategory Category => CheckCategory.Quality;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var target = NetworkProbeHelper.PublicDnsServers[0];
            var stats = await NetworkProbeHelper.PingAsync(target, 10, ctx.TimeoutMs).ConfigureAwait(false);
            var details = new List<string>
            {
                $"目标 {target} × 10 次",
                $"平均 {stats.AvgMs:0.#} ms · 抖动 {stats.JitterMs:0.#} ms · 丢包 {stats.LossPct:0.#}%",
                $"最小/最大: {stats.MinMs:0.#}/{stats.MaxMs:0.#} ms"
            };
            if (stats.LossPct > 10)
                return CheckOutcome.Fail($"外网丢包 {stats.LossPct:0.#}%", "丢包明显：出口带宽拥塞或链路质量差，请反馈信息中心做出口侧排查。", details.ToArray());
            if (stats.JitterMs > 50)
                return CheckOutcome.Warn($"抖动偏大（{stats.JitterMs:0.#} ms）", "延迟波动明显，网课/直播会卡顿：可能是 Wi-Fi 干扰或 AP 过载。", details.ToArray());
            if (stats.AvgMs > 150)
                return CheckOutcome.Warn($"平均延迟偏高（{stats.AvgMs:0.#} ms）", "请结合时段判断：高峰期偏高属带宽问题，非高峰偏高属链路问题。", details.ToArray());
            return CheckOutcome.Pass($"外网延迟良好（平均 {stats.AvgMs:0.#} ms，丢包 {stats.LossPct:0.#}%）", details.ToArray());
        }
    }

    // ---------------- MTU 探测 ----------------
    public sealed class MtuCheck : CheckBase
    {
        public override string Id => "quality.mtu";
        public override string Name => "链路 MTU 探测";
        public override CheckCategory Category => CheckCategory.Quality;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过 MTU 探测。");

            // payload = MTU - 28(IP+ICMP头)
            var (payload, mtu) = await ProbeAsync(gw, 1472).ConfigureAwait(false);
            if (payload == 0)
                // 全部载荷探测失败：多为网关禁 ICMP 或链路不通（连通性问题由 gateway.reachable 等项定性），
                // 不能推断出「MTU 偏小（约 0）」这类误导结论，如实报跳过。
                return CheckOutcome.Skip("网关未响应任何 MTU 探测包（可能禁 ICMP 或链路不通），无法推断 MTU；若网关连通性检查正常，此项可忽略。");
            var details = new List<string> { $"测试有效载荷 {payload} 字节（DF 置位）→ 推断 MTU {mtu}" };
            if (mtu >= 1500)
                return CheckOutcome.Pass("链路 MTU 为标准 1500。", details.ToArray());
            if (mtu >= 1464)
                return CheckOutcome.Warn($"MTU 约 {mtu}（PPPoE 典型值 1492）", "PPPoE/隧道开销导致大包分片：网页/视频偶发加载失败可与此相关，一般无需处理。", details.ToArray());
            return CheckOutcome.Warn($"MTU 偏小（约 {mtu}）", "多层隧道/VPN 会压低 MTU，影响大文件传输：如有明显问题请联系信息中心核查中间链路。", details.ToArray());
        }

        private static async Task<(int Payload, int Mtu)> ProbeAsync(string host, int startPayload)
        {
            int payload = startPayload;
            int[] fallbacks = { 1464, 1452, 1400, 1300, 1200 };
            foreach (var p in new[] { payload }.Concat(fallbacks))
            {
                try
                {
                    using var ping = new Ping();
                    // SendPingAsync 的 buffer 参数是 byte[]（载荷），int 大小需转换
                    var reply = await ping.SendPingAsync(host, 1200, new byte[p], new PingOptions(64, true)).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) return (p, p + 28);
                }
                catch { }
            }
            return (0, 0);
        }
    }
}

// ================= 检查实现：低速专项层（宿主 Services/Network/Checks.Throughput.cs 同源移植） =================

/// <summary>
/// 低速专项检查层（测速与吞吐，8 项）：
/// 链路速率与双工 → Wi-Fi 无线电细节 → DNS 解析耗时 → 分段延迟画像 →
/// 下载吞吐实测 → 后台占用 → TCP 全局参数 → 传递优化 P2P。
/// 全部只读探测；不自动修改任何配置（提速修复走询问式一键修复）。
/// </summary>
public static partial class NetworkChecks
{
    private static string FormatMbps(double mbps)
        => mbps >= 1000 ? $"{mbps / 1000:0.#} Gbps" : mbps > 0 ? $"{mbps:0} Mbps" : "未知";

    // ---------------- 1. 链路速率与双工协商 ----------------
    public sealed class NicRateCheck : CheckBase
    {
        public override string Id => "speed.nic_rate";
        public override string Name => "链路速率与双工协商";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null || !p.IsUp) return CheckOutcome.Skip("无活动网卡，跳过链路速率检查。");
            double mbps = p.SpeedBps / 1_000_000.0;
            ctx.SetShared("speed.linkmbps", mbps);

            var details = new List<string>
            {
                $"网卡：{p.Description}",
                $"类型：{(p.IsWireless ? "无线 Wi-Fi" : "有线以太网")}",
                $"协商速率：{FormatMbps(mbps)}"
            };

            // 一次 PowerShell 调用同时取节能托管状态与全双工状态（cmd 安全：无 % 与未引号的 &；输出带标记前缀防错位）
            bool powersave = false, halfDuplex = false, psKnown = false, duplexKnown = false;
            var ps = await ProcessRunner.RunAsync(
                "powershell -NoProfile -Command \"$a=Get-NetAdapter|Where-Object {$_.InterfaceGuid -eq '"
                + p.Id + "'}; if($a){ $p=''; try{ $p=(Get-NetAdapterPowerManagement -InterfaceIndex $a.InterfaceIndex).AllowComputerToTurnOffDevice }catch{}; Write-Output ('PWR='+$p); Write-Output ('DUP='+$a.FullDuplex) }\"",
                25000).ConfigureAwait(false);
            foreach (var raw in (ps.StdOut ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("PWR=", StringComparison.Ordinal))
                {
                    var v = line[4..].Trim();
                    psKnown = v.Length > 0 && !v.Equals("Unsupported", StringComparison.OrdinalIgnoreCase);
                    powersave = v.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.StartsWith("DUP=", StringComparison.Ordinal))
                {
                    duplexKnown = true;
                    halfDuplex = line[4..].Trim().Equals("False", StringComparison.OrdinalIgnoreCase);
                }
            }
            details.Add($"全双工：{(!duplexKnown ? "未知" : halfDuplex ? "否（半双工！）" : "是")}");
            details.Add($"节能托管（系统可关闭网卡省电）：{(!psKnown ? "未知" : powersave ? "开启" : "关闭")}");
            if (powersave) ctx.SetShared("nic.powersave", true);

            if (halfDuplex)
            {
                ctx.SetShared("nic.rate.low", true);
                return CheckOutcome.Fail("网卡处于半双工模式",
                    "半双工会使吞吐骤降、冲突重传剧增：多为网线老化/水晶头氧化或交换机端口协商异常。可尝试一键修复重新协商；无效请更换网线或换交换机端口。",
                    canFix: true, details.ToArray());
            }
            if (!p.IsWireless && mbps > 0 && mbps < 1000)
            {
                ctx.SetShared("nic.rate.low", true);
                return CheckOutcome.Warn($"有线链路仅协商到 {FormatMbps(mbps)}",
                    "教室有线网通常应为 1 Gbps：检查网线是否五类（CAT5e）及以上、水晶头是否氧化、交换机端口是否百兆；一键修复可重新协商，无效时更换网线。",
                    details.ToArray());
            }
            if (p.IsWireless && mbps > 0 && mbps < 100)
            {
                ctx.SetShared("nic.rate.low", true);
                return CheckOutcome.Warn($"无线协商速率偏低（{FormatMbps(mbps)}）",
                    "无线 PHY 速率低通常意味着信号弱或频段拥挤：尽量靠近 AP、切换 5G 频段 SSID，或改用有线连接。", details.ToArray());
            }
            return CheckOutcome.Pass($"链路速率正常（{FormatMbps(mbps)}，双工正常）", details.ToArray());
        }
    }

    // ---------------- 2. Wi-Fi 无线电细节 ----------------
    public sealed class WifiPhyCheck : CheckBase
    {
        public override string Id => "speed.wifi_phy";
        public override string Name => "Wi-Fi 频段与接收速率";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            if (ctx.Primary is { IsWireless: false })
                return CheckOutcome.Skip("当前使用有线连接，跳过 Wi-Fi 无线电检查。");
            var r = await ProcessRunner.RunAsync("netsh wlan show interfaces", 15000).ConfigureAwait(false);
            if (r.ExitCode != 0 || string.IsNullOrEmpty(r.StdOut))
                return CheckOutcome.Skip("本机无 Wi-Fi 接口或查询失败，跳过。");

            string radio = "";
            int channel = 0;
            double rx = 0;
            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.Trim();
                var m = Regex.Match(line, @"(?:无线电类型|Radio\s+type)\s*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
                if (m.Success && radio.Length == 0) radio = m.Groups[1].Value;
                m = Regex.Match(line, @"(?:信道|Channel)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && channel == 0) channel = int.Parse(m.Groups[1].Value);
                m = Regex.Match(line, @"(?:接收速率|Receive\s+rate)[^:\r\n：]*[:：]\s*([\d.]+)", RegexOptions.IgnoreCase);
                if (m.Success && rx == 0) rx = double.Parse(m.Groups[1].Value);
            }
            if (radio.Length == 0 && channel == 0)
                return CheckOutcome.Skip("Wi-Fi 未连接，跳过。");

            var details = new List<string>
            {
                $"无线电类型：{radio}",
                $"信道：{(channel > 0 ? channel.ToString() : "未知")}（{(channel <= 14 && channel > 0 ? "2.4 GHz" : channel >= 36 ? "5 GHz" : "?")}）",
                $"接收速率（PHY）：{FormatMbps(rx)}"
            };
            var signal = ctx.Wifi?.SignalPct ?? -1;
            if (signal >= 0) details.Add($"信号强度：{signal}%");

            bool is24G = channel > 0 && channel <= 14;
            if (is24G)
            {
                ctx.SetShared("wifi.24g", true);
                if (radio.Contains("802.11b", StringComparison.OrdinalIgnoreCase) || radio.Contains("802.11g", StringComparison.OrdinalIgnoreCase))
                    return CheckOutcome.Warn("2.4 GHz 频段 + 老协议（b/g）",
                        "老旧协议和 2.4G 频段速率天花板极低且极易受干扰：优先切换到 5 GHz SSID，或改用有线。", details.ToArray());
                if (channel != 1 && channel != 6 && channel != 11)
                {
                    ctx.SetShared("wifi.channel.overlap", true);
                    return CheckOutcome.Warn($"2.4 GHz 信道重叠（CH{channel}）",
                        "2.4G 只有 1/6/11 三个互不重叠信道，其余信道会与邻区 AP 相互干扰拖慢全体：建议 AP 侧调整或连接 5G。", details.ToArray());
                }
                if (rx > 0 && rx < 130)
                    return CheckOutcome.Warn($"2.4 GHz 接收速率偏低（{FormatMbps(rx)}）",
                        "多为信号弱或干扰大：靠近 AP、避开微波炉/蓝牙设备，或连接 5 GHz 频段。", details.ToArray());
                return CheckOutcome.Warn("连接在 2.4 GHz 频段",
                    "2.4G 频段在教室密集场景容易拥挤：即便当前速率尚可，高峰期也可能掉速，建议优先使用 5 GHz 或有线。", details.ToArray());
            }

            // 5 GHz
            if (rx > 0 && rx < 200)
                return CheckOutcome.Warn($"5 GHz 接收速率偏低（{FormatMbps(rx)}）",
                    "5G 频段穿墙能力弱：接收速率低说明离 AP 远或有遮挡，建议调整座位/加 AP 或改有线。", details.ToArray());
            return CheckOutcome.Pass($"Wi-Fi 状态良好（{radio}，接收 {FormatMbps(rx)}）", details.ToArray());
        }
    }

    // ---------------- 3. DNS 解析耗时 ----------------
    public sealed class DnsPerfCheck : CheckBase
    {
        private static readonly string[] Domains = { "www.msftconnecttest.com", "www.baidu.com", "qq.com" };

        public override string Id => "speed.dns_perf";
        public override string Name => "DNS 解析耗时";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var servers = (ctx.Primary?.DnsServers ?? new List<string>()).Take(2).ToList();
            if (servers.Count == 0) servers = NetworkProbeHelper.PublicDnsServers.Take(1).ToList();

            var details = new List<string>();
            double allAvg = 0;
            int allCount = 0, failCount = 0;
            foreach (var srv in servers)
            {
                long sum = 0; int ok = 0; int fail = 0;
                for (int i = 0; i < 3; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await DnsQueryHelper.ResolveAsync(srv, Domains[i % Domains.Length], 1500).ConfigureAwait(false);
                    if (r.Ok) { sum += r.ElapsedMs; ok++; }
                    else fail++;
                }
                failCount += fail;
                double avg = ok > 0 ? (double)sum / ok : 0;
                if (ok > 0) { allAvg += sum; allCount += ok; }
                details.Add($"{srv}：平均 {avg:0} ms（成功 {ok}/3）");
            }
            double overall = allCount > 0 ? allAvg / allCount : 0;
            details.Add($"总体平均解析耗时：{overall:0} ms，失败 {failCount} 次");

            if (failCount >= 5 || (allCount == 0))
            {
                ctx.SetShared("dns.slow", true);
                return CheckOutcome.Fail("DNS 解析大面积失败", "解析失败会让每个网页都要等待多次重试：一键修复可将 DNS 切换为公共 DNS（需校园网允许）并刷新缓存。", details.ToArray());
            }
            if (overall > 800)
            {
                ctx.SetShared("dns.slow", true);
                return CheckOutcome.Fail($"DNS 解析极慢（平均 {overall:0} ms）",
                    "打开每个网站都要先等 DNS，这是“网速慢”最容易被忽视的原因之一：建议一键修复切换 DNS。", details.ToArray());
            }
            if (overall > 300)
            {
                ctx.SetShared("dns.slow", true);
                return CheckOutcome.Warn($"DNS 解析偏慢（平均 {overall:0} ms）",
                    "正常应在 100 ms 内：解析慢会拖慢所有网站的首屏。可一键修复切换为更快的公共 DNS（若校园网限制外部 DNS 请反馈信息中心）。", details.ToArray());
            }
            return CheckOutcome.Pass($"DNS 解析迅速（平均 {overall:0} ms）", details.ToArray());
        }
    }

    // ---------------- 4. 分段延迟画像（校园拓扑感知） ----------------
    /// <summary>
    /// 学校真实链路：班级/教师电脑 →（部分班级经中转设备，常见为光猫/交换机）→ 楼宇软路由
    /// → 科技楼服务器 → 裸光纤 → 城域网；另有少量电脑直连科技楼服务器。
    /// 楼宇软路由与科技楼服务器是整栋楼/全校共享压力点，因此除「本机→网关」外，
    /// 还用 tracert 找出内网各跳并逐跳统计，把瓶颈定位到三段之一：
    /// 接入段（本机→直连网关）/ 核心段（软路由→科技楼服务器）/ 出口段（服务器→裸光纤→城域网）。
    /// </summary>
    public sealed class PathProfileCheck : CheckBase
    {
        public override string Id => "speed.path_profile";
        public override string Name => "分段延迟画像";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过分段延迟画像。");

            // 第 1 跳：本机 → 直连网关（班级中转设备或楼宇软路由，视教室接法而定）
            var stGw = await NetworkProbeHelper.PingAsync(gw!, 10, ctx.TimeoutMs).ConfigureAwait(false);
            var stDns = await NetworkProbeHelper.PingAsync(NetworkProbeHelper.PublicDnsServers[0], 10, ctx.TimeoutMs).ConfigureAwait(false);
            var stFar = await NetworkProbeHelper.PingAsync("www.baidu.com", 10, ctx.TimeoutMs).ConfigureAwait(false);

            var details = new List<string>
            {
                $"第1跳 接入段（班级中转设备或楼宇软路由）{gw}：平均 {stGw.AvgMs:0.#} ms · 抖动 {stGw.JitterMs:0.#} ms · 丢包 {stGw.LossPct:0.#}%"
            };

            // tracert 摸出内网中间跳（-d 免反解；-h 8 覆盖「中转→软路由→服务器→出口」典型 3-5 跳）
            var innerHops = await TraceInnerHopsAsync(ct, gw).ConfigureAwait(false);
            var innerStats = new List<(string Ip, PingStats Ping, string Role)>();
            foreach (var (ip, role) in innerHops)
            {
                if (ip == gw || innerStats.Any(x => x.Ip == ip)) continue;
                ct.ThrowIfCancellationRequested();
                // 内网跳探测限时收口：单跳 6 发 × ≤1500ms，避免整体诊断被拖长
                var st = await NetworkProbeHelper.PingAsync(ip, 6, Math.Min(ctx.TimeoutMs, 1500)).ConfigureAwait(false);
                innerStats.Add((ip, st, role));
                details.Add($"{role} {ip}：平均 {st.AvgMs:0.#} ms · 抖动 {st.JitterMs:0.#} ms · 丢包 {st.LossPct:0.#}%");
            }
            if (innerStats.Count == 0)
                details.Add("内网中间跳：tracert 未探测到（可能直连核心或中间设备禁 ICMP），按两段模式解读");

            details.Add($"公网段（223.5.5.5）：平均 {stDns.AvgMs:0.#} ms · 抖动 {stDns.JitterMs:0.#} ms · 丢包 {stDns.LossPct:0.#}%");
            if (!stFar.AllLost)
                details.Add($"互联网目标（baidu）：平均 {stFar.AvgMs:0.#} ms · 丢包 {stFar.LossPct:0.#}%");
            else
                details.Add("互联网目标（baidu）：目标禁 ICMP，跳过该段对比");

            // 拓扑快照数据：在判定/提前返回之前写入——无论结论如何都供拓扑图使用
            //（出口段/目标段/内网跳的结构化实测，键约定见 TopoKeys）
            ctx.SetShared(TopoKeys.Exit, new TopoSegmentStats(NetworkProbeHelper.PublicDnsServers[0], stDns, stDns.Received > 0));
            if (!stFar.AllLost)
                ctx.SetShared(TopoKeys.Target, new TopoSegmentStats("www.baidu.com", stFar, stFar.Received > 0));
            ctx.SetShared(TopoKeys.Hops, innerStats.Select(h => new TopoHop(h.Ip, h.Ping, h.Role)).ToList());

            // 判定 1：接入段 —— 班级中转设备/楼宇软路由的接入侧过载或劣化
            if (stGw.LossPct > 20 || stGw.AvgMs > 30 || stGw.JitterMs > 25)
            {
                ctx.SetShared("path.gw.bad", true);
                return CheckOutcome.Fail("接入段异常（本机 → 班级中转/楼宇软路由）",
                    "第一跳就高延迟/丢包，问题在教室到楼宇接入之间：先问同班/同楼层同学是否同时变慢——都慢则是班级中转设备（疑似光猫/交换机）或楼宇软路由过载，属本校报修；仅本机慢则换网线/换端口/换有线排除本机链路。",
                    details.ToArray());
            }

            // 判定 2：核心段 —— 网关之后内网各跳相对第一跳明显劣化（软路由上联/科技楼服务器压力）
            if (innerStats.Count > 0)
            {
                var core = innerStats[^1];
                double deltaMs = core.Ping.AvgMs - stGw.AvgMs;
                double deltaLoss = core.Ping.LossPct - stGw.LossPct;
                if (deltaMs > 15 || deltaLoss > 10)
                {
                    ctx.SetShared("path.core.bad", true);
                    return CheckOutcome.Warn("核心段压力（楼宇软路由 → 科技楼服务器）",
                        "到直连网关很快、到内网核心明显变慢：楼宇软路由上联、级联中转链路或科技楼服务器（全校共享核心）负载偏高。大流量任务（更新/资料分发）请错峰；持续出现请信息中心核查软路由连接数与服务器上联利用率。",
                        details.ToArray());
                }
            }

            // 判定 3：出口段 —— 内网快、出公网慢（裸光纤/城域网高峰拥塞）
            if (stDns.LossPct > 10 || stDns.AvgMs - stGw.AvgMs > 80)
            {
                ctx.SetShared("path.uplink.bad", true);
                return CheckOutcome.Warn("出口段拥塞（科技楼服务器 → 裸光纤 → 城域网）",
                    "内网各段都很快、出公网即慢：典型的裸光纤上联/城域网出口高峰期拥塞。可错峰使用、限制大流量应用；若持续如此，携 PDF 报告请教体局信息中心核查出口带宽与 QoS 策略。",
                    details.ToArray());
            }
            if (!stFar.AllLost && stFar.AvgMs - stDns.AvgMs > 100)
            {
                ctx.SetShared("path.remote.bad", true);
                return CheckOutcome.Warn("出口 → 互联网目标段偏慢",
                    "到公共 DNS 很快、到具体网站慢：多为该网站/CDN 本身或其到你所在地区的线路问题，换时段或换资源源站即可验证。", details.ToArray());
            }
            return CheckOutcome.Pass("各段延迟正常，瓶颈更可能在带宽占用或无线速率", details.ToArray());
        }

        /// <summary>
        /// tracert 到公网 DNS，提取「网关之后、出公网之前」的内网跳。
        /// 跃点号直接取自 tracert 行首（网关通常即第 1 跳，已在角色装配时剔除避免重复展示）；
        /// 角色标注：最后一个内网跳 = 科技楼核心（服务器侧出口），其前的内网跳 = 楼宇软路由/汇聚中转。
        /// 失败返回空列表（回退两段模式，不阻塞诊断）。
        /// </summary>
        private static async Task<List<(string Ip, string Role)>> TraceInnerHopsAsync(CancellationToken ct, string? gw)
        {
            var list = new List<(string, string)>();
            try
            {
                var r = await ProcessRunner.RunAsync("tracert -d -h 8 -w 400 223.5.5.5", 32000).ConfigureAwait(false);
                if (r.ExitCode != 0 || string.IsNullOrEmpty(r.StdOut)) return list;

                var ipRe = new Regex(@"\b(\d{1,3}(?:\.\d{1,3}){3})\b");
                var rows = new List<(int Hop, string Ip)>();
                foreach (var raw in r.StdOut.Split('\n'))
                {
                    ct.ThrowIfCancellationRequested();
                    var num = Regex.Match(raw, @"^\s*(\d{1,2})\s+");
                    if (!num.Success) continue;                                 // 只取「跃点数」行
                    var m = ipRe.Match(raw);
                    if (!m.Success) continue;                                   // 该跳全超时（* * *）
                    var ip = m.Groups[1].Value;
                    if (!NetworkProbeHelper.IsPrivateIp(ip)) break;             // 进入公网段：内网拓扑已完整
                    rows.Add((int.Parse(num.Groups[1].Value), ip));
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    var (hop, ip) = rows[i];
                    if (ip == gw) continue;                                     // 网关已作第1跳单独展示
                    var role = i == rows.Count - 1
                        ? $"第{hop}跳 核心段（科技楼服务器/核心出口侧）"
                        : $"第{hop}跳 核心段（楼宇软路由/汇聚中转）";
                    list.Add((ip, role));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* tracert 不存在/失败：回退两段模式 */ }
            return list;
        }
    }

    // ---------------- 5. 下载吞吐实测 ----------------
    public sealed class DownloadSpeedCheck : CheckBase
    {
        public override string Id => "speed.download";
        public override string Name => "下载吞吐实测";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var pre = await NetworkProbeHelper.HttpAsync("http://www.msftconnecttest.com/connecttest.txt",
                followRedirect: false, timeoutMs: 5000, readBody: false).ConfigureAwait(false);
            if (!pre.Ok)
                return CheckOutcome.Skip("当前无外网连接，跳过下载测速（请先解决连通性问题）。");

            var s = ctx.Settings;
            var result = await SpeedTestHelper.MeasureAsync(
                s.SpeedTestUrl, s.SpeedTestSeconds, s.SpeedTestConnections, (long)s.SpeedTestCapMb << 20,
                ctx.SpeedProgress, ct).ConfigureAwait(true);

            double link = ctx.GetShared<double>("speed.linkmbps");
            if (link <= 0 && ctx.Primary != null) link = ctx.Primary.SpeedBps / 1_000_000.0;
            result.LinkMbps = link;
            ctx.SpeedResult = result;
            ctx.SetShared("speed.result", result);

            if (!result.Success)
                return CheckOutcome.Warn("未能完成下载测速",
                    "测速节点不可达不代表网络故障（校园网白名单常见）：请以分段延迟画像与链路协商结果综合判断。"
                    + (result.Error.Length > 0 ? "原因：" + result.Error : ""));

            var details = new List<string>
            {
                $"代表带宽：{result.SpeedText}（峰值 {result.PeakText}）",
                $"测速节点：{result.EndpointLabel}",
                $"链路协商速率：{result.LinkText} · 实测效率：{result.EfficiencyText}",
                $"体验档位：{result.ExperienceBand}",
                $"总下载 {FormatHelper.Bytes(result.BytesTotal)} / 用时 {result.DurationMs / 1000.0:0.#} s",
                // 校园拓扑压力提示：测速流量会经楼宇软路由与科技楼服务器出口，短时占用共享带宽
                $"测速压力说明：限时 {result.DurationMs / 1000.0:0.#} s 多连接下载会短暂占用出口带宽（经楼宇软路由与科技楼服务器），建议避开上课高峰运行。"
            };
            details.AddRange(result.Endpoints.Select(e =>
                $"· {e.Label}：{(e.Success ? $"{e.MbpsAvg:0.#} Mbps（峰值 {e.MbpsPeak:0.#}，{FormatHelper.Bytes(e.BytesTotal)}）" : e.Error)}"));

            ctx.SetShared("speed.low", result.MbpsAvg < 20);
            if (result.MbpsAvg < 5)
                return CheckOutcome.Fail($"实测带宽 {result.SpeedText}，处于很差档位",
                    "结合下方原因分析逐项处置：常见根因为无线干扰、后台大流量占用或出口拥塞；修复后请复测验证。", details.ToArray());
            if (result.MbpsAvg < 20)
                return CheckOutcome.Warn($"实测带宽 {result.SpeedText}，网课可能卡顿",
                    "低于流畅观看 1080P 网课的舒适区间：查看低速原因分析的高嫌疑项，优先处理无线与后台占用。", details.ToArray());
            if (result.LinkMbps > 100 && result.Efficiency < 0.4)
                return CheckOutcome.Warn($"实测仅达链路速率的 {result.EfficiencyText}",
                    "链路本身很快、实测却远低于应有水平：多为出口拥塞、运营商/校园限速或后台占用，见原因分析。", details.ToArray());
            return CheckOutcome.Pass($"实测带宽 {result.SpeedText}（{result.ExperienceBand.Split('（')[0]}）", details.ToArray());
        }
    }

    // ---------------- 6. 后台带宽占用排查 ----------------
    public sealed class BackgroundUsageCheck : CheckBase
    {
        private static readonly string[] HogKeywords =
        {
            "onedrive", "thunder", "xunlei", "baidunetdisk", "baiduyunguanjia", "qqlive",
            "cloudmusic", "qqmusic", "kugou", "kuwo", "douyu", "bili", "qqdownload",
            "idm", "fdm", "utorrent", "qbittorrent", "xlwfp", "netdisk"
        };

        public override string Id => "speed.background";
        public override string Name => "后台带宽占用排查";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var text = await GetNetstatAsync(ctx).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text)) return CheckOutcome.Warn("无法读取活动连接（netstat 失败）", "跳过后台占用排查，不影响其他检查。");

            var byPid = new Dictionary<int, int>();
            foreach (var raw in text.Split('\n'))
            {
                var m = Regex.Match(raw, @"^\s*TCP\s+\S+\s+(\S+)\s+ESTABLISHED\s+(\d+)");
                if (!m.Success) continue;
                var remote = m.Groups[1].Value;
                if (remote.StartsWith("[", StringComparison.Ordinal)) continue;          // IPv6 跳过
                var ip = remote.Split(':')[0];
                if (NetworkProbeHelper.IsPrivateIp(ip) || NetworkProbeHelper.IsLoopbackOrInvalid(ip)) continue; // 只看公网外联
                int pid = int.TryParse(m.Groups[2].Value, out var t) ? t : 0;
                byPid[pid] = byPid.GetValueOrDefault(pid) + 1;
            }

            var names = new Dictionary<int, string>();
            foreach (var pid in byPid.Keys.OrderByDescending(k => byPid[k]).Take(8))
            {
                try { names[pid] = Process.GetProcessById(pid).ProcessName; }
                catch { names[pid] = $"PID {pid}"; }
            }
            int total = byPid.Values.Sum();
            var details = new List<string> { $"公网活动连接：{total} 个" };
            details.AddRange(byPid.OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => $"· {names.GetValueOrDefault(kv.Key, "PID " + kv.Key)}：{kv.Value} 个连接"));

            var hogs = names.Values
                .Where(n => HogKeywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Where(n => byPid.Any(kv => kv.Value >= 5 && names.GetValueOrDefault(kv.Key) == n))
                .Distinct().ToList();

            if (hogs.Count > 0)
            {
                ctx.SetShared("bg.busy", true);
                return CheckOutcome.Warn($"发现后台下载/同步类程序在占用网络（{string.Join("、", hogs)}）",
                    "网盘同步、下载器、音乐/视频客户端会在后台偷跑流量：上课前建议退出或限制其速度；传递优化 P2P 见专项检查。", details.ToArray());
            }
            if (total > 60)
            {
                ctx.SetShared("bg.busy", true);
                return CheckOutcome.Warn($"公网活动连接偏多（{total} 个）",
                    "大量外联连接通常来自浏览器多标签页、在线协作或自动更新：关闭不用的网页与程序可立竿见影。", details.ToArray());
            }
            return CheckOutcome.Pass("无明显后台带宽占用", details.ToArray());
        }
    }

    // ---------------- 7. TCP 全局参数 ----------------
    public sealed class TcpTuningCheck : CheckBase
    {
        public override string Id => "speed.tcp_tuning";
        public override string Name => "TCP 全局参数（接收窗口自调谐）";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh int tcp show global").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 TCP 全局参数", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";

            string level = "", rss = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var m = Regex.Match(line, @"(?:接收窗口.*调节级别|Receive\s+Window\s+Auto-Tuning\s+Level)\s*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
                if (m.Success && level.Length == 0) level = m.Groups[1].Value;
                m = Regex.Match(line, @"(?:接收端缩放|接收方缩放|Receive-Side\s+Scaling)[^:\r\n]*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
                if (m.Success && rss.Length == 0) rss = m.Groups[1].Value;
            }
            if (level.Length == 0)
                return CheckOutcome.Warn("无法识别接收窗口自调谐级别", "系统输出格式可能不同，请查看详细信息人工确认。", text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(14).ToArray());

            var details = new List<string> { $"接收窗口自动调节级别：{level}", $"接收端缩放（RSS）：{(string.IsNullOrEmpty(rss) ? "未知" : rss)}" };
            bool rssBad = rss.Equals("disabled", StringComparison.OrdinalIgnoreCase) || rss == "禁用";
            bool abnormal = !level.Equals("normal", StringComparison.OrdinalIgnoreCase) && level != "正常";

            if (level.Equals("disabled", StringComparison.OrdinalIgnoreCase) || level == "禁用")
            {
                ctx.SetShared("tcp.autotuning.abnormal", true);
                return CheckOutcome.Fail("TCP 接收窗口自动调节已被关闭",
                    "曾被“网络优化软件/全家桶”关闭的接收窗口自调谐会让高带宽链路跑不满：一键修复可恢复为 normal（无需重启）。", canFix: true, details.ToArray());
            }
            if (abnormal)
            {
                ctx.SetShared("tcp.autotuning.abnormal", true);
                return CheckOutcome.Warn($"TCP 接收窗口级别为 {level}",
                    "非 normal 级别通常是被优化软件改动过，会限制吞吐：一键修复可恢复默认值。", details.ToArray());
            }
            if (rssBad)
                return CheckOutcome.Warn("接收端缩放（RSS）已关闭",
                    "多核并行收包被关闭会限制高带宽下的吞吐：一键修复可一并恢复。", details.ToArray());
            return CheckOutcome.Pass("TCP 全局参数正常", details.ToArray());
        }
    }

    // ---------------- 8. 传递优化 P2P ----------------
    public sealed class DelivOptCheck : CheckBase
    {
        public override string Id => "speed.delivopt";
        public override string Name => "Windows 传递优化 P2P";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            int mode = -1;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization");
                mode = Convert.ToInt32(key?.GetValue("DODownloadMode", -1) ?? -1);
            }
            catch { }

            var text = await GetNetstatAsync(ctx).ConfigureAwait(false);
            int listen = 0, estab = 0;
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var raw in text.Split('\n'))
                {
                    if (!raw.Contains(":7680", StringComparison.Ordinal)) continue;
                    if (raw.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) listen++;
                    if (raw.Contains("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) estab++;
                }
            }

            string modeText = mode switch
            {
                0 => "仅 HTTP（无 P2P）", 1 => "局域网分发", 2 => "组内分发", 3 => "互联网分发",
                100 => "旁路模式（已关闭 P2P）", _ => "未配置（默认：局域网分发）"
            };
            var details = new List<string> { $"组策略分发模式：{modeText}", $"P2P 端口 7680：监听 {listen} · 活动连接 {estab}" };

            if (listen > 0 || estab > 0)
            {
                ctx.SetShared("delivopt.p2p", true);
                return CheckOutcome.Warn("传递优化 P2P 正在活动",
                    "Windows 会向局域网/互联网其他设备上传更新分片，可能占满上行导致全体变慢：一键修复可关闭（写入单个注册表值，可随时还原）。", canFix: true, details.ToArray());
            }
            if (mode == 2 || mode == 3)
                return CheckOutcome.Warn($"传递优化为 {modeText} 模式（当前无活动连接）",
                    "虽暂未活动，但高峰期可能突发占用上行：如需彻底规避可一键修复关闭。", details.ToArray());
            return CheckOutcome.Pass("传递优化未在占用带宽", details.ToArray());
        }
    }

    // ---------------- 共享：netstat 结果缓存（同一次会话复用） ----------------
    internal static async Task<string> GetNetstatAsync(CheckContext ctx)
    {
        var cached = ctx.GetShared<string>("netstat.tcp");
        if (cached != null) return cached;
        var r = await ProcessRunner.RunAsync("netstat -ano -p tcp", 25000).ConfigureAwait(false);
        var text = r.ExitCode == 0 ? r.StdOut : "";
        ctx.SetShared("netstat.tcp", text);
        return text;
    }
}

// ================= 检查实现：疑难排查层（宿主 Services/Network/Checks.Advanced.cs 同源移植） =================

/// <summary>
/// 疑难排查层（罕见/边缘问题，8 项，只读诊断、不改任何配置）：
/// IPv6 连通 / DNS 劫持与污染 / 网关级丢包 / 默认路由冲突 / 代理与直连差异 /
/// TCP 53 出境管控 / 校园认证客户端检测 / 本机协议栈自检。
/// 仅「全面诊断」模式运行（不进入快速体检与测速计划）。
/// </summary>
public static partial class NetworkChecks
{
    // ---------------- IPv6 连通 ----------------
    public sealed class Ipv6Check : CheckBase
    {
        public override string Id => "rare.ipv6";
        public override string Name => "IPv6 连通性";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.GetIPProperties().GatewayAddresses.Count > 0);
            if (up == null) return CheckOutcome.Skip("无活动网卡，跳过 IPv6 检查。");

            var globals = up.GetIPProperties().UnicastAddresses.Where(a =>
                a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !a.Address.IsIPv6LinkLocal && !a.Address.IsIPv6SiteLocal &&
                !a.Address.IsIPv6Teredo && !IPAddress.IsLoopback(a.Address)).ToList();
            if (globals.Count == 0)
                return CheckOutcome.Pass("未启用全局 IPv6 地址（校园网常见配置，不影响使用）", $"网卡：{up.Name}");

            var r = await NetworkProbeHelper.TcpAsync("2400:3200::1", 443, 2500).ConfigureAwait(false);
            var details = new List<string> { $"全局 IPv6：{globals[0].Address}（前缀来源 {globals[0].PrefixOrigin}）", $"IPv6 出口探测 2400:3200::1:443 → {(r.Ok ? $"可达（{r.ElapsedMs} ms）" : r.Error)}" };
            if (r.Ok)
                return CheckOutcome.Pass("IPv6 已启用且可出公网", details.ToArray());
            return CheckOutcome.Warn("IPv6 已启用但无法出公网",
                "常见于路由器只下发地址未放行出口、或校园网过滤 IPv6：多数网站会自动回退 IPv4，如个别站点打不开可尝试关闭该网卡的 IPv6。", details.ToArray());
        }
    }

    // ---------------- DNS 劫持 / 污染探测 ----------------
    public sealed class DnsHijackCheck : CheckBase
    {
        public override string Id => "rare.dnshijack";
        public override string Name => "DNS 劫持与污染探测";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            // 1) NXDOMAIN 探测：保证不存在的域名若“解析成功”即存在劫持
            var probe = $"nx-probe-{Guid.NewGuid():N}.invalid";
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(probe).ConfigureAwait(false);
                var shown = string.Join(", ", addrs.Select(a => a.ToString()).Take(3));
                details.Add($"探测域名 {probe}（保证不存在）→ 被解析为 {shown}");
                return CheckOutcome.Fail("检测到 DNS 劫持（不存在域名被强制解析）",
                    "运营商广告注入、流氓软件或门户静默劫持均会如此：多数网站打不开/弹广告可与此相关，先查 Hosts（本页已有专项），再考虑更换 DNS。",
                    details.ToArray());
            }
            catch { details.Add("探测域名未解析（NXDOMAIN 行为正确）"); }

            // 2) 系统解析 vs 权威公共 DNS 直查对照
            var direct = await DnsQueryHelper.ResolveAsync("223.5.5.5", "www.qq.com", 2000).ConfigureAwait(false);
            try
            {
                var sys = await Dns.GetHostAddressesAsync("www.qq.com").ConfigureAwait(false);
                var sysText = string.Join(", ", sys.Select(a => a.ToString()).Take(2));
                details.Add($"系统解析 www.qq.com → {sysText}");
                details.Add($"直查 223.5.5.5 → {(direct.Ok ? direct.AddressText : direct.Error)}");
                if (!direct.Ok)
                    details.Add("公共 DNS 直查不可达（外网受限时属正常，不作为劫持判据）");
            }
            catch (Exception ex)
            {
                details.Add($"系统解析失败：{ex.Message}（直查公共 DNS {(direct.Ok ? "可达 → 判定本机解析链路异常" : "也不可达 → 外网不通，非劫持")}）");
                if (direct.Ok)
                    return CheckOutcome.Warn("本机解析失败但公共 DNS 可达",
                        "解析链路异常（DNS 缓存损坏或 DNS 服务配置失效）：可尝试一键修复中的“刷新 DNS 缓存”。", details.ToArray());
            }
            return CheckOutcome.Pass("未发现 DNS 劫持迹象", details.ToArray());
        }
    }

    // ---------------- 网关级丢包 ----------------
    public sealed class GatewayLossCheck : CheckBase
    {
        public override string Id => "rare.gatewayloss";
        public override string Name => "网关链路丢包（×20）";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过网关丢包检查。");

            var stats = await NetworkProbeHelper.PingAsync(gw, 20, 1000).ConfigureAwait(false);
            var details = new List<string>
            {
                $"网关 {gw} × 20 次",
                $"收到 {stats.Received}/{stats.Sent} · 丢包 {stats.LossPct:0.#}% · 平均 {stats.AvgMs:0.#} ms · 抖动 {stats.JitterMs:0.#} ms"
            };
            if (stats.AllLost)
                return CheckOutcome.Fail($"网关完全无响应（{stats.Sent} 次全部超时）",
                    "第一跳即不通：网线/交换机口/AP 关联异常，请先看「物理连接状态」与「默认网关连通」两项结论。", details.ToArray());
            if (stats.LossPct > 20)
                return CheckOutcome.Fail($"网关丢包 {stats.LossPct:0.#}%",
                    "第一跳就丢包属于内网链路故障（网线老化/水晶头氧化/AP 过载/Wi-Fi 漫游）：换网口或换网线可快速定位。", details.ToArray());
            if (stats.LossPct > 0)
                return CheckOutcome.Warn($"网关偶发丢包 {stats.LossPct:0.#}%",
                    "轻微内网丢包：直播/网课会偶发卡顿，多为 Wi-Fi 干扰或 AP 负载，非高峰期复测确认。", details.ToArray());
            return CheckOutcome.Pass("网关链路无丢包", details.ToArray());
        }
    }

    // ---------------- 默认路由冲突 ----------------
    public sealed class RouteConflictCheck : CheckBase
    {
        public override string Id => "rare.route";
        public override string Name => "默认路由冲突";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("route print -4").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取路由表", r.Error ?? r.StdErr);

            var gateways = new List<string>();
            foreach (var line in (r.StdOut ?? "").Split('\n'))
            {
                // 只统计「活动路由」表：持久路由段属于历史残留配置，不代表系统当前生效路由；
                // 若仅持久路由残留（活动表无默认路由），仍应判为「未找到默认路由」而非误报唯一。
                if (line.Contains("永久路由", StringComparison.Ordinal)
                    || line.Contains("Persistent Routes", StringComparison.OrdinalIgnoreCase))
                    break;
                var m = Regex.Match(line, @"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+(\S+)");
                if (m.Success) gateways.Add(m.Groups[1].Value);
            }
            var distinct = gateways.Distinct().ToList();
            var details = gateways.Select((g, i) => $"默认路由 {i + 1}: {g}").ToList();
            if (distinct.Count == 0)
                return CheckOutcome.Fail("未找到 IPv4 默认路由", "系统没有默认网关路由：所有外网流量无处可去，请查看「IP 配置 / DHCP」两项并重新获取地址。", details.ToArray());
            if (distinct.Count > 1)
                return CheckOutcome.Warn($"存在 {distinct.Count} 条不同网关的默认路由",
                    "多网卡/VPN 残留路由会造成“时通时断、部分网站不通”的路由漂移：断开不用的网络或联系管理员清理残留路由。", details.ToArray());
            return CheckOutcome.Pass($"默认路由唯一（{distinct[0]}）", details.ToArray());
        }
    }

    // ---------------- 代理与直连差异 ----------------
    public sealed class ProxyDiffCheck : CheckBase
    {
        public override string Id => "rare.proxydiff";
        public override string Name => "代理与直连差异探测";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            using var sys = new HttpClient() { Timeout = TimeSpan.FromSeconds(8) };                       // 遵循系统代理/PAC
            using var direct = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(8)                                                          // 强制直连
            };
            bool sysOk, directOk;
            try { using var _ = await sys.GetAsync("http://www.msftconnecttest.com/connecttest.txt", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); sysOk = true; }
            catch { sysOk = false; }
            try { using var _ = await direct.GetAsync("http://www.msftconnecttest.com/connecttest.txt", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); directOk = true; }
            catch { directOk = false; }

            var details = new List<string> { $"走系统代理链路: {(sysOk ? "通" : "不通")}", $"强制直连: {(directOk ? "通" : "不通")}" };
            if (sysOk && directOk) return CheckOutcome.Pass("系统代理链路与直连一致（当前无代理干扰）", details.ToArray());
            if (!sysOk && directOk)
                return CheckOutcome.Fail("走系统代理不通、直连正常（典型代理故障）",
                    "系统代理/PAC 配置已失效：本页「系统代理检查」若同样告警可一键修复关闭代理；若它显示未启用，请检查是否有 PAC 残留或安全软件接管。", details.ToArray());
            if (sysOk && !directOk)
                return CheckOutcome.Warn("直连被拦但代理可达",
                    "校园网通常直连放行、代理受限：可能被出口策略管控，若日常使用正常可忽略。", details.ToArray());
            return CheckOutcome.Skip("两条链路均不通（外网不可达），跳过代理差异判断，请先看外网连通项结论。");
        }
    }

    // ---------------- TCP 53 出境管控 ----------------
    public sealed class Port53Check : CheckBase
    {
        public override string Id => "rare.port53";
        public override string Name => "TCP 53 出境管控";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var a = await NetworkProbeHelper.TcpAsync("223.5.5.5", 53, 2500).ConfigureAwait(false);
            var b = await NetworkProbeHelper.TcpAsync("119.29.29.29", 53, 2500).ConfigureAwait(false);
            var details = new List<string>
            {
                $"TCP 223.5.5.5:53 → {(a.Ok ? $"可达（{a.ElapsedMs} ms）" : a.Error)}",
                $"TCP 119.29.29.29:53 → {(b.Ok ? $"可达（{b.ElapsedMs} ms）" : b.Error)}"
            };
            if (a.Ok || b.Ok)
                return CheckOutcome.Pass("TCP 53 出境正常（公共 DNS 可用）", details.ToArray());
            return CheckOutcome.Warn("公共 DNS 的 TCP 53 出境被拦截",
                "校园网常见管控：若 UDP 53 同样被管控，则“修改 DNS”类修复（含一键修复的改 DNS）不会生效，属校方策略，需按认证门户方式上网或联系信息中心。", details.ToArray());
        }
    }

    // ---------------- 校园认证客户端检测 ----------------
    public sealed class AuthClientCheck : CheckBase
    {
        private static readonly string[] Signs =
            { "drcom", "drmain", "ruijie", "srun", "surfilter", "netkeeper", "h3c", "imcsvc", "supplicant" };

        public override string Id => "rare.authclient";
        public override string Name => "校园认证客户端检测";
        public override CheckCategory Category => CheckCategory.Rare;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            if (ctx.SharedBool("portal.detected") != true)
                return Task.FromResult(CheckOutcome.Skip("网络直通、未检测到认证门户，无需认证客户端。"));

            var found = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName.ToLowerInvariant();
                    if (Signs.Any(s => name.Contains(s, StringComparison.Ordinal))) found.Add(p.ProcessName);
                }
                catch { }
                finally { p.Dispose(); } // 全进程枚举不释放会累积进程句柄，直至 GC 兜底
            }
            if (found.Count > 0)
                return Task.FromResult(CheckOutcome.Pass($"检测到认证客户端进程：{string.Join(", ", found.Take(3))}",
                    "门户认证应优先由官方客户端完成；若客户端在线仍被门户拦截，多为客户端版本过旧或掉线。"));
            return Task.FromResult(CheckOutcome.Warn("检测到认证门户但未发现认证客户端",
                "部分学校要求专用客户端（Dr.COM/锐捷/Srun 等）完成认证：请确认是否需要安装；若网页登录即可，可忽略本提示。",
                "已扫描进程关键字：" + string.Join("/", Signs)));
        }
    }

    // ---------------- 本机协议栈自检 ----------------
    public sealed class StackSelfTestCheck : CheckBase
    {
        public override string Id => "rare.stack_selftest";
        public override string Name => "本机协议栈自检（环回）";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var acceptTask = listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    using var accepted = await acceptTask;
                    sw.Stop();
                    return CheckOutcome.Pass($"本机 TCP 协议栈自检通过（环回连接 {sw.ElapsedMilliseconds} ms）",
                        $"127.0.0.1:{port} 建连成功。若本项失败而其余网络项异常，优先怀疑 Winsock/LSP 损坏。");
                }
                finally { listener.Stop(); }
            }
            catch (Exception ex)
            {
                return CheckOutcome.Fail("本机协议栈自检失败",
                    "环回 TCP 都无法建立是 Winsock/LSP 损坏的典型症状（第三方网络软件/病毒破坏）：可执行一键修复中的「重置 Winsock」并重启。", $"异常：{ex.Message}");
            }
        }
    }

    // ---------------- HTTPS 证书链校验（TLS 拦截/中间盒探测） ----------------
    // 罕见层（仅全面诊断）。internet.https 已覆盖「握手失败→FAIL」的连通面，本项进一步区分
    // 证书异常的归属：自签/不受信任根/域名不匹配多为上网行为管理（TLS 解密网关）或透明劫持，
    // 证书时间无效多为系统时钟错误。校园网普遍部署行为管理设备，证书被替换属可解释现象，
    // 故判级上限为 WARN 不判 FAIL（避免把正常的安全设备误报成故障）。
    public sealed class TlsCertCheck : CheckBase
    {
        public override string Id => "rare.tls_cert";
        public override string Name => "HTTPS 证书链校验";
        public override CheckCategory Category => CheckCategory.Rare;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            // 证据收集在自定义校验回调里完成并放行握手（恒返回 true）：返回 false 会直接抛
            // AuthenticationException 丢掉链错误明细，放行后可拿到完整证书与链状态再判级。
            var policyErrors = SslPolicyErrors.None;
            var chainNotes = new List<string>();
            try
            {
                using var client = new TcpClient();
                client.ConnectAsync("www.baidu.com", 443).WaitAsync(TimeSpan.FromSeconds(8), ct)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                    (sender, cert, chain, errors) =>
                    {
                        policyErrors = errors;
                        if (chain?.ChainStatus != null)
                            foreach (var st in chain.ChainStatus)
                                if (st.Status != X509ChainStatusFlags.NoError && !string.IsNullOrEmpty(st.StatusInformation))
                                    chainNotes.Add(st.StatusInformation.Trim());
                        return true;
                    });
                ssl.AuthenticateAsClientAsync("www.baidu.com").WaitAsync(TimeSpan.FromSeconds(8), ct)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                var details = new List<string>
                {
                    ssl.RemoteCertificate is X509Certificate c
                        ? $"证书主体：{c.Subject}；签发者：{c.Issuer}"
                        : "未获取到对端证书"
                };
                if (policyErrors == SslPolicyErrors.None && chainNotes.Count == 0)
                    return Task.FromResult(CheckOutcome.Pass("HTTPS 证书链校验通过", details.ToArray()));

                if (policyErrors != SslPolicyErrors.None) details.Add($"校验标记：{policyErrors}");
                if (chainNotes.Count > 0) details.Add("链状态：" + string.Join("；", chainNotes.Take(3)));
                if (policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                    details.Add("证书域名与访问目标不匹配：TLS 解密网关或劫持设备的典型特征。");

                bool timeInvalid = chainNotes.Any(n => n.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0)
                                   || ssl.RemoteCertificate is X509Certificate2 c2 &&
                                      (c2.NotAfter < DateTime.Now || c2.NotBefore > DateTime.Now);
                var (head, tail) = timeInvalid
                    ? ("HTTPS 证书时间无效（已过期或未生效）",
                       "多与系统时钟偏差有关：结合「系统时钟校准」检查项校准时间后重测。")
                    : ("HTTPS 证书不受信任（疑似 TLS 解密/自签）",
                       "本校若部署上网行为管理（TLS 解密网关），此现象属正常，安装其根证书后即消失；未部署则可能存在中间人劫持，请反馈信息中心核查出口设备。");
                return Task.FromResult(CheckOutcome.Warn(head, tail, details.ToArray()));
            }
            catch (Exception ex)
            {
                // 连不上/握手未完成：连通面结论由 internet.https 负责，本项只做「能握手时」的证书深度校验
                var msg = (ex.Message ?? "").Trim();
                if (msg.Length > 90) msg = msg[..90] + "…";
                return Task.FromResult(CheckOutcome.Skip(
                    $"443 连接或 TLS 握手未完成，证书链校验无法进行（连通结论见「HTTPS / TLS 连通」项）：{msg}"));
            }
        }
    }
}
// ================= 一键修复服务（宿主 Services/Network/NetworkRepairService.cs 同源移植） =================

public interface INetworkRepairService
{
    /// <summary>根据诊断结果生成可执行的修复清单。</summary>
    List<RepairAction> BuildPlan(IReadOnlyList<CheckItem> results, CheckContext? ctx);
    /// <summary>执行选中的修复动作：普通动作本进程完成；管理类动作合并为一次 UAC 批处理。</summary>
    Task RunAsync(IReadOnlyList<RepairAction> actions, IProgress<RepairAction>? progress, CancellationToken ct);
}

/// <summary>
/// 一键修复服务（傻瓜模式）。
/// 安全边界：不改系统服务启动类型、不改虚拟内存、不导入注册表文件、不精简系统、不动 Defender；
/// 对服务仅允许「启动未运行的网络基础服务」（应 2026-10 用户需求由只读放开，仅启动、不改启动类型）；
/// 涉及网络配置的操作仅限：DNS 缓存 / ARP / DHCP 续租 / 网卡启停 / Winsock 重置 / 时间同步 / 代理复位，
/// 以及低速专项的单点可逆命令：TCP 自调谐（netsh）/ 传递优化单值（reg add 单值，可删除还原）/
/// 网卡节能托管（PowerShell 单项）/ 网卡重协商（netsh）。全部动作逐条说明、逐项可回滚。
/// </summary>
public sealed class NetworkRepairService : INetworkRepairService
{
    private readonly NetworkDiagSettingsStore _settings;
    private readonly Action<string, string> _log; // (level, message) → 插件日志适配（宿主日志服务同职责）
    private readonly string _logRoot;             // 会话日志根（宿主数据根 + \logs，与内置页共用）

    public NetworkRepairService(NetworkDiagSettingsStore settings, Action<string, string> log, string logRoot)
    {
        _settings = settings;
        _log = log;
        _logRoot = logRoot;
    }

    public List<RepairAction> BuildPlan(IReadOnlyList<CheckItem> results, CheckContext? ctx)
    {
        var plan = new List<RepairAction>();
        var failed = results.Where(r => r.Status is CheckStatus.Fail or CheckStatus.Warning).ToList();
        bool Has(string id) => failed.Any(f => f.Id == id);

        var adapter = ctx?.Primary ?? NetworkProbeHelper.GetPrimaryAdapter();
        var adapterName = adapter?.Name ?? "";
        var portalUrl = ctx?.GetShared<string>("portal.url");
        if (string.IsNullOrEmpty(portalUrl) && Uri.TryCreate(_settings.Current.PortalBaseUrl, UriKind.Absolute, out var pu))
            portalUrl = _settings.Current.PortalBaseUrl;

        if (Has("dns.resolve") || Has("dns.servers") || Has("dns.config"))
            plan.Add(new RepairAction("flushdns", "刷新 DNS 缓存",
                "清除本机 DNS 缓存，解决解析陈旧/污染造成的打不开网页。", false, false,
                new[] { "dns.resolve", "dns.servers", "dns.config" }));

        if (Has("gateway.arp"))
            plan.Add(new RepairAction("arp_clear", "清除 ARP 缓存",
                "删除错误的网关 ARP 表项并重新学习，解决 ARP 欺骗/冲突导致的断网。", true, false,
                new[] { "gateway.arp" }));

        if (Has("ip.config") || Has("ip.dhcp_lease") || Has("gateway.route"))
            plan.Add(new RepairAction("renew_dhcp", $"重新获取 IP 地址（{(adapterName.Length > 0 ? adapterName : "所有网卡")}）",
                "释放当前 IP 并向 DHCP 服务器重新申请，解决租约过期与 APIPA（169.254）问题。", true, false,
                new[] { "ip.config", "ip.dhcp_lease", "gateway.route" }));

        if (Has("physical.link"))
            plan.Add(new RepairAction("enable_adapter", $"启用网络适配器（{adapterName}）",
                "尝试重新启用被禁用或未连接的适配器。", true, false,
                new[] { "physical.link" }));

        if (Has("clock.skew"))
            plan.Add(new RepairAction("resync_clock", "同步系统时间",
                "向时间服务器强制同步系统时钟，修复证书错误与认证失败；同步失败常见于时间服务未运行，脚本会自动启动时间服务后重试一次。", true, false,
                new[] { "clock.skew" }));

        if (Has("proxy.system"))
            plan.Add(new RepairAction("proxy_user", "关闭用户级系统代理",
                "关闭 Internet 选项中的错误代理设置并立即刷新生效（单点设置，不导入任何注册表文件）。", false, false,
                new[] { "proxy.system" }));

        if (Has("proxy.winhttp") || Has("proxy.system"))
            plan.Add(new RepairAction("proxy_winhttp", "重置 WinHTTP 代理",
                "netsh winhttp reset proxy：清除系统服务级代理，恢复直连。", true, false,
                new[] { "proxy.winhttp" }));

        if (Has("stack.winsock") || (Has("dns.resolve") && Has("internet.http")))
            plan.Add(new RepairAction("winsock_reset", "重置 Winsock 目录（需重启生效）",
                "清除第三方 LSP 与损坏的 Winsock 目录，解决“能 ping 通但上不了网”。执行后需重启计算机。", true, true,
                new[] { "stack.winsock" }));

        if (Has("stack.services"))
            plan.Add(new RepairAction("start_net_services", "启动未运行的网络基础服务",
                "启动诊断中未运行的网络基础服务（DHCP/DNS 缓存/网络位置感知等）：仅启动，不改动任何服务的启动类型，"
                + "被禁用的服务会跳过并提示。需要管理员授权。", true, false,
                new[] { "stack.services" }));

        if (Has("dns.servers") && adapter != null && adapter.DnsServers.Count > 0)
            plan.Add(new RepairAction("dns_set_public", $"将 DNS 修改为公共 DNS（{_settings.Current.DnsPreferred}）",
                $"把“{adapterName}”的 DNS 设为 {_settings.Current.DnsPreferred} / {_settings.Current.DnsAlternate}。仅当校园网允许使用外部 DNS 时勾选。", true, true,
                new[] { "dns.servers" }));

        if (Has("portal.detect") || Has("portal.expiry"))
        {
#if !STABLE_BUILD
            var st = _settings.Current;
            var autoHint = st.PortalAutoLogin && st.PortalUsername.Length > 0 && st.PortalPassword.Length > 0
                ? "已开启自动登录：一键修复将直接提交认证表单，并用连通性复核验证；失败时自动回退浏览器人工登录。"
                : "调起默认浏览器打开检测到的认证门户；在 设置 → 网络诊断 配置账号并开启自动登录后，可免每次手动登录。";
#else
            // 正式版（STABLE_BUILD）：实验性自动登录整体剔除，仅保留浏览器人工登录引导
            const string autoHint = "调起默认浏览器打开检测到的认证门户。";
#endif
            var openPortal = new RepairAction("open_portal", "打开城域网认证页面",
                autoHint,
                false, false, new[] { "portal.detect", "portal.expiry" })
            { PortalUrl = portalUrl }; // 优先携带诊断时实际检测到的门户地址
            plan.Add(openPortal);
        }

        // ===== 提速类修复（低速专项；均为单点命令、可逆，遵循安全边界：不导入注册表文件、不改系统服务） =====
        bool SharedTrue(string k) => ctx?.SharedBool(k) == true;

        if (Has("speed.tcp_tuning") && SharedTrue("tcp.autotuning.abnormal"))
            plan.Add(new RepairAction("tcp_autotuning", "恢复 TCP 接收窗口自动调节",
                "netsh 单点设置：autotuninglevel=normal 并启用 RSS。被“网络优化软件”改小/关闭的接收窗口是低速常见原因，恢复后无需重启。", true, false,
                new[] { "speed.tcp_tuning" }));

        if (Has("speed.delivopt") && SharedTrue("delivopt.p2p"))
            plan.Add(new RepairAction("disable_delivopt", "关闭 Windows 传递优化 P2P 分发",
                "写入单个注册表值 DODownloadMode=100（旁路模式，非导入 .reg 文件），阻止系统向局域网/互联网上传更新分片占满上行。还原：删除该值即恢复默认。", true, false,
                new[] { "speed.delivopt" }));

        // 节能托管：只看本次诊断的共享键（写入点 NicRateCheck 全双工探测 + 两次探查仅记线索不判级，
        // 两项都可能 Pass）——若仍要求 Has("speed.nic_rate") 在 Failed 列表，节能开启而速率正常时
        // 修复动作永不入计划，与 hardware.nic_probe 线索「可用一键修复关闭网卡节能」的承诺脱节。
        if (SharedTrue("nic.powersave"))
            plan.Add(new RepairAction("nic_powersave_off", "关闭网卡节能托管",
                "禁止系统为省电降低网卡性能（AllowComputerToTurnOffDevice=Disabled），避免课中网卡休眠导致的瞬时卡顿。", true, false,
                new[] { "speed.nic_rate", "hardware.nic_probe" }));

        if (Has("speed.nic_rate") && SharedTrue("nic.rate.low"))
            plan.Add(new RepairAction("adapter_reset", $"网卡重协商（{(adapterName.Length > 0 ? adapterName : "主网卡")}）",
                "禁用并重新启用网卡，重新协商速率与双工；会短暂断网数秒，默认不勾选。", true, true,
                new[] { "speed.nic_rate" }));

        // 危险项默认不勾选（winsock 重置需重启 / 改 DNS / 网卡重协商会短暂断网）——
        // 与修复区提示「危险项默认不勾选」保持一致；此前所有动作默认勾选，属文案与行为矛盾。
        foreach (var a in plan) a.IsSelected = !a.Risky;

        return plan;
    }

    public async Task RunAsync(IReadOnlyList<RepairAction> actions, IProgress<RepairAction>? progress, CancellationToken ct)
    {
        var session = DiagSessionLog.Begin(_logRoot);
        session.Write(LogLevel.Info, "repair", $"开始一键修复，共 {actions.Count} 项", actions.Select(a => a.Id).ToArray());

        var normal = actions.Where(a => !a.NeedsAdmin).ToList();
        var admin = actions.Where(a => a.NeedsAdmin).ToList();

        foreach (var action in normal)
        {
            ct.ThrowIfCancellationRequested();
            action.Status = RepairStatus.Running;
            progress?.Report(action);
            try
            {
                var (ok, output) = await RunNormalAsync(action).ConfigureAwait(true);
                action.Status = ok ? RepairStatus.Ok : RepairStatus.Fail;
                action.Output = output;
                session.Write(ok ? LogLevel.Info : LogLevel.Error, "repair", $"[{(ok ? "OK" : "FAIL")}] {action.Title}", output);
            }
            catch (Exception ex)
            {
                action.Status = RepairStatus.Fail;
                action.Output = ex.Message;
            }
            progress?.Report(action);
        }

        if (admin.Count > 0)
        {
            // 合并为一次 UAC
            foreach (var a in admin) { a.Status = RepairStatus.Running; progress?.Report(a); }
            try
            {
                var (script, outLog) = BuildAdminScript(admin);
                session.Write(LogLevel.Info, "repair", $"提权脚本：{script}");
                var (exit, output) = await ElevationHelper.RunElevatedScriptAsync(script, outLog).ConfigureAwait(true);
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var a in admin)
                {
                    var okLine = lines.FirstOrDefault(l => l.Contains($"##OK##{a.Id}"));
                    var failLine = lines.FirstOrDefault(l => l.Contains($"##FAIL##{a.Id}"));
                    a.Status = okLine != null ? RepairStatus.Ok : failLine != null ? RepairStatus.Fail : RepairStatus.Fail;
                    a.Output = okLine != null ? "执行成功" : $"未在提权脚本输出中找到成功标记（用户可能取消了 UAC）\n{failLine}".Trim();
                    session.Write(a.Status == RepairStatus.Ok ? LogLevel.Info : LogLevel.Error, "repair",
                        $"[{a.Status}] {a.Title}", a.Output);
                    progress?.Report(a);
                }
            }
            catch (Exception ex)
            {
                foreach (var a in admin)
                {
                    a.Status = RepairStatus.Fail;
                    a.Output = $"提权执行失败：{ex.Message}";
                    progress?.Report(a);
                }
            }
        }

        session.Write(LogLevel.Info, "repair", "修复流程结束");
    }

    private async Task<(bool Ok, string Output)> RunNormalAsync(RepairAction action)
    {
        switch (action.Id)
        {
            case "flushdns":
            {
                var r = await ProcessRunner.RunAsync("ipconfig /flushdns").ConfigureAwait(true);
                return (r.ExitCode == 0, r.FullText);
            }
            case "proxy_user":
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                    key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    NativeWinINet.NotifyProxySettingsChanged();
                    return (true, "已关闭用户级系统代理并刷新 WinINet 设置");
                }
                catch (Exception ex) { return (false, ex.Message); }
            }
            case "open_portal":
            {
                // 优先使用诊断时实际检测到的门户地址（可能为 IP 门户），其次用户配置值
                var url = action.PortalUrl ?? _settings.Current.PortalBaseUrl;
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var portal))
                    return (false, "未检测到认证门户且未配置：请手动在浏览器打开认证页，或在 设置 → 网络诊断 中填写门户地址。");

#if !STABLE_BUILD
                // 实验性自动登录：配置了账号密码时先尝试直接提交认证表单，失败回退浏览器
                var s = _settings.Current;
                if (s.PortalAutoLogin && s.PortalUsername.Length > 0 && s.PortalPassword.Length > 0)
                {
                    var r = await PortalAutoLoginHelper.TryLoginAsync(
                        portal.ToString(), s.PortalUsername, s.PortalPassword, CancellationToken.None).ConfigureAwait(true);
                    if (r.VerifiedOnline) return (true, r.Message);
                    Process.Start(new ProcessStartInfo(portal.ToString()) { UseShellExecute = true });
                    return (true, $"{r.Message}\n已在默认浏览器打开 {portal} 供人工确认。");
                }
                Process.Start(new ProcessStartInfo(portal.ToString()) { UseShellExecute = true });
                return (true, $"已在默认浏览器打开 {portal}（提示：在 设置 → 网络诊断 配置账号并开启自动登录，可免每次手动登录）");
#else
                // 正式版（STABLE_BUILD）：仅保留浏览器人工登录路径
                Process.Start(new ProcessStartInfo(portal.ToString()) { UseShellExecute = true });
                return (true, $"已在默认浏览器打开 {portal}");
#endif
            }
            default:
                return (false, $"动作 {action.Id} 不属于普通权限类别");
        }
    }

    private (string ScriptPath, string OutLog) BuildAdminScript(IReadOnlyList<RepairAction> actions)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit", "temp");
        Directory.CreateDirectory(dir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var script = Path.Combine(dir, $"repair-{ts}.cmd");
        var outLog = Path.Combine(dir, $"repair-{ts}.log");

        var adapter = NetworkProbeHelper.GetPrimaryAdapter();
        var adapterName = adapter?.Name ?? "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo [QingShu] 青枢工具箱正在执行修复，请保持窗口开启…");

        foreach (var a in actions)
        {
            switch (a.Id)
            {
                case "arp_clear":
                    sb.AppendLine($"arp -d * >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "renew_dhcp":
                    if (adapterName.Length > 0)
                        sb.AppendLine($"ipconfig /release \"{adapterName}\" >nul 2>&1");
                    sb.AppendLine($"ipconfig /renew >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "enable_adapter":
                    sb.AppendLine($"netsh interface set interface \"{adapterName}\" enable >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "resync_clock":
                    // 失败回退：先启动 Windows 时间服务再重试一次。旧写法 net time \\\\localhost /set /y
                    // 是「向本机自身时钟对时」的空操作，无论是否真同步成功都会返回成功（假成功掩盖真实故障）。
                    sb.AppendLine($"w32tm /resync /force >nul 2>&1 && echo ##OK##{a.Id} || (net start w32time >nul 2>&1 & w32tm /resync /force >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id})");
                    break;
                case "proxy_winhttp":
                    sb.AppendLine($"netsh winhttp reset proxy >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "winsock_reset":
                    sb.AppendLine($"netsh winsock reset >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "start_net_services":
                    // 仅启动未运行的网络基础服务；被禁用（StartType=Disabled）的跳过计为失败，不改启动类型。
                    // PowerShell 以 $bad 作退出码：全启动成功返回 0 → cmd && 打 OK 标记；任一失败/被禁用 → FAIL
                    sb.AppendLine("powershell -NoProfile -Command \"$ids=@('Dhcp','Dnscache','NlaSvc','netprofm','iphlpsvc','BFE'); $bad=0; foreach($s in $ids){ $svc=Get-Service -Name $s -ErrorAction SilentlyContinue; if($svc -and $svc.Status -ne 'Running'){ if($svc.StartType -eq 'Disabled'){ $bad++ } else { try{ Start-Service -Name $s -ErrorAction Stop; Start-Sleep -Milliseconds 500 }catch{ $bad++ } } } }; exit $bad\" >nul 2>&1 && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "dns_set_public":
                    if (adapterName.Length > 0)
                    {
                        // 链式：主 DNS 设置失败即 FAIL，不再执行次要操作，避免被 add 次 DNS 的成功掩盖成 OK
                        sb.AppendLine($"netsh interface ip set dns \"{adapterName}\" static {_settings.Current.DnsPreferred} primary >nul 2>&1 && netsh interface ip add dns \"{adapterName}\" {_settings.Current.DnsAlternate} index=2 >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    }
                    break;
                case "tcp_autotuning":
                    // 两条都必须成功：主设置（autotuninglevel）失败不能被次设置（rss）成功掩盖成 OK
                    sb.AppendLine("(netsh int tcp set global autotuninglevel=normal >nul 2>&1) && (netsh int tcp set global rss=enabled >nul 2>&1) && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "disable_delivopt":
                    sb.AppendLine("reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization\" /v DODownloadMode /t REG_DWORD /d 100 /f >nul 2>&1 && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "nic_powersave_off":
                    sb.AppendLine("powershell -NoProfile -Command \"Get-NetAdapterPowerManagement | Where-Object {$_.AllowComputerToTurnOffDevice -eq 'Enabled'} | Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice Disabled\" >nul 2>&1 && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "adapter_reset":
                    if (adapterName.Length > 0)
                    {
                        sb.AppendLine($"netsh interface set interface \"{adapterName}\" disable >nul 2>&1");
                        sb.AppendLine("timeout /t 3 /nobreak >nul 2>&1");
                        sb.AppendLine($"netsh interface set interface \"{adapterName}\" enable >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    }
                    break;
                default:
                    sb.AppendLine($"echo ##FAIL##{a.Id} 未支持");
                    break;
            }
        }
        sb.AppendLine("echo ##DONE##");
        // cmd.exe 不容错 UTF-8 BOM：带 BOM 时首行 "@echo off" 会被 BOM 字节污染为非法命令
        // （报错 + 回显失控）。chcp 65001 在第 2 行生效，此后中文 echo 正常，故文件本体须无 BOM
        // （与 CleanupService 提权脚本同规格）。
        File.WriteAllText(script, sb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (script, outLog);
    }
}

// ================= 低速原因分析引擎（宿主 Services/Network/SlowSpeedAnalyzer.cs 同源移植） =================

/// <summary>
/// 低速原因分析引擎：把低速专项各项检查证据 + 测速实测结果，
/// 归并为按嫌疑度排序的原因清单（高/中/低），每条附依据与处置措施。
/// 纯规则推理、无外部依赖；供 UI 展示与 PDF 报告使用。
/// </summary>
public static class SlowSpeedAnalyzer
{
    public static List<SlowCause> Analyze(CheckContext? ctx, IReadOnlyList<CheckItem> results, SpeedTestResult? speed)
    {
        var causes = new List<SlowCause>();
        var byId = results.Where(r => r.Status is CheckStatus.Warning or CheckStatus.Fail)
                          .GroupBy(r => r.Id)
                          .ToDictionary(g => g.Key, g => g.First());

        void Add(string title, int weight, List<string> evidence, List<string> actions)
        {
            var e = evidence.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            var a = actions.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (a.Count == 0) a.Add("结合其他嫌疑项综合处置后复测验证。");
            causes.Add(new SlowCause
            {
                Title = title,
                Weight = weight,
                Level = weight >= 75 ? "高" : weight >= 50 ? "中" : "低",
                Evidence = e,
                Actions = a
            });
        }

        // ---- 1. 代理中转 ----
        if (byId.ContainsKey("proxy.system") || ctx?.SharedBool("proxy.enabled") == true)
            Add("流量经系统代理中转", 82,
                new() { "系统代理处于开启状态，所有请求都要经代理服务器转发，代理慢或过载时整体变慢。" },
                new() { "关闭系统代理（一键修复支持）；确需代理的请检查代理服务器负载与出口。" });

        // ---- 2. 链路协商异常（半双工/百兆/低无线速率） ----
        if (byId.TryGetValue("speed.nic_rate", out var nic) && nic.Status == CheckStatus.Fail)
            Add("网卡半双工/协商异常", 88,
                new() { "网卡处于半双工或速率协商异常状态，冲突重传会严重拖慢吞吐。" },
                new() { "一键修复重新协商网卡；无效则更换网线（CAT5e+）、水晶头或交换机端口。" });
        else if (byId.ContainsKey("speed.nic_rate"))
            Add("链路协商速率偏低", 60,
                new() { "有线未达千兆或无线 PHY 速率偏低：物理层先于出口限制了带宽上限。" },
                new() { "检查网线等级与端口；无线请靠近 AP 或切换 5G；一键修复可重协商。" });

        // ---- 3. Wi-Fi 频段/信道/信号 ----
        if (byId.ContainsKey("speed.wifi_phy") || byId.ContainsKey("wifi.signal"))
        {
            var ev = new List<string>();
            if (ctx?.SharedBool("wifi.24g") == true) ev.Add("工作在 2.4 GHz 频段（教室密集场景干扰大）");
            if (ctx?.SharedBool("wifi.channel.overlap") == true) ev.Add("信道与邻区 AP 重叠");
            if ((ctx?.Wifi?.SignalPct ?? 100) < 60) ev.Add($"信号强度仅 {ctx!.Wifi!.SignalPct}%");
            if (ev.Count == 0) ev.Add("无线链路质量欠佳（频段/信号/干扰之一）");
            Add("无线链路是瓶颈", 72, ev,
                new() { "优先改用有线；无法布线时切换 5 GHz SSID、缩短与 AP 距离、避开微波炉/蓝牙。" });
        }

        // ---- 4. 接入段：班级中转设备 / 楼宇软路由 ----
        if (byId.ContainsKey("speed.path_profile") && ctx?.SharedBool("path.gw.bad") == true)
            Add("接入段拥塞/劣化（班级中转设备或楼宇软路由）", 78,
                new() { "第一跳（本机 → 直连网关）就出现高延迟/丢包：班级中转设备（疑似光猫/交换机）或楼宇软路由的接入侧过载或劣化，与出口无关。" },
                new() { "先问同班/同楼层同学是否同时变慢：都慢=共享设备过载，报修本校信息中心核查软路由带机量与中转设备；仅本机慢=换网线/换端口/换有线排除本机链路。" });

        // ---- 4b. 核心段：楼宇软路由 → 科技楼服务器 ----
        if (byId.ContainsKey("speed.path_profile") && ctx?.SharedBool("path.core.bad") == true)
            Add("核心段压力（楼宇软路由 → 科技楼服务器）", 74,
                new() { "到直连网关很快、到内网核心明显变慢：楼宇软路由上联、级联中转链路或科技楼服务器（全校共享核心）负载偏高，同一时段并发越多越明显。" },
                new() { "大流量任务（系统更新/资料分发）错峰安排；持续出现请信息中心核查软路由连接数/CPU 与服务器上联利用率。" });

        // ---- 5. 出口段：科技楼服务器 → 裸光纤 → 城域网 ----
        if (byId.ContainsKey("speed.path_profile") && ctx?.SharedBool("path.uplink.bad") == true)
            Add("出口段拥塞（科技楼服务器 → 裸光纤 → 城域网）", 70,
                new() { "内网各段都很快、出公网即慢：典型的裸光纤上联/城域网出口高峰期拥塞，软路由与服务器转发压力也会叠加此时段。" },
                new() { "错峰使用大流量任务；持续如此请携 PDF 报告联系教体局信息中心核查出口带宽与 QoS 策略。" });

        // ---- 6. DNS 拖慢 ----
        if (byId.ContainsKey("speed.dns_perf"))
            Add("DNS 解析拖慢所有网站", 66,
                new() { "解析耗时偏高：每个网站的首次打开都要先等 DNS，是“感觉慢”的常见隐形原因。" },
                new() { "一键修复切换为更快的公共 DNS（若校园网限制外部 DNS，请信息中心调整递归策略）。" });

        // ---- 7. TCP 自调谐被改 ----
        if (byId.ContainsKey("speed.tcp_tuning"))
            Add("TCP 接收窗口自调谐被改", 76,
                new() { "接收窗口自动调节不是 normal（曾遭优化软件改动），高带宽链路跑不满。" },
                new() { "一键修复恢复 normal 并启用 RSS，无需重启。" });

        // ---- 8. 传递优化 P2P ----
        if (byId.ContainsKey("speed.delivopt"))
            Add("Windows 传递优化 P2P 占用上行", 58,
                new() { "系统正在向局域网/互联网上传更新分片，上行被打满时整个网络都会卡。" },
                new() { "一键修复关闭 P2P 分发（单值可逆）；或组策略统一配置教学机分发模式。" });

        // ---- 9. 后台占用 ----
        if (byId.ContainsKey("speed.background"))
            Add("后台程序占用带宽", 54,
                new() { "网盘同步/下载器/流媒体客户端等在后台消耗带宽与连接数。" },
                new() { "退出或限速后台程序；上课时段统一关闭自动同步与自动更新。" });

        // ---- 10. 测速结果的横向对比 ----
        if (speed is { Success: true })
        {
            if (speed.MbpsAvg < 20)
                Add("实测带宽本身不足", 68,
                    new() { $"下载实测仅 {speed.SpeedText}（{speed.ExperienceBand}）。" },
                    new() { "先处理上方高嫌疑项再复测；若复测依旧且各段延迟正常，多为出口带宽/限速问题，向教体局信息中心反馈。" });
            if (speed.LinkMbps > 100 && speed.Efficiency < 0.4)
                Add("实测远低于链路能力", 64,
                    new() { $"链路协商 {speed.LinkText}，实测效率仅 {speed.EfficiencyText}：瓶颈不在本机链路。" },
                    new() { "按出口拥塞/限速方向排查（错峰、限流、联系信息中心）；本机侧修复收益有限。" });
        }

        // ---- 11. VPN/多网卡 ----
        if (byId.ContainsKey("vpn.adapters"))
            Add("VPN/多网卡流量绕行", 60,
                new() { "VPN 或多网络并存会使流量绕行隧道，认证、路由与吞吐都受影响。" },
                new() { "关闭教学机上的 VPN；只保留教学所需网络。" });

        // ---- 12. MTU ----
        if (byId.ContainsKey("quality.mtu"))
            Add("链路 MTU 偏小", 32,
                new() { "存在隧道/PPPoE 压低 MTU：大包分片使大文件传输与网页加载偶发变慢。" },
                new() { "一般无需处理；持续影响请信息中心核查中间链路 MTU。" });

        return causes.OrderByDescending(c => c.Weight).ToList();
    }

    /// <summary>低速结论一句话（UI/报告用）。</summary>
    public static string Conclusion(IReadOnlyList<SlowCause> causes, SpeedTestResult? speed)
    {
        if (causes.Count == 0)
        {
            if (speed is { Success: true })
                return $"未发现明显拖速因素，实测带宽 {speed.SpeedText}；若仍感觉慢，多为出口带宽本身有限，建议错峰使用并反馈信息中心。";
            return "未发现明显拖速因素。";
        }
        var top = causes[0];
        return $"共识别 {causes.Count} 项嫌疑因素，首要嫌疑：{top.Title}（{top.Level}）。建议先执行其处置措施并复测对比。";
    }
}

// ================= 网络拓扑构建器（宿主 Services/Network/NetworkTopologyBuilder.cs 同源移植） =================

/// <summary>
/// 网络拓扑构建器：把一次诊断的共享上下文（结构化实测）+ 检查项状态，收敛为
/// 「本机 → 网关 →（内网跳 ≤3）→ 校园出口 → 互联网（+DNS/认证门户分支）」的拓扑快照。
/// 纯函数、无 IO、无副作用：布局坐标在此计算，视图层只渲染。
/// 数据来源优先级：结构化实测（TopoKeys.*）&gt; 对应检查项状态 &gt; Unknown（灰色未检出）。
/// </summary>
public static class NetworkTopologyBuilder
{
    // 分段健康阈值：与「分段延迟画像」判定口径一致（内网跳/出口段）
    private const double HopFailLossPct = 20;    // 内网跳丢包超此 → 异常
    private const double HopWarnAvgMs = 30;      // 内网跳平均延迟超此 → 警告
    private const double HopWarnJitterMs = 25;   // 内网跳抖动超此 → 警告
    private const double ExitWarnLossPct = 10;   // 出口段丢包超此 → 警告
    private const double ExitWarnDeltaMs = 80;   // 出口段相对接入段增量超此 → 警告
    private const int MaxInnerHops = 3;          // 拓扑最多展示的内网跳数（超出折叠）
    private const string NcsiProbe = "http://www.msftconnecttest.com/connecttest.txt";

    public static NetworkTopology Build(CheckContext? ctx, IReadOnlyList<CheckItem> results,
        string modeText = "", bool cancelled = false)
    {
        var p = ctx?.Primary;
        if (p == null) return Degraded(modeText);

        var items = new Dictionary<string, CheckStatus>();
        foreach (var it in results) items[it.Id] = it.Status;
        TopoHealth Stat(string id) => items.TryGetValue(id, out var s) ? Map(s) : TopoHealth.Unknown;

        // ---------- 1) 本机 ----------
        var localHealth = Worse(Stat("ip.config"), Stat("physical.link"));
        var localSub = string.IsNullOrEmpty(p.IpV4) ? "无 IPv4" : p.IpV4!;
        localSub += p.IsWireless
            ? (ctx?.Wifi != null ? $" · Wi-Fi {ctx.Wifi.SignalPct}%" : " · Wi-Fi")
            : " · 有线";
        var localTip = new List<string>
        {
            $"主机：{Environment.MachineName}",
            $"网卡：{p.Name}（{p.Description}）",
            $"链路速率：{(p.SpeedBps > 0 ? $"{p.SpeedBps / 1_000_000} Mbps" : "未知")}"
        };
        if (ctx?.Wifi != null) localTip.Add($"Wi-Fi：{ctx.Wifi.Ssid}（信号 {ctx.Wifi.SignalPct}%）");

        // ---------- 2) 网关（接入段） ----------
        var gwHealth = First(Stat("gateway.reachable"), Stat("ip.config"));
        var gwStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Gateway);
        var gwStatus = DescribePing(gwStats?.Ping) ?? "未实测";
        var gwIp = string.IsNullOrEmpty(p.Gateway) ? "未配置" : p.Gateway!;

        // ---------- 3) 内网中间跳（tracert 实测，仅测速模式产出） ----------
        var hops = (ctx?.GetShared<List<TopoHop>>(TopoKeys.Hops) ?? new List<TopoHop>())
            .Take(MaxInnerHops).ToList();

        // ---------- 4) 校园出口（公网段） ----------
        var exitStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Exit);
        var gwPing = gwStats?.Ping;
        TopoHealth exitHealth;
        string exitStatus;
        var exitTip = new List<string>();
        if (exitStats?.Ping is { } ep)
        {
            exitStatus = $"公网段 {ep.AvgMs:0.#} ms · 丢包 {ep.LossPct:0.#}%";
            if (ep.AllLost)
            {
                exitHealth = TopoHealth.Fail;
                exitStatus = "公网段完全无响应";
            }
            else if (ep.LossPct > ExitWarnLossPct ||
                     (gwPing is { Received: > 0 } gp && ep.AvgMs - gp.AvgMs > ExitWarnDeltaMs))
            {
                exitHealth = TopoHealth.Warn;
                exitTip.Add("出口段拥塞特征：内网快、出公网明显变慢（裸光纤/城域网高峰拥塞）");
            }
            else exitHealth = TopoHealth.Ok;
        }
        else
        {
            exitHealth = First(Stat("internet.icmp"), Stat("internet.http"));
            exitStatus = exitHealth == TopoHealth.Unknown ? "该模式未实测出口段" : "按外网 ICMP/HTTP 判定";
        }

        // ---------- 5) 互联网 ----------
        var inetHealth = First(Stat("internet.http"), Stat("internet.https"), Stat("internet.icmp"));
        var inetStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Inet);
        var target = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Target);
        var speed = ctx?.SpeedResult;
        var inetParts = new List<string>();
        if (inetStats != null)
            inetParts.Add(inetStats.Ok ? $"HTTP 通（{inetStats.ElapsedMs} ms）"
                : string.IsNullOrEmpty(inetStats.Note) ? "HTTP 不通" : inetStats.Note);
        if (target?.Ping is { Received: > 0 } tp) inetParts.Add($"到站点 {tp.AvgMs:0.#} ms");
        if (speed is { Success: true, MbpsAvg: > 0 }) inetParts.Add($"实测 {speed.MbpsAvg:0.#} Mbps");
        var inetStatus = inetParts.Count > 0 ? string.Join(" · ", inetParts.Take(2)) : "未实测";

        // ---------- 6) DNS 分支数据 ----------
        var dnsProbes = ctx?.GetShared<List<TopoDnsProbe>>(TopoKeys.Dns);

        // ---------- 7) 认证门户分支数据 ----------
        var portalDetected = ctx is not null && ctx.SharedBool("portal.detected");
        var portalStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Portal);
        var detectHealth = Stat("portal.detect");
        var expiryHealth = Stat("portal.expiry");
        var showPortal = portalDetected || detectHealth == TopoHealth.Fail || expiryHealth == TopoHealth.Fail;

        // ---------- 布局：链式坐标 ----------
        double x = 14;
        const double yChain = 46, hChain = 84, hBranch = 84, gap = 70, yDns = 186, yPortal = 306;

        TopoNode Node(double px, double py, double w, TopoRole role, string key, string badge,
            string title, string sub, TopoHealth health, string status, string tip, double h = hChain)
            => new()
            {
                Key = key, Title = title, Subtitle = sub, Badge = badge, Role = role,
                Health = health, StatusText = status, TooltipText = tip,
                X = px, Y = py, W = w, H = h
            };

        var local = Node(x, yChain, 168, TopoRole.Local, "local", "本机",
            Trim(Environment.MachineName, 14), localSub, localHealth,
            localHealth == TopoHealth.Unknown ? "未检出" : HealthText(localHealth),
            string.Join("\n", localTip));
        x += 168 + gap;
        var gw = Node(x, yChain, 156, TopoRole.Gateway, "gw", "网关 · 接入段",
            gwIp, "班级中转/楼宇软路由", gwHealth, gwStatus,
            $"网关地址：{gwIp}\n{gwStatus}");
        x += 156 + gap;

        var hopPings = new Dictionary<string, PingStats>(); // 节点键 → 该跳实测（连线标注用）
        var chain = new List<TopoNode> { local, gw };
        foreach (var h in hops)
        {
            var key = $"hop{chain.Count - 1}";
            var hopHealth = HopHealth(h.Ping);
            var hopStatus = DescribePing(h.Ping) ?? "未实测";
            chain.Add(Node(x, yChain, 138, TopoRole.InnerHop, key,
                h.Role.Contains("科技楼", StringComparison.Ordinal) ? "核心出口侧" : "汇聚中转",
                $"内网跳 {chain.Count - 1}", h.Ip, hopHealth, hopStatus, $"{h.Role}\n{hopStatus}"));
            hopPings[key] = h.Ping;
            x += 138 + gap;
        }

        var exit = Node(x, yChain, 158, TopoRole.Exit, "exit", "校园出口",
            "裸光纤 → 城域网", "出口段（全校共享）", exitHealth, exitStatus,
            string.Join("\n", exitTip));
        x += 158 + gap;
        var inet = Node(x, yChain, 148, TopoRole.Internet, "inet", "互联网",
            "互联网", "外网连通与带宽", inetHealth, inetStatus,
            $"HTTP 探测：{inetStats?.Address ?? NcsiProbe}\n{inetStatus}");
        x += 148;
        chain.Add(exit);
        chain.Add(inet);
        var canvasW = x + 14;

        // ---------- DNS 分支节点 ----------
        var dnsNodes = new List<TopoNode>();
        if (dnsProbes is { Count: > 0 })
        {
            var exitCx = exit.X + exit.W / 2;
            var dx = Math.Max(14, exitCx - 304);
            for (int i = 0; i < Math.Min(2, dnsProbes.Count); i++)
            {
                var d = dnsProbes[i];
                dnsNodes.Add(new TopoNode
                {
                    Key = $"dns{i}",
                    Title = i == 0 ? "DNS 主" : "DNS 备",
                    Subtitle = d.Server,
                    Badge = "DNS 服务器",
                    Role = TopoRole.Dns,
                    Health = d.Ok ? TopoHealth.Ok : TopoHealth.Fail,
                    StatusText = d.Ok ? $"解析 {d.ElapsedMs} ms" : "无响应",
                    TooltipText = $"{d.Server}\n逐台探测：{(d.Ok ? $"解析正常（{d.ElapsedMs} ms）" : "无响应")}",
                    X = dx + i * 164, Y = yDns, W = 148, H = hBranch
                });
            }
        }
        else
        {
            var dnsHealth = First(Stat("dns.resolve"), Stat("dns.servers"), Stat("dns.config"));
            if (dnsHealth != TopoHealth.Unknown || p.DnsServers.Count > 0)
                dnsNodes.Add(new TopoNode
                {
                    Key = "dns0",
                    Title = "DNS 解析",
                    Subtitle = p.DnsServers.Count > 0 ? p.DnsServers[0] : "未配置",
                    Badge = "DNS 服务器",
                    Role = TopoRole.Dns,
                    Health = dnsHealth,
                    StatusText = dnsHealth == TopoHealth.Unknown ? "未检出" : HealthText(dnsHealth),
                    TooltipText = $"配置的 DNS：{string.Join(", ", p.DnsServers)}",
                    X = Math.Max(14, exit.X + exit.W / 2 - 74), Y = yDns, W = 148, H = hBranch
                });
        }

        // ---------- 认证门户分支节点 ----------
        TopoNode? portal = null;
        if (showPortal)
        {
            var portalHealth = expiryHealth == TopoHealth.Ok ? TopoHealth.Ok : First(detectHealth, expiryHealth);
            var portalStatus = portalHealth switch
            {
                TopoHealth.Ok => "在线（外网正常）",
                TopoHealth.Fail when expiryHealth == TopoHealth.Fail => "状态接口显示离线/过期",
                TopoHealth.Fail => "未认证/已过期",
                TopoHealth.Warn => "门户可达性异常",
                _ => "状态未知"
            };
            var portalTip = new List<string>();
            if (!string.IsNullOrEmpty(portalStats?.Address)) portalTip.Add(portalStats!.Address);
            if (!string.IsNullOrEmpty(portalStats?.Note)) portalTip.Add(portalStats!.Note);
            portal = new TopoNode
            {
                Key = "portal",
                Title = HostOf(portalStats?.Address),
                Subtitle = "城域网认证",
                Badge = "认证门户",
                Role = TopoRole.Portal,
                Health = portalHealth,
                StatusText = portalStatus,
                TooltipText = string.Join("\n", portalTip),
                X = Math.Max(14, exit.X + exit.W / 2 - 84),
                Y = dnsNodes.Count > 0 ? yPortal : yDns,
                W = 168, H = hBranch
            };
        }

        var nodes = chain.Concat(dnsNodes).ToList();
        if (portal != null) nodes.Add(portal);

        // ---------- 连线 ----------
        var byKey = nodes.ToDictionary(n => n.Key, n => n);
        var edges = new List<TopoEdge>
        {
            // 本机 → 网关：链路类型 + 接入段实测
            new()
            {
                From = "local", To = "gw",
                Label = LinkLabel(p.IsWireless, gwPing),
                Health = Worse(local.Health, gw.Health), Dashed = p.IsWireless
            }
        };
        // 网关 → 内网跳 →…→ 校园出口（逐跳实测标注）
        string prevKey = "gw";
        foreach (var n in chain.Skip(2).Where(n => n.Key != "inet"))
        {
            edges.Add(new TopoEdge
            {
                From = prevKey, To = n.Key,
                Label = n.Key == "exit"
                    ? (exitStats?.Ping is { Received: > 0 } xp ? $"{xp.AvgMs:0.#} ms" : "")
                    : DescribePing(hopPings.GetValueOrDefault(n.Key)) ?? "",
                Health = Worse(byKey[prevKey].Health, n.Health),
                Dashed = n.Health == TopoHealth.Unknown
            });
            prevKey = n.Key;
        }
        // 校园出口 → 互联网（测速带宽优先，其次 HTTP 耗时）
        var inetLabel = speed is { Success: true, MbpsAvg: > 0 } ? $"{speed.MbpsAvg:0.#} Mbps"
            : inetStats is { Ok: true } ? $"{inetStats.ElapsedMs} ms" : "";
        edges.Add(new TopoEdge
        {
            From = "exit", To = "inet", Label = inetLabel,
            Health = Worse(exit.Health, inet.Health), Dashed = inet.Health == TopoHealth.Unknown
        });
        // 校园出口 → DNS（逐台探测标注耗时；无探测数据的快速模式按结论给短标注）
        foreach (var d in dnsNodes)
        {
            var dnsLabel = dnsProbes != null
                ? (d.Health == TopoHealth.Ok ? $"{DnsElapsed(dnsProbes, d.Key)} ms" : "无响应")
                : d.Health switch
                {
                    TopoHealth.Fail => "解析失败",
                    TopoHealth.Warn => "偏慢",
                    _ => ""
                };
            edges.Add(new TopoEdge
            {
                From = "exit", To = d.Key,
                Label = dnsLabel,
                Health = Worse(exit.Health, d.Health),
                Dashed = d.Health == TopoHealth.Unknown
            });
        }
        // 校园出口 → 认证门户（被拦截点）
        if (portal != null)
            edges.Add(new TopoEdge
            {
                From = "exit", To = "portal",
                Label = portal.Health == TopoHealth.Ok ? "在线" : "未认证",
                Health = Worse(exit.Health, portal.Health),
                Dashed = portal.Health == TopoHealth.Unknown
            });

        var canvasH = portal != null && dnsNodes.Count > 0 ? yPortal + hBranch + 16
            : dnsNodes.Count > 0 || portal != null ? yDns + hBranch + 16
            : yChain + hChain + 16;

        return new NetworkTopology
        {
            Nodes = nodes,
            Edges = edges,
            CanvasW = canvasW,
            CanvasH = canvasH,
            SummaryText = Summarize(chain, portal, cancelled),
            BuiltFrom = BuildFrom(modeText)
        };
    }

    // ---------- 降级拓扑：无适配器 ----------
    private static NetworkTopology Degraded(string modeText)
    {
        var node = new TopoNode
        {
            Key = "local",
            Title = Trim(Environment.MachineName, 14),
            Subtitle = "未找到可用网络适配器",
            Badge = "本机",
            Role = TopoRole.Local,
            Health = TopoHealth.Fail,
            StatusText = "无适配器",
            TooltipText = "NetworkInterface 未返回可用的非回环适配器：请检查网卡硬件、驱动或在设备管理器中启用网卡。",
            X = 14, Y = 46, W = 168, H = 84
        };
        return new NetworkTopology
        {
            Nodes = { node },
            Edges = new List<TopoEdge>(),
            CanvasW = 196,
            CanvasH = 146,
            SummaryText = "未生成完整拓扑：未找到可用网络适配器（请先检查物理连接与网卡驱动）。",
            BuiltFrom = BuildFrom(modeText)
        };
    }

    // ---------- 摘要 ----------
    private static string Summarize(List<TopoNode> chain, TopoNode? portal, bool cancelled)
    {
        string summary;
        if (portal is { Health: TopoHealth.Fail })
            summary = "认证断点：城域网认证未通过 —— 重新登录门户即可恢复，链路其余段见拓扑着色";
        else
        {
            var fail = chain.FirstOrDefault(n => n.Health == TopoHealth.Fail);
            if (fail != null)
                summary = $"断点定位：{fail.Badge}「{fail.Title}」异常 —— {fail.StatusText}";
            else
            {
                var warn = chain.FirstOrDefault(n => n.Health == TopoHealth.Warn);
                summary = warn != null
                    ? $"链路可用但有瓶颈：{warn.Badge} —— {warn.StatusText}"
                    : "全链路正常：本机 → 网关 → 校园出口 → 互联网 各段实测通过";
            }
        }
        if (cancelled) summary += "（诊断已取消：灰色节点为未完成项）";
        return summary;
    }

    // ---------- 工具 ----------
    private static string BuildFrom(string modeText)
    {
        var mode = string.IsNullOrWhiteSpace(modeText) ? "诊断" : modeText;
        return $"{mode} · {DateTime.Now:HH:mm:ss} 实测生成";
    }

    private static TopoHealth Map(CheckStatus s) => s switch
    {
        CheckStatus.Pass or CheckStatus.Fixed => TopoHealth.Ok,
        CheckStatus.Warning => TopoHealth.Warn,
        CheckStatus.Fail => TopoHealth.Fail,
        _ => TopoHealth.Unknown
    };

    private static TopoHealth Worse(TopoHealth a, TopoHealth b) => (int)a > (int)b ? a : b;

    private static TopoHealth First(params TopoHealth[] hs)
    {
        foreach (var h in hs) if (h != TopoHealth.Unknown) return h;
        return TopoHealth.Unknown;
    }

    private static TopoHealth HopHealth(PingStats st)
    {
        if (st.AllLost || st.LossPct > HopFailLossPct) return TopoHealth.Fail;
        if (st.AvgMs > HopWarnAvgMs || st.JitterMs > HopWarnJitterMs) return TopoHealth.Warn;
        return TopoHealth.Ok;
    }

    private static string? DescribePing(PingStats? st)
        => st == null ? null
        : st.AllLost ? "完全无响应"
        : $"平均 {st.AvgMs:0.#} ms · 丢包 {st.LossPct:0.#}%";

    private static string LinkLabel(bool wireless, PingStats? gwPing)
    {
        var link = wireless ? "无线" : "有线";
        return gwPing is { Received: > 0 } ? $"{link} · {gwPing.AvgMs:0.#} ms" : link;
    }

    private static long DnsElapsed(List<TopoDnsProbe>? probes, string key)
    {
        if (probes == null) return 0;
        var idx = key == "dns0" ? 0 : 1;
        return idx < probes.Count ? probes[idx].ElapsedMs : 0;
    }

    private static string HealthText(TopoHealth h) => h switch
    {
        TopoHealth.Ok => "正常",
        TopoHealth.Warn => "有瓶颈",
        TopoHealth.Fail => "异常",
        _ => "未检出"
    };

    private static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "认证门户";
        var s = url;
        var i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        var j = s.IndexOfAny(new[] { '/', '?', ':' });
        if (j >= 0) s = s[..j];
        if (s.Length > 24) s = s[..24];
        return string.IsNullOrEmpty(s) ? "认证门户" : s;
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max];
}
