using System;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 「已播秒数」的本地时钟：把"引擎只在心跳前后推一次快照"补成"界面按秒往前走"。
///
/// ★ 队列页与总览页**共用同一个类、同一套口径**。
///   引擎约 60 秒才推一次快照，两次之间界面收不到任何更新；两个页面要是各写一套插值，
///   同一个「课件进度」就会长成两个数 —— 这正是这类"看起来在动"的显示最经典的翻车方式。
///
/// 三条纪律，每一条都来自真实缺陷：
///   1. **单调**：插值按墙钟走、账本按真实学时走，两者必然对不齐（学时取整、25% 概率少 1 秒、
///      模拟暂停不计学时、心跳发送耗时）。新快照一到就会把超前的显示值拽回去 ——
///      肉眼看到的就是「进度条回退」。取历史最大值最省事也最诚实：宁可停一拍，绝不倒着走。
///   2. **暂停停表**：引擎模拟暂停（"人离开了"那几秒到几十秒）一秒都不计学时，
///      界面必须跟着停 —— 否则暂停期间虚高、暂停一结束又掉回去。
///   3. **换内容清零**：换了课件 / 换了课程，进度本来就该从头算；不清就会卡在旧的高位上不动。
/// </summary>
public sealed class LiveProgressClock
{
    private DateTimeOffset _anchor = DateTimeOffset.Now;
    private double _floor;
    private double _snapshot;
    private string? _identity;

    /// <summary>当前应显示的已播秒数（单调 floor）。</summary>
    public double Shown => _floor;

    /// <summary>
    /// 收到一次引擎快照：记录基准值、复位插值锚点；归属键变了就把 floor 清零。
    /// </summary>
    /// <param name="identity">归属键。它一变（换课件 / 换课程）就意味着进度从头算。</param>
    /// <param name="playedSeconds">快照里的已播秒数。</param>
    /// <param name="now">当前时刻。</param>
    /// <returns>是否发生了"换内容"（调用方据此重置进度条等界面态）。</returns>
    public bool Accept(string identity, double playedSeconds, DateTimeOffset now)
    {
        var switched = !string.Equals(identity, _identity, StringComparison.Ordinal);
        if (switched)
        {
            _identity = identity;
            _floor = 0;
        }

        _snapshot = playedSeconds;
        _anchor = now;
        return switched;
    }

    /// <summary>
    /// 推进一步（快照到达时、以及每秒 tick 时都走这一条路），返回此刻应当显示的已播秒数。
    ///
    /// 暂停窗口内停表：把锚点顶到当前时刻，返回值原地不动 —— 这段墙钟整段剔除，
    /// 暂停结束后是接着暂停前那一点往前走，而不是"先虚高、快照一到再掉回去"。
    /// </summary>
    public double Advance(double targetSeconds, DateTimeOffset? pausingUntil, DateTimeOffset now)
    {
        if (IsPausing(pausingUntil, now))
        {
            _anchor = now;
            return _floor;
        }

        var elapsed = Math.Max(0, (now - _anchor).TotalSeconds);
        _floor = AdvanceDisplay(_floor, InterpolatePlayed(_snapshot, targetSeconds, elapsed));
        return _floor;
    }

    /// <summary>清零（停止挂课 / 换用户时用）。</summary>
    public void Reset()
    {
        _floor = 0;
        _snapshot = 0;
        _identity = null;
        _anchor = DateTimeOffset.Now;
    }

    // ── 纯函数（public static，供 --selftest 离线验算）──────────────────

    /// <summary>
    /// 把"已播秒数"按本地流逝的时间往前推，并封顶目标时长（绝不显示超过 100%）。
    ///
    /// 抽成纯函数是为了能在 <c>--selftest</c> 里直接验算 ——
    /// 这种"看起来在动"的东西最容易只凭编译通过就交付，实际跑起来还是死的。
    /// </summary>
    public static double InterpolatePlayed(double snapshotPlayed, double target, double elapsedSeconds)
    {
        var v = snapshotPlayed + (elapsedSeconds > 0 ? elapsedSeconds : 0);
        if (target > 0 && v > target) return target;
        return v;
    }

    /// <summary>
    /// 显示值的单调器：候选值比"已经显示过的最大值"小就维持原值。
    ///
    /// 这一行就是「进度条回退」的解药。抽成纯函数是为了能在 <c>--selftest</c> 里
    /// 用真实日志里的那组数字复现缺陷（心跳 #1 → 模拟暂停 21s → 心跳 #2），
    /// 而不是只靠肉眼看界面。
    /// </summary>
    public static double AdvanceDisplay(double shownBefore, double candidate)
        => candidate > shownBefore ? candidate : shownBefore;

    /// <summary>此刻是否处在引擎的模拟暂停窗口内（此期间界面停表，不插值）。</summary>
    public static bool IsPausing(DateTimeOffset? pausingUntil, DateTimeOffset now)
        => pausingUntil is { } until && now < until;
}
