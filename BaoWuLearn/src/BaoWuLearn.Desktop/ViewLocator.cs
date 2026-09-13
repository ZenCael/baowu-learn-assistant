using Avalonia.Controls;
using Avalonia.Controls.Templates;
using BaoWuLearn.Desktop.ViewModels;

namespace BaoWuLearn.Desktop;

/// <summary>
/// 约定式视图定位：ViewModels.XxxViewModel → Views.XxxView。
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null) return new TextBlock { Text = "无数据" };

        var name = data.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(name);
        if (type is null)
            return new TextBlock { Text = $"未找到视图：{name}" };

        return (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
