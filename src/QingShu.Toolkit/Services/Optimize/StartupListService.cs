using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace QingShu.Toolkit.Services.Optimize;

public sealed record StartupEntry(string Name, string Command, string Source);

/// <summary>
/// 启动项查看（只读！）：遵循设计边界，本工具不代改启动项，
/// 仅展示来源与命令，一键跳转任务管理器由管理员自行处理。
/// </summary>
public interface IStartupListService
{
    Task<List<StartupEntry>> GetAsync();
}

public sealed class StartupListService : IStartupListService
{
    public Task<List<StartupEntry>> GetAsync() => Task.Run(() =>
    {
        var list = new List<StartupEntry>();
        void ReadRun(RegistryKey baseKey, string source)
        {
            try
            {
                using var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key == null) return;
                foreach (var name in key.GetValueNames())
                {
                    var cmd = key.GetValue(name)?.ToString() ?? "";
                    if (cmd.Length > 0) list.Add(new StartupEntry(name, cmd, source));
                }
            }
            catch { }
        }
        ReadRun(Registry.CurrentUser, "HKCU\\...\\Run");
        ReadRun(Registry.LocalMachine, "HKLM\\...\\Run");

        try
        {
            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                     })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.EnumerateFiles(folder))
                    list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(f), f, "启动文件夹"));
            }
        }
        catch { }
        return list.OrderBy(x => x.Source).ThenBy(x => x.Name).ToList();
    });
}
