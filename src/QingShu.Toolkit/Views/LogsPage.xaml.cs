using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

public sealed partial class LogsPage : Page
{
    public LogsViewModel Vm { get; }

    public LogsPage()
    {
        Vm = App.Services.GetRequiredService<LogsViewModel>();
        InitializeComponent();
    }
}
