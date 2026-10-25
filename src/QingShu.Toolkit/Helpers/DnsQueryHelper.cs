using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Helpers;

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
