using System.Text.Json;

namespace BaoWuLearn.Core.Models;

/// <summary>
/// 平台响应包装。平台的响应结构不完全统一（有的给 isSuccess，有的只给 code），
/// 因此用 <see cref="ApiResponseReader"/> 做兼容判断，而不是直接反序列化。
/// </summary>
public sealed class ApiResponse<T>
{
    public bool IsSuccess { get; set; }
    public int Code { get; set; }
    public string? Msg { get; set; }
    public T? Data { get; set; }
}

/// <summary>响应结构兼容读取器。</summary>
public static class ApiResponseReader
{
    public static bool IsOk(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (root.TryGetProperty("isSuccess", out var s))
        {
            if (s.ValueKind == JsonValueKind.True) return true;
            if (s.ValueKind == JsonValueKind.False) return false;
        }

        if (root.TryGetProperty("code", out var c))
        {
            return c.ValueKind switch
            {
                JsonValueKind.Number => c.GetInt32() == 200,
                JsonValueKind.String => c.GetString() is "200" or "0",
                _ => false,
            };
        }

        // 既无 isSuccess 也无 code，但有 data —— 视为成功
        return root.TryGetProperty("data", out _);
    }

    public static JsonElement? Data(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null
            ? d : null;

    public static string? Message(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "msg", "message", "errorMsg" })
            if (root.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
        return null;
    }
}

/// <summary>通用分页结果。</summary>
public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Total { get; set; }
    public int Count => Items.Count;
}

/// <summary>图形验证码。</summary>
public sealed class CaptchaData
{
    /// <summary>验证码会话 ID，登录时需原样回传。</summary>
    public string? Id { get; set; }

    /// <summary>接口返回的图片原始值（base64 / data URI / 图片 URL 三者之一）。</summary>
    public string? Image { get; set; }

    /// <summary>
    /// 是否已确认为 base64 图片。
    /// 判定依据是「能解码且解码结果的魔数是已知图片格式」，而不是字符串长相 ——
    /// 平台的 base64 可能以 <c>/</c> 开头，只看前缀会和相对 URL 混淆。
    /// </summary>
    public bool IsBase64 { get; set; }

    /// <summary>若为 GIF 动图则为 true（渲染时需逐帧播放）。</summary>
    public bool IsAnimated { get; set; }

    /// <summary>base64 形式下已解码好的图片字节，界面层可直接用来建位图。</summary>
    public byte[]? Bytes { get; set; }
}

/// <summary>登录结果。</summary>
public sealed class LoginResult
{
    public string? AccessToken { get; set; }
    public string? UserName { get; set; }
    public string? UserNo { get; set; }
    public string? RealName { get; set; }
    public JsonElement? Raw { get; set; }
}

/// <summary>学习专区。</summary>
public sealed class ZoneItem
{
    public string ZoneName { get; set; } = "";
    public string Guid { get; set; } = "";
    public string CenterCode { get; set; } = "";
    public string OlClassNo { get; set; } = "";
    public string OlClassType { get; set; } = "";
}

/// <summary>
/// 可选的学习中心（课程页「学习中心」下拉框的候选项）。
/// 来自 selectCurrentStuHomeCourseAuthCenterList，按平台的 sort 升序排。
/// </summary>
public sealed class CenterOption
{
    public string CenterCode { get; init; } = "";
    public string CenterName { get; init; } = "";
    public int Sort { get; init; } = 999;

    /// <summary>"1" 表示该中心当前可用（实测全部为 1，仅作留档）。</summary>
    public string? IsActivate { get; init; }

    /// <summary>下拉框里显示的样子：中文名带编码，编码认不出来时至少还能看见编码。</summary>
    public string Display => CenterName.Length == 0 || CenterName == CenterCode
        ? CenterCode
        : $"{CenterName}（{CenterCode}）";
}

/// <summary>课程（含学时/成绩信息）。</summary>
public sealed class CourseItem
{
    public string CourseName { get; set; } = "";
    public string Guid { get; set; } = "";
    public string CenterCode { get; set; } = "";
    public string CenterName { get; set; } = "";
    public string CourseNo { get; set; } = "";
    public string OlClassNo { get; set; } = "";
    public string OlClassType { get; set; } = "";
    public string? LearnStatus { get; set; }

    // ── 平台课程卡片上的展示字段 ─────────────────────────
    /// <summary>课程学时（平台原值，单位通常是小时）。</summary>
    public double? CourseHours { get; set; }

    /// <summary>封面图相对路径，使用时需拼网关前缀。</summary>
    public string? ImageUrl { get; set; }

    /// <summary>课程分类名（如"形势政策"）。</summary>
    public string? CourseTypeName { get; set; }

    /// <summary>必修 1 / 选修 0。</summary>
    public string? IsMustTeach { get; set; }

    /// <summary>浏览人次（专区卡片用作热度）。</summary>
    public string? ViewCnt { get; set; }

    /// <summary>专区内的课程数量（专区卡片用）。</summary>
    public string? CourseNum { get; set; }

    public string? TeacherName { get; set; }
    public string? BeginTime { get; set; }
    public string? EndTime { get; set; }

    /// <summary>要求学时（单位与接口一致，联调时校准）。</summary>
    public double? RequiredDuration { get; set; }

    /// <summary>已完成学时。</summary>
    public double? CompletedDuration { get; set; }

    /// <summary>要求学时的单位（平台 attributeUnit，如"分钟"）。</summary>
    public string? RequiredUnit { get; set; }

    /// <summary>
    /// 已完成学时的**平台格式化文本**（finishValueMS）。
    /// 平台前端对 CE002（学习时长）就是直接显示这个字段，而不是 finishValue 原值，
    /// 因为原值单位是毫秒、不适合直接给人看。
    /// </summary>
    public string? CompletedText { get; set; }

    /// <summary>
    /// 视频时长（CE002 学习时长）占总得分的百分比。
    ///
    /// ★ 各课不一样（实测有 70/30 也有 80/20），直接决定"挂多久才到分数线"：
    ///   得分 ≈ 已学时长 ÷ 要求时长 × 权重。60 分及格的课程，
    ///   权重 80 要挂到 75% 时长、权重 70 要挂到 85.7% —— 界面把它亮出来，
    ///   用户挑课挂课才有依据（实测 48.08/58 分钟 × 80% = 66.32 分，严丝合缝）。
    /// 数据来自 finishInfo details 里 CE002 的 percentage；未查询过为 null（界面不显示）。
    /// </summary>
    public double? DurationScoreWeight { get; set; }

    public double? LearnScore { get; set; }
    public double? PassScore { get; set; }

    public bool HasScoreInfo
        => RequiredDuration.HasValue || CompletedDuration.HasValue || !string.IsNullOrWhiteSpace(CompletedText);

    public bool IsFinished
        => RequiredDuration is > 0 && CompletedDuration is { } done && done >= RequiredDuration;

    public double ProgressRatio
    {
        get
        {
            if (RequiredDuration is not > 0 || CompletedDuration is not { } done) return 0;
            var r = done / RequiredDuration.Value;
            return r < 0 ? 0 : r > 1 ? 1 : r;
        }
    }

    public string DurationText
    {
        get
        {
            if (!HasScoreInfo) return "—";

            // 优先用平台自己的格式化文本（CE002 的 finishValueMS），
            // 拿不到再退回"已完成 / 要求"的原值拼装。
            var done = !string.IsNullOrWhiteSpace(CompletedText)
                ? CompletedText
                : Format(CompletedDuration);
            var need = Format(RequiredDuration) + (string.IsNullOrWhiteSpace(RequiredUnit) ? "" : RequiredUnit);
            return $"{done} / {need}";
        }
    }

    /// <summary>课程学时文本。</summary>
    public string HoursText => CourseHours is { } h
        ? (h == Math.Floor(h) ? ((long)h).ToString() : h.ToString("0.##")) + " 学时"
        : "—";

    public string MustTeachText => IsMustTeach switch
    {
        "1" => "必修",
        "0" => "选修",
        _ => "",
    };

    /// <summary>封面的完整 URL；无封面时返回 null。</summary>
    public string? ImageFullUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ImageUrl)) return null;
            if (ImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return ImageUrl;
            return Http.ApiEndpoints.Host + "/learn-gateway" + ImageUrl;
        }
    }

    private static string Format(double? v)
    {
        if (v is not { } x) return "—";
        return x == Math.Floor(x) ? ((long)x).ToString() : x.ToString("0.##");
    }
}

/// <summary>课件（视频/文档），挂课心跳需要用到 wareId。</summary>
public sealed class WareItem
{
    public string WareId { get; set; } = "";
    public string WareName { get; set; } = "";
    public string WareType { get; set; } = "1";
    public string? CataNo { get; set; }
    public int? DurationSeconds { get; set; }

    /// <summary>关键帧提示点（平台用于"看完打点"），原样透传。</summary>
    public string? MarkTimePoint { get; set; }

    /// <summary>是否已学完。</summary>
    public bool Learned { get; set; }

    /// <summary>
    /// 视频转码指纹（v1.0.44+，目录树字段）。非空 = 该课件有 HLS 流，
    /// 播放地址 = Host + "/learn-gateway/video/" + HashCode + "/index.m3u8"。
    /// 网页端播放器同款拼法（stu bundle 静态提取，2026-09-22）。
    /// </summary>
    public string? HashCode { get; set; }

    /// <summary>原始文件相对路径（目录树字段）。与 HashCode 二选一：视频无 hashCode 时走直链。</summary>
    public string? WareUrl { get; set; }

    /// <summary>内容类型（"1"=视频，其余=PDF/附件等走预览-下载通道）。播放器判定用这个字段。</summary>
    public string? ContentType { get; set; }

    /// <summary>时长文本，如 "7分24秒" / "21:03"。</summary>
    public string DurationText
    {
        get
        {
            if (DurationSeconds is not { } s || s <= 0) return "—";
            var ts = TimeSpan.FromSeconds(s);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}小时{ts.Minutes}分"
                : ts.TotalMinutes >= 1 ? $"{ts.Minutes}分{ts.Seconds}秒" : $"{ts.Seconds}秒";
        }
    }
}

/// <summary>
/// 单个课件在服务端的播放进度（<c>getMaxTimeAndLastTime</c>）。
///
/// ★★ 平台维护的是**两本账**（2026-09-13 定案，v1.0.36）：
///  - 这里是**位置账**：最远播放位置 <see cref="MaxPlaySeconds"/>，续播必须从它起算，
///    不能从 0 重播（否则 curPlayTime 超不过历史最远位置，心跳成功但学时不动）。
///  - 成绩单 CE002 的 finishValue 是**学时账**：每次心跳最多只计入 60 秒
///    （见 <c>Humanize.MaxCreditPerBeatSeconds</c>），超出部分丢掉。
///
/// 曾经这里写着"各课件 maxPlayTime 之和恰等于 CE002 的 finishValue"，并据此推出
/// "该值已达课件时长时没有可挂的增量，应直接跳过"——**这个等式是错的**。
/// 早期版本的账本按未截断的间隔前进，于是位置总比学时多 1~2%；
/// 普通课程按分数线打折（60 分 ÷ 80% 权重 = 只挂 75% 时长）有余量吸收，看不出来，
/// 但「分数线 100 + 视频占分 100%」的零余量课（零余量 PDF 专区）要求 100% 时长，
/// 位置早就顶在 15:00、学时只有 14分44秒，得分永远卡在 98 分。
/// 所以位置到顶**不等于**学时到顶 —— 后者才是能不能收工的判据，
/// 见 <c>LearnEngine.NeedsTopUp</c>。
/// </summary>
public sealed class WareProgress
{
    public string CataNo { get; set; } = "";
    public string WareId { get; set; } = "";

    /// <summary>服务端记录的最远播放位置（HH:MM:SS 原文）。</summary>
    public string MaxPlayTime { get; set; } = "";

    /// <summary>服务端记录的最后播放位置（HH:MM:SS 原文）。</summary>
    public string LastPlayTime { get; set; } = "";

    /// <summary>最远播放位置（秒）—— 学时的实际计算依据。</summary>
    public int MaxPlaySeconds { get; set; }

    /// <summary>把 HH:MM:SS 文本解析为秒。</summary>
    public static int ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Split(':');
        if (parts.Length != 3) return 0;
        return int.TryParse(parts[0], out var h)
               && int.TryParse(parts[1], out var m)
               && int.TryParse(parts[2], out var s)
            ? h * 3600 + m * 60 + s
            : 0;
    }
}

/// <summary>课程详情 + 课件列表。</summary>
public sealed class CourseDetail
{
    public string CourseNo { get; set; } = "";
    public string OlClassNo { get; set; } = "";
    public string CourseName { get; set; } = "";
    public string CenterCode { get; set; } = "";
    public string? LearnStatus { get; set; }
    public List<WareItem> Wares { get; set; } = new();

    /// <summary>课程要求总时长（秒），由课件时长求和得出。</summary>
    public int TotalDurationSeconds => Wares.Sum(w => w.DurationSeconds ?? 0);
}

/// <summary>个人信息（来自 queryStudentDetails）。</summary>
public sealed class StudentProfile
{
    public string StuName { get; set; } = "";
    public string StuCode { get; set; } = "";
    public string OrgName { get; set; } = "";
    public string PostName { get; set; } = "";

    /// <summary>累计学习时长（秒）。</summary>
    public double TotalLearnTimeSeconds { get; set; }

    /// <summary>已获总学时。</summary>
    public double TotalGetHours { get; set; }

    public string OrgShortName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(OrgName)) return "";
            var parts = OrgName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 2 ? OrgName : string.Join(" / ", parts[^2..]);
        }
    }

    public string LearnTimeText
    {
        get
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, TotalLearnTimeSeconds));
            return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分钟";
        }
    }
}

/// <summary>
/// 学时统计（来自 queryArchivesStudyDurationStatistics）。
/// 对应网页端"个人中心"顶部的学时看板。
/// </summary>
public sealed class StudyHours
{
    /// <summary>总学时。</summary>
    public double TotalGetHours { get; set; }

    /// <summary>网络自学学时（= 公开课 + 学习专区）。</summary>
    public double OnlineSelfStudyHours { get; set; }

    /// <summary>集中培训学时（= 网络专题班 + 培训班 + 面授班）。</summary>
    public double CentralizedTrainingHours { get; set; }

    public double PublicCourseHours { get; set; }
    public double StudyZoneHours { get; set; }
    public double OnlineTopicHours { get; set; }
    public double TrainCourseHours { get; set; }
    public double FaceClassHours { get; set; }

    /// <summary>统计年度，用于界面标注。</summary>
    public int Year { get; set; } = DateTime.Now.Year;

    public bool IsEmpty => TotalGetHours <= 0 && OnlineSelfStudyHours <= 0 && CentralizedTrainingHours <= 0;
}

/// <summary>
/// 我的已选课程 / 班级记录。
///
/// 平台的两类归档接口返回粒度不同，这里统一到同一个模型：
///  - 公开课（queryArchivesOlClassPage）   → 课程级：有 CourseName / CourseNo / CourseScore
///  - 专区·专题班·培训班（queryArchivesMineOlClassPage）→ 班级级：有 OlClassName / Score / 完成课程数
/// 挂课需要 CourseNo + OlClassNo，两者都提供。
/// </summary>
public sealed class MyCourseItem
{
    // ── 定位 ────────────────────────────────────────────
    public string CenterCode { get; set; } = "";
    public string CenterName { get; set; } = "";
    public string OlClassNo { get; set; } = "";
    public string OlClassType { get; set; } = "";
    public string OlClassName { get; set; } = "";
    public string CourseNo { get; set; } = "";
    public string CourseName { get; set; } = "";
    public string Guid { get; set; } = "";

    // ── 进度与成绩 ──────────────────────────────────────
    public double? CourseScore { get; set; }
    public double? Score { get; set; }

    /// <summary>
    /// 视频时长（CE002）占总得分的百分比（见 <see cref="CourseItem.DurationScoreWeight"/>）。
    /// 界面在副标题里显示「视频占分 80%」；未查询过为 null 不显示。
    /// </summary>
    public double? DurationScoreWeight { get; set; }

    /// <summary>课程学时（来自课程卡片的 courseHours，仅课程级记录有）。</summary>
    public double? CourseHours { get; set; }

    public double? GetHours { get; set; }
    public double? TotalGetHours { get; set; }
    public double TotalLearnTimeSeconds { get; set; }
    public int? LearnNum { get; set; }

    /// <summary>学习状态：0 未开始 / 1 进行中 / 2 已完成。</summary>
    public string? StudyStatus { get; set; }

    /// <summary>班级完成状态：同上取值。</summary>
    public string? FinishStatus { get; set; }

    /// <summary>
    /// 本人在这门课上的学习状态（班内课程列表的 learnStatus，取值同 StudyStatus）。
    ///
    /// ⚠ 班内课程列表（onlineClassCourse/getOnlineClassCourseSortPage）**只有这个字段**能反映
    ///   本人进度：不返回 studyStatus / finishStatus / totalLearnTime。
    ///   若不把它取出来，展开出来的子课程状态永远是空的，只能显示成「—」。
    /// </summary>
    public string? LearnStatus { get; set; }

    public int? FinishCourseNumber { get; set; }
    public int? NotFinishCourseNumber { get; set; }

    /// <summary>
    /// 合集（班级）里的课程总数 —— myClassPage 的 <c>courseNum</c>。
    /// 例：某平台公开专区 = 21。
    /// </summary>
    public int? CourseNum { get; set; }

    /// <summary>
    /// 本人在该合集里已学（完成 + 进行中）的课程数 —— myClassPage 的 <c>learnNum</c>。
    /// 例：学员甲在公开课合集 267 门里学了 4 门 → learnNum=4。
    /// </summary>
    public int? LearnedCourseNum { get; set; }

    /// <summary>班级学时（myClassPage 的 classHours，字符串，可能为空）。</summary>
    public double? ClassHours { get; set; }

    /// <summary>班级起止日期（myClassPage 的 beginTime / endTime，形如 2026-09-01）。</summary>
    public string BeginDate { get; set; } = "";
    public string EndDate { get; set; } = "";

    /// <summary>来源：1 指派 / 2 自选。</summary>
    public string? LearnSource { get; set; }

    /// <summary>必修 1 / 选修 0。</summary>
    public string? IsMustTeach { get; set; }

    public long? FinishTime { get; set; }
    public long? LastLearnTime { get; set; }

    /// <summary>分类标签（网络自学·公开课 / 网络自学·学习专区 / 集中培训·网络专题班 …）。</summary>
    public string Category { get; set; } = "";

    /// <summary>该记录是"班级/专区级"还是"课程级"。</summary>
    public bool IsClassLevel => !string.IsNullOrEmpty(OlClassName) && string.IsNullOrEmpty(CourseName);

    /// <summary>界面主标题。</summary>
    public string Title => !string.IsNullOrEmpty(CourseName) ? CourseName
        : !string.IsNullOrEmpty(OlClassName) ? OlClassName : "(未命名)";

    /// <summary>能否挂课：必须有 courseNo 与 olClassNo。</summary>
    public bool CanLearn => !string.IsNullOrEmpty(CourseNo) && !string.IsNullOrEmpty(OlClassNo);

    public string LearnSourceText => LearnSource switch
    {
        "1" => "指派",
        "2" => "自选",
        _ => "",
    };

    public string MustTeachText => IsMustTeach switch
    {
        "1" => "必修",
        "0" => "选修",
        _ => "",
    };

    /// <summary>
    /// 状态文案。三个来源依次兜底：
    ///   studyStatus（公开课归档）/ finishStatus（班级归档）/ learnStatus（班内课程列表）。
    /// </summary>
    public string StatusText => (StudyStatus ?? FinishStatus ?? LearnStatus) switch
    {
        "0" => "未开始",
        "1" => "进行中",
        "2" => "已完成",
        _ => "",
    };

    public string ScoreText => CourseScore is { } cs ? cs.ToString("0.##")
        : Score is { } s ? s.ToString("0.##") : "—";

    public string HoursText => CourseHours is { } ch ? FormatHours(ch)
        : ClassHours is { } clh ? FormatHours(clh)
        : GetHours is { } h ? FormatHours(h)
        : TotalGetHours is { } t ? FormatHours(t) : "—";

    private static string FormatHours(double v)
        => v == Math.Floor(v) ? ((long)v).ToString() : v.ToString("0.##");

    public string LearnTimeText
    {
        get
        {
            if (TotalLearnTimeSeconds <= 0) return "—";
            var ts = TimeSpan.FromSeconds(TotalLearnTimeSeconds);
            return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分";
        }
    }
}

/// <summary>课程目录树节点（queryCourseOutlineContentTreeListSimple）。</summary>
public sealed class CatalogNode
{
    public string CataNo { get; set; } = "";
    public string CataName { get; set; } = "";

    /// <summary>该目录下的课件。</summary>
    public List<WareItem> Wares { get; set; } = new();

    public int TotalDurationSeconds => Wares.Sum(w => w.DurationSeconds ?? 0);

    public string DurationText
    {
        get
        {
            var ts = TimeSpan.FromSeconds(TotalDurationSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}小时{ts.Minutes}分"
                : $"{ts.Minutes}分{ts.Seconds}秒";
        }
    }
}
