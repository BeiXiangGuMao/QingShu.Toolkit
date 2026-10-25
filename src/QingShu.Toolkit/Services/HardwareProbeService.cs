using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services;

public interface IHardwareProbeService
{
    DeviceFacts Facts { get; }
    Task<DeviceFacts> EnsureLoadedAsync();
}

/// <summary>设备信息探测（序列号 / MachineGuid / CPU / 内存 / 网卡），供 PDF 报告与仪表盘使用。</summary>
public sealed class HardwareProbeService : IHardwareProbeService
{
    public DeviceFacts Facts { get; } = new();

    private Task<DeviceFacts>? _loadTask; // 缓存任务而非标志位：避免并发调用拿到半成品数据

    public Task<DeviceFacts> EnsureLoadedAsync()
    {
        if (_loadTask != null) return _loadTask;
        Facts.ToolVersion = AppVersion.DisplayText;
        Facts.HostName = Environment.MachineName;
        var (memLoad, totalPhys, _) = NativeMethods.GetMemoryStatus();
        Facts.TotalRamBytes = totalPhys;

        _loadTask = Task.Run(async () =>
        {
            try
            {
                // 操作系统
                Facts.OsVersion = $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    var display = key?.GetValue("DisplayVersion")?.ToString();
                    var name = key?.GetValue("ProductName")?.ToString();
                    if (!string.IsNullOrEmpty(name)) Facts.OsVersion = name! + (string.IsNullOrEmpty(display) ? "" : $" ({display})");
                }
                catch { }
                Facts.OsBuild = Environment.OSVersion.Version.ToString();

                // CPU
                Facts.CpuName = await WmiHelper.FirstValueAsync("SELECT Name FROM Win32_Processor", "Name").ConfigureAwait(false);

                // BIOS 序列号
                Facts.SerialNumber = await WmiHelper.FirstValueAsync("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber").ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(Facts.SerialNumber)) Facts.SerialNumber = "未知";

                // MachineGuid（注册表只读）
                try
                {
                    Facts.MachineGuid = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid")?.ToString() ?? "";
                }
                catch { }

                // 主网卡
                var adapters = NetworkProbeHelper.GetAllAdapters();
                var primary = adapters.FirstOrDefault(a => a.IsUp && a.Gateway != null) ?? adapters.FirstOrDefault();
                if (primary != null)
                {
                    Facts.NicModel = primary.Description;
                    Facts.MacAddress = await WmiHelper.FirstValueAsync(
                        "SELECT MACAddress FROM Win32_NetworkAdapter WHERE PhysicalAdapter=true AND NetEnabled=true", "MACAddress")
                        .ConfigureAwait(false);
                }
            }
            catch { }
            Facts.Loaded = true;
            return Facts;
        });
        return _loadTask;
    }
}

/// <summary>版本号辅助。</summary>
public static class AppVersion
{
    /// <summary>纯数字版本号（更新比较 / UA / 机器可读场景一律使用本值）。</summary>
    public static string Text
    {
        get
        {
            try { return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"; }
            catch { return "1.0.0"; }
        }
    }

    /// <summary>展示用版本号：Beta 阶段带预发布后缀（取自 csproj 的 InformationalVersion，如 1.2.0-beta）。</summary>
    public static string DisplayText
    {
        get
        {
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                var attr = asm is null
                    ? null
                    : System.Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                        as System.Reflection.AssemblyInformationalVersionAttribute;
                return string.IsNullOrWhiteSpace(attr?.InformationalVersion) ? Text : attr!.InformationalVersion;
            }
            catch { return Text; }
        }
    }
}
