using System;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services;

/// <summary>
/// 窗口材质策略（Win10 优先适配）：
///  · Win11（Build 22000+）：Mica
///  · Win10：自动回退 DesktopAcrylic（深色模式下提高不透明度，保证 WCAG 可读性）
///  · 均不支持：无材质（保持不透明页面背景）
/// </summary>
public sealed class WindowBackdropService : IDisposable
{
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _config;
    private ICompositionSupportsSystemBackdrop? _target;
    private bool _usingMica;
    private bool _attached;

    public enum BackdropKind { None, Mica, Acrylic }

    public BackdropKind Kind { get; private set; } = BackdropKind.None;

    public bool TryAttach(Window window)
    {
        if (_attached) return Kind != BackdropKind.None;
        try
        {
            _target = window.As<ICompositionSupportsSystemBackdrop>();
            _config = new SystemBackdropConfiguration { IsInputActive = true };
            window.Activated += (_, e) =>
            {
                if (_config != null) _config.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
            };
            window.Closed += (_, _) => Dispose();

            var build = Environment.OSVersion.Version.Build;
            if (build >= 22000 && MicaController.IsSupported())
            {
                _mica = new MicaController();
                _mica.AddSystemBackdropTarget(_target!);
                _mica.SetSystemBackdropConfiguration(_config);
                _usingMica = true;
                Kind = BackdropKind.Mica;
                _attached = true;
                return true;
            }
            if (DesktopAcrylicController.IsSupported())
            {
                // WinUI 3 的 Window 无 ActualTheme（那是 FrameworkElement 属性），用应用级主题判定
                CreateAcrylic(Application.Current.RequestedTheme == ApplicationTheme.Dark);
                _acrylic!.AddSystemBackdropTarget(_target!);
                _acrylic.SetSystemBackdropConfiguration(_config);
                Kind = BackdropKind.Acrylic;
                _attached = true;
                return true;
            }
        }
        catch { }
        Kind = BackdropKind.None;
        return false;
    }

    private void CreateAcrylic(bool dark)
    {
        _acrylic?.RemoveAllSystemBackdropTargets();
        _acrylic?.Dispose();
        _acrylic = new DesktopAcrylicController();
        if (dark)
        {
            // 深色：提高不透明度（TintOpacity 越大越实），避免文字对比度不足
            _acrylic.TintColor = Color.FromArgb(255, 28, 28, 30);   // #1C1C1E
            _acrylic.TintOpacity = 0.85f;
            _acrylic.FallbackColor = Color.FromArgb(235, 28, 28, 30);
        }
        else
        {
            _acrylic.TintColor = Color.FromArgb(255, 242, 242, 247); // #F2F2F7
            _acrylic.TintOpacity = 0.72f;
            _acrylic.FallbackColor = Color.FromArgb(230, 242, 242, 247);
        }
    }

    /// <summary>主题切换时重建亚克力参数；Mica 由系统随配置自动适配。</summary>
    public void UpdateTheme(ElementTheme theme)
    {
        if (_config != null)
            _config.Theme = theme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (!_usingMica && Kind == BackdropKind.Acrylic && _acrylic != null)
            CreateAcrylic(theme == ElementTheme.Dark);
    }

    /// <summary>页面根背景应使用半透明笔刷让材质透出。</summary>
    public static Brush? TryGetPageBackground(AppSettings settings, BackdropKind kind, bool dark)
    {
        if (!settings.UseAcrylicBackdrop || kind == BackdropKind.None) return null; // 回退不透明
        return ResolveThemeBrush("PageBackgroundAcrylicBrush", dark);
    }

    /// <summary>
    /// 按「元素实际主题」解析主题字典中的画刷。
    /// 注意：Application.Resources[key] 只按应用级（=系统）主题解析 ThemeDictionaries，
    /// 应用内经 RootGrid.RequestedTheme 切换浅/深后它仍返回系统主题变体 ——
    /// 系统深色 + 应用内浅色时会拿到深色刷铺底，左侧栏等透明区域随之发黑。
    /// 此处显式按 dark 参数取对应主题字典（Palette.xaml 经 MergedDictionaries 合入，
    /// 其 ThemeDictionaries 位于合并字典内部，需逐层查找）。
    /// </summary>
    public static Brush? ResolveThemeBrush(string key, bool dark)
    {
        try
        {
            var themeKey = dark ? "Dark" : "Light";
            var appRes = Application.Current.Resources;

            if (TryFromThemeDictionary(appRes, themeKey, key, out var brush)) return brush;
            foreach (var merged in appRes.MergedDictionaries)
            {
                if (TryFromThemeDictionary(merged, themeKey, key, out brush)) return brush;
            }
            // 兜底：定义在主题字典之外的普通资源
            var map = (System.Collections.Generic.IDictionary<object, object>)appRes;
            return map.TryGetValue(key, out var raw) && raw is Brush plain ? plain : null;
        }
        catch { return null; }
    }

    private static bool TryFromThemeDictionary(ResourceDictionary rd, string themeKey, string key, out Brush? brush)
    {
        brush = null;
        if (rd.ThemeDictionaries.TryGetValue(themeKey, out var themeObj) &&
            themeObj is ResourceDictionary themeDict &&
            ((System.Collections.Generic.IDictionary<object, object>)themeDict).TryGetValue(key, out var val) &&
            val is Brush b)
        {
            brush = b;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        _mica?.RemoveAllSystemBackdropTargets();
        _mica?.Dispose();
        _mica = null;
        _acrylic?.RemoveAllSystemBackdropTargets();
        _acrylic?.Dispose();
        _acrylic = null;
        _config = null;
        _usingMica = false;
        Kind = BackdropKind.None;
        _attached = false;
    }
}
