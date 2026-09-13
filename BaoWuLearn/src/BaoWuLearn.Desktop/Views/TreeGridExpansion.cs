using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;

namespace BaoWuLearn.Desktop.Views;

/// <summary>
/// 树网格的"子行晚到"补偿。
///
/// 课程页与学习队列页都是**点开箭头才去拉班内课程**（一个专区动辄 20+ 门课，
/// 一进页就全拉会把平台请求打爆）。而树网格在展开的那一刻只按**当时已有的**
/// 子行铺行，一秒后拉回来的子行不会自己长出来 —— 用户看到的就是
/// 「点了箭头没反应，得收了再点一次」。
///
/// 所以子行到货后补一次：先收起再展开。
/// 对"本来就没展开"的行，收起是空操作、展开反而会误展开，所以这里只在
/// **行确实在展开中**时才会被执行到 —— 调用方是在展开回调里等数据回来后调用的。
/// </summary>
internal static class TreeGridExpansion
{
    /// <summary>
    /// 子行到货后重新展开一次（只处理顶层行：两层树里只有顶层是合集）。
    /// 行已经不在列表里（换菜单 / 翻页 / 刷新过）时静默跳过。
    /// </summary>
    public static void ReExpand<T>(
        HierarchicalTreeDataGridSource<T>? source,
        ObservableCollection<T> level0,
        T row)
        where T : class
    {
        if (source is null) return;

        var index = -1;
        for (var i = 0; i < level0.Count; i++)
        {
            if (ReferenceEquals(level0[i], row))
            {
                index = i;
                break;
            }
        }

        if (index < 0) return;

        try
        {
            var path = new IndexPath(index);
            source.Collapse(path);
            source.Expand(path);
        }
        catch (Exception)
        {
            // 收起/展开期间行被移出列表这类竞态：忽略即可，
            // 用户下一次点箭头会重新走一遍（拉过的合集不会再发请求）。
        }
    }
}
