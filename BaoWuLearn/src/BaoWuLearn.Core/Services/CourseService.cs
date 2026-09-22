using System.Text.Json;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;

namespace BaoWuLearn.Core.Services;

/// <summary>
/// 课程与专区数据访问：专区列表、课程列表、成绩/学时、课件详情。
/// </summary>
public sealed class CourseService
{
    private const string DefaultTenant = "BSTA";

    private readonly ApiClient _api;

    public CourseService(ApiClient api) => _api = api;

    /// <summary>我的学习专区列表。</summary>
    public async Task<List<ZoneItem>> GetZonesAsync(string learnStatus = "1", CancellationToken ct = default)
    {
        var payload = new
        {
            current = 1,
            size = 100,
            data = new
            {
                classType = "ZE0",
                isLearnNum = "1",
                keyWord = "",
                lastLearnTime = "1",
                learnStatus,
                sortClass = "1",
                sortType = "desc",
                status = "",
            },
        };

        // 必须在 JsonDocument 释放前完成映射（JsonElement 依赖底层文档的生命周期）
        using var doc = await _api.PostRawAsync(ApiEndpoints.ZoneList, payload, ct: ct);

        return ExtractRecords(doc).Select(r => new ZoneItem
        {
            Guid = Str(r, "guid") ?? "",
            CenterCode = Str(r, "centerCode") ?? "",
            OlClassNo = Str(r, "olClassNo") ?? "",
            OlClassType = Str(r, "olClassType") ?? "",
            ZoneName = Str(r, "olClassName") ?? Str(r, "className") ?? "(未命名专区)",
        }).ToList();
    }

    /// <summary>公开课列表。</summary>
    public async Task<List<CourseItem>> GetPublicCoursesAsync(
        string learnStatus = "1", int current = 1, int size = 100, CancellationToken ct = default)
    {
        var payload = new
        {
            current,
            size,
            data = new
            {
                learnStatus,
                searchInfo = "",
                searchType = "1",
                sortClass = "1",
                sortType = "desc",
            },
        };

        var doc = await _api.PostRawAsync(ApiEndpoints.PublicCourses, payload, ct: ct);
        return ExtractRecords(doc).Select(ToCourse).ToList();
    }

    // ── 课程管理页（学习中心）的四个分类 ────────────────────
    //
    // 网页端 /learnManagement/:centerCode 下：
    //   网络自学 → 公开课程（onlineClassCourse/newestOnlineClassCoursePage）
    //   网络自学 → 学习专区（onlineClass/newestOnlineClassPage）
    //   集中培训 → 我的网络专题班 / 我的培训班（onlineClass/queryMainOnlineClassPage，靠 olClassType 区分）
    //
    // 注意：查询"平台上的全部课程"要带 ignoreCenter=true（跨站点），
    // 只看本中心会漏掉大部分内容。

    /// <summary>公开课程（网络自学）。</summary>
    public async Task<PagedResult<CourseItem>> GetPublicCoursePageAsync(
        string centerCode, int current = 1, int size = 20, string sortFlag = "2", CancellationToken ct = default)
    {
        var payload = new
        {
            current,
            size,
            data = new
            {
                centerCode,
                olClassType = "OCE",
                isMine = "",
                sortFlag,
                ignoreCenter = true,
                beginTime = "",
                endTime = "",
            },
        };
        return await FetchPageAsync(ApiEndpoints.PublicCourseNewest, payload, ct);
    }

    /// <summary>学习专区（网络自学）。sortFlag: "1" 最新，"2" 默认。</summary>
    public async Task<PagedResult<CourseItem>> GetZonePageAsync(
        string centerCode, int current = 1, int size = 20, string sortFlag = "2", CancellationToken ct = default)
    {
        var payload = new
        {
            current,
            size,
            data = new
            {
                centerCode,
                olClassType = "ZE0",
                isMine = "",
                sortFlag,
                ignoreCenter = true,
                beginTime = "",
                endTime = "",
            },
        };
        return await FetchPageAsync(ApiEndpoints.ZoneNewest, payload, ct);
    }

    /// <summary>集中培训：网络专题班（ZE1）或培训班（TCE）。</summary>
    public async Task<PagedResult<CourseItem>> GetMainClassPageAsync(
        string centerCode, string olClassType, int current = 1, int size = 20, CancellationToken ct = default)
    {
        var payload = new
        {
            current,
            size,
            data = new
            {
                centerCode,
                olClassType,
                isMine = "",
                sortFlag = "2",
                beginTime = "",
                endTime = "",
            },
        };
        return await FetchPageAsync(ApiEndpoints.MainOnlineClass, payload, ct);
    }

    // ── 全库索引：把分页目录翻到底（课程页"全库搜索"用）────────────
    //
    // 网页端一页只回 20 条。搜索若只在手头这一页里找就等于没找 ——
    // 用户记得有门课叫「信息安全法」，它可能在第 7 页，而搜索框只过滤了当前 20 行
    // （用户实测反馈原话：「这个查询就是在下方列表当前显示的范围内查询，意义不大」）。
    // 这里按页循环把整个目录取回来，搜索改在**全量数据源**上做。

    /// <summary>全库索引的单页请求条数。平台把 size 截断也不影响：翻页只认空页与总数。</summary>
    public const int IndexPageSize = 500;

    /// <summary>全库索引的请求次数上限 —— 平台若完全忽略 size（永远只回 20 条）也不会无限翻页。</summary>
    public const int IndexMaxRequests = 40;

    /// <summary>全库索引的条数上限（兜底，防止目录畸大时把内存和界面拖死）。</summary>
    public const int IndexMaxItems = 3000;

    /// <summary>
    /// 把一个分页接口**翻到底**。
    ///
    /// 特意做成静态方法、且只依赖一个"取第 N 页"的委托：自检可以用假数据把平台的三种脾气
    /// （①老实听 size ②把 size 截到 20 ③根本不回 total）都验一遍，不必登录、不打真接口。
    /// </summary>
    public static async Task<List<CourseItem>> FetchAllPagesAsync(
        Func<int, int, CancellationToken, Task<PagedResult<CourseItem>>> fetchPage,
        int preferredSize = IndexPageSize,
        int maxRequests = IndexMaxRequests,
        int maxItems = IndexMaxItems,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        var all = new List<CourseItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var size = Math.Max(1, preferredSize);

        for (var page = 1; page <= maxRequests; page++)
        {
            ct.ThrowIfCancellationRequested();

            var res = await fetchPage(page, size, ct);

            if (res.Total > 0) total = res.Total;

            // ★ 平台可能把 size 截断（各接口上限不统一）。**不要**据此缩小后续请求的 size：
            //   页号是按"第 N 页 × 当前 size"定位的，size中途一变，下一页的偏移就错了位，
            //   会重读旧区间、空转到请求上限 —— 索引悄悄缺尾巴（v1.0.38 自检当场抓住过）。
            //   正确姿势：size 恒定，靠「空页 = 到底」和「取够 total」两道闸停。
            //   「这页没满 = 到底」也不能用作判据 —— ② 那种"每次都截到 20"的脾气会在第一页就误判收工。

            foreach (var c in res.Items)
            {
                var key = (c.CourseNo ?? "") + "@" + (c.OlClassNo ?? "");
                if (seen.Add(key)) all.Add(c);
            }

            onProgress?.Invoke(all.Count, total);

            if (res.Items.Count == 0) break;                 // 空页 = 到底了
            if (total > 0 && all.Count >= total) break;      // 平台给了总数，够了
            if (all.Count >= maxItems) break;                // 兜底
        }

        return all;
    }

    /// <summary>统一的分页取数：解析 records / total。</summary>
    private async Task<PagedResult<CourseItem>> FetchPageAsync(string url, object payload, CancellationToken ct)
    {
        using var doc = await _api.PostRawAsync(url, payload, ct: ct);
        var result = new PagedResult<CourseItem>();
        if (doc is null) return result;

        var data = ApiResponseReader.Data(doc.RootElement);
        var scope = data ?? doc.RootElement;

        result.Total = (int?)(Num(scope, "total")) ?? 0;
        if (scope.TryGetProperty("records", out var arr) && arr.ValueKind == JsonValueKind.Array)
            result.Items = arr.EnumerateArray().Select(ToCourse).ToList();

        return result;
    }

    // ── 平台检索（ES）─────────────────────────────────────
    //
    // 目录接口一页 20 条、且不支持关键字，"搜索"过去只能靠客户端把整库翻回来
    // （FetchAllPagesAsync）。平台其实自带 ES 全文检索 —— 网页端那个大搜索框就是它。
    // 一次请求一页、带真 total，比翻页建索引又快又准，所以两个浏览型页签改走它。
    //
    // 报文（2026-09-13 从 homeSearch 页 chunk 提取 + 实测）：
    //   { "current":1, "size":20, "data":{ "search":"信息安全", "lib":"course" } }
    // 响应 data：{ total, records[] }，records[].rawData 带 courseNo / olClassNo / olClassType。

    /// <summary>
    /// 用平台 ES 检索取一页结果。<paramref name="lib"/> 传
    /// <see cref="ApiEndpoints.EsLibCourse"/> 或 <see cref="ApiEndpoints.EsLibZone"/>。
    ///
    /// ★ 关键字为空**不要**调它 —— 平台对空关键字返回 <c>total:0</c>（不是全量），
    ///   空关键字必须走普通目录接口。这条约束由调用方（课程页 ViewModel）把关，
    ///   这里也兜一道：空关键字直接返回空页，省一次必然无意义的请求。
    /// </summary>
    public async Task<PagedResult<CourseItem>> SearchCatalogAsync(
        string lib, string keyword, int current = 1, int size = 20, CancellationToken ct = default)
    {
        var result = new PagedResult<CourseItem>();
        if (string.IsNullOrWhiteSpace(keyword)) return result;

        var payload = new
        {
            current,
            size,
            data = new { search = keyword.Trim(), lib },
        };

        using var doc = await _api.PostRawAsync(ApiEndpoints.EsSearchFromEs, payload, ct: ct);
        if (doc is null) return result;

        var data = ApiResponseReader.Data(doc.RootElement);
        if (data is not { } scope) return result;

        result.Total = (int?)(Num(scope, "total")) ?? 0;
        if (scope.TryGetProperty("records", out var arr) && arr.ValueKind == JsonValueKind.Array)
            result.Items = arr.EnumerateArray().Select(ParseEsRecord).ToList();

        return result;
    }

    /// <summary>
    /// ES 记录 → 课程对象（public 是为了让 <c>--selftest</c> 能拿真实抓下来的样本离线验算）。
    ///
    /// ★ ES 记录是**两层**的：可检索字段（title / tags / description）在顶层，
    ///   业务字段（courseNo / olClassNo / olClassType / centerCode）在 <c>rawData</c> 里。
    ///   只读顶层拿不到课程编号、只读 rawData 又没有标题 —— 必须两边都取，rawData 优先。
    ///
    /// ★ 专区记录（dataType=zone）没有 courseNo：它代表"合集"这一行。此时**不能**拿
    ///   顶层 subTitle 当 courseNo（那是专区自己的编号），否则这一行会被当成课程级行，
    ///   既没有复选框也没有展开箭头（见 <c>MyCourseRow.FromCourse</c> 的同款注释）。
    /// </summary>
    public static CourseItem ParseEsRecord(JsonElement r)
    {
        var raw = r.TryGetProperty("rawData", out var x) && x.ValueKind == JsonValueKind.Object
            ? x
            : r;

        var dataType = Str(r, "dataType") ?? "";
        var isCourse = dataType.Length == 0 || dataType.Equals("course", StringComparison.OrdinalIgnoreCase);

        return new CourseItem
        {
            CourseName = Str(raw, "courseName") ?? Str(raw, "olClassName")
                         ?? Str(r, "title") ?? "(未命名课程)",
            Guid = Str(r, "guid") ?? Str(raw, "guid") ?? "",
            CenterCode = Str(raw, "centerCode") ?? Str(r, "centerCode") ?? "",
            CenterName = Str(raw, "centerName") ?? Str(r, "centerName") ?? "",
            CourseNo = Str(raw, "courseNo") ?? (isCourse ? Str(r, "subTitle") : null) ?? "",
            OlClassNo = Str(raw, "olClassNo") ?? Str(raw, "classNo") ?? "",
            OlClassType = Str(raw, "olClassType") ?? "",
            LearnStatus = Str(raw, "learnStatus"),
            CourseHours = Num(raw, "courseHours") ?? Num(raw, "classHours") ?? Num(r, "hours"),
            CourseNum = Str(raw, "courseNum") ?? Str(r, "courseNum"),
            ImageUrl = Str(raw, "imageUrl") ?? Str(r, "imageUrl"),
            TeacherName = Str(raw, "teacherName"),
            BeginTime = Str(raw, "beginTime") ?? Str(r, "beginTime"),
            EndTime = Str(raw, "endTime") ?? Str(r, "endTime"),
        };
    }

    // ── 学习中心 ──────────────────────────────────────────

    /// <summary>
    /// 当前账号可进入的学习中心列表（网页端首页顶部那个站点下拉）。
    /// 返回 <c>centerName</c>（中文名）+ <c>centerCode</c>，按平台给的 sort 升序。
    /// </summary>
    public async Task<List<CenterOption>> GetCentersAsync(CancellationToken ct = default)
    {
        var doc = await _api.PostRawAsync(ApiEndpoints.StuHomeCenterList, new { }, ct: ct);
        var list = new List<CenterOption>();
        if (doc is null) return list;

        using (doc)
        {
            var data = ApiResponseReader.Data(doc.RootElement);
            if (data is not { } scope || scope.ValueKind != JsonValueKind.Array) return list;

            foreach (var c in scope.EnumerateArray())
            {
                var code = Str(c, "centerCode");
                if (string.IsNullOrEmpty(code)) continue;

                list.Add(new CenterOption
                {
                    CenterCode = code,
                    CenterName = Str(c, "centerName") ?? code,
                    Sort = (int?)(Num(c, "sort")) ?? 999,
                    IsActivate = Str(c, "isActivate"),
                });
            }
        }

        return list.OrderBy(c => c.Sort).ThenBy(c => c.CenterCode, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 课程目录树：每个课件的编号、时长、打点、下载地址。
    ///
    /// 这是挂课的权威来源 —— 网页端播放器左侧的目录就是用它渲染的
    /// （见 @cld-tms/stu CourseStudy 页的 queryCourseOutlineContentTreeListSimple 调用）。
    /// 平台把该接口放在 rls 服务下，路径与其他 ols 接口不同。
    /// </summary>
    public async Task<List<CatalogNode>> GetCatalogAsync(
        string centerCode, string courseNo, CancellationToken ct = default)
    {
        var payload = new { centerCode, courseNo, isAppendPre = "1" };

        var doc = await _api.PostRawAsync(ApiEndpoints.CatalogTree, payload, ct: ct);
        var result = new List<CatalogNode>();
        if (doc is null) return result;

        using (doc)
        {
            var data = ApiResponseReader.Data(doc.RootElement);
            if (data is not { } scope) return result;

            // 平台有两种形态：带 cataNo 的目录数组，或包一层 content
            var list = scope.ValueKind == JsonValueKind.Array
                ? scope.EnumerateArray().ToList()
                : scope.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().ToList()
                    : new List<JsonElement>();

            foreach (var cat in list)
            {
                var node = new CatalogNode
                {
                    CataNo = Str(cat, "cataNo") ?? "",
                    CataName = Str(cat, "cataName") ?? Str(cat, "name") ?? "(未命名目录)",
                };

                // 目录下的课件挂在 content 数组；有的结构直接是 children
                foreach (var key in new[] { "content", "children", "wares" })
                {
                    if (!cat.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

                    foreach (var w in arr.EnumerateArray())
                    {
                        var ware = ToWare(w);
                        if (ware is not null) node.Wares.Add(ware);
                    }
                    if (node.Wares.Count > 0) break;
                }

                if (node.Wares.Count > 0) result.Add(node);
            }
        }

        return result;
    }

    /// <summary>把目录树里的一个节点转成课件；非课件节点（纯目录）返回 null。</summary>
    private static WareItem? ToWare(JsonElement w)
    {
        var wareCode = Str(w, "wareCode");
        var wareType = Str(w, "wareType");
        if (string.IsNullOrEmpty(wareCode) && string.IsNullOrEmpty(wareType)) return null;

        var duration = Num(w, "duration");

        return new WareItem
        {
            WareId = wareCode ?? Str(w, "wareId") ?? "",
            WareName = Str(w, "newContentName") ?? Str(w, "contentName")
                       ?? Str(w, "wareName") ?? "(未命名课件)",
            WareType = wareType ?? "1",
            CataNo = Str(w, "cataNo"),
            DurationSeconds = duration is { } d && d > 0 ? (int)Math.Round(d) : null,
            MarkTimePoint = Str(w, "markeTimePoint"),
            Learned = Str(w, "learnedStatus") == "1",
            // v1.0.44：下载三件套。播放器源码（index-BFFGkfPu.js）证明目录响应
            // 一直带着这三个字段，此前没提取。
            HashCode = Str(w, "hashCode"),
            WareUrl = Str(w, "wareUrl"),
            ContentType = Str(w, "contentType"),
        };
    }

    /// <summary>
    /// 某个专区 / 班级下的课程列表（自动翻页，最多 50 页）。
    ///
    /// 入参就是"我的已选课程"里班级级记录的三个定位字段，
    /// 因此个人中心的"学习专区 / 网络专题班 / 培训班"都能用这一条展开成可挂课的课程。
    /// </summary>
    public async Task<List<CourseItem>> GetClassCoursesAsync(
        string centerCode, string olClassNo, string olClassType, CancellationToken ct = default)
    {
        const int pageSize = 1000;
        const int maxPage = 50;

        var all = new List<CourseItem>();
        for (var page = 1; page <= maxPage; page++)
        {
            ct.ThrowIfCancellationRequested();

            var payload = new
            {
                current = page,
                size = pageSize,
                data = new
                {
                    centerCode,
                    courseName = "",
                    courseTypeCode = "",
                    isMine = "1",
                    isRecursiveCourse = "1",
                    olClassNo,
                    olClassType,
                },
            };

            using var doc = await _api.PostRawAsync(ApiEndpoints.ZoneCourses, payload, ct: ct);
            var courses = ExtractRecords(doc).Select(ToCourse).ToList();

            // 接口有时不回 olClassNo / centerCode（班级级上下文里省略了），这里补回去，
            // 否则这些课程没法用来挂课（心跳与 initLearnRecord 都依赖这两个字段）。
            foreach (var c in courses)
            {
                if (string.IsNullOrEmpty(c.CenterCode)) c.CenterCode = centerCode;
                if (string.IsNullOrEmpty(c.OlClassNo)) c.OlClassNo = olClassNo;
                if (string.IsNullOrEmpty(c.OlClassType)) c.OlClassType = olClassType;
            }

            if (courses.Count == 0) break;

            all.AddRange(courses);
            if (courses.Count < pageSize) break;
        }

        return all;
    }

    /// <summary>某个专区下的课程列表（自动翻页，最多 50 页）。</summary>
    public Task<List<CourseItem>> GetZoneCoursesAsync(
        ZoneItem zone, CancellationToken ct = default)
        => GetClassCoursesAsync(zone.CenterCode, zone.OlClassNo, zone.OlClassType, ct);

    /// <summary>查询单门课程的学时完成情况（对应网页端的 finishInfo）。</summary>
    public async Task<CourseItem?> EnrichWithScoreAsync(CourseItem course, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(course.CenterCode) || string.IsNullOrEmpty(course.CourseNo) || string.IsNullOrEmpty(course.OlClassNo))
            return course;

        var payload = new
        {
            centerCode = course.CenterCode,
            courseNo = course.CourseNo,
            olClassNo = course.OlClassNo,
            tenantCode = DefaultTenant,
        };

        try
        {
            var doc = await _api.PostRawAsync(ApiEndpoints.CourseScore, payload, ct: ct);
            if (doc is null) return course;

            using (doc)
            {
                var data = ApiResponseReader.Data(doc.RootElement);
                if (data is not { } d) return course;

                course.LearnScore = Num(d, "learnScore");
                course.PassScore = Num(d, "passScore");

                if (d.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in details.EnumerateArray())
                    {
                        var attr = Str(item, "attributeCode");
                        if (attr != "CE002") continue;   // CE002 = 学习时长

                        course.RequiredDuration = Num(item, "predValue");
                        course.CompletedDuration = Num(item, "finishValue");
                        course.RequiredUnit = Str(item, "attributeUnit");

                        // 平台前端对 CE002 显示的是 finishValueMS（已格式化），不是 finishValue 原值。
                        course.CompletedText = Str(item, "finishValueMS");

                        // percentage = 视频时长占总得分的百分比（各课不同，实测 70 / 80 都有）。
                        // 顺手写进会话缓存 —— 权重是课程配置，挂课期间不会变。
                        if (Num(item, "percentage") is { } pct)
                        {
                            course.DurationScoreWeight = pct;
                            CacheWeight(course, pct);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 单门课程查成绩失败不影响整体。
            // 必须捕获 Exception 而非仅 ApiException：批量查询用 Task.WhenAll 汇总，
            // 任何逃逸的异常都会让整批查询失败，调用方只会看到"正在查询…"卡住。
        }

        return course;
    }

    /// <summary>批量查成绩（并发受控）。</summary>
    public async Task EnrichWithScoreAsync(IEnumerable<CourseItem> courses, int concurrency = 4, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = courses.Select(async c =>
        {
            await gate.WaitAsync(ct);
            try { await EnrichWithScoreAsync(c, ct); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    // ── 视频占分权重（CE002 percentage）────────────────────
    // 权重是课程自身的配置（挂多久才到分数线就由它决定），一次查到后整个会话都不会变，
    // 所以按课程身份缓存 —— 列表页反复刷新、合集反复展开都不再重复发请求。

    private readonly object _weightLock = new();
    private readonly Dictionary<string, double?> _weightCache = new(StringComparer.Ordinal);

    private static string WeightKey(CourseItem c)
        => $"{c.CenterCode}|{c.CourseNo}|{c.OlClassNo}";

    private void CacheWeight(CourseItem course, double? weight)
    {
        if (string.IsNullOrEmpty(course.CourseNo)) return;
        lock (_weightLock) _weightCache[WeightKey(course)] = weight;
    }

    /// <summary>取会话内已缓存的视频占分权重；没查过返回 null（不发请求）。</summary>
    public double? CachedDurationWeight(CourseItem course)
    {
        lock (_weightLock)
            return _weightCache.TryGetValue(WeightKey(course), out var w) ? w : null;
    }

    /// <summary>
    /// 查单门课程的视频占分权重（finishInfo 的 CE002 percentage），带会话缓存。
    /// 查不到（接口失败 / 成绩单没有 CE002 明细）返回 null，调用方按"不显示"处理。
    /// </summary>
    public async Task<double?> FetchDurationWeightAsync(CourseItem course, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(course.CourseNo) || string.IsNullOrEmpty(course.OlClassNo))
            return null;

        var cached = CachedDurationWeight(course);
        if (cached is { } hit) return hit;

        var payload = new
        {
            centerCode = course.CenterCode,
            courseNo = course.CourseNo,
            olClassNo = course.OlClassNo,
            tenantCode = DefaultTenant,
        };

        try
        {
            var doc = await _api.PostRawAsync(ApiEndpoints.CourseScore, payload, ct: ct);
            if (doc is null) return null;

            using (doc)
            {
                var data = ApiResponseReader.Data(doc.RootElement);
                if (data is not { } d) return null;

                double? weight = null;
                if (d.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in details.EnumerateArray())
                    {
                        if (Str(item, "attributeCode") != "CE002") continue;
                        weight = Num(item, "percentage");
                        break;
                    }
                }

                // 查到了就落缓存；成绩单里确实没有 CE002（null）也记下来，
                // 免得界面每次刷新都对着同一门课空发请求。接口异常不缓存，下次重试。
                CacheWeight(course, weight);
                return weight;
            }
        }
        catch (Exception)
        {
            return null;   // 与 EnrichWithScoreAsync 同口径：单门失败不影响整体
        }
    }

    /// <summary>
    /// 查单个课件在服务端的播放进度（getMaxTimeAndLastTime）。
    ///
    /// **挂课必需**：平台按每个课件的 maxPlayTime 累计学时，从 00:00:00 重播
    /// 不会增加任何学时。失败返回 null，由调用方决定兜底策略。
    /// </summary>
    public async Task<WareProgress?> GetWareProgressAsync(
        CourseItem course, WareItem ware, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(course.CenterCode) || string.IsNullOrEmpty(course.CourseNo)
            || string.IsNullOrEmpty(course.OlClassNo) || string.IsNullOrEmpty(ware.CataNo)
            || string.IsNullOrEmpty(ware.WareId))
            return null;

        try
        {
            var payload = new
            {
                centerCode = course.CenterCode,
                courseNo = course.CourseNo,
                olClassNo = course.OlClassNo,
                cataNo = ware.CataNo,
                wareId = ware.WareId,
            };

            using var doc = await _api.PostRawAsync(ApiEndpoints.WareProgress, payload, ct: ct);
            if (doc is null) return null;

            var data = ApiResponseReader.Data(doc.RootElement);
            if (data is not { } d) return null;

            var max = Str(d, "maxPlayTime") ?? "";
            return new WareProgress
            {
                CataNo = ware.CataNo!,
                WareId = ware.WareId,
                MaxPlayTime = max,
                LastPlayTime = Str(d, "lastPlayTime") ?? "",
                MaxPlaySeconds = WareProgress.ParseClock(max),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>取课程详情与课件列表（挂课需要 wareId）。</summary>
    public async Task<CourseDetail?> GetCourseDetailAsync(CourseItem course, CancellationToken ct = default)
    {
        // 第一步：按 guid 取详情，补齐 courseNo / olClassNo
        var detailPayload = new
        {
            centerCode = course.CenterCode,
            guid = course.Guid,
            stuClient = true,
        };

        var detailDoc = await _api.PostRawAsync(ApiEndpoints.DetailCourse, detailPayload, ct: ct);
        var courseNo = course.CourseNo;
        var olClassNo = course.OlClassNo;
        var learnStatus = course.LearnStatus;

        if (detailDoc is not null)
        {
            using (detailDoc)
            {
                var d = ApiResponseReader.Data(detailDoc.RootElement);
                if (d is { } dd)
                {
                    courseNo = Str(dd, "courseNo") ?? courseNo;
                    olClassNo = Str(dd, "olClassNo") ?? olClassNo;
                    learnStatus = Str(dd, "learnStatus") ?? learnStatus;
                }
            }
        }

        if (string.IsNullOrEmpty(courseNo) || string.IsNullOrEmpty(olClassNo)) return null;

        var result = new CourseDetail
        {
            CourseNo = courseNo,
            OlClassNo = olClassNo,
            CourseName = course.CourseName,
            CenterCode = course.CenterCode,
            LearnStatus = learnStatus,
        };

        // 第二步：取课程目录树 —— 课件的权威来源，带时长与打点。
        var catalog = await GetCatalogAsync(course.CenterCode, courseNo, ct);
        foreach (var node in catalog) result.Wares.AddRange(node.Wares);

        // 兜底：个别课程不在目录树里，退回课程信息接口。
        if (result.Wares.Count == 0)
        {
            var infoPayload = new { centerCode = course.CenterCode, courseNo, olClassNo };
            var infoDoc = await _api.PostRawAsync(ApiEndpoints.CourseInfo, infoPayload, ct: ct);

            if (infoDoc is not null)
            {
                using (infoDoc)
                {
                    var d = ApiResponseReader.Data(infoDoc.RootElement);
                    if (d is { } dd)
                    {
                        result.CourseName = Str(dd, "courseName") ?? result.CourseName;
                        result.CenterCode = Str(dd, "centerCode") ?? result.CenterCode;

                        foreach (var key in new[] { "wares", "wareList", "courseWareList", "cataList", "details" })
                        {
                            if (!dd.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

                            foreach (var w in arr.EnumerateArray())
                                result.Wares.Add(new WareItem
                                {
                                    WareId = Str(w, "wareCode") ?? Str(w, "wareId") ?? Str(w, "id") ?? "",
                                    WareName = Str(w, "wareName") ?? Str(w, "name") ?? "(未命名课件)",
                                    WareType = Str(w, "wareType") ?? "1",
                                    CataNo = Str(w, "cataNo"),
                                    DurationSeconds = ParseDurationSeconds(w),
                                });

                            if (result.Wares.Count > 0) break;
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>课程维度成绩计算（每完成一门课程后调用）。</summary>
    public async Task<JsonDocument?> SaveCourseDetailAsync(CourseItem course, CancellationToken ct = default)
    {
        var payload = new
        {
            classNo = course.OlClassNo,
            courseNo = course.CourseNo,
        };
        return await _api.PostRawAsync(ApiEndpoints.SaveComputeCourseDetail, payload, ct: ct);
    }

    /// <summary>视频播完后的成绩落库。</summary>
    public async Task<JsonDocument?> SaveAfterVideoPlayedAsync(object payload, CancellationToken ct = default)
        => await _api.PostRawAsync(ApiEndpoints.SaveComputeAfterVideoPlayed, payload, ct: ct);

    /// <summary>初始化学习记录。</summary>
    public async Task<JsonDocument?> InitLearnRecordAsync(object payload, CancellationToken ct = default)
        => await _api.PostRawAsync(ApiEndpoints.InitLearnRecord, payload, ct: ct);

    // ── 解析辅助 ──────────────────────────────────────────

    private static List<JsonElement> ExtractRecords(JsonDocument? doc)
    {
        var list = new List<JsonElement>();
        if (doc is null) return list;

        var data = ApiResponseReader.Data(doc.RootElement);
        var scope = data ?? doc.RootElement;

        if (scope.ValueKind == JsonValueKind.Array)
        {
            list.AddRange(scope.EnumerateArray());
            return list;
        }

        foreach (var key in new[] { "records", "list", "rows", "content" })
            if (scope.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                list.AddRange(arr.EnumerateArray());
                return list;
            }

        return list;
    }

    private static CourseItem ToCourse(JsonElement r) => new()
    {
        CourseName = Str(r, "courseName") ?? Str(r, "olClassName") ?? Str(r, "name") ?? "(未命名课程)",
        Guid = Str(r, "guid") ?? Str(r, "courseGuid") ?? "",
        CenterCode = Str(r, "centerCode") ?? "",
        CenterName = Str(r, "centerName") ?? "",
        CourseNo = Str(r, "courseNo") ?? "",
        OlClassNo = Str(r, "olClassNo") ?? "",
        OlClassType = Str(r, "olClassType") ?? "",
        LearnStatus = Str(r, "learnStatus"),
        CourseHours = Num(r, "courseHours") ?? Num(r, "classHours"),
        ImageUrl = Str(r, "imgUrl") ?? Str(r, "imageUrl"),
        CourseTypeName = Str(r, "courseTypeName"),
        IsMustTeach = Str(r, "isMustTeach"),
        ViewCnt = Str(r, "viewCnt"),
        CourseNum = Str(r, "courseNum"),
        TeacherName = Str(r, "teacherName"),
        BeginTime = Str(r, "beginTime"),
        EndTime = Str(r, "endTime"),
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

    /// <summary>课件时长字段在不同接口里有多种命名，统一折算为秒。</summary>
    private static int? ParseDurationSeconds(JsonElement w)
    {
        foreach (var key in new[] { "duration", "wareTime", "videoTime", "totalTime", "timeLength", "playTime" })
        {
            var v = Num(w, key);
            if (v is not { } x || x <= 0) continue;

            // 约定：小于 1000 视为分钟，否则视为秒
            return x < 1000 ? (int)Math.Round(x * 60) : (int)Math.Round(x);
        }
        return null;
    }
}
