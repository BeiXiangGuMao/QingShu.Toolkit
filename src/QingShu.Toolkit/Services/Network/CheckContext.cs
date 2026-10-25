using System;
using System.Collections.Generic;
using System.Threading;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 诊断共享上下文：一次会话内的网卡快照 / Wi-Fi 信息 / 设置 / 会话日志 /
/// 以及检查项之间共享的结论（如“外网是否可达”供认证过期判定联动）。
/// </summary>
public sealed class CheckContext
{
    public required AppSettings Settings { get; init; }
    public required ISessionLog Log { get; init; }
    public CancellationToken Ct { get; init; }

    public AdapterSnapshot? Primary { get; set; }
    public List<AdapterSnapshot> Adapters { get; set; } = new();
    public WifiSnapshot? Wifi { get; set; }

    public int PingCount => Math.Clamp(Settings.PingCount, 1, 10);
    public int TimeoutMs => Math.Clamp(Settings.PingTimeoutMs, 500, 8000);

    private readonly Dictionary<string, object> _shared = new();

    public void SetShared<T>(string key, T value) => _shared[key] = value!;
    public T? GetShared<T>(string key) => _shared.TryGetValue(key, out var v) && v is T t ? t : default;
    public bool SharedBool(string key) => GetShared<bool>(key);
}

/// <summary>单项检查接口。所有检查实现均为无状态，可安全复用。</summary>
public interface ICheck
{
    string Id { get; }
    string Name { get; }
    CheckCategory Category { get; }
    /// <summary>是否纳入“快速体检”（约 30-60 秒档）。</summary>
    bool InQuickMode { get; }
    Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct);
}

/// <summary>检查实现基类（日志辅助）。</summary>
public abstract class CheckBase : ICheck
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract CheckCategory Category { get; }
    public virtual bool InQuickMode => false;
    public abstract Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct);

    protected void Log(CheckContext ctx, string message, object? data = null)
        => ctx.Log.Write(LogLevel.Debug, $"check.{Id}", message, data);
}
