using System;
using System.Collections.Concurrent;
using System.IO;

namespace QingShu.Gala;

/// <summary>
/// 事件留痕文件（运行日志落盘）：带时间戳追加，超 1MB 轮转为 .old（只留一代，防无限增长）。
/// 主控/服务端各一份（gala-master.log / gala-agent.log），复盘不依赖内存环形缓冲或面板导出时机；
/// 独立程序与插件共用。同一文件内加锁串行（并发事件不再交错写/轮转竞态）；
/// 留痕尽力而为：任何 IO 失败静默吞掉，绝不影响主流程。
/// </summary>
public static class GalaEventLog
{
    private const long RotateBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);

    public static void Append(string? folder, string fileName, string message)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(fileName)) return;
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            // 同文件串行化：metrics 周期事件与指令回执并发追加不再交错，轮转也不再与追加竞态
            var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());
            lock (gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > RotateBytes)
                {
                    var old = path + ".old";
                    try { File.Delete(old); } catch { }
                    try { File.Move(path, old); } catch { }
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { } // 留痕失败不影响主流程（磁盘只读/权限不足等场景）
    }
}
