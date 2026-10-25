using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; }

    public SettingsPage()
    {
        Vm = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }
}
