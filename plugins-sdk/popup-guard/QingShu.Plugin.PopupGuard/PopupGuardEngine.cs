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
    private IntPtr _hookShow;
    private Win32Interop.WinEventDelegate? _hookProc;
    private Win32Interop.WinEventDelegate? _hookShowProc;
    private System.Threading.Timer? _timer;
    private int _ownPid;
    private DateTime _day = DateTime.Today;
    private int _blocked;

    /// <summary>前台新窗口宽限期：给正常启动的窗口留出展示时间，期满仍未关闭则照拦。</summary>
    private static readonly TimeSpan ForegroundGrace = TimeSpan.FromSeconds(2);
    // 事件驱动常态下的轮询克备间隔（与宿主内置 PopupBlockerService 同源同步）：
    // 钩子在位时 20 秒（防钩子丢事件 + 克备），存在宽限观察窗口时压回 3 秒（宽限满 2s 后快速拦截）；
    // 全钩子安装失败时恒 3 秒。
    private const int IdlePeriodMs = 20_000;
    private const int FallbackPeriodMs = 3_000;

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

    /// <summary>启动扫描（幂等：timer 非空即返回）。基线快照 + 双事件钩子 + 自适应轮询。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_timer != null) return;
            _ownPid = Process.GetCurrentProcess().Id;
            SnapshotExistingWindows();
            Enabled = _settings().Enabled;
            if (!Enabled) return; // 已停用：不装钩子不轮询（Toggle 开启时再 Start）
            // 双事件钩子：前台切换 + 窗口显示（后台弹出的广告不抢前台，事件驱动后即时拦）
            _hookProc = OnWinEvent;
            _hookShowProc = OnWinEvent;
            _hook = Win32Interop.HookForegroundEvent(_hookProc);
            _hookShow = Win32Interop.HookWindowShowEvent(_hookShowProc);
            bool hooked = _hook != IntPtr.Zero || _hookShow != IntPtr.Zero;
            _timer = new System.Threading.Timer(_ => SafeScan(), null, 3000, hooked ? IdlePeriodMs : FallbackPeriodMs);
            _log("info", $"弹窗拦截引擎已启动（事件驱动 + 全局白名单策略，白名单 {_settings().Whitelist.Count} 项"
                + (hooked ? $"；轮询克备 {(hooked ? IdlePeriodMs / 1000 : FallbackPeriodMs / 1000)} 秒）"
                          : "；事件钩子安装失败，退化为 3 秒轮询）"));
        }
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        try { Win32Interop.UnhookForegroundEvent(_hook); } catch { }
        try { Win32Interop.UnhookForegroundEvent(_hookShow); } catch { }
        lock (_lock) { _timer = null; _hook = IntPtr.Zero; _hookShow = IntPtr.Zero; }
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
        if (evt == Win32Interop.EVENT_SYSTEM_FOREGROUND)
        {
            if (idObject == 0) SafeScan();
            return;
        }
        // EVENT_OBJECT_SHOW 高频（tooltip/菜单都触发）：仅「不在基线内的顶层窗口」才升级为扫描，
        // 其余直接返回——回调侧先过滤是事件驱动开销可控的关键
        if (evt == Win32Interop.EVENT_OBJECT_SHOW
            && idObject == Win32Interop.OBJID_WINDOW
            && hwnd != IntPtr.Zero)
        {
            lock (_lock)
            {
                if (_knownWindows.Contains(hwnd.ToInt64())) return;
            }
            SafeScan();
        }
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
            // 基线快照：锁内一次性拷贝后传入增量枚举（与宿主内置同源）——扫描中 Allow()/拦截路径
            // 会写 _knownWindows，直接传引用会与枚举回调并发修改同一 HashSet；快照只读，
            // 新进基线的窗口下轮才跳过（可接受）
            ICollection<long> baseline;
            lock (_lock) baseline = new List<long>(_knownWindows);
            foreach (var w in Win32Interop.GetVisibleWindows(_ownPid, baseline))
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
                        // 已发过 WM_CLOSE 仍可见 → 顽固弹窗（忽略关闭消息），强制隐藏兜底；
                        // 同一弹窗只计一次数（隐藏动作不重复计数）。
                        // 隐藏失败不进基线（与宿主内置同源同步）：句柄已失效（下轮尾部清理回收）
                        // 或权限异常——保留 attempts 下轮重试，避免一次失败就永久漏拦
                        if (Win32Interop.HideWindow(w.Handle))
                        {
                            lock (_lock)
                            {
                                _attempts.Remove(handle);
                                _knownWindows.Add(handle);
                            }
                            _log("warn", $"窗口未响应关闭消息，已强制隐藏：\"{w.Title}\" class={w.ClassName} process={w.ProcessName}.exe pid={w.Pid}");
                        }
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

            // 轮询间隔自适应（与宿主内置同源）：宽限观察窗口在 → 3s；否则克备 20s；全钩子失败恒 3s
            AdjustTimerPeriod();
        }
    }

    /// <summary>按宽限观察态与钩子在位情况调整克备轮询周期（幂等：间隔未变时 Change 开销可忽略）。</summary>
    private void AdjustTimerPeriod()
    {
        System.Threading.Timer? t;
        bool anyHook;
        int graceCount;
        lock (_lock)
        {
            t = _timer;
            anyHook = _hook != IntPtr.Zero || _hookShow != IntPtr.Zero;
            graceCount = _grace.Count;
        }
        if (t == null) return;
        int period = !anyHook ? FallbackPeriodMs : (graceCount > 0 ? FallbackPeriodMs : IdlePeriodMs);
        try { t.Change(period, period); } catch { } // timer 可能刚被 Dispose
    }

    /// <summary>豁免类窗口放行并固化进基线。</summary>
    private void Allow(long handle)
    {
        lock (_lock) _knownWindows.Add(handle);
    }

    /// <summary>白名单匹配：进程名归一（去 .exe 转小写），支持前/后缀通配（如 wps* / *player）。</summary>
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
            if (item.StartsWith("*") && item.Length > 1 && name.EndsWith(item[1..], StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>清空最近拦截列表（视图「清空列表」按钮）。</summary>
    public void ClearRecent()
    {
        lock (_lock) _recent.Clear();
    }
}
