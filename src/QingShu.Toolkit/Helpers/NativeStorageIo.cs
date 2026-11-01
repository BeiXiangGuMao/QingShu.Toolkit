using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QingShu.Toolkit.Helpers;

/// <summary>
/// 存储原始 IO 的最小 Win32 底座（U盘工具专用）：
/// \\.\PhysicalDriveN / \\.\X: 句柄 + DeviceIoControl。
/// 仅收录广为文档化的控制码（winioctl.h）：卷锁定/卸载、磁盘属性刷新、
/// 存储设备描述符查询；不做任何未文档化调用，不做固件级操作。
/// </summary>
public static class NativeStorageIo
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
                    return System.Text.Encoding.ASCII.GetString(b, (int)off, end - (int)off).Trim();
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
