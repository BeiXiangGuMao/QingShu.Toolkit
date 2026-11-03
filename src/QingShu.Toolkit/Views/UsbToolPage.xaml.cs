using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

/// <summary>U盘工具页：探查 → 检测/扩容鉴别 → 查杀/免疫 → 修复。破坏性操作在此做二次确认。</summary>
public sealed partial class UsbToolPage : Page
{
    public UsbToolViewModel Vm { get; }

    public UsbToolPage()
    {
        Vm = App.Services.GetRequiredService<UsbToolViewModel>();
        InitializeComponent();
        // 离开页面收尾：取消在途破坏性操作（步骤边界生效）。VM 为 transient 且 USB 服务层
        // 无重入护栏——不收尾则旧操作后台写盘不止，复入页的新 VM 可对同一U盘并发二次操作。
        this.Unloaded += (_, _) => Vm.Dispose();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (Vm.Devices.Count == 0 && !Vm.IsBusy)
            _ = Vm.RefreshDevicesCommand.ExecuteAsync(null);
    }

    // ================= 扩容盘检测（破坏性，二次确认） =================

    private async void CapacityTest_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedDevice == null) return;
        var chk = new CheckBox
        {
            Content = "我已确认U盘数据可全部丢失（探针区域将被覆盖，检测期间U盘不可用）"
        };
        var dlg = new ContentDialog
        {
            Title = "扩容盘检测（硬件级写校验）",
            Content = new StackPanel { Spacing = 10, Children =
            {
                new TextBlock
                {
                    Text = "检测原理：向盘内 4 处区域（8MB / 中点 / 95% / 末尾内缩）各写入 4MB 特征数据并回读比对。\n" +
                           "· 虚标容量的劣质U盘（黑片/升级片）会在超出真实容量的位置暴露：回读失败或内容不符。\n" +
                           "· 写入区域的原有数据将永久丢失；检测后U盘可能需要重新插拔恢复访问。\n" +
                           "· 请先备份重要文件。",
                    TextWrapping = TextWrapping.Wrap
                },
                chk
            }},
            PrimaryButtonText = "开始检测",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = this.XamlRoot
        };
        chk.Checked += (_, _) => dlg.IsPrimaryButtonEnabled = true;
        chk.Unchecked += (_, _) => dlg.IsPrimaryButtonEnabled = false;
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await Vm.RunCapacityVerifiedAsync();
    }

    // ================= 格式化重建（破坏性，二次确认 + 文件系统选择） =================

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        var d = Vm.SelectedDevice;
        if (d == null) return;

        var rbExfat = new RadioButton { Content = $"exFAT（大容量推荐{(d.Drive.SizeBytes > 32L * 1024 * 1024 * 1024 ? "，FAT32 不支持超 32GB" : "")}）", IsChecked = true };
        var rbFat32 = new RadioButton { Content = "FAT32（兼容性最好，仅 ≤32GB 生效，超出自动转 exFAT）" };
        var rbNtfs = new RadioButton { Content = "NTFS（支持权限与大文件，部分设备/老系统读取慢）" };
        var chk = new CheckBox { Content = $"我已确认：{d.Drive.Summary} 上的全部数据将被清空" };

        var dlg = new ContentDialog
        {
            Title = "格式化重建分区表（高级）",
            Content = new StackPanel { Spacing = 10, Children =
            {
                new TextBlock
                {
                    Text = "用于：分区表损坏、容量识别异常、无法格式化、隐性坏区需重新划分。\n" +
                           "过程 = clean 清空分区表 → 新建主分区 → 快速格式化 → 重新分配盘符（需管理员授权）。\n" +
                           "注意：这是修复的最后手段，会清空U盘全部数据，请先备份。",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = "选择文件系统：", FontSize = 12 },
                rbExfat, rbFat32, rbNtfs,
                chk
            }},
            PrimaryButtonText = "执行重建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = this.XamlRoot
        };
        chk.Checked += (_, _) => dlg.IsPrimaryButtonEnabled = true;
        chk.Unchecked += (_, _) => dlg.IsPrimaryButtonEnabled = false;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var fs = rbNtfs.IsChecked == true ? "ntfs" : rbFat32.IsChecked == true ? "fat32" : "exfat";
        await Vm.RebuildPartitionVerifiedAsync(fs);
    }
}
