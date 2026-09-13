namespace BaoWuLearn.Core.Behavior;

/// <summary>挂课的完成策略 —— 决定"什么时候算挂够、可以换下一门"。</summary>
public enum CompletionPolicy
{
    /// <summary>挂满才跳：把课件时长全部挂完再换下一门。成绩最稳，但耗时最长。</summary>
    FullDuration = 0,

    /// <summary>
    /// 及格就跳：只挂到「课程分数线折算出来的时长」就换下一门。
    ///
    /// 平台的得分构成是 已学时长 ÷ 要求时长 × **视频占分权重**（finishInfo details
    /// 里 CE002 的 percentage，各课不同，实测 70/30、80/20 都有）—— 剩下的分
    /// 在考试等其他环节。所以想拿 60 分，需要挂到的时长比例是 分数线 ÷ 权重：
    /// 权重 80 的课挂到 75% 就够，权重 70 的课要挂到 85.7%。
    /// 对那些要求时长明显长于课件总时长的课（挂到底也满不了）这更是唯一可行的做法。
    /// </summary>
    PassScore = 1,
}

/// <summary>
/// 挂课策略配置。
///
/// 与 <see cref="Humanize"/> 同一种模式：核心层放静态配置，界面层写入。
/// 这样引擎不必为了一个开关去改 Start 的签名，自检也能直接读到当前策略。
/// </summary>
public static class LearnPolicy
{
    /// <summary>当前策略。默认挂满 —— 行为与历史版本一致，不会让人误以为出了问题。</summary>
    public static CompletionPolicy Current { get; set; } = CompletionPolicy.FullDuration;

    /// <summary>分数线缺省值：平台多数课程的及格线就是 60。</summary>
    public const double DefaultPassScore = 60;

    /// <summary>
    /// 按当前策略把"课程要求时长"折算成"实际要挂到的时长"。
    /// 挂满策略原样返回；及格策略按「分数线 ÷ 视频占分权重」打折。
    ///
    /// ★ 权重参数是 v1.0.24 补的：旧版写死「分数线 ÷ 100」，隐含假设视频占满分 ——
    ///   对权重 80 的课会把目标算成 60% 时长，而实际要挂到 75%（60 ÷ 80）
    ///   才到分数线。停课的权威判定始终是真实成绩回读，所以旧版只是估算偏乐观，
    ///   没有多挂或少跳，但日志里那个"目标"数字会骗人。
    ///   权重未知（成绩单还没回读）时退回旧口径。
    /// </summary>
    public static double? TargetSeconds(double? fullSeconds, double? passScore, double? durationWeight = null)
    {
        if (fullSeconds is not { } full || full <= 0) return fullSeconds;
        if (Current != CompletionPolicy.PassScore) return full;

        var pass = NormalizePassScore(passScore);
        var ratio = durationWeight is > 0 ? Math.Min(pass / durationWeight.Value, 1.0) : pass / 100.0;
        return full * ratio;
    }

    /// <summary>取课程的分数线；平台没给或给得不合理时退回缺省的 60 分。</summary>
    public static double NormalizePassScore(double? passScore)
        => passScore is > 0 and <= 100 ? passScore.Value : DefaultPassScore;

    /// <summary>
    /// 这门课是不是「零余量」课：分数线已经顶到视频占分权重，必须挂满 100% 时长。
    ///
    /// 得分 = 已学时长 ÷ 要求时长 × 视频占分权重（CE002 percentage），
    /// 所以要拿 <paramref name="passScore"/> 分，需要挂到的时长比例是
    /// <c>分数线 ÷ 权重</c>。当这个比例 ≥ 1（比如平台实测到的「分数线 100 +
    /// 视频占分 100%」PDF 专区课）时，一点余量都没有 —— 平台少记一秒就是
    /// 99 分而不是 100 分。这种课必须在日志里说清楚，否则用户看到
    /// 「挂够了却没到线」只会以为程序偷懒。
    ///
    /// 权重未知时按 100 处理（与 <see cref="TargetSeconds"/> 的旧口径一致）。
    /// 公开为 public 是为了让 <c>--selftest</c> 能离线验算。
    /// </summary>
    public static bool IsZeroMargin(double? passScore, double? durationWeight)
    {
        if (Current != CompletionPolicy.PassScore) return false;
        var weight = durationWeight is > 0 ? durationWeight.Value : 100.0;
        return NormalizePassScore(passScore) >= weight;
    }
}
