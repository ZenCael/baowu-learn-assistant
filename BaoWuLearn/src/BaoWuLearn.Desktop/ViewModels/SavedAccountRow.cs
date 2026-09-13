using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 登录页账号下拉里的一行。
///
/// 命令直接挂在行上（而不是在模板里绕到父 DataContext 去取），
/// 这样 XAML 模板只有 <c>{Binding UseCommand}</c> 这种直白写法，
/// 编译期绑定也能校验通过。
/// </summary>
public sealed partial class SavedAccountRow : ObservableObject
{
    public SavedAccountRow(
        string userNo,
        string? displayName,
        bool hasPassword,
        string? lastLoginAt,
        Action<SavedAccountRow> use,
        Action<SavedAccountRow> delete)
    {
        UserNo = userNo;
        DisplayName = displayName;
        HasPassword = hasPassword;
        LastLoginAt = lastLoginAt;

        UseCommand = new RelayCommand(() => use(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public string UserNo { get; }

    public string? DisplayName { get; }

    /// <summary>该账号是否已保存密码。</summary>
    public bool HasPassword { get; }

    public string? LastLoginAt { get; }

    /// <summary>下拉里的主文案：有姓名就带上，没姓名只显示工号。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(DisplayName)
        ? UserNo
        : $"{UserNo} · {DisplayName}";

    /// <summary>副文案。</summary>
    public string HintText => HasPassword ? "已保存密码 · 选中即填" : "未保存密码";

    public IRelayCommand UseCommand { get; }

    public IRelayCommand DeleteCommand { get; }
}
