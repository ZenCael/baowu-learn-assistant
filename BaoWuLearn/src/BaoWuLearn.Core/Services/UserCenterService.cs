using System.Text.Json;
using BaoWuLearn.Core.Behavior;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;

namespace BaoWuLearn.Core.Services;

/// <summary>
/// 个人中心数据访问：个人信息、学时统计、我的已选课程。
///
/// 接口与字段均取自平台前端 bundle（@cld-tms/stu 的 index-CXIMa7I9 / index-DU-Mw1Qn），
/// 并用真实令牌逐条实测过响应结构。
/// </summary>
public sealed class UserCenterService
{
    private readonly ApiClient _api;

    public UserCenterService(ApiClient api) => _api = api;

    // ── 个人信息 ──────────────────────────────────────────

    /// <summary>个人信息：姓名 / 工号 / 组织 / 岗位 / 累计学习时长。</summary>
    public async Task<StudentProfile?> GetProfileAsync(CancellationToken ct = default)
    {
        using var doc = await _api.PostRawAsync(ApiEndpoints.StudentDetails, new { }, ct: ct);
        var d = ApiResponseReader.Data(doc!.RootElement);
        if (d is not { } data) return null;

        return new StudentProfile
        {
            StuName = Str(data, "stuName") ?? "",
            StuCode = Str(data, "stuCode") ?? "",
            OrgName = Str(data, "orgName") ?? "",
            PostName = Str(data, "postName") ?? "",
            TotalLearnTimeSeconds = Num(data, "totalLearnTime") ?? 0,
            TotalGetHours = Num(data, "totalGetHours") ?? 0,
        };
    }

    // ── 学时统计 ──────────────────────────────────────────

    /// <summary>
    /// 学时统计。对应网页端个人中心顶部的三个数字：
    /// 总学时 / 网络自学学时 / 集中培训学时，以及各自的细分。
    /// </summary>
    /// <param name="stuCode">工号（即登录名）。</param>
    /// <param name="yearOffset">0 = 本年度，1 = 上一年度（网页端的年度切换）。</param>
    public async Task<StudyHours?> GetStudyHoursAsync(
        string stuCode, int yearOffset = 0, CancellationToken ct = default)
    {
        var year = DateTime.Now.Year - yearOffset;
        var payload = new
        {
            stuCode,
            yearFlag = yearOffset == 0 ? "2" : "1",
            beginTimeBegin = $"{year}-01-01 00:00:00",
            endTimeEnd = $"{year}-12-31 23:59:59",
        };

        using var doc = await _api.PostRawAsync(ApiEndpoints.StudyDurationStats, payload, ct: ct);
        var d = ApiResponseReader.Data(doc!.RootElement);
        if (d is not { } data) return null;

        return new StudyHours
        {
            Year = year,
            TotalGetHours = Num(data, "totalGetHours") ?? 0,
            OnlineSelfStudyHours = Num(data, "onlineSelfStudyHours") ?? 0,
            CentralizedTrainingHours = Num(data, "centralizedTrainingHours") ?? 0,
            PublicCourseHours = Num(data, "publicCourseHours") ?? 0,
            StudyZoneHours = Num(data, "studyZoneHours") ?? 0,
            OnlineTopicHours = Num(data, "onlineTopicHours") ?? 0,
            TrainCourseHours = Num(data, "trainCourseHours") ?? 0,
            FaceClassHours = Num(data, "faceClassHours") ?? 0,
        };
    }

    // ── 我的已选课程 ──────────────────────────────────────

    /// <summary>已选课程的分类标签。</summary>
    public static class Categories
    {
        public const string PublicCourse = "网络自学 · 公开课";
        public const string StudyZone = "网络自学 · 学习专区";
        public const string OnlineTopic = "集中培训 · 网络专题班";
        public const string TrainCourse = "集中培训 · 培训班";
        public const string FaceClass = "集中培训 · 面授班";
    }

    /// <summary>
    /// 一次归档加载的结果：数据 + 每个失败分类的错误。
    ///
    /// 以前单个分类失败是被静默吞掉的 —— 接口一挂，界面就是"无声的空白"，
    /// 用户根本分不清"真的没课"还是"查询失败了"。错误必须可见。
    /// </summary>
    public sealed class ArchiveLoadResult
    {
        public List<MyCourseItem> Items { get; } = new();
        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// 我的全部已选课程（五个分类合并，带失败明细）。
    ///
    /// 平台在个人中心按五个标签页分别取数，其中：
    ///  - 公开课走 queryArchivesOlClassPage（课程级，带 courseNo / 成绩）
    ///  - 学习专区 / 网络专题班 / 培训班走 queryArchivesMineOlClassPage（班级级）
    /// 这里把它们并成一份列表，供"学习队列"页直接使用。
    /// </summary>
    public Task<ArchiveLoadResult> GetMyCoursesAsync(
        string stuCode, int yearOffset = 0, CancellationToken ct = default)
        => LoadArchivesAsync(stuCode, yearOffset, null, ct);

    /// <summary>
    /// 只取某一类已选班级（olClassType：ZE0 学习专区 / ZE1 网络专题班 / TCE 培训班）。
    ///
    /// 课程页的「我的网络专题班」「我的培训班」两个菜单用它 ——
    /// 与平台前端一致：这两个菜单列的就是**本人**已加入的班级，不是全平台班级。
    /// </summary>
    public Task<ArchiveLoadResult> GetMyClassesAsync(
        string stuCode, string olClassType, int yearOffset = 0, CancellationToken ct = default)
        => LoadArchivesAsync(stuCode, yearOffset, olClassType, ct);

    /// <param name="onlyClassType">
    /// 非空时只查该 olClassType；为空时五个分类全查。
    /// </param>
    private async Task<ArchiveLoadResult> LoadArchivesAsync(
        string stuCode, int yearOffset, string? onlyClassType, CancellationToken ct)
    {
        var result = new ArchiveLoadResult();

        // v1.0.15 实测（学员甲 100001 账号）：归档记录大多不带 beginTime/endTime 字段，
        // 带 beginTimeBegin/endTimeEnd（乃至 yearFlag）过滤会把整批记录滤成空 ——
        // 971278 之前能查到纯属那几门课恰好有时间字段。这里只传 stuCode（+ 分类），
        // 查全量，与"我的已选"的语义一致。

        // 1) 公开课（课程级）—— 只在"全部分类"时才查
        if (onlyClassType is null)
        {
            await TryLoadAsync(result, Categories.PublicCourse, async () =>
            {
                var payload = new { current = 1, size = 500, data = new { stuCode } };
                return await _api.PostRawAsync(ApiEndpoints.MyArchivesPublic, payload, ct: ct);
            }, ParsePublicCourse, ct);
        }

        // 2) 学习专区 / 网络专题班 / 培训班（班级级）
        //
        // ★ v1.0.16 修正：这里原先走 queryArchivesMineOlClassPage，实测对学员甲这类账号
        //   恒返回 0 条（网页端明明有 2 个专区），改用平台真正的"我加入的合集"接口
        //   student/myClassPage —— body 只传 classType，服务端按 token 认人。
        foreach (var (type, label) in new[]
                 {
                     ("ZE0", Categories.StudyZone),
                     ("ZE1", Categories.OnlineTopic),
                     ("TCE", Categories.TrainCourse),
                 })
        {
            if (onlyClassType is not null && !string.Equals(type, onlyClassType, StringComparison.Ordinal))
                continue;

            await TryLoadAsync(result, label, async () =>
            {
                var payload = new { current = 1, size = 500, data = new { classType = type } };
                return await _api.PostRawAsync(ApiEndpoints.MyClassPage, payload, ct: ct);
            }, ParseClassLevel, ct);
        }

        return result;
    }

    /// <summary>
    /// 把一门课程加入"我的已选课程"。
    ///
    /// 网页端没有显式的"加入"按钮 —— 点进课程开始学习时就自动进了已选列表，
    /// 触发动作就是 learnRecord/initLearnRecord。这里复刻同一条调用。
    /// </summary>
    public async Task<bool> JoinCourseAsync(
        string centerCode, string courseNo, string olClassNo, CancellationToken ct = default)
    {
        var payload = new
        {
            centerCode,
            courseNo,
            olClassNo,
            pageId = Humanize.NewPageId(),
        };

        using var doc = await _api.PostRawAsync(ApiEndpoints.InitLearnRecord, payload, ct: ct);
        return doc is not null && ApiResponseReader.IsOk(doc.RootElement);
    }

    // ── 内部 ──────────────────────────────────────────────

    private async Task TryLoadAsync(
        ArchiveLoadResult sink,
        string category,
        Func<Task<JsonDocument?>> fetch,
        Func<JsonElement, string, MyCourseItem?> map,
        CancellationToken ct)
    {
        try
        {
            using var doc = await fetch();
            if (doc is null)
            {
                sink.Errors.Add($"{category}：接口返回空响应");
                return;
            }

            var data = ApiResponseReader.Data(doc.RootElement);
            if (data is null)
            {
                // 服务端明确拒绝（statusCode 非 200 / data:null）—— 必须报出来，
                // 不能当成"这个分类没有课"处理。
                sink.Errors.Add(
                    $"{category}：{ApiResponseReader.Message(doc.RootElement) ?? "服务端返回 data 为空"}");
                return;
            }

            var scope = data.Value;
            if (!scope.TryGetProperty("records", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                sink.Errors.Add($"{category}：响应里没有 records 字段");
                return;
            }

            foreach (var r in arr.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var item = map(r, category);
                if (item is not null) sink.Items.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单个分类取数失败不影响其余分类，但错误必须上报 —— 任何逃逸异常
            // 都会让上层整页失败，所以在这里收口并记入 Errors。
            sink.Errors.Add($"{category}：{ex.Message}");
        }
    }

    /// <summary>公开课归档：课程级记录。</summary>
    private static MyCourseItem? ParsePublicCourse(JsonElement r, string category) => new()
    {
        Category = category,
        Guid = Str(r, "guid") ?? "",
        CenterCode = Str(r, "centerCode") ?? "",
        CenterName = Str(r, "centerName") ?? "",
        OlClassNo = Str(r, "olClassNo") ?? "",
        OlClassType = Str(r, "olClassType") ?? "",
        OlClassName = Str(r, "olClassName") ?? "",
        CourseNo = Str(r, "courseNo") ?? "",
        CourseName = Str(r, "courseName") ?? "",
        CourseScore = Num(r, "courseScore"),
        CourseHours = Num(r, "courseHours"),
        GetHours = Num(r, "getHours"),
        LearnNum = (int?)(Num(r, "learnNum")),
        StudyStatus = Str(r, "studyStatus"),
        LearnSource = Str(r, "learnSource"),
        IsMustTeach = Str(r, "isMustTeach"),
        TotalLearnTimeSeconds = Num(r, "totalLearnTime") ?? 0,
        FinishTime = Long(r, "finishTime"),
        LastLearnTime = Long(r, "lastLearnTime"),
    };

    /// <summary>
    /// 专区 / 专题班 / 培训班：班级（合集）级记录，来自 student/myClassPage。
    ///
    /// 实测返回字段（2026-09-11，某平台公开专区）：
    ///   guid / olClassType / olClassNo / olClassName / beginTime / endTime / isEnd /
    ///   classHours / imageUrl / tenantCode / centerCode / courseNum / maxTime / learnNum
    /// 其中 courseNum = 合集课程总数、learnNum = 本人已学门数（这两个是界面汇总口径）。
    /// </summary>
    private static MyCourseItem? ParseClassLevel(JsonElement r, string category) => new()
    {
        Category = category,
        CenterCode = Str(r, "centerCode") ?? "",
        CenterName = Str(r, "centerName") ?? "",
        Guid = Str(r, "guid") ?? "",
        OlClassNo = Str(r, "olClassNo") ?? "",
        OlClassType = Str(r, "olClassType") ?? "",
        OlClassName = Str(r, "olClassName") ?? "",
        CourseNum = (int?)Num(r, "courseNum"),
        LearnedCourseNum = (int?)Num(r, "learnNum"),
        ClassHours = Num(r, "classHours"),
        BeginDate = Str(r, "beginTime") ?? "",
        EndDate = Str(r, "endTime") ?? "",
        // 兼容旧接口字段（若某天换回去或有别的账号口径不同，不至于白丢数据）
        Score = Num(r, "score"),
        TotalGetHours = Num(r, "totalGetHours"),
        FinishStatus = Str(r, "finishStatus"),
        FinishCourseNumber = (int?)Num(r, "finishCourseNumber"),
        NotFinishCourseNumber = (int?)Num(r, "notFinishCourseNumber"),
        LearnSource = Str(r, "learnSource"),
        FinishTime = Long(r, "finishTime"),
    };

    private static string? Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static double? Num(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String when double.TryParse(p.GetString(), out var v) => v,
            _ => null,
        };
    }

    private static long? Long(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetInt64(),
            JsonValueKind.String when long.TryParse(p.GetString(), out var v) => v,
            _ => null,
        };
    }
}
