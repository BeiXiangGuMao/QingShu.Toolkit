using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
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
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            await File.WriteAllTextAsync(ConfigPath, json).ConfigureAwait(false);
        }
        catch { /* 磁盘只读等场景静默 */ }
        Changed?.Invoke();
    }

    public void Reset()
    {
        try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); } catch { }
        Current = new AppSettings();
        Changed?.Invoke();
    }
}
