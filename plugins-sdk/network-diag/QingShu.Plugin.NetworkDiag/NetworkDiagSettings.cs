using System;
using System.IO;
using System.Text.Json;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.NetworkDiag;

/// <summary>
/// 诊断引擎设置快照（宿主 config.json「网络诊断」域字段的只读镜像）：
/// 字段名与宿主内置设置模型逐一同名同默认值，诊断/修复服务只读消费；
/// 插件绝不回写宿主配置（宿主设置页是这些字段的唯一写入方）。
/// </summary>
public sealed class DiagSettings
{
    /// <summary>城域网认证门户地址（如 http://10.x.x.x 或认证域名）</summary>
    public string PortalBaseUrl { get; set; } = "";
    /// <summary>门户自动登录（实验性）：配置账号后一键修复会尝试直接提交认证表单</summary>
    public bool PortalAutoLogin { get; set; } = false;
    /// <summary>认证账号（仅保存在本机 config.json）</summary>
    public string PortalUsername { get; set; } = "";
    /// <summary>认证密码（仅保存在本机 config.json）</summary>
    public string PortalPassword { get; set; } = "";
    /// <summary>认证状态查询接口（可选，如门户的 /status 页）</summary>
    public string AuthStatusUrl { get; set; } = "";
    /// <summary>判定“认证在线”的关键字（| 分隔）</summary>
    public string AuthOkKeywords { get; set; } = "success|在线|已认证|认证成功";
    /// <summary>校园内网探测地址（如教体局 OA / 内网资源 IP 或域名）</summary>
    public string IntranetProbeHost { get; set; } = "";
    public string DnsPreferred { get; set; } = "223.5.5.5";
    public string DnsAlternate { get; set; } = "119.29.29.29";
    public int PingCount { get; set; } = 4;
    public int PingTimeoutMs { get; set; } = 1500;
    /// <summary>延迟警告阈值（ms）</summary>
    public int LatencyWarnMs { get; set; } = 80;
    /// <summary>延迟不合格阈值（ms）</summary>
    public int LatencyFailMs { get; set; } = 300;
    /// <summary>测速每端点限时（秒，3-30）</summary>
    public int SpeedTestSeconds { get; set; } = 8;
    /// <summary>测速并发连接数（1-8）</summary>
    public int SpeedTestConnections { get; set; } = 4;
    /// <summary>测速总下载上限（MB，防止超大带宽长时间跑满）</summary>
    public int SpeedTestCapMb { get; set; } = 256;
    /// <summary>自定义测速 URL（优先于内置节点；留空使用内置候选）</summary>
    public string SpeedTestUrl { get; set; } = "";
}

/// <summary>
/// 插件设置存储（settings.json）：PDF 报告表单记忆五项（学校/年级/班级/报告人/设备编号）。
/// 原子写（tmp + replace），损坏时回退默认；首次启动从宿主 config.json 一次性迁移旧内置实现的
/// 同名 Last* 键（JsonDocument 大小写不敏感、幂等：settings.json 建立后不再迁移、原子写、
/// 损坏回退默认，popup-guard/disk-cleanup 批同款）。同时维护诊断设置只读镜像 Current
/// （LoadOrCreate 与 RefreshDiagFromHost 各读一次宿主 config.json）。
/// 插件卸载回退内置页后，内置页继续读写宿主 config.json，互不破坏。
/// </summary>
internal sealed class NetworkDiagSettingsStore
{
    private static readonly string FileName = "settings.json";

    // ---- 报告表单记忆五项（插件私有，settings.json 持久化） ----
    public string LastSchool { get; private set; } = "";
    public string LastGrade { get; private set; } = "";
    public string LastClassName { get; private set; } = "";
    public string LastReporter { get; private set; } = "";
    public string LastDeviceTag { get; private set; } = "";

    // ---- 诊断设置只读镜像（宿主 config.json；LoadOrCreate/RefreshDiagFromHost 时刷新） ----
    public DiagSettings Current { get; private set; } = new();

    private string _path = "";
    private IPluginContext? _ctx;

    public static NetworkDiagSettingsStore LoadOrCreate(IPluginContext ctx)
    {
        var store = new NetworkDiagSettingsStore
        {
            _path = Path.Combine(ctx.DataFolder, FileName),
            _ctx = ctx
        };
        var migrated = false;
        try
        {
            if (File.Exists(store._path))
            {
                var doc = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(store._path));
                if (doc != null)
                {
                    store.LastSchool = doc.LastSchool ?? "";
                    store.LastGrade = doc.LastGrade ?? "";
                    store.LastClassName = doc.LastClassName ?? "";
                    store.LastReporter = doc.LastReporter ?? "";
                    store.LastDeviceTag = doc.LastDeviceTag ?? "";
                }
                return store;
            }
        }
        catch (Exception ex)
        {
            ctx.Log("warn", $"settings.json 读取失败（按默认设置继续）：{ex.Message}");
        }

        // 首次启动：从宿主 config.json 迁移旧内置实现的同名 Last* 键（幂等：settings.json 建立后不再迁移）
        migrated = MigrateFromHost(ctx, store, store.Current);
        store.Save();
        if (migrated)
            ctx.Log("info", "已从宿主 config.json 迁移网络诊断报告表单记忆（Last* 五项）");
        return store;
    }

    /// <summary>
    /// 每次诊断开始前刷新诊断设置镜像（宿主设置页可能在插件存活期间修改门户地址/DNS 等；
    /// 只读宿主 config.json，不写任何文件，损坏时保持现值）。
    /// </summary>
    public void RefreshDiagFromHost()
    {
        var fresh = new DiagSettings();
        try
        {
            var cfg = Path.Combine(_ctx?.HostDataRoot ?? "", "config.json");
            if (!File.Exists(cfg)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            ReadDiagnostics(doc.RootElement, fresh);
            Current = fresh;
        }
        catch (Exception ex)
        {
            _ctx?.Log("warn", $"宿主诊断设置刷新失败（沿用上次镜像）：{ex.Message}");
        }
    }

    /// <summary>导出报告成功后写入表单记忆并落盘（字段与内置页语义逐字对应）。</summary>
    public void SetLastReport(string school, string grade, string className, string reporter, string deviceTag)
    {
        LastSchool = school;
        LastGrade = grade;
        LastClassName = className;
        LastReporter = reporter;
        LastDeviceTag = deviceTag;
        Save();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new StoreDto
            {
                LastSchool = LastSchool,
                LastGrade = LastGrade,
                LastClassName = LastClassName,
                LastReporter = LastReporter,
                LastDeviceTag = LastDeviceTag
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
            _ctx?.Log("warn", $"网络诊断插件设置保存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从宿主 config.json 迁移 Last* 五项（幂等迁移，只读宿主配置）；同时把诊断设置域字段
    /// 读入 diag 镜像（首次装载即有可用镜像）。返回是否迁移到任何 Last* 键。
    /// </summary>
    private static bool MigrateFromHost(IPluginContext ctx, NetworkDiagSettingsStore store, DiagSettings diag)
    {
        try
        {
            var cfg = Path.Combine(ctx.HostDataRoot, "config.json");
            if (!File.Exists(cfg))
            {
                ctx.Log("info", "宿主 config.json 不存在，按插件默认报告表单与诊断设置初始化");
                return false;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            ReadDiagnostics(doc.RootElement, diag);
            int migrated = 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("LastSchool", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    store.LastSchool = p.Value.GetString() ?? ""; migrated++;
                }
                else if (p.Name.Equals("LastGrade", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    store.LastGrade = p.Value.GetString() ?? ""; migrated++;
                }
                else if (p.Name.Equals("LastClassName", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    store.LastClassName = p.Value.GetString() ?? ""; migrated++;
                }
                else if (p.Name.Equals("LastReporter", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    store.LastReporter = p.Value.GetString() ?? ""; migrated++;
                }
                else if (p.Name.Equals("LastDeviceTag", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    store.LastDeviceTag = p.Value.GetString() ?? ""; migrated++;
                }
            }
            return migrated > 0;
        }
        catch (Exception ex)
        {
            ctx.Log("warn", $"宿主设置迁移失败（按插件默认继续）：{ex.Message}");
            return false;
        }
    }

    /// <summary>读取宿主 config.json 中网络诊断域字段（大小写不敏感；类型不符跳过保持默认）。</summary>
    private static void ReadDiagnostics(JsonElement root, DiagSettings d)
    {
        foreach (var p in root.EnumerateObject())
        {
            var name = p.Name;
            if (name.Equals("PortalBaseUrl", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.PortalBaseUrl = p.Value.GetString() ?? "";
            else if (name.Equals("PortalAutoLogin", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                d.PortalAutoLogin = p.Value.GetBoolean();
            else if (name.Equals("PortalUsername", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.PortalUsername = p.Value.GetString() ?? "";
            else if (name.Equals("PortalPassword", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.PortalPassword = p.Value.GetString() ?? "";
            else if (name.Equals("AuthStatusUrl", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.AuthStatusUrl = p.Value.GetString() ?? "";
            else if (name.Equals("AuthOkKeywords", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.AuthOkKeywords = p.Value.GetString() ?? "";
            else if (name.Equals("IntranetProbeHost", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.IntranetProbeHost = p.Value.GetString() ?? "";
            else if (name.Equals("DnsPreferred", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.DnsPreferred = p.Value.GetString() ?? "";
            else if (name.Equals("DnsAlternate", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.DnsAlternate = p.Value.GetString() ?? "";
            else if (name.Equals("PingCount", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var pc))
                d.PingCount = Math.Clamp(pc, 1, 10);
            else if (name.Equals("PingTimeoutMs", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var pt))
                d.PingTimeoutMs = Math.Clamp(pt, 500, 8000);
            else if (name.Equals("LatencyWarnMs", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var lw))
                d.LatencyWarnMs = lw;
            else if (name.Equals("LatencyFailMs", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var lf))
                d.LatencyFailMs = lf;
            else if (name.Equals("SpeedTestSeconds", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var ss))
                d.SpeedTestSeconds = Math.Clamp(ss, 3, 30);
            else if (name.Equals("SpeedTestConnections", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var sc))
                d.SpeedTestConnections = Math.Clamp(sc, 1, 8);
            else if (name.Equals("SpeedTestCapMb", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var sm))
                d.SpeedTestCapMb = sm;
            else if (name.Equals("SpeedTestUrl", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                d.SpeedTestUrl = p.Value.GetString() ?? "";
        }
    }

    private sealed class StoreDto
    {
        public string LastSchool { get; set; } = "";
        public string LastGrade { get; set; } = "";
        public string LastClassName { get; set; } = "";
        public string LastReporter { get; set; } = "";
        public string LastDeviceTag { get; set; } = "";
    }
}
