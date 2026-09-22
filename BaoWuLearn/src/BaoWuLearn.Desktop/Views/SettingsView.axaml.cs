using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BaoWuLearn.Desktop.ViewModels;

namespace BaoWuLearn.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>下载根目录「浏览…」：系统原生目录选择器（v1.0.45）。</summary>
    private async void PickDownloadDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null || !sp.CanPickFolder) return;

        IStorageFolder? start = null;
        try
        {
            var cur = vm.DownloadDirText;
            if (!string.IsNullOrWhiteSpace(cur) && Directory.Exists(cur))
                start = await sp.TryGetFolderFromPathAsync(cur);
        }
        catch { /* 起始位置拿不到就用系统默认 */ }

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择课件下载根目录",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0)
        {
            vm.DownloadDirText = folders[0].Path.LocalPath;
            vm.SaveStatus = "已选择目录，点下方「保存设置」生效";
        }
    }

    /// <summary>「用默认」：清空自定义路径（回到系统「下载」文件夹），仍需保存。</summary>
    private void ResetDownloadDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.DownloadDirText = "";
        vm.SaveStatus = "已清空自定义目录，点下方「保存设置」生效";
    }
}
