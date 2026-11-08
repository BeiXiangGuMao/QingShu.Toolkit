using System;
using System.IO;

namespace QingShu.Gala;

/// <summary>
/// 配置/状态文件原子写与损坏恢复（完整性底座）：
/// · 写入 = 先落同目录 .tmp 临时文件再原子替换（File.Move overwrite）——进程崩溃/断电
///   不再留半截 JSON；替换前把旧文件轮转为 .bak（只留一代），写坏也有上一份可回退。
/// · 读取 = 主文件优先，失败自动回退 .bak——现场最坏丢一次变更，不丢整份拓扑配置。
/// 全部尽力而为：备份失败不阻塞写入，回退失败返回 null 由调用方兜底（模板/忽略）。
/// </summary>
public static class GalaFile
{
    public static void WriteAtomic(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { } // 备份尽力而为
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true); // .NET 8：目标存在时替换（同卷内原子）
    }

    /// <summary>读取（主文件优先，主文件损坏/不可读时回退 .bak）。返回 null = 主文件与备份都不可用。</summary>
    public static string? ReadWithBackup(string path, Action<string>? onFallback = null)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception primary)
        {
            try
            {
                var bak = path + ".bak";
                if (!File.Exists(bak)) return null;
                var text = File.ReadAllText(bak);
                onFallback?.Invoke($"主文件读取失败（{primary.Message}），已回退上一份备份：{bak}");
                return text;
            }
            catch { return null; }
        }
    }
}
