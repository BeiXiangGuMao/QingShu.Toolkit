using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Popup;

public interface IPopupBlockerService
{
    bool Enabled { get; }
    /// <summary>本会话累计拦截次数（按天归零）</summary>
    int BlockedCount { get; }
    event Action<int>? Blocked;
    void Start();
    void SetEnabled(bool enabled);
    /// <summary>重新加载白名单（设置变更后调用）。</summary>
    void ReloadWhitelist();
    /// <summary>最近拦截记录（时间 / 标题 / 进程）。</summary>
    IReadOnlyList<(DateTime Time, string Title, string Process)> Recent { get; }
}

/// <summary>
/// 被动弹窗拦截 —— 全局白名单策略（按需求采用最激进档）：
///  · 只放行“白名单进程”的系统与教学/办公软件窗口，其余新出现的可见窗口一律关闭
///  · 被动原则：不做注入、不挂钩 DLL，仅扫描可见顶层窗口并 PostMessage(WM_CLOSE)
///  · 安全护栏：
///     1) 启动时已存在的窗口一律放行（只拦“新出现”的窗口）
///     2) 前台新窗口仅宽限 2 秒（弹出即抢焦点是弹窗常态，不能豁免，否则永远拦不到）——
///        宽限满或失去前台后立即拦截；旧版“前台永不拦且进基线”导致抢焦点弹窗被永久放行
///     3) 系统外壳窗口类（托盘/桌面/输入）永不拦截
///     4) 工具窗口(WS_EX_TOOLWINDOW，多为提示/悬浮条)不拦截，避免误伤输入法等
///     5) UWP 宿主 ApplicationFrameHost 在白名单内
///  · 顽固弹窗：WM_CLOSE 被忽略时下一轮强制 SW_HIDE 隐藏（同一弹窗只计一次数）
/// </summary>
public sealed class PopupBlockerService : IPopupBlockerService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly object _lock = new();
    // 扫描串行化：前台切换事件回调（UI 线程消息泵）与 3 秒 timer 回调（线程池）可并发进入，
    // 拦截状态机（尝试次数/宽限）必须全程串行，否则同一新窗口会被双发 WM_CLOSE、双计数
    private readonly object _scanLock = new();
    private readonly HashSet<long> _knownWindows = new();
    // 已发过 WM_CLOSE 仍在的窗口 → 视为顽固弹窗，下轮 SW_HIDE 兜底
    private readonly Dictionary<long, byte> _attempts = new();
    // 前台新窗口宽限起点（弹出即抢焦点属常态，只延迟判定不豁免）
    private readonly Dictionary<long, DateTime> _grace = new();
    private readonly List<(DateTime, string, string)> _recent = new();
    private IntPtr _hook;
    private NativeMethods.WinEventDelegate? _hookProc;
    private System.Threading.Timer? _timer;
    private int _ownPid;
    private DateTime _day = DateTime.Today;
    private int _blocked;

    /// <summary>前台新窗口宽限期：给正常启动的窗口留出展示时间，期满仍未关闭则照拦。</summary>
    private static readonly TimeSpan ForegroundGrace = TimeSpan.FromSeconds(2);

    public bool Enabled { get; private set; }
    public int BlockedCount => _blocked;
    public event Action<int>? Blocked;

    /// <summary>快照返回：扫描线程写、UI 线程读，List 非线程安全必须拷贝。</summary>
    public IReadOnlyList<(DateTime Time, string Title, string Process)> Recent
    {
        get { lock (_lock) return _recent.ToList(); }
    }

    public PopupBlockerService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
        Enabled = settings.Current.PopupBlockEnabled;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_timer != null) return;
            _ownPid = Process.GetCurrentProcess().Id;
            SnapshotExistingWindows();
            // 前台切换事件钩子：即时响应（被动监听，不注入）
            _hookProc = OnWinEvent;
            _hook = NativeMethods.HookForegroundEvent(_hookProc);
            _timer = new System.Threading.Timer(_ => SafeScan(), null, 3000, 3000);
            _log.App(LogLevel.Info, "popup", _hook != IntPtr.Zero
                ? $"弹窗拦截已启动（全局白名单策略，白名单 {_settings.Current.PopupWhitelist.Count} 项）"
                : $"弹窗拦截已启动（前台切换钩子安装失败，退化为 3 秒轮询；白名单 {_settings.Current.PopupWhitelist.Count} 项）");
        }
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        _settings.Current.PopupBlockEnabled = enabled;
        _ = _settings.SaveAsync();
        if (enabled)
        {
            lock (_lock)
            {
                SnapshotExistingWindows(); // 重新启用时以当前窗口为新基线
                _attempts.Clear();         // 观察态一并作废：基线已重拍，旧窗口不应再被追拦
                _grace.Clear();
            }
        }
        _log.App(LogLevel.Info, "popup", $"弹窗拦截{(enabled ? "已开启" : "已关闭")}");
    }

    public void ReloadWhitelist() { /* 白名单在每次扫描时从设置读取，无需缓存清理 */ }

    private void SnapshotExistingWindows()
    {
        lock (_lock)
        {
            _knownWindows.Clear();
            foreach (var w in NativeMethods.GetVisibleWindows(_ownPid))
                _knownWindows.Add(w.Handle.ToInt64());
        }
    }

    private void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == NativeMethods.EVENT_SYSTEM_FOREGROUND && idObject == 0)
            SafeScan();
    }

    private void SafeScan()
    {
        try
        {
            if (DateTime.Today != _day) { _day = DateTime.Today; Interlocked.Exchange(ref _blocked, 0); UiDispatcher.Run(() => Blocked?.Invoke(0)); }
            if (!Enabled) return;
            Scan();
        }
        catch { }
    }

    private void Scan()
    {
        // 全程串行：WinEvent 回调（UI 线程）与 timer 回调（线程池）并发扫描会双发关闭消息、双计数
        lock (_scanLock)
        {
            var whitelist = _settings.Current.PopupWhitelist;
            var foreground = NativeMethods.GetForegroundWindowSafe();
            var seen = new HashSet<long>();
            foreach (var w in NativeMethods.GetVisibleWindows(_ownPid))
            {
                var handle = w.Handle.ToInt64();
                seen.Add(handle);
                lock (_lock)
                {
                    if (_knownWindows.Contains(handle)) continue; // 已处理完毕（基线）窗口跳过
                }

                try
                {
                    // 豁免类：系统外壳 / 工具窗口 / 白名单进程 / 无标题无类名的异常窗口
                    // → 放行并固化进基线（此后不再重判）
                    if (NativeMethods.IsSystemShellWindow(w) || w.IsToolWindow) { Allow(handle); continue; }
                    if (IsWhitelisted(w.ProcessName, whitelist)) { Allow(handle); continue; }
                    if (string.IsNullOrWhiteSpace(w.Title) && string.IsNullOrWhiteSpace(w.ClassName)) { Allow(handle); continue; }

                    // 前台宽限：新窗口正处前台时只延迟判定（弹出即抢焦点是弹窗常态），
                    // 宽限满或用户切走后立即拦截；绝不在此进基线——旧版在此放行且窗口已入基线，
                    // 导致抢焦点弹窗此后被永久跳过（「拦截失效」根因）
                    if (w.Handle == foreground)
                    {
                        lock (_lock)
                        {
                            if (!_grace.TryGetValue(handle, out var first))
                            {
                                _grace[handle] = DateTime.UtcNow;
                                continue;
                            }
                            if (DateTime.UtcNow - first < ForegroundGrace) continue; // 宽限未满，保持观察
                            _grace.Remove(handle);
                        }
                    }

                    // —— 执行拦截（全局白名单策略：非白名单新窗口一律关闭） ——
                    byte attempts;
                    lock (_lock)
                    {
                        if (!_attempts.TryGetValue(handle, out attempts)) attempts = 0;
                    }

                    if (attempts == 0)
                    {
                        if (NativeMethods.TryCloseWindow(w.Handle))
                        {
                            lock (_lock) _attempts[handle] = 1; // 不进基线：下轮复核是否真被关掉
                            Interlocked.Increment(ref _blocked);
                            lock (_lock)
                            {
                                _recent.Insert(0, (DateTime.Now, w.Title, w.ProcessName));
                                if (_recent.Count > 200) _recent.RemoveAt(_recent.Count - 1);
                            }
                            _log.App(LogLevel.Warn, "popup", $"已拦截新窗口：\"{w.Title}\" class={w.ClassName} process={w.ProcessName}.exe pid={w.Pid}");
                            var count = _blocked; // timer/WinEvent 回调线程，事件需调度回 UI 线程
                            UiDispatcher.Run(() => Blocked?.Invoke(count));
                        }
                        // PostMessage 失败（句柄即将失效等）：保持观察，下轮重试
                    }
                    else
                    {
                        // 已发过 WM_CLOSE 仍可见 → 顽固弹窗（忽略关闭消息），强制隐藏兜底并固化进基线；
                        // 同一弹窗只计一次数（隐藏动作不重复计数）
                        NativeMethods.HideWindow(w.Handle);
                        lock (_lock)
                        {
                            _attempts.Remove(handle);
                            _knownWindows.Add(handle);
                        }
                        _log.App(LogLevel.Warn, "popup", $"窗口未响应关闭消息，已强制隐藏：\"{w.Title}\" class={w.ClassName} process={w.ProcessName}.exe pid={w.Pid}");
                    }
                }
                catch { }
            }

            // 尾部清理：已消失窗口的宽限/重试悬挂条目（防止字典随时间无限增长）
            lock (_lock)
            {
                if (_grace.Count > 0)
                    foreach (var h in _grace.Keys.ToList())
                        if (!seen.Contains(h)) _grace.Remove(h);
                if (_attempts.Count > 0)
                    foreach (var h in _attempts.Keys.ToList())
                        if (!seen.Contains(h)) _attempts.Remove(h);
            }
        }
    }

    /// <summary>豁免类窗口放行并固化进基线。</summary>
    private void Allow(long handle)
    {
        lock (_lock) _knownWindows.Add(handle);
    }

    private static bool IsWhitelisted(string processName, List<string> whitelist)
    {
        var name = processName.Trim().ToLowerInvariant().Replace(".exe", "");
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var w in whitelist)
        {
            var item = w.Trim().ToLowerInvariant().Replace(".exe", "");
            if (item.Length == 0) continue;
            if (name == item) return true;
            // 支持前缀通配（如 wps*）
            if (item.EndsWith("*") && name.StartsWith(item[..^1], StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        NativeMethods.UnhookForegroundEvent(_hook);
    }
}
