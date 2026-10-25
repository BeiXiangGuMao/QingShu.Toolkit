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
    public static Task<List<T>> QueryAsync<T>(string wql, Func<ManagementObject, T> selector)
        => Task.Run(() =>
        {
            var list = new List<T>();
            try
            {
                using var searcher = new ManagementObjectSearcher(wql);
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
                using var searcher = new ManagementObjectSearcher(wql);
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
