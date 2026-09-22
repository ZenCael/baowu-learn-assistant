using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BaoWuLearn.Desktop.ViewModels;

namespace BaoWuLearn.Desktop.Views;

public partial class DownloadView : UserControl
{
    public DownloadView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 「更改保存目录…」——系统原生目录选择器（v1.0.45：路径是选出来的，
    /// 不是让用户手打的）。起始位置落在当前生效的下载根目录上。
    /// </summary>
    private async void PickRoot_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadViewModel vm) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null || !sp.CanPickFolder) return;

        IStorageFolder? start = null;
        try
        {
            if (Directory.Exists(vm.CurrentRoot))
                start = await sp.TryGetFolderFromPathAsync(vm.CurrentRoot);
        }
        catch { /* 起始位置拿不到就用系统默认，不影响选择 */ }

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择课件保存目录",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0)
            vm.ApplyPickedDownloadRoot(folders[0].Path.LocalPath);
    }
}
