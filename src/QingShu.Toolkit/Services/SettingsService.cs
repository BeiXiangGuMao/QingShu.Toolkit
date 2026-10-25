using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task SaveAsync();
    void Reset();
    event Action? Changed;
    string ConfigPath { get; }
}

/// <summary>配置服务：JSON 持久化到 %LOCALAPPDATA%\QingShu.Toolkit\config.json。</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文可读
        PropertyNameCaseInsensitive = true
    };

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit");

    public string ConfigPath { get; } = Path.Combine(DataDir, "config.json");

    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    private readonly SemaphoreSlim _saveGate = new(1, 1); // 多处 fire-and-forget 保存需串行化，防并发写坏配置

    public SettingsService()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        var snapshot = Current; // Reset 可能替换引用，先捕获快照
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            // 先写临时文件再原子替换：进程中途被杀不会留下半截配置导致全部重置
            var tmp = ConfigPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch { /* 磁盘只读等场景静默 */ }
        finally { _saveGate.Release(); }
        // SaveAsync 可能从任意线程调用（ConfigureAwait(false) 链），Changed 必须调度回 UI
        UiDispatcher.Run(() => Changed?.Invoke());
    }

    public void Reset()
    {
        try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); } catch { }
        Current = new AppSettings();
        UiDispatcher.Run(() => Changed?.Invoke());
    }
}
