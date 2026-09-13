namespace BaoWuLearn.Core.Behavior;

/// <summary>
/// 行为拟人化参数。
///
/// 背景：心跳上报的 payload <b>没有签名、没有时间戳防伪</b>，服务端唯一能观察到的就是
/// 「请求的时间分布」与「字段取值的合理性」。所以拟人化的目标不是伪造签名，
/// 而是让整条请求序列落在真人行为的统计区间内。
///
/// 本类集中管理所有随机化参数，便于后续按实测反馈统一调整。
/// </summary>
public static class Humanize
{
    /// <summary>拟人化强度档位。</summary>
    public enum Level
    {
        /// <summary>关闭：全部取固定值，便于调试。</summary>
        Off = 0,

        /// <summary>轻度：仅做基本抖动。</summary>
        Light = 1,

        /// <summary>标准（默认）：全套随机化。</summary>
        Standard = 2,
    }

    public static Level Current { get; set; } = Level.Standard;

    private static Random Rng => Random.Shared;

    // ── 心跳节奏 ──────────────────────────────────────────

    /// <summary>心跳间隔：58–63 秒（均值约 60.5），而非固定 60 秒。</summary>
    public static TimeSpan HeartbeatInterval()
    {
        if (Current == Level.Off) return TimeSpan.FromSeconds(60);
        var sec = Current == Level.Light
            ? 59 + Rng.NextDouble() * 2      // 59–61
            : 58 + Rng.NextDouble() * 5;     // 58–63
        return TimeSpan.FromSeconds(sec);
    }

    /// <summary>进度标记间隔：28–34 秒。</summary>
    public static TimeSpan ProgressMarkInterval()
    {
        if (Current == Level.Off) return TimeSpan.FromSeconds(30);
        return TimeSpan.FromSeconds(28 + Rng.NextDouble() * 6);
    }

    /// <summary>课程之间的随机停顿：30–120 秒（避免"一课接一课"的机械连贯）。</summary>
    public static TimeSpan CourseGap()
    {
        if (Current == Level.Off) return TimeSpan.FromSeconds(5);
        return TimeSpan.FromSeconds(30 + Rng.NextDouble() * 90);
    }

    /// <summary>登录成功后先"浏览"一会儿再挂课：4–12 秒。</summary>
    public static TimeSpan BrowsingDelay()
    {
        if (Current == Level.Off) return TimeSpan.FromSeconds(1);
        return TimeSpan.FromSeconds(4 + Rng.NextDouble() * 8);
    }

    // ── 字段随机化 ────────────────────────────────────────

    // （ShouldBlur 已于 v1.0.28 删除）曾按 3.5% 概率给心跳打 isBlur=1「模拟失焦」，
    // 但 2026-09-12 实测证明平台对 isBlur=1 的心跳整条拒绝（status=blur，学时一分不记），
    // 模拟失焦=纯丢学时。心跳报文里 isBlur 现恒为 "0"。

    /// <summary>本次上报的学时秒数。与真实间隔严格对齐，允许 -1 秒的微小偏差，绝不虚报。</summary>
    public static int LearnSeconds(double actualElapsedSeconds)
    {
        var baseVal = (int)Math.Floor(actualElapsedSeconds);
        if (Current == Level.Off) return Math.Max(1, baseVal);
        // 偶尔少报 1 秒（真人计时也不会卡得严丝合缝），但绝不多报
        var jitter = Rng.NextDouble() < 0.25 ? -1 : 0;
        return Math.Max(1, baseVal + jitter);
    }

    /// <summary>
    /// 平台对**单次心跳**计入的学时上限（秒）—— 超过的部分直接丢掉。
    ///
    /// 2026-09-13 用一整晚日志对账定案：把每一次「结算后回读的学习时长」减去
    /// 前序各跳上报的 learnTime，平台真正计入的增量恒等于
    /// <c>Σ min(单跳学时, 60)</c> —— 133 次采样逐秒吻合，0 例外。
    /// 最干净的一处证据是三个「分数线 100 + 视频占分 100%」的专区课程：
    /// 本地账本跑到 900 / 900 / 900 秒，平台只认 884 / 892 / 885 秒，
    /// 而这三个数正好等于各自心跳序列的 Σ min(·, 60)。
    ///
    /// ★ 为什么这条必须进代码：拟人化的心跳间隔是 58–63 秒，凡是超过 60 秒的那一跳
    ///   都会丢 1~3 秒，几十跳累计就是 1~2% 的时长。普通课程要按分数线打折
    ///   （60 分 ÷ 80% 权重 = 只挂 75% 时长），这点损耗根本看不出来；但零余量的
    ///   专区课要求 100% 时长，账本够了平台还差十几秒 —— 于是 98 分"完成"、
    ///   怎么也到不了 100 分。本地账本必须按同一口径前进才对得上账。
    /// </summary>
    public const int MaxCreditPerBeatSeconds = 60;

    /// <summary>
    /// 本跳平台真正会计入的学时 = 上报学时截到单跳上限。
    ///
    /// 上报值用截断后的秒数（而不是照实报 63 让平台自己截）：这样"平台计入多少"
    /// 不再依赖那个上限假设，账本与平台永远一致，零余量课程才挂得满。
    /// </summary>
    public static int CreditedSeconds(double actualElapsedSeconds, int maxPerBeat = MaxCreditPerBeatSeconds)
        => Math.Min(LearnSeconds(actualElapsedSeconds), maxPerBeat);

    /// <summary>播放进度允许的微小偏差（秒）。只允许落后，不允许超前。</summary>
    public static double ProgressJitter()
    {
        if (Current == Level.Off) return 0;
        return -Rng.NextDouble() * 1.5;
    }

    // ── 随机行为插入 ──────────────────────────────────────

    /// <summary>是否在本次心跳时插入一次"随机暂停"（模拟人离开一下）。</summary>
    public static bool ShouldPause()
    {
        if (Current == Level.Off) return false;
        return Rng.NextDouble() < 0.04;   // 约每 25 次心跳一次（≈25 分钟）
    }

    /// <summary>随机暂停时长：5–30 秒。</summary>
    public static TimeSpan PauseDuration()
        => TimeSpan.FromSeconds(5 + Rng.NextDouble() * 25);

    /// <summary>生成一个页面会话 ID（对应网页端的 pageId，UUID 形式）。</summary>
    public static string NewPageId() => Guid.NewGuid().ToString();

    /// <summary>把秒数格式化为 <c>HH:mm:ss</c>（平台 curPlayTime 格式）。</summary>
    public static string FormatPlayTime(double totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
