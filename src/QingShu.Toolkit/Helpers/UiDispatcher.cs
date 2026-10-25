using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace QingShu.Toolkit.Helpers;

/// <summary>
/// WinUI 3 桌面端 UI 线程默认没有 SynchronizationContext：
/// await 之后的代码、Progress&lt;T&gt; 回调、后台服务事件都会落在线程池线程上，
/// 一旦触碰 XAML 绑定属性立即抛 COMException 0x8001010E。
/// 本类提供两件武器：
///  1) DispatcherQueueSynchronizationContext —— App.OnLaunched 安装到 UI 线程，
///     使 ConfigureAwait(true) 恒定回到 UI 线程（全项目既有写法自动受益）；
///  2) UiDispatcher.Run —— 后台服务在"事件源侧"显式调度回 UI 线程。
/// </summary>
public static class UiDispatcher
{
    /// <summary>主窗口 UI 线程队列（App.OnLaunched 在 UI 线程上捕获）。</summary>
    public static DispatcherQueue? Queue { get; private set; }

    public static void Initialize(DispatcherQueue queue) => Queue = queue;

    /// <summary>当前调用是否已在 UI 线程（Queue 未初始化时按 true 处理，避免误吞）。</summary>
    public static bool HasThreadAccess => Queue?.HasThreadAccess ?? true;

    /// <summary>在 UI 线程执行动作；若已在 UI 线程则同步执行（可重入安全）。</summary>
    public static void Run(Action action)
    {
        var q = Queue;
        if (q is null || q.HasThreadAccess)
        {
            action();
            return;
        }
        q.TryEnqueue(() => action());
    }
}

/// <summary>
/// 基于 DispatcherQueue 的 SynchronizationContext。
/// 安装后：ConfigureAwait(true) 的后续、new Progress&lt;T&gt; 回调、
/// TaskScheduler.FromCurrentSynchronizationContext() 全部正确路由回 UI 线程。
/// </summary>
public sealed class DispatcherQueueSynchronizationContext : SynchronizationContext
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueSynchronizationContext(DispatcherQueue queue) => _queue = queue;

    public override void Post(SendOrPostCallback d, object? state)
        => _queue.TryEnqueue(() => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_queue.HasThreadAccess)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim(false);
        Exception? error = null;
        _queue.TryEnqueue(() =>
        {
            try { d(state); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public override SynchronizationContext CreateCopy() => this;
}
