using System;
using System.IO;
using System.Threading;

namespace QingShu.Toolkit;

/// <summary>
/// 自定义入口（DISABLE_XAML_GENERATED_MAIN）：
/// 崩溃兜底日志 + 单实例互斥 → 启动 WinUI 应用。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }
        catch { }

        using var mutex = new Mutex(true, @"Local\QingShu.Toolkit.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Interop.NativeMethods.ShowMessageBox("青枢工具箱已经在运行中，请查看任务栏。", "青枢工具箱", 0x40);
            return 0;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ => new App());
        return 0;
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingShu.Toolkit");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n\n");
        }
        catch { }
    }
}
