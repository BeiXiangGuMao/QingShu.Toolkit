using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace QingShu.Plugin.UsbTool;

// ============================================================================
// 自包含底层互操作（从宿主内置实现同源移植，零宿主内部依赖）：
//   FormatHelper / WmiHelper / NativeStorageIo / ElevationHelper / ProcessRunner
// 仅保留 U 盘工具实际使用的成员；行为与内置版逐行对应，内置页回退后表现一致。
// ============================================================================

/// <summary>展示格式化（宿主 Helpers/FormatHelper 同源移植，仅保留 U 盘工具用到的 Bytes）。</summary>
internal static class FormatHelper
{
    public static string Bytes(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }
}

/// <summary>
/// WMI 查询封装（宿主 Helpers/WmiHelper 同源移植）：全部在后台线程执行，避免卡 UI。
/// System.Management 程序集运行期经默认加载上下文共享宿主副本（插件 csproj ExcludeAssets=runtime）。
/// </summary>
internal static class WmiHelper
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
}

/// <summary>
/// 存储原始 IO 的最小 Win32 底座（宿主 Helpers/NativeStorageIo 同源移植，U盘工具专用）：
/// \\.\PhysicalDriveN / \\.\X: 句柄 + DeviceIoControl。
/// 仅收录广为文档化的控制码（winioctl.h）：卷锁定/卸载、磁盘属性刷新、
/// 存储设备描述符查询；不做任何未文档化调用，不做固件级操作。
/// </summary>
internal static class NativeStorageIo
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;

    // CTL_CODE 常量（FILE_DEVICE_FILE_SYSTEM=9 / IOCTL_DISK_BASE=7 / IOCTL_STORAGE_BASE=0x2D）
    private const uint FSCTL_LOCK_VOLUME = 0x00090014;
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090018;
    private const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x000740C8;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr inBuf, uint inSize,
        byte[]? outBuf, uint outSize, out uint returned, IntPtr overlapped);

    /// <summary>打开物理盘 / 卷句柄。exclusive=true 时 share=0（独占）。</summary>
    public static SafeFileHandle OpenRaw(string path, bool write, bool exclusive)
    {
        uint access = write ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ;
        uint share = exclusive ? 0 : FILE_SHARE_READ | FILE_SHARE_WRITE;
        return CreateFileW(path, access, share, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
    }

    /// <summary>锁定并卸载卷（用于破坏性原始读写前把文件系统挤出缓存）。任一步失败返回 false（不抛）。</summary>
    public static bool TryLockAndDismountVolume(string letter)
    {
        try
        {
            using var h = OpenRaw($@"\\.\{letter}:", write: true, exclusive: true);
            if (h.IsInvalid) return false;
            if (!DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero))
            {
                // 锁不上（有文件占用）仍尝试卸载：卸载会丢弃该卷的缓存一致性风险由调用方提示
            }
            if (!DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero))
                return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>刷新磁盘属性（让系统重新识别容量/分区变化）。best-effort。</summary>
    public static bool TryUpdateDiskProperties(string physicalPath)
    {
        try
        {
            using var h = OpenRaw(physicalPath, write: true, exclusive: false);
            if (h.IsInvalid) return false;
            return DeviceIoControl(h, IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero);
        }
        catch { return false; }
    }

    /// <summary>存储设备描述符（厂商/型号/版本/总线/可移动介质）。解析失败返回 null（USB 桥差异大，属正常降级）。</summary>
    public static (string Vendor, string Product, string Revision, string BusType, bool Removable)? QueryDeviceDescriptor(string physicalPath)
    {
        try
        {
            using var h = OpenRaw(physicalPath, write: false, exclusive: false);
            if (h.IsInvalid) return null;
            // STORAGE_PROPERTY_QUERY：PropertyId=0(StorageDeviceProperty) QueryType=0(Standard)
            var inBuf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(inBuf, 0, 0);
                Marshal.WriteInt32(inBuf, 4, 0);
                var outBuf = new byte[4096];
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 8, outBuf, (uint)outBuf.Length, out _, IntPtr.Zero))
                    return null;

                uint vendorOff = LE32(outBuf, 12), productOff = LE32(outBuf, 16),
                     revOff = LE32(outBuf, 20), bus = LE32(outBuf, 28);
                // STORAGE_DEVICE_DESCRIPTOR 布局：Type@8 / RemovableMedia@9（随后 2 字节对齐填充，
                // VendorIdOffset@12）。此前误读 outBuf[10]（=填充字节恒 0），可移动标志永远 false。
                bool removable = outBuf[9] != 0;
                static string Str(byte[] b, uint off)
                {
                    if (off == 0 || off >= b.Length) return "";
                    int end = (int)off;
                    while (end < b.Length && b[end] != 0) end++;
                    return Encoding.ASCII.GetString(b, (int)off, end - (int)off).Trim();
                }
                string busText = bus switch
                {
                    1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE1394", 5 => "SSA", 6 => "光纤", 7 => "SAS",
                    8 => "SATA", 9 => "SD", 10 => "MMC", 11 => "虚拟", 12 => "文件后端", 13 => "存储空间", 14 => "NVMe",
                    15 => "SCSI 虚拟", 16 => "NVMe 虚拟",
                    _ => $"BusType {bus}"
                };
                return (Str(outBuf, vendorOff), Str(outBuf, productOff), Str(outBuf, revOff), busText, removable);
            }
            finally { Marshal.FreeHGlobal(inBuf); }
        }
        catch { return null; }
    }

    private static uint LE32(byte[] b, int off) =>
        off + 4 <= b.Length ? (uint)(b[off] | b[off + 1] << 8 | b[off + 2] << 16 | b[off + 3] << 24) : 0;
}

/// <summary>
/// 提权辅助（宿主 Interop/ElevationHelper 同源移植，仅保留 U 盘工具用到的提权脚本运行）：
/// 管理类修复动作合并为单个脚本一次性 UAC 提权执行。
/// </summary>
internal static class ElevationHelper
{
    /// <summary>
    /// 以管理员身份运行一个 cmd 批处理脚本（触发一次 UAC）。
    /// 脚本内部应输出 ##OK##/##FAIL## 标记到 stdout，stdout 被重定向到 outLog。
    /// </summary>
    public static Task<(int ExitCode, string Output)> RunElevatedScriptAsync(string scriptPath, string outLog, int timeoutMs = 300_000)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c \"\"{scriptPath}\" > \"{outLog}\" 2>&1\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return (-1, "##FAIL## 提权脚本执行超时");
                }
                var output = File.Exists(outLog) ? File.ReadAllText(outLog) : "";
                return (p.ExitCode, output ?? "");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC 弹窗被用户取消：返回哨兵文案，调用方按「未执行」呈现，绝不当失败中断
                return (-1, "##UAC_CANCELLED##");
            }
            catch (Exception ex)
            {
                return (-1, $"##FAIL## 无法启动提权脚本：{ex.Message}");
            }
        });
    }
}

/// <summary>
/// 静默命令运行器（宿主 Helpers/ProcessRunner 同源移植，仅保留 U 盘工具用到的通用 cmd 运行）：
/// attrib -s -h 全盘解除隐藏使用；UTF-8 捕获输出。
/// </summary>
internal static class ProcessRunner
{
    public sealed record CmdResult(int ExitCode, string Output);

    public static Task<CmdResult> RunAsync(string command, int timeoutMs = 30_000)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c {command}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return new CmdResult(-1, "##FAIL## 命令执行超时");
                }
                return new CmdResult(p.ExitCode, p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
            }
            catch (Exception ex)
            {
                return new CmdResult(-1, $"##FAIL## {ex.Message}");
            }
        });
    }
}
