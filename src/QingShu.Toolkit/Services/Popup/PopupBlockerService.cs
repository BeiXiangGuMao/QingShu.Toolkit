using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
///     2) 前台窗口（正在使用）永不拦截
///     3) 系统外壳窗口类（托盘/桌面/输入）永不拦截
///     4) 工具窗口(WS_EX_TOOLWINDOW，多为提示/悬浮条)不拦截，避免误伤输入法等
///     5) UWP 宿主 ApplicationFrameHost 在白名单内
/// </summary>
public sealed class PopupBlockerService : IPopupBlockerService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly object _lock = new();
    private readonly HashSet<long> _knownWindows = new();
    private readonly List<(DateTime, string, string)> _recent = new();
    private IntPtr _hook;
    private NativeMethods.WinEventDelegate? _hookProc;
    private System.Threading.Timer? _timer;
    private int _ownPid;
    private DateTime _day = DateTime.Today;
    private int _blocked;

    public bool Enabled { get; private set; }
    public int BlockedCount => _blocked;
    public event Action<int>? Blocked;
    public IReadOnlyList<(DateTime Time, string Title, string Process)> Recent => _recent;

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
            _log.App(LogLevel.Info, "popup", $"弹窗拦截已启动（全局白名单策略，白名单 {_settings.Current.PopupWhitelist.Count} 项）");
        }
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        _settings.Current.PopupBlockEnabled = enabled;
        _ = _settings.SaveAsync();
        if (enabled) SnapshotExistingWindows(); // 重新启用时以当前窗口为新基线
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
            if (DateTime.Today != _day) { _day = DateTime.Today; Interlocked.Exchange(ref _blocked, 0); Blocked?.Invoke(0); }
            if (!Enabled) return;
            Scan();
        }
        catch { }
    }

    private void Scan()
    {
        var whitelist = _settings.Current.PopupWhitelist;
        var foreground = NativeMethods.GetForegroundWindowSafe();
        foreach (var w in NativeMethods.GetVisibleWindows(_ownPid))
        {
            bool isNew;
            lock (_lock) isNew = _knownWindows.Add(w.Handle.ToInt64());
            if (!isNew) continue;

            try
            {
                // 护栏 1：前台窗口永不拦
                if (w.Handle == foreground) continue;
                // 护栏 2：系统外壳类 / 工具窗口不拦
                if (NativeMethods.IsSystemShellWindow(w)) continue;
                if (w.IsToolWindow) continue;
                // 护栏 3：进程白名单
                if (IsWhitelisted(w.ProcessName, whitelist)) continue;
                // 护栏 4：无标题且无类名的异常窗口不碰
                if (string.IsNullOrWhiteSpace(w.Title) && string.IsNullOrWhiteSpace(w.ClassName)) continue;

                // —— 执行拦截（全局白名单策略：非白名单新窗口一律关闭） ——
                if (NativeMethods.TryCloseWindow(w.Handle))
                {
                    Interlocked.Increment(ref _blocked);
                    lock (_lock)
                    {
                        _recent.Insert(0, (DateTime.Now, w.Title, w.ProcessName));
                        if (_recent.Count > 200) _recent.RemoveAt(_recent.Count - 1);
                    }
                    _log.App(LogLevel.Warn, "popup", $"已拦截新窗口：\"{w.Title}\" class={w.ClassName} process={w.ProcessName}.exe pid={w.Pid}");
                    Blocked?.Invoke(_blocked);
                }
            }
            catch { }
        }
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
