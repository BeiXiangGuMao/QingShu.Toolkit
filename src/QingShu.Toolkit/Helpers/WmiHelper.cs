using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Helpers;

/// <summary>
/// WMI 查询封装：全部在后台线程执行，避免卡 UI。
/// </summary>
public static class WmiHelper
{
    /// <summary>WMI 枚举统一 20 秒硬超时：ManagementObjectSearcher 默认无超时，个别慢类
    /// （Win32_PnPSignedDriver / Win32_PnPEntity 全表枚举）在低配机/杀软环境可能挂数十秒，
    /// 超出诊断逐项硬超时后遗留任务变孤儿继续占线程。枚举超时抛异常 → 外层 catch 返回已
    /// 获取部分，检查项按「信息不可得」降级，孤儿任务最迟 20 秒自终止（内存防御一环）。</summary>
    private static EnumerationOptions EnumOpts()
        => new() { Timeout = TimeSpan.FromSeconds(20) };

    public static Task<List<T>> QueryAsync<T>(string wql, Func<ManagementObject, T> selector)
        => Task.Run(() =>
        {
            var list = new List<T>();
            try
            {
                using var searcher = new ManagementObjectSearcher(new ObjectQuery(wql), EnumOpts());
                foreach (var o in searcher.Get())
                {
                    if (o is ManagementObject mo)
                    {
                        try { list.Add(selector(mo)); }
                        catch { }
                        mo.Dispose();
                    }
                }
            }
            catch { }
            return list;
        });

    public static Task<string> FirstValueAsync(string wql, string property)
        => Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(new ObjectQuery(wql), EnumOpts());
                foreach (var o in searcher.Get())
                {
                    if (o is ManagementObject mo)
                    {
                        try
                        {
                            var v = mo[property]?.ToString();
                            mo.Dispose();
                            if (!string.IsNullOrWhiteSpace(v)) return v;
                        }
                        catch { mo.Dispose(); }
                    }
                }
            }
            catch { }
            return "";
        });
}
