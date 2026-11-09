using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.DiskCleanup;

/// <summary>
/// 插件设置（settings.json）：久远阈值（天）/ 大文件阈值（MB）/ 删除是否进回收站。
/// 原子写（tmp + replace），损坏时回退默认；首次启动从宿主 config.json 一次性迁移旧内置实现的
/// 同名设置（幂等：settings.json 建立后不再迁移）。与宿主设置页共用的历史口径改为插件私有存储，
/// 插件卸载回退内置页后，内置页继续读写宿主 config.json（互不破坏）。
/// </summary>
internal sealed class CleanupSettingsStore
{
    private static readonly string FileName = "settings.json";

    public int ImOldDays { get; set; } = 180;

    public int LargeFileMinMb { get; set; } = 100;

    public bool DeleteToRecycleBin { get; set; } = true;

    [JsonIgnore]
    private string _path = "";

    [JsonIgnore]
    private IPluginContext? _ctx;

    public static CleanupSettingsStore LoadOrCreate(IPluginContext ctx)
    {
        var store = new CleanupSettingsStore
        {
            _path = Path.Combine(ctx.DataFolder, FileName),
            _ctx = ctx
        };
        try
        {
            if (File.Exists(store._path))
            {
                var doc = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(store._path));
                if (doc != null)
                {
                    if (doc.ImOldDays is > 0) store.ImOldDays = doc.ImOldDays;
                    if (doc.LargeFileMinMb is > 0) store.LargeFileMinMb = doc.LargeFileMinMb;
                    if (doc.DeleteToRecycleBin is not null) store.DeleteToRecycleBin = doc.DeleteToRecycleBin.Value;
                }
                return store;
            }
        }
        catch (Exception ex)
        {
            ctx.Log("warn", $"settings.json 读取失败（按默认设置继续）：{ex.Message}");
        }

        // 首次启动：从宿主 config.json 迁移旧内置实现的同名设置（幂等：settings.json 建立后不再迁移）
        MigrateFromHost(ctx, store);
        store.Save();
        return store;
    }

    /// <summary>仅输入合法且与现值不同时写入（避免半输入态污染；阈值钳制口径与内置页一致）。</summary>
    public void UpdateImOldDays(int value)
    {
        if (value <= 0 || value == ImOldDays) return;
        ImOldDays = Math.Clamp(value, 1, 3650);
        Save();
    }

    public void UpdateLargeFileMinMb(int value)
    {
        if (value <= 0 || value == LargeFileMinMb) return;
        LargeFileMinMb = Math.Clamp(value, 1, 1024 * 1024);
        Save();
    }

    public void UpdateDeleteToRecycleBin(bool value)
    {
        if (value == DeleteToRecycleBin) return;
        DeleteToRecycleBin = value;
        Save();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new StoreDto
            {
                ImOldDays = ImOldDays,
                LargeFileMinMb = LargeFileMinMb,
                DeleteToRecycleBin = DeleteToRecycleBin
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            _ctx?.Log("warn", $"磁盘清理设置保存失败：{ex.Message}");
        }
    }

    private static void MigrateFromHost(IPluginContext ctx, CleanupSettingsStore store)
    {
        try
        {
            var cfg = Path.Combine(ctx.HostDataRoot, "config.json");
            if (!File.Exists(cfg))
            {
                ctx.Log("info", "宿主 config.json 不存在，按插件默认清理设置初始化");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            int migrated = 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("ImOldDays", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var oldDays) && oldDays > 0)
                {
                    store.ImOldDays = Math.Clamp(oldDays, 1, 3650);
                    migrated++;
                }
                else if (p.Name.Equals("LargeFileMinMb", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var minMb) && minMb > 0)
                {
                    store.LargeFileMinMb = Math.Clamp(minMb, 1, 1024 * 1024);
                    migrated++;
                }
                else if (p.Name.Equals("DeleteToRecycleBin", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    store.DeleteToRecycleBin = p.Value.GetBoolean();
                    migrated++;
                }
            }
            ctx.Log("info", migrated > 0
                ? $"已从宿主 config.json 迁移磁盘清理设置（{migrated} 项）"
                : "宿主 config.json 未配置清理项，按插件默认清理设置初始化");
        }
        catch (Exception ex)
        {
            ctx.Log("warn", $"宿主设置迁移失败（按插件默认清理设置继续）：{ex.Message}");
        }
    }

    private sealed class StoreDto
    {
        public int ImOldDays { get; set; } = 180;
        public int LargeFileMinMb { get; set; } = 100;
        public bool? DeleteToRecycleBin { get; set; } = true;
    }
}
