using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

public sealed partial class DiskCleanupPage : Page
{
    public CleanupViewModel Vm { get; }

    public DiskCleanupPage()
    {
        Vm = App.Services.GetRequiredService<CleanupViewModel>();
        InitializeComponent();
    }

    private void OpenLargeFolder_Click(object sender, RoutedEventArgs e)
    {
        var sel = Vm.LargeFiles.FirstOrDefault(x => x.IsSelected) ?? Vm.LargeFiles.FirstOrDefault();
        if (sel == null) return;
        try
        {
            var target = System.IO.File.Exists(sel.Item.Path) ? sel.Item.Path : sel.Item.Dir;
            Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch { }
    }
}
