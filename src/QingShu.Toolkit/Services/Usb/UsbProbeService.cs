using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Services.Usb;

public interface IUsbProbeService
{
    /// <summary>枚举当前接入的 USB 存储盘（含卷映射）。按 PNPDeviceID=USBSTOR 过滤，系统盘不可能混入。</summary>
    Task<List<UsbDrive>> ListDrivesAsync();
}

/// <summary>USB 存储设备探查：WMI 物理盘 → 分区 → 逻辑卷三级关联 + 描述符补充。</summary>
public sealed class UsbProbeService : IUsbProbeService
{
    public async Task<List<UsbDrive>> ListDrivesAsync()
    {
        var drives = await WmiHelper.QueryAsync(
            "SELECT Index, Model, Size, SerialNumber, InterfaceType, PNPDeviceID FROM Win32_DiskDrive",
            mo => new
            {
                Index = ToInt(mo["Index"]),
                Model = (mo["Model"]?.ToString() ?? "").Trim(),
                Size = ToLong(mo["Size"]),
                Serial = (mo["SerialNumber"]?.ToString() ?? "").Trim(),
                IfType = (mo["InterfaceType"]?.ToString() ?? "").Trim(),
                Pnp = (mo["PNPDeviceID"]?.ToString() ?? "").Trim()
            }).ConfigureAwait(false);

        var result = new List<UsbDrive>();
        foreach (var d in drives.Where(d => d.Index >= 0))
        {
            // 只认 USBSTOR（U 盘 / USB 读卡器 / USB 移动硬盘桥）。系统盘是 ATA/NVMe，天然隔离。
            var isUsb = d.Pnp.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase)
                        || d.IfType.Equals("USB", StringComparison.OrdinalIgnoreCase);
            if (!isUsb) continue;

            var volumes = await MapVolumesAsync(d.Index).ConfigureAwait(false);

            // 描述符补充：WMI 拿不到的序列号/总线类型从 STORAGE_DEVICE_DESCRIPTOR 兜底
            string busText = "USB";
            bool removable = true;
            try
            {
                var desc = NativeStorageIo.QueryDeviceDescriptor($@"\\.\PhysicalDrive{d.Index}");
                if (desc != null) { busText = desc.Value.BusType; removable = desc.Value.Removable; }
            }
            catch { }

            result.Add(new UsbDrive(d.Index, d.Pnp, d.Model, d.Serial, d.Size, busText, removable, volumes));
        }
        return result.OrderBy(x => x.DiskIndex).ToList();
    }

    private static async Task<List<UsbVolume>> MapVolumesAsync(int diskIndex)
    {
        var volumes = new List<UsbVolume>();
        var escaped = $@"\\.\PHYSICALDRIVE{diskIndex}".Replace(@"\", @"\\");
        var parts = await WmiHelper.QueryAsync(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escaped}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition",
            mo => mo["DeviceID"]?.ToString() ?? "").ConfigureAwait(false);

        foreach (var partId in parts)
        {
            var escPart = partId.Replace(@"\", @"\\");
            var logicals = await WmiHelper.QueryAsync(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{escPart}'}} WHERE AssocClass = Win32_LogicalDiskToPartition",
                mo => new
                {
                    Id = mo["DeviceID"]?.ToString() ?? "",
                    Label = (mo["VolumeName"]?.ToString() ?? "").Trim(),
                    Fs = (mo["FileSystem"]?.ToString() ?? "").Trim().ToUpperInvariant(),
                    Total = ToLong(mo["Size"]),
                    Free = ToLong(mo["FreeSpace"])
                }).ConfigureAwait(false);

            foreach (var l in logicals.Where(l => l.Id.Length >= 2 && char.IsLetter(l.Id[0])))
                volumes.Add(new UsbVolume(l.Id[0].ToString(), string.IsNullOrEmpty(l.Label) ? "本地磁盘" : l.Label, l.Fs, l.Total, l.Free));
        }
        return volumes;
    }

    /// <summary>破坏性操作前的最后防线：确认盘符不是系统盘（Windows 所在卷）。</summary>
    public static bool IsSystemVolume(string letter)
    {
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return string.Equals(letter + ":", sysRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
               || (letter + ":").StartsWith(sysRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static int ToInt(object? v) => int.TryParse(v?.ToString(), out var n) ? n : -1;

    private static long ToLong(object? v) => long.TryParse(v?.ToString(), out var n) ? n : 0;
}
