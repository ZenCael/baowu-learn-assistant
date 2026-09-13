using Avalonia.Controls;
using Avalonia.Threading;
using BaoWuLearn.Desktop.ViewModels;

namespace BaoWuLearn.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// 关闭窗口时先把挂课引擎收尾（补发最后一次结算），收完再真正关闭。
    ///
    /// 为什么不能直接关：收尾要等引擎停下来，而等待期间 UI 线程会被占用，
    /// 引擎推快照、挂机秒表 tick 全排在它后面 —— Mac 上挂课中点关闭就是整个卡死。
    /// 这里用 e.Cancel 把关闭延后一拍，让收尾在后台跑完。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.TryBeginShutdown())
        {
            e.Cancel = true;                    // 先别关，去收尾
            _ = FinishShutdownAsync(vm);
        }

        base.OnClosing(e);
    }

    private async Task FinishShutdownAsync(MainWindowViewModel vm)
    {
        await vm.ShutdownAsync();

        // 收尾完成。这一次 TryBeginShutdown 会返回 false，于是走正常关闭流程。
        Dispatcher.UIThread.Post(Close);
    }
}
