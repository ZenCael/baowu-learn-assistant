namespace BaoWuLearn.Core.Http;

/// <summary>
/// 平台接口地址清单（全部来自运行时抓包与前端 API 库静态提取）。
/// </summary>
public static class ApiEndpoints
{
    /// <summary>网关根地址。</summary>
    public const string Host = "https://learn.baowugroup.com";

    /// <summary>学习业务服务前缀。</summary>
    public const string OpsBase = Host + "/learn-gateway/service/tms/ols";

    /// <summary>认证服务前缀。</summary>
    public const string AuthBase = Host + "/learn-gateway/service/ss/auth/user";

    // ── 认证 ──────────────────────────────────────────────
    /// <summary>图形验证码。</summary>
    public const string CaptchaImage = AuthBase + "/captchaImage";

    /// <summary>账号密码登录。</summary>
    public const string Login = AuthBase + "/login";

    /// <summary>
    /// token 续期（2026-09-12 从网页前端静态提取）：vben 前端定义了
    /// <c>POST /auth/refresh</c>（withCredentials，cookie 凭证），但
    /// <c>enableRefreshToken:!1</c> —— 全站零调用，是死代码；服务端路由实测存在
    /// （无凭证 401「账号未登录」）。
    ///
    /// ★ **客户端刻意不调用此端点**：当前网页前端没有任何流量打它，审计记录里
    ///   唯一规律性调用方 = 自报非标准客户端，指纹风险与未验证的收益不对称。
    ///   仅作调查结论存档；若将来平台前端启用续期（正常流量出现），可再评估。
    /// </summary>
    public const string RefreshToken = Host + "/learn-gateway/service/ss/auth/refresh";

    // ── 课程与专区 ────────────────────────────────────────
    /// <summary>公开课列表（分页）。</summary>
    public const string PublicCourses = OpsBase + "/student/queryPageOpenClass";

    /// <summary>公开课列表（首页版，按时间排序，带封面与学时）。</summary>
    public const string PublicCourseNewest = OpsBase + "/onlineClassCourse/newestOnlineClassCoursePage";

    /// <summary>公开课列表（学生首页版，与上者同源）。</summary>
    public const string PublicCourseHome = OpsBase + "/onlineClassCourse/pcStuCenterHomeOpenCoursePage";

    /// <summary>学习专区列表（按时间排序）。</summary>
    public const string ZoneNewest = OpsBase + "/onlineClass/newestOnlineClassPage";

    /// <summary>学习专区列表（按热度排序）。</summary>
    public const string ZoneHottest = OpsBase + "/onlineClass/hottestOnlineClassPage";

    // ── 平台检索（ES）─────────────────────────────────────
    //
    // 2026-09-13 从网页端「首页大搜索框」逆向得到，并用真实 token 实测确认。
    // 路径挂在 **rls** 服务下（与课程目录的 ols 不同前缀），名字虽叫 teacherLib，
    // 其实是三合一索引，靠报文体里的 lib 选库：
    //   course = 公开课程目录（guid 形如 CS2025OCC…）
    //   zone   = 学习专区（olClassType 全是 ZE0；专题班 ZE1 / 培训班 TCE 不在索引里）
    //   teacher= 讲师
    // ★ 三条实测口径：① 空关键词返回 total:0（不是全量），客户端必须在关键字为空时
    //   走普通目录接口；② data.centerCode 传与不传 total 一样 —— 它不按学习中心过滤；
    //   ③ 记录本身带 rawData，里面有 courseNo / olClassNo / olClassType / centerCode，
    //   检索结果不必二次查询就能直接进挂机队列。
    // 端点虽在 rls 前缀下，仍走同一网关与同一套请求头。

    /// <summary>ES 检索：分页取结果（lib 选库，见上方注释）。</summary>
    public const string EsSearchFromEs = Host + "/learn-gateway/service/tms/rls/teacherLib/searchFromEs";

    /// <summary>ES 检索：各来源命中数（网页端搜索页 tab 上的角标）。</summary>
    public const string EsCountFromEs = Host + "/learn-gateway/service/tms/rls/teacherLib/countFromEs";

    /// <summary>ES 检索选库：公开课程目录。</summary>
    public const string EsLibCourse = "course";

    /// <summary>ES 检索选库：学习专区。</summary>
    public const string EsLibZone = "zone";

    /// <summary>网络专题班 / 培训班列表（按 olClassType 区分：ZE1 专题班，TCE 培训班）。</summary>
    public const string MainOnlineClass = OpsBase + "/onlineClass/queryMainOnlineClassPage";

    /// <summary>学习专区列表。</summary>
    public const string ZoneList = OpsBase + "/student/myClassPage";

    /// <summary>专区下的课程列表（分页）。</summary>
    public const string ZoneCourses = OpsBase + "/onlineClassCourse/getOnlineClassCourseSortPage";

    /// <summary>课程成绩 / 学时完成情况。</summary>
    public const string CourseScore = OpsBase + "/onlineClassCourse/finishInfo";

    /// <summary>按 guid 查课程详情。</summary>
    public const string DetailCourse = OpsBase + "/onlineClassCourse/detailOnlineClassCourse";

    /// <summary>课程信息（含课件列表）。</summary>
    public const string CourseInfo = OpsBase + "/onlineClassCourse/getOnlineClassCourseInfo";

    /// <summary>课程目录树（含每个课件的时长、打点、下载地址）—— 挂课所需的权威来源。</summary>
    public const string CatalogTree =
        Host + "/learn-gateway/service/tms/rls/courseOutline/queryCourseOutlineContentTreeListSimple";

    /// <summary>课程互动信息（收藏 / 点赞状态）。</summary>
    public const string NetInfo = OpsBase + "/onlineClassCourse/getNetInfo";

    // ── 个人中心 ──────────────────────────────────────────
    /// <summary>个人信息（姓名 / 工号 / 组织 / 岗位 / 累计学习时长）。</summary>
    public const string StudentDetails = OpsBase + "/student/queryStudentDetails";

    /// <summary>学时统计（总学时 / 网络自学 / 集中培训 及其构成）。</summary>
    public const string StudyDurationStats = OpsBase + "/student/queryArchivesStudyDurationStatistics";

    /// <summary>我的已选公开课（课程级，含成绩与获得学时）。</summary>
    public const string MyArchivesPublic = OpsBase + "/student/queryArchivesOlClassPage";

    /// <summary>
    /// 我加入的合集（班级）：公开课 OCE / 学习专区 ZE0 / 网络专题班 ZE1 / 培训班 TCE。
    ///
    /// ★ 这是班级级归档的**正确**接口（v1.0.16 站点实测）：body 只需
    ///   <c>{ current, size, data: { classType } }</c>，服务端按 token 认人。
    ///   此前用的 queryArchivesMineOlClassPage 对很多账号返回空（见其注释）。
    /// </summary>
    public const string MyClassPage = OpsBase + "/student/myClassPage";

    /// <summary>
    /// ⚠ 已废弃：曾用于"我的专区 / 专题班 / 培训班"。
    /// 实测（2026-09-11，学员甲 100001）该接口带任何过滤都返回 0 条，
    /// 而网页端同一账号有 2 个专区 —— 正确接口是 <see cref="MyClassPage"/>。
    /// 仅保留常量以免历史代码引用断裂，新代码不要再用。
    /// </summary>
    public const string MyArchivesMine = OpsBase + "/student/queryArchivesMineOlClassPage";

    /// <summary>我的课程收藏。</summary>
    public const string MyCourseCollect = OpsBase + "/netCollect/myCourseCollectPage";

    /// <summary>加入收藏（"加入已选课程"的等价动作之一）。</summary>
    public const string SaveNetCollect = OpsBase + "/netCollect/saveNetCollect";

    /// <summary>我的面授班（LMS 侧）。</summary>
    public const string MyFaceClass = Host + "/learn-gateway/service/tms/lms/classTotalScore/queryArchivesMineLmClassPage";

    /// <summary>服务器当前时间（用于校验本地时钟偏移）。</summary>
    public const string CurSystemTime = OpsBase + "/student/getCurSystemTime";

    /// <summary>当前用户可用的学习中心列表。</summary>
    public const string StuHomeCenterList = OpsBase + "/student/selectCurrentStuHomeCourseAuthCenterList";

    // ── 挂课心跳 ──────────────────────────────────────────
    /// <summary>初始化学习记录（开始学习时调用）。</summary>
    public const string InitLearnRecord = OpsBase + "/learnRecord/initLearnRecord";

    /// <summary>学时心跳上报（约 60s 一次）。</summary>
    public const string SaveLearnHertRecord = OpsBase + "/learnHertRecord/saveLearnHertRecord";

    /// <summary>播放/暂停等操作记录。</summary>
    public const string ListenVideoOptRecord = OpsBase + "/learnVideoRecord/listenVideoOptRecord";

    /// <summary>进度标记（约 30s 一次）。</summary>
    public const string ListenVideoMarkProgress = OpsBase + "/learnWareProgress/listenVideoMarkProgress";

    /// <summary>
    /// 单个课件在服务端的播放进度（最远位置 maxPlayTime / 最后位置 lastPlayTime）。
    ///
    /// **平台按 maxPlayTime 逐课件累计学时**（实测：各课件 maxPlayTime 之和恰等于
    /// finishInfo 的 CE002 finishValue）。因此续播必须从服务端记录的位置起算，
    /// 否则从 00:00:00 重播永远超不过历史最远位置，学时一分钟都不会涨。
    /// </summary>
    public const string WareProgress = OpsBase + "/learnWareProgress/getMaxTimeAndLastTime";

    // ── 成绩结算 ──────────────────────────────────────────
    /// <summary>课程维度成绩计算。</summary>
    public const string SaveComputeCourseDetail = OpsBase + "/computeTask/saveComputeTask4StuCourseDetail";

    /// <summary>视频播完后的成绩落库。</summary>
    public const string SaveComputeAfterVideoPlayed = OpsBase + "/computeTask/saveComputeTask4AfterVideoPlayed";

    /// <summary>查询计算任务。</summary>
    public const string GetComputeTask = OpsBase + "/computeTask/getComputeTask";

    // ── 课件文件下载（v1.0.44+，PDF/附件通道）──────────────
    //
    // 2026-09-22 从 stu bundle 静态提取（FilePreview-WaM-ajig.js）：网页端非视频
    // 课件先查"预览文件"拿 fileId，再走通用文件下载。两接口都要鉴权（token 头），
    // 与视频静态区（VideoStreamBase，零鉴权）是两个世界。

    /// <summary>查课件预览/附件文件（body {businessNo: wareCode}，返回含 fileId 的数组）。</summary>
    public const string FilePreviewUrl = Host + "/learn-gateway/service/tms/adm/filePreview/queryPreviewUrl";

    /// <summary>按 fileId 下载原始文件（GET，query 参数；网页端给 600s 超时档）。</summary>
    public const string FileDownload = Host + "/learn-gateway/service/ss/file/downloadFile";

    /// <summary>
    /// 视频静态区基址（零鉴权公开目录，实测无凭据 200）。
    /// HLS 播放地址 = 本基址 + "/" + 目录树 hashCode + "/index.m3u8"。
    /// </summary>
    public const string VideoStreamBase = Host + "/learn-gateway/video";
}
