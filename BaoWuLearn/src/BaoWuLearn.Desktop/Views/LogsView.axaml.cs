using Avalonia.Controls;

namespace BaoWuLearn.Desktop.Views;

/// <summary>
/// 实时日志页。滚动条钉在底部时，新消息到达自动跟随；
/// 用户向上拖离底部 = 想回头看历史，此时停止跟随，拖回底部即恢复。
/// </summary>
public partial class LogsView : UserControl
{
    // 是否"钉"在底部。初始在底部（空列表），新消息一路跟随。
    private bool _pinned = true;

    public LogsView() => InitializeComponent();

    private void OnLogScrollerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // 内容变高（新消息到达 / 过滤后列表变化）：只有此前钉在底部才跟随。
        if (e.ExtentDelta.Y > 0 && _pinned)
            sv.ScrollToEnd();

        // 重新计算钉住状态：滚到底部附近（4px 容差）即视为钉住。
        // ScrollToEnd 自身触发的滚动也会走到这里，结果仍是 true，状态自洽。
        _pinned = sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 4;
    }
}
