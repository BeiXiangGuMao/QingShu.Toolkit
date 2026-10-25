using System;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Composition;
using WinRT;

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
                CreateAcrylic(window.ActualTheme == ElementTheme.Dark);
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
            _acrylic.TintColor = Color.FromArgb(255, 15, 23, 42);   // #0F172A
            _acrylic.TintOpacity = 0.85f;
            _acrylic.FallbackColor = Color.FromArgb(235, 15, 23, 42);
        }
        else
        {
            _acrylic.TintColor = Color.FromArgb(255, 248, 250, 252); // #F8FAFC
            _acrylic.TintOpacity = 0.72f;
            _acrylic.FallbackColor = Color.FromArgb(230, 248, 250, 252);
        }
    }

    /// <summary>主题切换时重建亚克力参数；Mica 由系统随配置自动适配。</summary>
    public void UpdateTheme(ElementTheme theme)
    {
        if (_config != null)
            _config.Theme = theme == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        if (!_usingMica && Kind == BackdropKind.Acrylic && _acrylic != null)
            CreateAcrylic(theme == ElementTheme.Dark);
    }

    /// <summary>页面根背景应使用半透明笔刷让材质透出。</summary>
    public static Brush? TryGetPageBackground(AppSettings settings, BackdropKind kind, bool dark)
    {
        try
        {
            if (!settings.UseAcrylicBackdrop || kind == BackdropKind.None) return null; // 回退不透明
            var key = dark ? "PageBackgroundAcrylicBrush" : "PageBackgroundAcrylicBrush";
            return Application.Current.Resources[key] as Brush;
        }
        catch { return null; }
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
