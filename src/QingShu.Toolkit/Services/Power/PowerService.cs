using System;
using System.Threading;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;

namespace QingShu.Toolkit.Services.Power;

public enum PowerUiState { Eco, Balanced, High }

public sealed record PowerUpdate(PowerUiState State, bool IsOnBattery, string Description, string ModeText);

public interface IPowerService
{
    /// <summary>当前功耗标识状态（用于三色小标识）。</summary>
    PowerUiState Current { get; }
    bool IsOnBattery { get; }
    event Action<PowerUpdate>? Changed;
    void Start();
    /// <summary>切换模式（auto/balanced/performance/eco），写入配置。</summary>
    void SetMode(string mode);
}

/// <summary>
/// 功耗管理服务 —— “自动高功耗/低功耗切换”：
///  · auto 模式：接通电源 → 高性能；使用电池 → 低功耗（EcoQoS）
///  · 本程序在低功耗态自动应用 EcoQoS（PROCESS_POWER_THROTTLING），高性能态解除
///  · 状态仅用于状态栏三色小标识：绿=低功耗 / 蓝=平衡 / 红=高性能（禁止大面积色块）
/// </summary>
public sealed class PowerService : IPowerService, IDisposable
{
    private readonly ISettingsService _settings;
    private Timer? _timer;
    private bool _started;

    public PowerUiState Current { get; private set; } = PowerUiState.Balanced;
    public bool IsOnBattery { get; private set; }
    public event Action<PowerUpdate>? Changed;

    public PowerService(ISettingsService settings)
    {
        _settings = settings;
        _settings.Changed += Recompute;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Recompute();
        // 轮询兜底（WinRT 电源事件在部分 Win10 机型上不可靠）
        _timer = new Timer(_ => Recompute(), null, 10_000, 10_000);
    }

    public void SetMode(string mode)
    {
        _settings.Current.PowerMode = mode;
        _ = _settings.SaveAsync();
        Recompute();
    }

    private void Recompute()
    {
        bool onBattery = false;
        bool known = NativeMethods.TryGetPowerStatus(out var acOnline, out _);
        if (known) onBattery = !acOnline;

        var mode = _settings.Current.PowerMode;
        PowerUiState next;
        switch (mode)
        {
            case "performance": next = PowerUiState.High; break;
            case "eco": next = PowerUiState.Eco; break;
            case "balanced": next = PowerUiState.Balanced; break;
            default: // auto
                if (!known) next = PowerUiState.Balanced;
                else next = onBattery ? PowerUiState.Eco : PowerUiState.High;
                break;
        }

        IsOnBattery = onBattery;
        bool stateChanged = next != Current;
        Current = next;
        ApplyEcoQos(next);

        if (stateChanged)
        {
            var modeText = mode switch
            {
                "performance" => "手动·高性能",
                "eco" => "手动·低功耗",
                "balanced" => "手动·平衡",
                _ => "自动切换"
            };
            var desc = next switch
            {
                PowerUiState.High => onBattery ? "高性能（电池供电，建议接通电源）" : "高性能（已接通电源）",
                PowerUiState.Eco => "低功耗（已应用 EcoQoS 节能）",
                _ => "平衡"
            };
            var update = new PowerUpdate(next, onBattery, desc, modeText);
            // Timer 回调在线程池线程：事件必须在“源侧”调度回 UI 线程，
            // 否则订阅方更新绑定属性时抛 COMException 0x8001010E
            UiDispatcher.Run(() => Changed?.Invoke(update));
        }
    }

    private void ApplyEcoQos(PowerUiState state)
    {
        if (!_settings.Current.ApplyEcoQosToSelf) return;
        bool enable = state == PowerUiState.Eco;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            NativeMethods.SetProcessEcoQos(p.Id, enable);
        }
        catch { }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _settings.Changed -= Recompute;
    }
}
