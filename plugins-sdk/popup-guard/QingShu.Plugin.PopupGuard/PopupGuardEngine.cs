using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace QingShu.Plugin.PopupGuard;

/// <summary>引擎配置快照（每次扫描时从插件设置层拉取，改白名单/开关即时生效）。</summary>
internal sealed record PopupGuardSettings(bool Enabled, IReadOnlyList<string> Whitelist);

/// <summary>一条拦截记录（时间 / 窗口标题 / 进程名）。</summary>
public sealed record BlockedEntry(DateTime Time, string Title, string Process);

/// <summary>
/// 被动弹窗拦截引擎（从宿主内置 PopupBlockerService 移植，语义保持一致）：
///  · 全局白名单策略：白名单进程放行，其余新出现的可见顶层窗口一律关闭
///  · 被动原则：不做注入、不挂钩 DLL，仅扫描可见顶层窗口并 PostMessage(WM_CLOSE)
///  · 安全护栏（与内置实现逐条对应）：
///     1) 启动时已存在的窗口一律放行（只拦「新出现」的窗口）
///     2) 前台新窗口仅宽限 2 秒（弹出即抢焦点是弹窗常态，不能豁免）——宽限满或失去前台后立即拦截
///     3) 系统外壳窗口类（托盘/桌面/输入）永不拦截
///     4) 工具窗口(WS_EX_TOOLWINDOW)不拦截，避免误伤输入法等
///     5) UWP 宿主 ApplicationFrameHost 在默认白名单内
///  · 顽固弹窗：WM_CLOSE 被忽略时下一轮强制 SW_HIDE 隐藏（同一弹窗只计一次数）
///  引擎完全脱离 UI 调度：拦截计数/列表由视图层定时轮询拉取（1s 粒度）。
/// </summary>
internal sealed class PopupGuardEngine : IDisposable
{
    private readonly Func<PopupGuardSettings> _settings;
    private readonly Action<string, string> _log; // (level, message)
    private readonly object _lock = new();
    // 扫描串行化：前台切换事件回调（UI 线程消息泵）与 3 秒 timer 回调（线程池）可并发进入，
    // 拦截状态机（尝试次数/宽限）必须全程串行，否则同一新窗口会被双发 WM_CLOSE、双计数
    private readonly object _scanLock = new();
    private readonly HashSet<long> _knownWindows = new();
    // 已发过 WM_CLOSE 仍在的窗口 → 视为顽固弹窗，下轮 SW_HIDE 兜底
    private readonly Dictionary<long, byte> _attempts = new();
    // 前台新窗口宽限起点（弹出即抢焦点属常态，只延迟判定不豁免）
    private readonly Dictionary<long, DateTime> _grace = new();
    private readonly List<BlockedEntry> _recent = new();
    private IntPtr _hook;
    private Win32Interop.WinEventDelegate? _hookProc;
    private System.Threading.Timer? _timer;
    private int _ownPid;
    private DateTime _day = DateTime.Today;
    private int _blocked;

    /// <summary>前台新窗口宽限期：给正常启动的窗口留出展示时间，期满仍未关闭则照拦。</summary>
    private static readonly TimeSpan ForegroundGrace = TimeSpan.FromSeconds(2);

    public bool Enabled { get; private set; }
    public int BlockedCount => _blocked;

    /// <summary>快照返回：扫描线程写、UI 线程读，List 非线程安全必须拷贝。</summary>
    public IReadOnlyList<BlockedEntry> Recent
    {
        get { lock (_lock) return _recent.ToList(); }
    }

    public PopupGuardEngine(Func<PopupGuardSettings> settings, Action<string, string> log)
    {
        _settings = settings;
        _log = log;
        Enabled = settings().Enabled;
    }

    /// <summary>启动扫描（幂等：timer 非空即返回）。基线快照 + 前台钩子 + 3 秒轮询。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_timer != null) return;
            _ownPid = Process.GetCurrentProcess().Id;
            SnapshotExistingWindows();
            Enabled = _settings().Enabled;
            if (!Enabled) return; // 已停用：不装钩子不轮询（Toggle 开启时再 Start）
            _hookProc = OnWinEvent;
            _hook = Win32Interop.HookForegroundEvent(_hookProc);
            _timer = new System.Threading.Timer(_ => SafeScan(), null, 3000, 3000);
            _log("info", $"弹窗拦截引擎已启动（全局白名单策略，白名单 {_settings().Whitelist.Count} 项"
                + (_hook != IntPtr.Zero ? "" : "；前台切换钩子安装失败，退化为 3 秒轮询") + "）");
        }
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        try { Win32Interop.UnhookForegroundEvent(_hook); } catch { }
        lock (_lock) { _timer = null; _hook = IntPtr.Zero; }
    }

    /// <summary>开关切换（视图层调用）：重拍基线，按需启停扫描。</summary>
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (enabled)
        {
            lock (_lock)
            {
                SnapshotExistingWindows(); // 重新启用时以当前窗口为新基线
                _attempts.Clear();         // 观察态一并作废：基线已重拍，旧窗口不应再被追拦
                _grace.Clear();
            }
            if (_timer == null) Start();
        }
        _log("info", $"弹窗拦截引擎{(enabled ? "已开启" : "已关闭")}");
    }

    private void SnapshotExistingWindows()
    {
        lock (_lock)
        {
            _knownWindows.Clear();
            foreach (var w in Win32Interop.GetVisibleWindows(_ownPid))
                _knownWindows.Add(w.Handle.ToInt64());
        }
    }

    private void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == Win32Interop.EVENT_SYSTEM_FOREGROUND && idObject == 0)
            SafeScan();
    }

    private void SafeScan()
    {
        try
        {
            if (DateTime.Today != _day) { _day = DateTime.Today; Interlocked.Exchange(ref _blocked, 0); }
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
            var settings = _settings();
            if (!settings.Enabled) return;
            var whitelist = settings.Whitelist;
            var foreground = Win32Interop.GetForegroundWindowSafe();
            var seen = new HashSet<long>();
            foreach (var w in Win32Interop.GetVisibleWindows(_ownPid))
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
                    if (Win32Interop.IsSystemShellWindow(w) || w.IsToolWindow) { Allow(handle); continue; }
                    if (IsWhitelisted(w.ProcessName, whitelist)) { Allow(handle); continue; }
                    if (string.IsNullOrWhiteSpace(w.Title) && string.IsNullOrWhiteSpace(w.ClassName)) { Allow(handle); continue; }

                    // 前台宽限：新窗口正处前台时只延迟判定（弹出即抢焦点是弹窗常态），
                    // 宽限满或用户切走后立即拦截；绝不在此进基线——抢焦点弹窗被永久跳过是历史根因
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
                        if (Win32Interop.TryCloseWindow(w.Handle))
                        {
                            lock (_lock) _attempts[handle] = 1; // 不进基线：下轮复核是否真被关掉
                            Interlocked.Increment(ref _blocked);
                            lock (_lock)
                            {
                                _recent.Insert(0, new BlockedEntry(DateTime.Now, w.Title, w.ProcessName));
                                if (_recent.Count > 200) _recent.RemoveAt(_recent.Count - 1);
                            }
                            _log("warn", $"已拦截新窗口：\"{w.Title}\" class={w.ClassName} process={w.ProcessName}.exe pid={w.Pid}");
                        }
                        // PostMessage 失败（句柄即将失效等）：保持观察，下轮重试
                    }
                    else
                    {
                        // 已发过 WM_CLOSE 仍可见 → 顽固弹窗（忽略关闭消息），强制隐藏兜底并固化进基线；
                        // 同一弹窗只计一次数（隐藏动作不重复计数）
                        Win32Interop.HideWindow(w.Handle);
                        lock (_lock)
                        {
                            _attempts.Remove(handle);
                            _knownWindows.Add(handle);
                        }
                        _log("warn", $"窗口未响应关闭消息，已强制隐藏：\"{w.Title}\" class={w.ClassName} process={w.ProcessName}.exe pid={w.Pid}");
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

    /// <summary>白名单匹配：进程名归一（去 .exe 转小写），支持前缀通配（如 wps*）。</summary>
    private static bool IsWhitelisted(string processName, IReadOnlyList<string> whitelist)
    {
        var name = processName.Trim().ToLowerInvariant().Replace(".exe", "");
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var w in whitelist)
        {
            var item = w.Trim().ToLowerInvariant().Replace(".exe", "");
            if (item.Length == 0) continue;
            if (name == item) return true;
            if (item.EndsWith("*") && name.StartsWith(item[..^1], StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>清空最近拦截列表（视图「清空列表」按钮）。</summary>
    public void ClearRecent()
    {
        lock (_lock) _recent.Clear();
    }
}
