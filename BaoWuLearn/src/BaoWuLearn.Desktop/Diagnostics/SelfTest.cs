using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BaoWuLearn.Core.Behavior;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Desktop.Services;
using BaoWuLearn.Desktop.ViewModels;
using BaoWuLearn.Desktop.Views;
using Avalonia;

namespace BaoWuLearn.Desktop.Diagnostics;

/// <summary>
/// 启动自检（用 <c>--selftest</c> 触发）。
///
/// 目的：在**真实的应用进程与渲染环境**里，把「取验证码 → 解码 → 生成位图」这条链路跑一遍。
/// 桌面程序在没有调试器时很难定位问题，这个开关能让使用者在 Windows 或 macOS 上
/// 一条命令拿到诊断结论，不用装任何开发工具。
/// </summary>
public static class SelfTest
{
    public static async Task<string> RunAsync(MainWindowViewModel vm, Window? host)
    {
        // host 仅为兼容旧签名保留；行渲染自检用独立的离屏窗口挂载（见下）。
        _ = host;
        var sb = new StringBuilder();
        void W(string line)
        {
            sb.AppendLine(line);
            Console.WriteLine(line);
        }

        // 让布局与渲染走几拍（Background 优先级排空 + 一段真实等待）。
        // 行渲染类的自检都靠它把"数据已塞进去"变成"控件真的画出来了"。
        async Task PumpAsync()
        {
            for (var i = 0; i < 5; i++)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(300);
        }

        W("========== 宝武学习助手 · 启动自检 ==========");
        W($"版本     : {vm.VersionText}");
        W($"时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"系统     : {Environment.OSVersion} / {(Environment.Is64BitProcess ? "x64" : "x86")}");
        W($"运行时   : {Environment.Version}");
        W($"日志目录 : {AppPaths.LogDirectory}");
        W("");

        var pass = 0;
        var fail = 0;

        // 启动时登录页会自己抓一次验证码。自检要是正好撞上，RefreshCaptchaAsync 会被
        // 「正在刷新」的锁挡掉、**0 毫秒直接返回**，于是 captchaId 为空、报出三条假失败
        //（2026-09-11 实测：同一份代码有一次 19/19、有一次 16/19，就是这个时序）。
        // 先等它收工（成功 → CaptchaId 有值，失败 → Error 有值），再开始正式计时。
        for (var i = 0; i < 32 && vm.Login.CaptchaId is null && vm.Login.Error is null; i++)
            await Task.Delay(250);

        for (var round = 1; round <= 3; round++)
        {
            W($"─── 第 {round} 轮：刷新验证码 ───");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await vm.Login.RefreshCaptchaAsync();
            }
            catch (Exception ex)
            {
                sw.Stop();
                fail++;
                W($"  ✖ 命令抛出了异常（不应发生）：{ex.GetType().Name}: {ex.Message}");
                W("");
                continue;
            }

            sw.Stop();

            var image = vm.Login.CaptchaImage;
            var ok = image is not null && vm.Login.Error is null;

            W($"  耗时      : {sw.ElapsedMilliseconds} ms");
            W($"  captchaId : {vm.Login.CaptchaId ?? "<null>"}");
            W($"  状态      : {vm.Login.StatusText}");
            W($"  错误      : {vm.Login.Error ?? "<无>"}");
            W($"  位图      : {(image is null ? "<null>  ← 未生成" : $"{image.PixelSize.Width} × {image.PixelSize.Height}")}");
            W($"  动图      : {(vm.Login.CaptchaIsAnimated ? "是" : "否")}");
            W($"  结论      : {(ok ? "PASS" : "FAIL")}");
            W("");

            if (ok) pass++; else fail++;
        }

        // ── 页面渲染自检 ──────────────────────────────────
        // 逐个切到各页面并把对应的 View 真正实例化一次。
        // 编译期只能校验绑定路径，模板里的动态资源/样式/自定义类
        // 要等到控件构造时才可能抛错，所以这一步必须跑在真实进程里。
        W("─── 页面渲染自检 ───");
        var locator = new ViewLocator();
        foreach (var (key, title) in new[]
                 {
                     ("dashboard", "总览"),
                     ("fleet", "多挂机"),
                     ("courses", "课程"),
                     ("queue", "学习队列"),
                     ("logs", "运行日志"),
                     ("settings", "设置"),
                 })
        {
            try
            {
                vm.NavigateCommand.Execute(key);
                var view = locator.Build(vm.CurrentPage);

                if (view is TextBlock bad)
                {
                    fail++;
                    W($"  {title,-6} : FAIL  → 未解析到视图（{bad.Text}）");
                    continue;
                }

                pass++;
                W($"  {title,-6} : PASS  → {view.GetType().Name}");
            }
            catch (Exception ex)
            {
                fail++;
                W($"  {title,-6} : FAIL  → {ex.GetType().Name}: {ex.Message}");
            }
        }
        W("");

        // ── 队列页数据行渲染自检 ──────────────────────────
        // v1.0.13 换成 TreeDataGrid 后出现过"表头在、数据行没有"的现象 ——
        // 光把视图实例化一次抓不到这种错：列是代码构造的、行是数据驱动渲染的，
        // 必须真的把行塞进去、让布局跑完、再数一数控件渲染出几行。
        // 注意：不能借用主窗口 —— 主界面层挂 IsVisible=IsLoggedIn，自检没登录时
        // 整层不进可视化树；这里用一个独立的离屏窗口真实挂载。
        W("─── 队列页数据行渲染自检 ───");
        try
        {
            vm.NavigateCommand.Execute("queue");
            var view = new ViewLocator().Build(vm.CurrentPage!);
            view.DataContext = vm.Queue;

            Window? w = null;
            try
            {
                w = new Window { Width = 1100, Height = 700, Content = view, ShowInTaskbar = false };
                w.Show();

                vm.Queue.VisibleCourses.Add(new MyCourseRow(new MyCourseItem
                {
                    CourseName = "自检课程甲",
                    CourseNo = "ST-1",
                    OlClassNo = "SCL-1",
                    OlClassType = "OCE",
                    Category = UserCenterService.Categories.PublicCourse,
                }));
                vm.Queue.VisibleCourses.Add(new MyCourseRow(new MyCourseItem
                {
                    OlClassName = "自检专区乙",
                    OlClassNo = "SZ-1",
                    OlClassType = "ZE0",
                    Category = UserCenterService.Categories.StudyZone,
                }));

                // 让布局与渲染走几拍（Background 优先级排空 + 一段真实等待）
                for (var i = 0; i < 5; i++)
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(300);

                var tree = w.GetVisualDescendants().OfType<TreeDataGrid>().FirstOrDefault();
                var rendered = tree?.Rows?.Count ?? -1;

                var ok = rendered == 2;
                if (ok) pass++; else fail++;

                W($"  注入行数   : 2（1 课程行 + 1 班级行）");
                W($"  渲染行数   : {(rendered < 0 ? "<TreeDataGrid 未找到>" : rendered.ToString())}");
                W($"  结论       : {(ok ? "PASS" : "FAIL")}");
                W("");
            }
            finally
            {
                vm.Queue.VisibleCourses.Clear();   // 清场，别把假数据留给真界面
                w?.Close();
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL  → {ex.GetType().Name}: {ex.Message}");
            W("");
        }

        // ── 挂机列表行状态自检 ────────────────────────────────
        // 用户实测提的问题：「这门课分数已经过了分数线，还写着等待不合适」。
        // 及格就跳策略下这门课根本不会再挂，写成待办的「等待」等于误导；
        // 但「挂满才跳」策略下分数够了也照样要挂到时长满 —— 那时候写「已及格」就是骗人。
        // 所以这一档的状态口径必须与引擎 ShouldSkipCourse **逐字对齐**。
        W("─── 挂机列表行状态自检 ───");
        try
        {
            var savedPolicy = LearnPolicy.Current;
            try
            {
                var passing = new CourseItem
                {
                    CourseName = "自检已过线课", CourseNo = "PQ-1", OlClassNo = "PCL-1",
                    LearnScore = 60.84, PassScore = 60,
                };
                var low = new CourseItem
                {
                    CourseName = "自检未过线课", CourseNo = "PQ-2", OlClassNo = "PCL-2",
                    LearnScore = 12.5, PassScore = 60,
                };

                LearnPolicy.Current = CompletionPolicy.PassScore;
                var passRow = new QueueRow(1, passing);
                var lowRow = new QueueRow(2, low);
                var passState = passRow.StatusText;      // 已及格
                var lowState = lowRow.StatusText;        // 等待

                // 挂满策略：分数够了也还没挂完 → 必须仍显示「等待」
                LearnPolicy.Current = CompletionPolicy.FullDuration;
                var fullState = new QueueRow(1, passing).StatusText;
                LearnPolicy.Current = savedPolicy;

                passRow.IsCurrent = true;
                var currentState = passRow.StatusText;   // 在挂（优先级最高）
                passRow.IsCurrent = false;
                passRow.IsDone = true;                   // 时长也挂满了
                var doneState = passRow.StatusText;

                var ok = passState == "已及格" && !passRow.IsWaiting
                         && lowState == "等待" && lowRow.IsWaiting
                         && fullState == "等待"
                         && currentState == "在挂"
                         && doneState == "已完成";

                if (ok) pass++; else fail++;

                W($"  及格策略 得分60.84/线60 : {passState}（应「已及格」）");
                W($"  及格策略 得分12.5/线60  : {lowState}（应「等待」）");
                W($"  挂满策略 得分60.84/线60 : {fullState}（应仍为「等待」——分数够了也得挂满）");
                W($"  正在挂的那一门          : {currentState}（应「在挂」）");
                W($"  时长挂满的那一门        : {doneState}（应「已完成」）");
                W($"  结论                    : {(ok ? "PASS" : "FAIL")}");
            }
            finally
            {
                LearnPolicy.Current = savedPolicy;   // 不污染后续运行
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 达标判定口径自检（v1.0.31）────────────────────────
        // 上下文：白天日志（2026-09-12）里三门课**全部**出现自相矛盾的两行：
        //   [14:18:57] ✓ 「习近平法治思想专题辅导报告」已够分数线（62.57），提前结束该课
        //   [14:19:08] ⚠ 习近平法治思想专题辅导报告 结算后仍未达标（72分57秒 / 115分钟），可稍后重跑
        // 实测那门课已完成 63.4% 时长、得分 63.43 ≥ 60 线，早就达标了。
        // 根因：引擎跑的是 ShouldSkipCourse（及格策略只看分数），收尾日志却用 course.IsFinished
        //（要求 100% 挂满）——两套口径必然打架，用户照提示"稍后重跑"就白挂几个小时。
        //
        // 同一份日志还暴露了另一个后果（P0）：RunWareAsync 里"已够分数线"只 break 掉**当前课件**，
        // 外层的课件遍历循环没做课程级检查 → 某长视频课 18:54:57 够线后连切 15 个课件，
        // 每个只挂 1 分钟、每个都发一次「视频完成结算」：平台侧看到的就是
        // 「43 分钟的视频 1 分钟就播完」。所以这一份判据现在同时管住三处，这里把它钉死，
        // 防止将来又有人把 IsFinished 用回判定路径、或让某处漏掉课程级检查。
        W("─── 达标判定口径自检 ───");
        try
        {
            var savedPolicy = LearnPolicy.Current;
            try
            {
                // 真实数据：习近平法治思想专题辅导报告 —— 要求 115 分钟、已学 72.95 分钟（63.4%）、得分 63.43
                var halfDone = new CourseItem
                {
                    CourseName = "自检半程已过线课", CourseNo = "DQ-1", OlClassNo = "DCL-1",
                    RequiredDuration = 115, CompletedDuration = 72.95,
                    LearnScore = 63.43, PassScore = 60,
                };

                LearnPolicy.Current = CompletionPolicy.PassScore;
                var passSkip = LearnEngine.ShouldSkipCourse(halfDone);   // 应 true

                LearnPolicy.Current = CompletionPolicy.FullDuration;
                var fullSkip = LearnEngine.ShouldSkipCourse(halfDone);   // 挂满策略下应 false
                LearnPolicy.Current = savedPolicy;

                var rawFinished = halfDone.IsFinished;   // 只挂了 63.4% → 必为 false

                var ok = passSkip && !fullSkip && !rawFinished;

                if (ok) pass++; else fail++;

                W($"  及格策略 72.95/115分 得分63.43/线60 : {(passSkip ? "达标收工" : "继续挂")}（应达标收工）");
                W($"  挂满策略 同一门课                   : {(fullSkip ? "跳过" : "继续挂")}（应继续挂满）");
                W($"  course.IsFinished（要求100%挂满）   : {(rawFinished ? "true" : "false")}（必须 false —— 收尾日志不能再用它）");
                W($"  结论 : {(ok ? "PASS" : "FAIL")}（同一判据管住「开课跳过」「中途停止切课件」「收尾报已完成」）");
            }
            finally
            {
                LearnPolicy.Current = savedPolicy;   // 不污染后续运行
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 结算落库判据自检（v1.0.31）────────────────────────
        // 上下文：「结算已提交但 100 秒内成绩没有变化」在白天日志里误报 3 次
        //（14:20:38 / 19:13:56 / 20:15:25）。根因是探针拿全局的成绩观测版本号当判据：
        //   ① 换课时它被重置为 0，上一门课没跑完的探针带着旧版本号，永远等不到"更大"；
        //   ② 只有心跳回读会推进它，探针自己的回读反而不算数 —— 引擎一停就必然误报。
        // 现在改成"与发起结算那一刻这门课的成绩基线对比"，这里把四档判定钉死。
        W("─── 结算落库判据自检 ───");
        try
        {
            var baseCourse = new CourseItem
            {
                CourseName = "自检课", CourseNo = "SQ-1",
                LearnScore = 60.77, CompletedDuration = 92.4,
            };
            var baseline = (Score: baseCourse.LearnScore, Duration: baseCourse.CompletedDuration);

            var same = LearnEngine.ScoreMovedSince(baseline, baseCourse);   // 应 false
            var scoreUp = LearnEngine.ScoreMovedSince(baseline,
                new CourseItem { LearnScore = 61.42, CompletedDuration = 92.4 });    // 应 true
            var durUp = LearnEngine.ScoreMovedSince(baseline,
                new CourseItem { LearnScore = 60.77, CompletedDuration = 92.75 });   // 应 true
            var jitter = LearnEngine.ScoreMovedSince(baseline,
                new CourseItem { LearnScore = 60.772, CompletedDuration = 92.401 }); // 应 false

            var ok = !same && scoreUp && durUp && !jitter;

            if (ok) pass++; else fail++;

            W($"  成绩一动没动       : {(same ? "算落库" : "继续等")}（应继续等）");
            W($"  只有得分涨 0.65    : {(scoreUp ? "算落库" : "继续等")}（应算落库）");
            W($"  只有时长涨 0.35 分 : {(durUp ? "算落库" : "继续等")}（应算落库）");
            W($"  浮点抖动 0.002     : {(jitter ? "算落库" : "继续等")}（应继续等，别把噪声当落库）");
            W($"  结论 : {(ok ? "PASS" : "FAIL")}（判据锚在这门课的成绩上，不再依赖全局版本号）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 课程页数据行渲染自检 ──────────────────────────────
        // 课程页这一版从「平铺列表 + └ 制表符假装层级」换成 TreeDataGrid（与学习队列页同构）。
        // 与队列页同理：列是代码构造的、行是数据驱动渲染的，光把视图实例化抓不到
        // "表头在、数据行没有"，必须把行塞进去、让布局跑完、再数一数控件渲染出几行。
        W("─── 课程页数据行渲染自检 ───");
        try
        {
            vm.NavigateCommand.Execute("courses");
            var view = new ViewLocator().Build(vm.CurrentPage!);
            view.DataContext = vm.Courses;

            Window? cw = null;
            try
            {
                cw = new Window { Width = 1200, Height = 720, Content = view, ShowInTaskbar = false };
                cw.Show();

                var classRow = new MyCourseRow(new MyCourseItem
                {
                    OlClassName = "自检专区甲",
                    OlClassNo = "CZ-1",
                    OlClassType = "ZE0",
                    Category = UserCenterService.Categories.StudyZone,
                });

                vm.Courses.VisibleRows.Add(classRow);
                vm.Courses.VisibleRows.Add(new MyCourseRow(new MyCourseItem
                {
                    CourseName = "自检课程乙",
                    CourseNo = "CT-1",
                    OlClassNo = "CCL-1",
                    OlClassType = "OCE",
                    Category = UserCenterService.Categories.PublicCourse,
                }));

                await PumpAsync();

                var tree = cw.GetVisualDescendants().OfType<TreeDataGrid>().FirstOrDefault();
                var topRows = tree?.Rows?.Count ?? -1;

                var src = tree?.Source as HierarchicalTreeDataGridSource<MyCourseRow>;

                // ① 合集行的子行**已经就绪**时展开：子行必须真的作为树节点渲染出来。
                //    （这正是旧版"用 └ 制表符假装层级"做不到的 —— 那边子行只是平铺行。）
                //    先把汇总标记置上，展开时就不会再去拉一次班内课程（自检没登录，拉了必失败）。
                var child = MyCourseRow.FromCourse(new CourseItem
                {
                    CourseNo = "CT-2",
                    CourseName = "自检班内课程",
                    OlClassNo = "CZ-1",
                    CenterCode = "C001",
                }, classRow.Category, nested: true, parentKey: classRow.Key);
                classRow.Children.Add(child);
                classRow.ApplyChildStats(new[] { child });

                // ★ 展开接口收的是行路径（IndexPath），不是行对象本身：
                //   第 0 个顶层行就是刚注入的那个合集行。
                src?.Expand(new IndexPath(0));
                await PumpAsync();

                var expandedRows = tree?.Rows?.Count ?? -1;

                // ② 合集行的子行**还没到货**时展开，随后才填进去 —— 这才是课程页的真实节奏
                //    （课程页不做预取：用户点箭头才去拉班内课程，一秒左右才回来）。
                //    树网格必须能跟着集合变化把子行长出来，否则用户看到的就是"点了箭头没反应"。
                var lateRow = new MyCourseRow(new MyCourseItem
                {
                    OlClassName = "自检专区丙",
                    OlClassNo = "CZ-3",
                    OlClassType = "ZE0",
                    Category = UserCenterService.Categories.StudyZone,
                });
                vm.Courses.VisibleRows.Add(lateRow);
                // 先把它标成"班内明细已拉过（拉到 0 门）"：自检没登录，
                // 不标的话展开会真的去请求平台班内课程接口（自检不该打真接口）。
                lateRow.ApplyChildStats(Array.Empty<MyCourseRow>());
                await PumpAsync();

                var beforeLate = tree?.Rows?.Count ?? -1;    // 还没展开 → 4（含甲合集的子行）
                // ★ 路径要按**展开后**的实际行序算：0=甲合集、1=甲的子行、2=课程乙、3=丙合集。
                //   写成 (1) 会点到甲合集的那个子行上（它没有 Children，Expand 是空操作）——
                //   这个自检第一次跑就踩了自己的坑，全靠把每步行数打出来才发现。
                src?.Expand(new IndexPath(3));               // 子行还是空的
                await PumpAsync();

                var emptyExpanded = tree?.Rows?.Count ?? -1; // 展开空合集 → 仍是 4

                var lateChild = MyCourseRow.FromCourse(new CourseItem
                {
                    CourseNo = "CT-4",
                    CourseName = "自检迟到的班内课程",
                    OlClassNo = "CZ-3",
                    CenterCode = "C001",
                }, lateRow.Category, nested: true, parentKey: lateRow.Key);
                lateRow.Children.Add(lateChild);             // 到货
                lateRow.ApplyChildStats(new[] { lateChild });
                await PumpAsync();

                // 控件自己会不会跟着集合变化长出来？只记录，不当断言 ——
                // 这是 TreeDataGrid 内部行为，各版本未必一致，别把自检绑死在它上面。
                var arrivedOnItsOwn = tree?.Rows?.Count ?? -1;

                // 两页真正的保障是这一步：子行到货后补一次展开（见 TreeGridExpansion）。
                TreeGridExpansion.ReExpand(src, vm.Courses.VisibleRows, lateRow);
                await PumpAsync();

                var afterReExpand = tree?.Rows?.Count ?? -1;   // 应 5

                var ok = topRows == 2 && expandedRows == 3
                         && beforeLate == 4 && emptyExpanded == 4 && afterReExpand == 5;

                if (ok) pass++; else fail++;

                W($"  注入顶层行数 : 2（1 合集行 + 1 课程行）");
                W($"  渲染顶层行数 : {(topRows < 0 ? "<TreeDataGrid 未找到>" : topRows.ToString())}");
                W($"  展开已就绪合集 : {(expandedRows < 0 ? "<TreeDataGrid 未找到>" : expandedRows.ToString())}（应 3 = 2 顶层 + 1 子行）");
                W($"  展开空合集     : {(emptyExpanded < 0 ? "<未找到>" : emptyExpanded.ToString())}（应 4，子行还没到）");
                W($"  子行到货（控件自愈） : {(arrivedOnItsOwn < 0 ? "<未找到>" : arrivedOnItsOwn.ToString())}（仅记录）");
                W($"  补一次展开后    : {(afterReExpand < 0 ? "<未找到>" : afterReExpand.ToString())}（应 5 —— 课程页正是「点开才去拉」）");
                W($"  结论           : {(ok ? "PASS" : "FAIL")}");
                W("");
            }
            finally
            {
                vm.Courses.VisibleRows.Clear();   // 清场，别把假数据留给真界面
                cw?.Close();
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL  → {ex.GetType().Name}: {ex.Message}");
            W("");
        }

        // ── 挂机列表「正在挂」行高亮自检 ──────────────────────
        // 用户原话：「正在挂的这个课太不显眼了，就左边一个蓝色亮条」。
        // 现在整行靠 Classes.current 条件类绑定上强调色底 + 文字加粗 + 行内进度条。
        // 条件类绑定是**运行时**才挂上去的（XAML 编译期只校验绑定路径），
        // 所以必须在真实可视化树里核对：那一行的 Border 上到底有没有 current 这个类。
        W("─── 挂机列表「正在挂」行高亮自检 ───");
        try
        {
            vm.NavigateCommand.Execute("queue");
            var view = new ViewLocator().Build(vm.CurrentPage!);
            view.DataContext = vm.Queue;

            Window? qw = null;
            try
            {
                qw = new Window { Width = 1200, Height = 720, Content = view, ShowInTaskbar = false };
                qw.Show();

                var course = new CourseItem
                {
                    CourseName = "自检在挂课", CourseNo = "HQ-1", OlClassNo = "HCL-1",
                    LearnScore = 30, PassScore = 60,
                };
                vm.Queue.QueueItems.Add(new QueueRow(1, course) { IsCurrent = true });
                vm.Queue.QueueItems.Add(new QueueRow(2, new CourseItem
                {
                    CourseName = "自检等待课", CourseNo = "HQ-2", OlClassNo = "HCL-2",
                }));
                vm.Queue.QueueItems.Add(new QueueRow(3, new CourseItem
                {
                    CourseName = "自检挂满的课", CourseNo = "HQ-3", OlClassNo = "HCL-3",
                })
                { IsDone = true });
                vm.Queue.IsConsoleOpen = true;   // 挂机操作区默认收起，这里显式打开

                await PumpAsync();

                var highlighted = qw.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("queuerow") && b.Classes.Contains("current"))
                    .ToList();
                var titleAccent = qw.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Classes.Contains("queuetitle") && t.Classes.Contains("current"));
                var bar = qw.GetVisualDescendants().OfType<ProgressBar>()
                    .Count(p => p.IsVisible);
                // 有结果的（已完成 / 已及格）状态走成功色 —— 同样只在运行时才挂得上去
                var successStatus = qw.GetVisualDescendants().OfType<TextBlock>()
                    .Count(t => t.Classes.Contains("questatus") && t.Classes.Contains("success"));

                var ok = highlighted.Count == 1 && titleAccent && bar >= 1 && successStatus == 1;

                if (ok) pass++; else fail++;

                W($"  带 current 类的行 : {highlighted.Count}（应 1 —— 只有正在挂的那一行）");
                W($"  标题转强调色      : {(titleAccent ? "是" : "否")}");
                W($"  带 success 类状态 : {successStatus}（应 1 —— 已完成/已及格的行）");
                W($"  可见进度条        : {bar}（应 ≥1 —— 当前行的实时进度）");
                W($"  结论              : {(ok ? "PASS" : "FAIL")}");
                W("");
            }
            finally
            {
                vm.Queue.QueueItems.Clear();
                vm.Queue.IsConsoleOpen = false;
                qw?.Close();
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL  → {ex.GetType().Name}: {ex.Message}");
            W("");
        }

        // ── 登录页账号下拉自检 ────────────────────────────
        // 登录页是"页面渲染自检"覆盖不到的那一个（它不在导航里，只在未登录时显示），
        // 而 v1.0.18 恰好在这儿第一次用了 Popup（账号下拉）。
        // Popup 的坑很隐蔽：属性名写错、模板不渲染都只有运行时才暴露 ——
        // 所以用一个隔离的 VM + 临时存档，真实挂载一次。
        W("─── 登录页账号下拉自检 ───");
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"bw-selftest-login-{Guid.NewGuid():N}.json");
            var api = new ApiClient();
            Window? lw = null;
            try
            {
                var loginVm = new LoginViewModel(
                    new AuthService(api), new AccountStore(tempPath), _ => { }, _ => { });

                loginVm.SavedAccounts.Add(new SavedAccountRow("100001", "自检账号甲", true, null, _ => { }, _ => { }));
                loginVm.SavedAccounts.Add(new SavedAccountRow("100001", null, false, null, _ => { }, _ => { }));

                var view = new LoginView { DataContext = loginVm };
                lw = new Window { Width = 480, Height = 760, Content = view, ShowInTaskbar = false };
                lw.Show();

                loginVm.IsAccountListOpen = true;

                for (var i = 0; i < 5; i++)
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(300);

                var box = view.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                var popup = view.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                var list = popup?.Child?.GetLogicalDescendants().OfType<ItemsControl>().FirstOrDefault();

                var ok = box is not null
                         && popup is not null && popup.IsOpen
                         && list is not null && list.ItemCount == 2;

                if (ok) pass++; else fail++;

                W($"  员工号输入框 : {(box is null ? "未找到" : "已渲染")}");
                W($"  账号下拉     : {(popup is null ? "未找到" : popup.IsOpen ? "已展开" : "未展开")}");
                W($"  下拉项数     : {(list is null ? "<模板未渲染>" : list.ItemCount.ToString())}（应 2）");
                W($"  结论         : {(ok ? "PASS" : "FAIL")}");
                W("");
            }
            finally
            {
                lw?.Close();
                api.Dispose();
                SafeDelete(tempPath);
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL  → {ex.GetType().Name}: {ex.Message}");
            W("");
        }

        // ── 心跳倒计时自检 ────────────────────────────────
        // 「下次心跳」是每秒推进的，光"编译通过"说明不了它会动。
        // 这里用同一目标时刻、不同"当前时刻"验算：秒数必须递减。
        W("─── 心跳倒计时自检 ───");
        try
        {
            var t0 = DateTimeOffset.Now;
            var target = t0.AddSeconds(30);

            var remain30 = QueueViewModel.FormatBeatCountdown(target, EngineState.Running, t0);
            var remain20 = QueueViewModel.FormatBeatCountdown(target, EngineState.Running, t0.AddSeconds(10));
            var expired = QueueViewModel.FormatBeatCountdown(target, EngineState.Running, t0.AddSeconds(45));
            var idle = QueueViewModel.FormatBeatCountdown(target, EngineState.Paused, t0);

            var ok = remain30 == "约 30 秒后" && remain20 == "约 20 秒后"
                     && expired == "约 0 秒后" && idle == "—";

            if (ok) pass++; else fail++;

            W($"  剩余 30s 时 : {remain30}");
            W($"  剩余 20s 时 : {remain20}");
            W($"  已过期      : {expired}");
            W($"  已暂停      : {idle}");
            W($"  结论        : {(ok ? "PASS" : "FAIL")}（秒数须随当前时刻递减，不能是固定值）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 合集行口径自检 ────────────────────────────────
        // 用户实测反馈：合集行写「已完成 1 门 · 未完成 1 门」，却展开出 21 门课程，看着自相矛盾。
        // 根因是平台那两个字段只统计「已开始学习」的课程，不是合集课程总数
        // （已用真实账号逐班核对原始报文确认，见 缺陷修复说明-v1.0.8.md）。
        // 这里用真实测到的那组数字验算文案：21 门 = 19 未开始 + 1 进行中 + 1 已完成。
        W("─── 合集行口径自检 ───");
        try
        {
            var classRow = new MyCourseRow(new MyCourseItem
            {
                OlClassName = "自检用学习专区",
                OlClassNo = "SELFTEST",
                CenterCode = "C001",
                OlClassType = "ZE0",
                Category = "网络自学 · 学习专区",
                // v1.0.16 起班级行来自 student/myClassPage，平台直接给这几个数：
                CourseNum = 21,           // 合集课程总数（courseNum）
                LearnedCourseNum = 1,     // 本人已学门数（learnNum）
                ClassHours = 14,          // 专区学时（classHours）
            });

            var children = new List<MyCourseRow>();
            for (var i = 0; i < 21; i++)
            {
                children.Add(MyCourseRow.FromCourse(new CourseItem
                {
                    CourseNo = "SELF" + i,
                    CourseName = "自检课程 " + (i + 1),
                    OlClassNo = "SELFTEST",
                    CenterCode = "C001",
                    LearnStatus = i == 0 ? "2" : i == 1 ? "1" : "0",
                }, classRow.Category, nested: true, parentKey: classRow.Key));
            }

            var before = classRow.DetailText;
            classRow.ApplyChildStats(children);        // 明细到货（树网格展开时由加载器调用）
            var after = classRow.DetailText;
            var afterCollapsed = after;                // 到货后收起态与展开态同一份汇总

            var childStates = string.Join("/", new[]
            {
                children[0].StatusText, children[1].StatusText, children[2].StatusText,
            });

            // 专区行的「状态」「学时」两列：myClassPage 不返回状态字段，得靠推导，
            // 否则界面上是一列空白（用户看到的专区行必须三列都有内容）。
            var freshZone = new MyCourseRow(new MyCourseItem
            {
                OlClassName = "自检未开始专区",
                OlClassNo = "SELFTEST2",
                OlClassType = "ZE0",
                Category = "网络自学 · 学习专区",
                CourseNum = 4,
                LearnedCourseNum = 0,
                ClassHours = 2,
            });
            var zeroState = freshZone.StatusText;      // 已学 0 门 → 未开始
            var zeroDetail = freshZone.DetailText;     // 共 4 门 · 尚未开始学习
            var zoneState = classRow.StatusText;       // 已学 1 门 → 进行中
            var zoneHours = classRow.HoursText;        // classHours=14 → "14"

            // 核心不变式：
            //  ① 明细未到货（ChildStatsReady=false）：报平台 myClassPage 给的权威口径
            //     「共 N 门 · 已学 M 门」（N=courseNum，M=learnNum）。
            //  ② 明细到货（ApplyChildStats 置 ChildStatsReady）：收起态也显示真实汇总，
            //     且「已完成 + 进行中 + 未开始」必须等于子行总数 —— 当初的矛盾就出在这里。
            //  ③ 状态 / 学时列非空且口径正确（云 classHours 直接显示）。
            //  ★ 子树展开状态现在归 TreeDataGrid 管，行上没有自己的开关了，
            //    所以这里只验"汇总与实际门数同源"，不再验展开/收起两态互切。
            var ok = before == "共 21 门 · 已学 1 门"
                     && after == "21 门 · 已完成 1 · 进行中 1 · 未开始 19"
                     && afterCollapsed == after
                     && childStates == "已完成/进行中/未开始"
                     && zoneState == "进行中"
                     && zeroState == "未开始"
                     && zeroDetail == "共 4 门 · 尚未开始学习"
                     && zoneHours == "14";

            if (ok) pass++; else fail++;

            W($"  未预取副标题 : {before}（明细未到 → 报平台 courseNum/learnNum 口径）");
            W($"  预取后副标题 : {after}（明细已到 → 真实汇总）");
            W($"  收起态副标题 : {afterCollapsed}（预取到货后收起也显示真实汇总）");
            W($"  子行状态     : {childStates}");
            W($"  专区状态列   : 已学1门→{zoneState} / 已学0门→{zeroState}（应 进行中 / 未开始）");
            W($"  专区学时列   : {zoneHours}（classHours=14）");
            W($"  零进度副标题 : {zeroDetail}");
            W($"  结论         : {(ok ? "PASS" : "FAIL")}（预取后 已完成+进行中+未开始 必须等于子行总数，且收起态与展开态一致）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 挂机区按秒插值自检 ─────────────────────────────
        // 用户实测反馈：进度条和时长只在心跳（约 60 秒）时跳一格，两次之间完全冻住。
        // 修法是界面上按本地流逝时间插值。插值是纯函数，必须验算：
        // 正常推进、封顶不超过 100%、倒流（时钟回拨）不动 —— 三种都不能错。
        W("─── 挂机区按秒插值自检 ───");
        try
        {
            var t1 = QueueViewModel.InterpolatePlayed(300, 600, 5);     // +5 秒
            var t2 = QueueViewModel.InterpolatePlayed(590, 600, 400);   // 冲过终点要封顶
            var t3 = QueueViewModel.InterpolatePlayed(300, 600, -20);   // 时钟回拨不能倒着走
            var t4 = QueueViewModel.InterpolatePlayed(0, 0, 60);        // 目标未知时不封顶

            var ok = t1 == 305 && t2 == 600 && t3 == 300 && t4 == 60;

            if (ok) pass++; else fail++;

            W($"  快照 300s + 5s  → {t1}（应为 305）");
            W($"  快照 590s + 400s → {t2}（应为 600，封顶）");
            W($"  快照 300s - 20s → {t3}（应为 300，不许倒流）");
            W($"  目标 0 时       → {t4}（应为 60，不封顶）");
            W($"  结论            : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 进度显示单调性自检（"进度条回退"）────────────────
        // 复现 2026-09-11 20:46~20:47 的真实日志：
        //   心跳 #1 报 00:05:01 → 引擎插入 21 秒模拟暂停（一秒都不计学时）→ 心跳 #2 只报 00:05:59，
        //   而界面按墙钟在这一共 82 秒里已经插值推到 00:06:23。
        //   新快照一到，显示值整段掉回 00:05:59 —— 用户截图里的"进度条会回退"。
        // 这一项钉两条不变式：① 显示值只许前进；② 模拟暂停窗口内界面停表。
        W("─── 进度显示单调性自检 ───");
        try
        {
            const double snap1 = 301;      // 心跳 #1：00:05:01
            const double snap2 = 359;      // 心跳 #2：00:05:59（引擎实际只推进 58s）
            const double wallClock = 82;   // 两次心跳的真实墙钟间隔（含 21s 模拟暂停）

            var interpolated = QueueViewModel.InterpolatePlayed(snap1, 1229, wallClock);
            var rollback = interpolated - snap2;                        // 旧逻辑要回退的秒数
            var shown = QueueViewModel.AdvanceDisplay(0, interpolated);  // 插值先行
            var afterSnapshot = QueueViewModel.AdvanceDisplay(shown, snap2);   // 回落快照到达
            var stillForward = QueueViewModel.AdvanceDisplay(afterSnapshot, 400) > afterSnapshot;

            var now = DateTimeOffset.Now;
            var pauseOk = QueueViewModel.IsPausing(now.AddSeconds(11), now)      // 暂停中 → 停表
                          && !QueueViewModel.IsPausing(now.AddSeconds(-1), now)  // 已结束 → 恢复
                          && !QueueViewModel.IsPausing(null, now);               // 没暂停 → 正常

            var ok = Math.Abs(rollback - 24) < 0.01
                     && Math.Abs(afterSnapshot - interpolated) < 0.01
                     && stillForward && pauseOk;

            if (ok) pass++; else fail++;

            W($"  心跳 #1 → #2     : 墙钟 {wallClock:0}s，含模拟暂停 21s（不计学时）");
            W($"  引擎实际推进     : {snap2 - snap1:0}s（{snap1:0}s → {snap2:0}s）");
            W($"  界面插值推到     : {interpolated:0}s（超前 {rollback:0}s）");
            W($"  旧逻辑此刻显示   : {snap2:0}s ← 回退 {rollback:0}s，就是用户看到的那个现象");
            W($"  新逻辑此刻显示   : {afterSnapshot:0}s（维持不回退，随后从 {afterSnapshot:0}s 继续前进）");
            W($"  前进仍然生效     : {(stillForward ? "是" : "否")}");
            W($"  暂停窗口判定     : {(pauseOk ? "正确（暂停中停表 / 结束恢复 / 无暂停正常）" : "错误")}");
            W($"  结论             : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 进度时钟状态机自检（v1.0.32）──────────────────────
        // 队列页与总览页以前各写一套插值状态（锚点 + 单调 floor + 归属键），
        // 于是出了"总览页的计时器不按秒跳"这种只修一边就漏另一边的问题。
        // 现在两页共用 LiveProgressClock，这一项把它的三条纪律钉死：
        //   ① 换内容（换课件 / 换课程）必须清零，否则新课件卡在旧高位上不动；
        //   ② 显示值只许前进，快照回落不许把显示值拽回去；
        //   ③ 模拟暂停窗口内停表，窗外的墙钟时间照常计入。
        W("─── 进度时钟状态机自检 ───");
        try
        {
            var t0 = DateTimeOffset.Now;
            var clock = new LiveProgressClock();

            // ① 首个快照入钟（首次也算"换内容"）→ 再换一个课件，进度必须清零重来
            var first = clock.Accept("c1#1", 100, t0);
            var beforeSwitch = clock.Advance(600, null, t0);            // 100s
            var switched = clock.Accept("c1#2", 30, t0);                // 换到第 2 个课件
            var afterSwitch = clock.Advance(600, null, t0);             // 30s（清零后重新爬）

            // ② 单调：正常前进，随后到达一个"回落"的快照，显示值不许跟着退
            var advanced = clock.Advance(600, null, t0.AddSeconds(10)); // 30 + 10 = 40s
            clock.Accept("c1#2", 35, t0.AddSeconds(10));                // 回落快照（35 < 40）
            var noRollback = clock.Advance(600, null, t0.AddSeconds(10));

            // ③ 暂停停表：窗内怎么等都不动，窗外照常走
            clock.Accept("c1#2", 40, t0.AddSeconds(10));
            var paused = clock.Advance(600, t0.AddSeconds(30), t0.AddSeconds(20));
            var afterPause = clock.Advance(600, t0.AddSeconds(30), t0.AddSeconds(40));

            var ok = first && switched
                     && Math.Abs(beforeSwitch - 100) < 0.01
                     && Math.Abs(afterSwitch - 30) < 0.01
                     && Math.Abs(advanced - 40) < 0.01
                     && Math.Abs(noRollback - 40) < 0.01
                     && Math.Abs(paused - 40) < 0.01
                     && afterPause > paused;

            if (ok) pass++; else fail++;

            W($"  换内容清零       : 100s →(换课件)→ {afterSwitch:0}s（应为 30s）");
            W($"  正常前进         : 30s + 墙钟 10s → {advanced:0}s（应为 40s）");
            W($"  快照回落         : 收到 35s 后仍显示 {noRollback:0}s（应为 40s，不许倒着走）");
            W($"  暂停窗内停表     : {paused:0}s（应为 40s，原地不动）");
            W($"  暂停结束恢复     : {afterPause:0}s（应大于 40s）");
            W($"  结论             : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 完成策略折算自检 ──────────────────────────────
        // 设置项「挂满才跳 / 及格就跳」。折算公式：目标时长 × 分数线% ÷ 视频占分权重。
        // 分数线缺失或不合理（≤0 或 >100）要退回 60 分；
        // 权重未知时退回旧的 /100 口径。实测各课权重不同（70/30、80/20 都有）——
        // 权重 80 的课 60 分要挂到 75% 时长（48.08/58 × 80% = 66.32 分，用户实测）。
        W("─── 完成策略折算自检 ───");
        try
        {
            var saved = LearnPolicy.Current;
            try
            {
                LearnPolicy.Current = CompletionPolicy.FullDuration;
                var full = LearnPolicy.TargetSeconds(1800, 60);

                LearnPolicy.Current = CompletionPolicy.PassScore;
                var pass60 = LearnPolicy.TargetSeconds(1800, 60);     // 60% → 1080
                var pass75 = LearnPolicy.TargetSeconds(1800, 75);     // 75% → 1350
                var passDefault = LearnPolicy.TargetSeconds(1800, null); // 没给分数线 → 按 60
                var passBad = LearnPolicy.TargetSeconds(1800, 150);   // 荒谬分数线 → 按 60

                // 权重口径：60 分 ÷ 权重 = 需要挂到的时长比例
                var w80 = LearnPolicy.TargetSeconds(3480, 60, 80);   // 60/80=75% → 2610（43m30s）
                var w70 = (LearnPolicy.TargetSeconds(3480, 60, 70) ?? 0);   // 60/70≈85.7% → 2982.86
                var wUnk = LearnPolicy.TargetSeconds(3480, 60, null); // 权重未知 → 旧口径 60% → 2088
                var wOver = LearnPolicy.TargetSeconds(3480, 60, 50);  // 60/50>100% → 封顶全长

                var ok = full == 1800 && pass60 == 1080 && pass75 == 1350
                         && passDefault == 1080 && passBad == 1080
                         && w80 == 2610 && Math.Abs(w70 - 2982.857) < 0.01
                         && wUnk == 2088 && wOver == 3480;

                if (ok) pass++; else fail++;

                W($"  挂满策略 1800s     → {full}（应原样 1800）");
                W($"  及格 60 分 1800s  → {pass60}（应 1080）");
                W($"  及格 75 分 1800s  → {pass75}（应 1350）");
                W($"  无分数线（默认60） → {passDefault}（应 1080）");
                W($"  分数线 150（荒谬） → {passBad}（应 1080）");
                W($"  权重80: 60分/58m  → {w80}（应 2610 = 75% —— 04 课实测口径）");
                W($"  权重70: 60分/58m  → {w70:0.##}（应 ≈2982.86）");
                W($"  权重未知          → {wUnk}（应 2088 = 旧 /100 口径）");
                W($"  权重50（60/50>1） → {wOver}（应封顶全长 3480）");
                W($"  结论               : {(ok ? "PASS" : "FAIL")}");
            }
            finally
            {
                LearnPolicy.Current = saved;   // 不污染后续运行
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 平台单跳学时上限自检（v1.0.35）──────────────────
        // 2026-09-13 定案：平台对单次心跳最多只计入 60 秒，超出部分丢掉。
        // 这里直接用**当晚那三个「分数线 100 + 视频占分 100%」专区课程的真实心跳序列**验算
        // Σ min(单跳, 60)，结果必须与平台回读的学习时长逐秒一致（884 / 892 / 885 秒）。
        // 这样这条口径一旦被后人改坏，自检会立刻发现，而不是等用户挂到 98 分才发现。
        W("─── 平台单跳学时上限自检（v1.0.35）───");
        try
        {
            // 三个专区课程的心跳 learnTime 序列（摘自 baowu-learn-20260913-083054.log）
            int[] netLaw = { 62, 61, 58, 61, 60, 61, 62, 62, 62, 62, 61, 61, 58, 60, 48 };
            int[] netLawNew = { 62, 61, 59, 59, 58, 58, 58, 60, 59, 62, 58, 61, 59, 61, 61, 4 };
            int[] dataMgmt = { 59, 61, 62, 62, 60, 62, 62, 62, 60, 60, 62, 59, 62, 59, 48 };

            static int Raw(int[] beats) => beats.Sum();
            static int Capped(int[] beats) => beats.Sum(b => Math.Min(b, Humanize.MaxCreditPerBeatSeconds));

            const int cap = Humanize.MaxCreditPerBeatSeconds;
            var ok = cap == 60
                     && Raw(netLaw) == 899 && Capped(netLaw) == 884     // 平台回读 14分44秒
                     && Raw(netLawNew) == 900 && Capped(netLawNew) == 892 // 平台回读 14分52秒
                     && Raw(dataMgmt) == 900 && Capped(dataMgmt) == 885  // 平台回读 14分45秒
                     // 截断口径本身：58 秒照记、63 秒只记 60
                     && Humanize.LearnSeconds(63) is >= 62 and <= 63
                     && Humanize.MaxCreditPerBeatSeconds == 60;

            // CreditedSeconds 在关闭拟人化抖动后必须严格等于 min(间隔, 60)
            var humanSaved = Humanize.Current;
            int credited63, credited60, credited58;
            try
            {
                Humanize.Current = Humanize.Level.Off;
                credited63 = Humanize.CreditedSeconds(63);
                credited60 = Humanize.CreditedSeconds(60);
                credited58 = Humanize.CreditedSeconds(58);
                ok = ok && credited63 == 60 && credited60 == 60 && credited58 == 58
                        && Humanize.CreditedSeconds(0.4) == 1;
            }
            finally
            {
                Humanize.Current = humanSaved;
            }

            if (ok) pass++; else fail++;

            W($"  单跳上限                  : {cap}s（应 60）");
            W($"  网络安全法  Σ原始/平台口径 : {Raw(netLaw)} / {Capped(netLaw)} 秒（平台回读 14分44秒 = 884）");
            W($"  新版宣贯    Σ原始/平台口径 : {Raw(netLawNew)} / {Capped(netLawNew)} 秒（平台回读 14分52秒 = 892）");
            W($"  数据管理办法 Σ原始/平台口径 : {Raw(dataMgmt)} / {Capped(dataMgmt)} 秒（平台回读 14分45秒 = 885）");
            W($"  CreditedSeconds(63/60/58)  : {credited63} / {credited60} / {credited58}（截断到 60）");
            W($"  结论                      : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 零余量课程识别自检（v1.0.35）────────────────────
        // 「分数线 100 + 视频占分 100%」的 PDF 专区课：目标 = 100% 时长，一秒余量都没有。
        // 识别对了才会在日志里给出「本课零余量：必须挂满完整时长」那行提示。
        W("─── 零余量课程识别自检（v1.0.35）───");
        try
        {
            var policySaved = LearnPolicy.Current;
            try
            {
                LearnPolicy.Current = CompletionPolicy.PassScore;
                var zeroMargin = LearnPolicy.IsZeroMargin(100, 100);   // 专区课 → 零余量
                var zeroNoWeight = LearnPolicy.IsZeroMargin(100, null); // 权重未知，按 100 算 → 仍是零余量
                var normal80 = LearnPolicy.IsZeroMargin(60, 80);        // 60/80 = 75% 时长
                var normal100 = LearnPolicy.IsZeroMargin(60, 100);      // 60/100 = 60% 时长
                var defaultLine = LearnPolicy.IsZeroMargin(null, null); // 缺省分数线 60 → 非零余量
                var target = LearnPolicy.TargetSeconds(900, 100, 100);  // 零余量 → 目标就是全长

                LearnPolicy.Current = CompletionPolicy.FullDuration;
                var fullMode = LearnPolicy.IsZeroMargin(100, 100);      // 挂满模式下不谈"零余量"

                var ok = zeroMargin && zeroNoWeight && !normal80 && !normal100
                         && !defaultLine && target == 900 && !fullMode;

                if (ok) pass++; else fail++;

                W($"  分数线100/占分100% → {zeroMargin}（应 True = 必须挂满 15m00s）");
                W($"  分数线100/占分未知 → {zeroNoWeight}（应 True，权重缺失按 100）");
                W($"  分数线60/占分80%   → {normal80}（应 False = 挂 75% 就够）");
                W($"  分数线60/占分100%  → {normal100}（应 False = 挂 60% 就够）");
                W($"  分数线缺省(60)     → {defaultLine}（应 False）");
                W($"  零余量目标(900s)   → {target}（应 900，不打折）");
                W($"  挂满模式下         → {fullMode}（应 False）");
                W($"  结论               : {(ok ? "PASS" : "FAIL")}");
            }
            finally
            {
                LearnPolicy.Current = policySaved;
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 位置满 ≠ 学时满 · 补学时判据自检（v1.0.36）────────
        // 2026-09-13 用户升级到 v1.0.35 实测，当场证伪了 WareProgress 的老注释
        // （"各课件 maxPlayTime 之和 == CE002 的 finishValue"）：零余量 PDF 专区
        // 三门课的位置都到了 15:00，成绩单却只有 14分44 / 14分52 / 14分45 秒，
        // 得分 98.22 / 99.11 / 98.33 卡死不再涨。
        // 这一节把"位置满但学时不满 → 必须补学时"固化下来，
        // 防止后人再把它"优化"成"位置到顶就跳过"。
        W("─── 位置满≠学时满 · 补学时判据自检（v1.0.36）───");
        try
        {
            // (位置, 平台已计入学时, 课件要求时长, 实测得分) —— 均摘自 baowu-learn-20260913-091541.log
            (int Pos, int Learned, int Required, double Score)[] cases =
            {
                (899, 884, 900, 98.22),   // 中华人民共和国网络安全法
                (900, 892, 900, 99.11),   // 新版《中华人民共和国网络安全法》的宣贯与解读
                (900, 885, 900, 98.33),   // 中国宝武《数据管理办法》（2025版）
            };

            var ok = true;
            foreach (var c in cases)
            {
                var posFull = c.Pos >= c.Required - 1;                                  // 位置到顶（IsWareFull 的口径）
                var learnedShort = c.Learned < c.Required;                              // 平台学时没到顶
                var scoreMatches = Math.Abs(c.Learned / (double)c.Required * 100 - c.Score) < 0.05;  // 得分 = 学时÷要求
                var needs = LearnEngine.NeedsTopUp(c.Pos, c.Required, courseNeedsTopUp: true);
                var noNeedWhenReached = !LearnEngine.NeedsTopUp(c.Pos, c.Required, courseNeedsTopUp: false);
                var noNeedWhenNotFull = !LearnEngine.NeedsTopUp(400, c.Required, courseNeedsTopUp: true);

                ok = ok && posFull && learnedShort && scoreMatches
                     && needs && noNeedWhenReached && noNeedWhenNotFull;

                W($"  位置{c.Pos}s 平台学时{c.Learned}s / 要求{c.Required}s → 得分 {c.Score:0.##}"
                  + $"（验算 {c.Learned / (double)c.Required * 100:0.##}）"
                  + $" 缺口 {c.Required - c.Learned}s");
            }

            // 缺口上界：心跳间隔最大 63s、单跳只记 60s → 每跳最多丢 3s；
            // 15 分钟课 ≈ 15 跳 → 最坏 45s，一跳（≤60s）就能补平。
            var worstGap = (63 - Humanize.MaxCreditPerBeatSeconds) * (900 / 58.0);
            var oneBeatEnough = Humanize.MaxCreditPerBeatSeconds >= worstGap;
            var capSane = LearnEngine.MaxTopUpBeats >= 2 && LearnEngine.MaxTopUpBeats <= 12;
            // 边界：IsWareFull 的容差是 >= target - 1，所以 899/900 算满、898/900 不算
            var fullBoundary = LearnEngine.NeedsTopUp(899, 900, true);
            var notFullBoundary = LearnEngine.NeedsTopUp(898, 900, true);
            // 门控（CourseNeedsTopUp = 未达标 + 零余量）由"零余量课程识别自检"那一节负责，
            // 这里只验位置侧的判据，避免两节重复断言同一件事
            ok = ok && oneBeatEnough && capSane && fullBoundary && !notFullBoundary;

            if (ok) pass++; else fail++;

            W($"  最坏缺口（15 分钟课）     : ≈{worstGap:0}s ——单跳上限 {Humanize.MaxCreditPerBeatSeconds}s"
              + $" 能否一跳补平：{oneBeatEnough}");
            W($"  位置 899/900 判满 → 补学时 : {fullBoundary}（应 True，与 IsWareFull 的容差一致）");
            W($"  位置 898/900 未满 → 不补   : {notFullBoundary}（应 False，差 1 秒以上才补）");
            W($"  补学时跳数上限            : {LearnEngine.MaxTopUpBeats}（应 2~12）");
            W($"  结论                      : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 清除已合格课程判据自检（v1.0.37）────────────────
        // 挂机列表新增的「清除已合格」按钮只该摘掉**引擎本来也会一路跳过**的课：
        // 正在挂的那门不能动（删它会让运行循环立刻收尾、打断当前），
        // 没挂完的更不能动。判据复用 ShouldSkipCourse，这里把四种组合钉死，
        // 免得以后有人顺手改成"分数 ≥ 分数线就删"，把挂满策略下的课误清掉。
        W("─── 清除已合格课程判据自检（v1.0.37）───");
        try
        {
            var policySavedForClear = LearnPolicy.Current;
            try
            {
                var passedCourse = new CourseItem
                {
                    CourseName = "已过线", CourseNo = "CLR-A", OlClassNo = "X",
                    LearnScore = 72.5, PassScore = 60,
                };
                var pending = new CourseItem
                {
                    CourseName = "没挂完", CourseNo = "CLR-B", OlClassNo = "X",
                    LearnScore = 31, PassScore = 60,
                };
                var finished = new CourseItem
                {
                    CourseName = "已挂满", CourseNo = "CLR-C", OlClassNo = "X",
                    RequiredDuration = 900, CompletedDuration = 900,
                    LearnScore = 0, PassScore = 60,
                };

                LearnPolicy.Current = CompletionPolicy.PassScore;
                var clearPassed = LearnEngine.ShouldClearPassed(passedCourse, isCurrentRunning: false);
                var keepCurrent = !LearnEngine.ShouldClearPassed(passedCourse, isCurrentRunning: true);
                var keepPending = !LearnEngine.ShouldClearPassed(pending, isCurrentRunning: false);
                var clearFinished = LearnEngine.ShouldClearPassed(finished, isCurrentRunning: false);

                // 挂满才跳策略下，分数再高也不算合格（与界面「已及格」同源）——不能被清走
                LearnPolicy.Current = CompletionPolicy.FullDuration;
                var keepHighScoreUnfinished = !LearnEngine.ShouldClearPassed(passedCourse, isCurrentRunning: false);

                var ok = clearPassed && keepCurrent && keepPending && clearFinished && keepHighScoreUnfinished;
                if (ok) pass++; else fail++;

                W($"  已过线（72.5 / 60）        : 清 {clearPassed}（应 True）");
                W($"  已过线但正在挂             : 清 {!keepCurrent}（应 False —— 不打断当前）");
                W($"  没挂完（31 / 60）          : 清 {!keepPending}（应 False）");
                W($"  已挂满（900 / 900 秒）     : 清 {clearFinished}（应 True）");
                W($"  挂满策略 + 高分未挂满      : 清 {!keepHighScoreUnfinished}（应 False，与引擎口径一致）");
                W($"  结论                       : {(ok ? "PASS" : "FAIL")}");
            }
            finally
            {
                LearnPolicy.Current = policySavedForClear;
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 课件序号文案自检 ──────────────────────────────
        // 挂机操作区要显示"共 N 个视频 · 正在播第 X 个"。序号按大纲原始顺序（不是挂机顺序），
        // 未发车显示"准备开始"，总数未知返回空串让提示藏起来 —— 三种都不能出错。
        W("─── 课件序号文案自检 ───");
        try
        {
            var p1 = QueueViewModel.FormatWareProgress(3, 21);   // 正常播放中
            var p2 = QueueViewModel.FormatWareProgress(0, 21);   // 课件还没发车
            var p3 = QueueViewModel.FormatWareProgress(3, 0);    // 总数未知（引擎未挂课）
            var p4 = QueueViewModel.FormatWareProgress(1, 1);    // 单课件课程

            var ok = p1 == "共 21 个视频 · 正在播第 3 个"
                     && p2 == "共 21 个视频 · 准备开始"
                     && p3 == ""
                     && p4 == "共 1 个视频 · 正在播第 1 个";

            if (ok) pass++; else fail++;

            W($"  第3/21个   : {p1}");
            W($"  未发车     : {p2}");
            W($"  总数未知   : {(p3 == "" ? "<空串，隐藏>" : p3)}");
            W($"  单课件课程 : {p4}");
            W($"  结论       : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 结算节奏自检 ──────────────────────────────────
        // ★ 2026-09-11 实测（真实账号 + 平台接口对照）：平台账本是**两段式**的 ——
        //   心跳只更新每个课件的 maxPlayTime，成绩单的 CE002（学习时长）/ learnScore
        //   要等调用结算接口（computeTask/saveComputeTask4AfterVideoPlayed）之后才刷新，
        //   而且**不必等视频播完**：播到一半发一次，已播时长立刻入账
        //   （实测 CE002 18.75 → 25.63 分、得分 17.98 → 24.58，40 秒内落库）。
        //   所以引擎必须按周期补发结算；少了它，「得分」在长视频里会一动不动，
        //   用户看到的就是"挂了半天、分数还是 0"。
        W("─── 结算节奏自检 ───");
        try
        {
            var interval = LearnEngine.SettlementInterval;
            var justBefore = LearnEngine.NeedsPeriodicSettlement(interval - TimeSpan.FromSeconds(1));
            var onTime = LearnEngine.NeedsPeriodicSettlement(interval);
            var tooEarly = LearnEngine.NeedsPeriodicSettlement(TimeSpan.FromSeconds(30));
            var withHistory = LearnEngine.NeedsEntrySettlement(600);   // 服务端已记 10 分钟
            var freshWare = LearnEngine.NeedsEntrySettlement(0);       // 全新课件

            var ok = interval == TimeSpan.FromMinutes(3)
                     && !justBefore && onTime && !tooEarly
                     && withHistory && !freshWare;

            if (ok) pass++; else fail++;

            W($"  结算周期       : {interval.TotalMinutes:0} 分钟（应为 3）");
            W($"  距上次 2分59秒 : {(justBefore ? "补发" : "不补发")}（应不补发）");
            W($"  距上次 3分00秒 : {(onTime ? "补发" : "不补发")}（应补发）");
            W($"  距上次 30 秒   : {(tooEarly ? "补发" : "不补发")}（应不补发）");
            W($"  课件有历史进度 : {(withHistory ? "开局补一次" : "不补")}（应补，否则分数停在旧值）");
            W($"  课件全新无进度 : {(freshWare ? "开局补一次" : "不补")}（应不补）");
            W($"  结论           : {(ok ? "PASS" : "FAIL")}（心跳不计学时、结算才刷新成绩单，周期不能省）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 结算应答解析自检 ──────────────────────────────────
        // 上下文（v1.0.19）：结算接口是异步任务，HTTP 层几乎永远 200 ——
        // 只看状态码等于什么都没看。平台把"受理 / 被拒"表达在响应体里，
        // 之前客户端整段丢弃，于是「发了结算但分数不涨」谁也说不清。
        W("─── 结算应答解析自检 ───");
        try
        {
            using var okDoc = JsonDocument.Parse("""{"isSuccess":true,"data":"task-abc123"}""");
            using var rejectDoc = JsonDocument.Parse("""{"isSuccess":false,"message":"课件进度不存在"}""");
            using var flagDoc = JsonDocument.Parse("""{"success":false,"msg":"no permission"}""");
            using var blankDoc = JsonDocument.Parse("{}");

            var okOutcome = LearnEngine.ReadSettleOutcome(okDoc.RootElement);
            var rejectOutcome = LearnEngine.ReadSettleOutcome(rejectDoc.RootElement);
            var flagOutcome = LearnEngine.ReadSettleOutcome(flagDoc.RootElement);
            var blankOutcome = LearnEngine.ReadSettleOutcome(blankDoc.RootElement);
            var nullOutcome = LearnEngine.ReadSettleOutcome(null);

            var ok = okOutcome.Accepted && okOutcome.Message == "task-abc123"
                     && !rejectOutcome.Accepted && rejectOutcome.Message == "课件进度不存在"
                     && !flagOutcome.Accepted && flagOutcome.Message == "no permission"
                     && blankOutcome.Accepted
                     && nullOutcome.Accepted;

            if (ok) pass++; else fail++;

            W($"  isSuccess:true+taskId : 受理（taskId={okOutcome.Message ?? "<无>"}）");
            W($"  isSuccess:false       : {(rejectOutcome.Accepted ? "受理 ← 错" : "被拒")}（原因：{rejectOutcome.Message ?? "<无>"}）");
            W($"  success:false / msg   : {(flagOutcome.Accepted ? "受理 ← 错" : "被拒")}（原因：{flagOutcome.Message ?? "<无>"}）");
            W($"  空响应体 / 无响应     : {(blankOutcome.Accepted && nullOutcome.Accepted ? "按受理处理（不误报失败）" : "误报 ← 错")}");
            W($"  结论                  : {(ok ? "PASS" : "FAIL")}（HTTP 200 ≠ 平台受理，必须读响应体）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 登录过期检测自检（v1.0.25）─────────────────────────
        // 事故背景：整晚挂机时 token 过期，平台把过期表达在 HTTP 200 响应体里，
        // 心跳"看起来"都正常，引擎空转两个多小时、白提交 58 次结算。
        // 这里离线验算两件事：响应体过期特征识别、JWT 有效期解析。
        W("─── 登录过期检测自检 ───");
        try
        {
            // 1) 响应体特征：真实事故报文 + 网关通用风格 + 正常业务报文（不能误报）
            var expiredSettle = ApiClient.LooksLikeTokenExpiry("""{"isSuccess":false,"statusCode":200,"message":"token过期"}""");
            var expiredRuoYi = ApiClient.LooksLikeTokenExpiry("""{"code":401,"msg":"认证失败，无法访问系统资源"}""");
            var expiredPlain = ApiClient.LooksLikeTokenExpiry("请重新登录后再操作");
            var normalScore = ApiClient.LooksLikeTokenExpiry("""{"isSuccess":true,"data":{"learnScore":66.32,"attributeCode":"CE002"}}""");
            var emptyBody = ApiClient.LooksLikeTokenExpiry("");

            // 2) JWT 有效期解析：三段式 + exp 才有值；opaque token / 缺 exp 返回 null
            //    合成 payload：{"sub":"selftest","exp":1757646000}
            const long expSeconds = 1757646000;
            var fakeJwt = "eyJhbGciOiJIUzI1NiJ9."
                        + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                              """{"sub":"selftest","exp":1757646000}"""))
                              .TrimEnd('=').Replace('+', '-').Replace('/', '_')
                        + ".sig";
            var jwtExp = AuthService.TryGetTokenExpiry(fakeJwt);
            var opaque = AuthService.TryGetTokenExpiry("not-a-jwt-token");
            var noExp = AuthService.TryGetTokenExpiry(
                "h." + Convert.ToBase64String("""{"sub":"x"}"""u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s");

            var ok = expiredSettle && expiredRuoYi && expiredPlain
                     && !normalScore && !emptyBody
                     && jwtExp == DateTimeOffset.FromUnixTimeSeconds(expSeconds)
                     && opaque is null && noExp is null;

            if (ok) pass++; else fail++;

            W($"  结算被拒报文（token过期）  : {(expiredSettle ? "命中" : "漏报 ← 错")}——昨晚空转 2 小时的根源");
            W($"  网关 401 风格（认证失败）  : {(expiredRuoYi ? "命中" : "漏报 ← 错")}");
            W($"  明文「请重新登录」        : {(expiredPlain ? "命中" : "漏报 ← 错")}");
            W($"  正常成绩报文              : {(!normalScore ? "不误报" : "误报 ← 错")}");
            W($"  空响应体                  : {(!emptyBody ? "不误报" : "误报 ← 错")}");
            W($"  JWT exp 解析              : {(jwtExp == DateTimeOffset.FromUnixTimeSeconds(expSeconds) ? $"正确（{jwtExp:u}）" : "错误 ←")}");
            W($"  非 JWT / 缺 exp           : {(opaque is null && noExp is null ? "返回 null（无法本地读有效期，靠过期检测兜底）" : "误报 ← 错")}");
            W($"  结论                      : {(ok ? "PASS" : "FAIL")}（检出 → 自动暂停 → 重登续挂）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 会话自然日失效自检（v1.0.34）────────────────────────
        // 事故背景：2026-09-13 00:00:54，23:47:59 建立的会话被平台切断（前 60 秒的心跳还全成功）。
        // 把它与 v1.0.26 存档的跨日样本（23:37:18 登录 → 00:01:45 首次过期）对齐后发现：
        // **两次登录差了 10 分钟，失效落点却都在 0 点整** → 平台会话按自然日失效。
        // ★ 旧结论「活跃滑动续期」就此作废：那个 24m27s 是"登录 → 首次检出过期"的时长，
        //   不是 TTL —— 它本来就是一条跨日样本，被当成普通样本了。
        // 这里离线验算三件事：跨日边界、预警时机、以及"保活为什么说谎"。
        W("─── 会话自然日失效自检 ───");
        try
        {
            var off = TimeSpan.FromHours(8);

            // 1) 跨日边界：任何时刻的"下一个 0 点"都必须是紧邻的那一个
            var at0945 = new DateTimeOffset(2026, 9, 12, 9, 45, 0, off);
            var at2347 = new DateTimeOffset(2026, 9, 12, 23, 47, 59, off);
            var at0005 = new DateTimeOffset(2026, 9, 13, 0, 5, 0, off);

            var mid0945 = MainWindowViewModel.NextMidnight(at0945);
            var mid2347 = MainWindowViewModel.NextMidnight(at2347);
            var mid0005 = MainWindowViewModel.NextMidnight(at0005);

            var boundaryOk = mid0945 == new DateTimeOffset(2026, 9, 13, 0, 0, 0, off)
                             && mid2347 == new DateTimeOffset(2026, 9, 13, 0, 0, 0, off)
                             && mid0005 == new DateTimeOffset(2026, 9, 14, 0, 0, 0, off);

            // 2) 预警时机：23:45 之后建立的会话只剩十几分钟寿命 → 登录当场提醒（null）
            var d0945 = MainWindowViewModel.MidnightWarningDelay(at0945);
            var d2344 = MainWindowViewModel.MidnightWarningDelay(
                new DateTimeOffset(2026, 9, 12, 23, 44, 0, off));
            var d2347 = MainWindowViewModel.MidnightWarningDelay(at2347);
            var d0005 = MainWindowViewModel.MidnightWarningDelay(at0005);

            var delayOk = d0945 is { TotalMinutes: > 800 and < 900 }        // 09:45 → 14 小时后（23:45）
                          && d2344 is { TotalMinutes: > 0 and <= 1 }         // 23:44 → 1 分钟后
                          && d2347 is null                                   // 23:47 登录 → 当场提醒
                          && d0005 is { TotalMinutes: > 1400 and < 1440 };   // 00:05 → 当晚 23:45（23h40m）

            // 3) 保活为什么说谎：平台用 200 + isSuccess:false 表达过期，而 GetProfileAsync 走
            //    ApiResponseReader.Data() —— 过期报文里根本没有 data，于是**静默返回 null
            //    而不抛异常**，"没抛异常就算正常"的判据当场失效，会话死了还报"保活正常"。
            using var expireDoc = System.Text.Json.JsonDocument.Parse(
                """{"isSuccess":false,"statusCode":200,"message":"token过期"}""");
            using var profileDoc = System.Text.Json.JsonDocument.Parse(
                """{"isSuccess":true,"data":{"stuName":"学员甲","totalLearnTime":15720}}""");
            var expireData = ApiResponseReader.Data(expireDoc.RootElement);
            var profileData = ApiResponseReader.Data(profileDoc.RootElement);

            var lieOk = expireData is null && profileData is not null;

            var ok = boundaryOk && delayOk && lieOk;
            if (ok) pass++; else fail++;

            W($"  跨日边界 09:45 / 23:47 / 00:05 : {(boundaryOk ? "次日 0 点 / 次日 0 点 / 再次日 0 点" : "错误 ←")}");
            W($"  预警时机 09:45 登录            : {(d0945 is { TotalMinutes: > 800 and < 900 } ? $"约 {(int)d0945!.Value.TotalMinutes} 分钟后（23:45）" : "错误 ←")}");
            W($"  预警时机 23:44 登录            : {(d2344 is { TotalMinutes: > 0 and <= 1 } ? "1 分钟后（23:45）" : "错误 ←")}");
            W($"  预警时机 23:47 登录            : {(d2347 is null ? "当场提醒（只剩 13 分钟寿命）" : "错误 ←")}");
            W($"  预警时机 00:05 登录            : {(d0005 is { TotalMinutes: > 1400 and < 1440 } ? $"约 {(int)d0005!.Value.TotalMinutes} 分钟后（当晚 23:45）" : "错误 ←")}");
            W($"  保活谎报根因（过期报文）      : {(lieOk ? "取不到 data → 静默 null（\"没抛异常\"≠\"会话还活着\"）" : "错误 ←")}");
            W($"  结论                          : {(ok ? "PASS" : "FAIL")}（跨不过 0 点 → 23:45 提醒 → 0 点后重登）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 续期端点响应解析自检（v1.0.26，v1.0.27 保留）────────────────
        // 背景：网页前端有 POST /service/ss/auth/refresh 但 enableRefreshToken:!1 没启用。
        // 端点真实响应形态未知，TryReadRefreshedToken 按网关通用封套穷举字段；
        // 这里离线验算五种形态：data 字符串 / data.accessToken / data.jwt / 顶层 jwt / 拒绝与乱结构。
        // ★ 运行时刻意不调用该端点（死代码接口的调用流量 = 唯一审计指纹），
        //   解析器仅作调查结论存档。
        W("─── 续期端点响应解析自检 ───");
        try
        {
            static string? Read(string json)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return AuthService.TryReadRefreshedToken(doc.RootElement);
            }

            var asDataString = Read("""{"isSuccess":true,"statusCode":200,"message":"","data":"new-token-abc"}""");
            var asDataObject = Read("""{"isSuccess":true,"data":{"accessToken":"tok-2","jwt":"j"}}""");
            var asTopJwt = Read("""{"isSuccess":true,"jwt":"top-level-jwt"}""");
            var asRejected = Read("""{"isSuccess":false,"statusCode":401,"message":"账号未登录","jwt":null}""");
            var asGarbage = Read("""{"data":{"weird":[1,2,3]}}""");

            var ok = asDataString == "new-token-abc" && asDataObject == "tok-2"
                     && asTopJwt == "top-level-jwt" && asRejected is null && asGarbage is null;

            if (ok) pass++; else fail++;

            W($"  data 为字符串        : {(asDataString == "new-token-abc" ? "取出" : "漏 ← 错")}");
            W($"  data.accessToken 优先: {(asDataObject == "tok-2" ? "取出" : "漏 ← 错")}");
            W($"  顶层 jwt 兜底        : {(asTopJwt == "top-level-jwt" ? "取出" : "漏 ← 错")}");
            W($"  isSuccess:false      : {(asRejected is null ? "拒绝（返回 null）" : "误取 ← 错")}");
            W($"  无 token 字段的乱结构: {(asGarbage is null ? "返回 null" : "误取 ← 错")}");
            W($"  结论                 : {(ok ? "PASS" : "FAIL")}（解析器存档备用，运行时不调用该端点）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 结算台账文案自检 ──────────────────────────────────
        // 上下文：结算与得分刷新原先完全发生在引擎内部，界面上看不见，
        // 用户的原话是「我也不知道做了结算没有，得分有没有刷新」。
        // 这三种状态必须能被区分（没结算 / 提交了等落库 / 真的刷新了 / 平台拒绝）。
        W("─── 结算台账文案自检 ───");
        try
        {
            var t = new DateTimeOffset(2026, 9, 11, 19, 37, 6, TimeSpan.FromHours(8));

            var none = QueueViewModel.FormatSettleLedger(0, null, null, null);
            var pending = QueueViewModel.FormatSettleLedger(3, t, true, null);
            var refreshed = QueueViewModel.FormatSettleLedger(3, t, true, t.AddSeconds(40));
            var rejected = QueueViewModel.FormatSettleLedger(1, t, false, null);

            var probeOk = true;
            for (var i = 1; i < LearnEngine.ScoreProbeDelays.Length; i++)
                if (LearnEngine.ScoreProbeDelays[i] <= LearnEngine.ScoreProbeDelays[i - 1]) probeOk = false;
            var probeTotal = LearnEngine.ScoreProbeDelays.Sum();

            var ok = none == "尚未提交结算"
                     && pending.Contains("已结算 3 次") && pending.Contains("等平台落库")
                     && refreshed.Contains("得分已刷新")
                     && rejected.Contains("平台未受理")
                     && probeOk && probeTotal <= 150;

            if (ok) pass++; else fail++;

            W($"  从没结算       : {none}");
            W($"  已提交·待落库  : {pending}");
            W($"  已提交·已刷新  : {refreshed}");
            W($"  平台拒绝       : {rejected}");
            W($"  盯成绩节奏     : {string.Join("+", LearnEngine.ScoreProbeDelays)} = {probeTotal:0}s（应递增且 ≤150s）");
            W($"  结论           : {(ok ? "PASS" : "FAIL")}（结算了没有、刷新了没有，界面上必须看得见）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 课件收尾等待自检 ──────────────────────────────────
        // 上下文：进度距课件目标不足一个心跳周期时，不能再硬等满 58–63 秒的整个周期 ——
        // 否则视频早就播完了，界面还要空转最多一分钟才切下一个课件
        //（用户看到的就是"进度条已经满了、播放时长还在涨，过了一会才切"）。
        W("─── 课件收尾等待自检 ───");
        try
        {
            var normal = TimeSpan.FromSeconds(60);
            var plenty = LearnEngine.NextBeatInterval(0, 3600, normal);        // 剩余 1 小时
            var tail = LearnEngine.NextBeatInterval(3580, 3600, normal);       // 剩余 20 秒
            var tiny = LearnEngine.NextBeatInterval(3599.5, 3600, normal);     // 剩余 0.5 秒
            var done = LearnEngine.NextBeatInterval(3600, 3600, normal);       // 已经播满

            var ok = plenty == normal
                     && tail == TimeSpan.FromSeconds(20)
                     && tiny == TimeSpan.FromSeconds(LearnEngine.MinTailWaitSeconds)
                     && done == normal;

            if (ok) pass++; else fail++;

            W($"  剩余 60 分钟 : {plenty.TotalSeconds:0}s（应保持完整周期 60s）");
            W($"  剩余 20 秒   : {tail.TotalSeconds:0}s（应缩到 20s，而不是等满 60s 才收尾）");
            W($"  剩余 0.5 秒  : {tiny.TotalSeconds:0}s（应取下限 {LearnEngine.MinTailWaitSeconds:0}s，不发碎心跳）");
            W($"  已播满       : {done.TotalSeconds:0}s（交回正常周期，由播满判断立即收尾）");
            W($"  结论         : {(ok ? "PASS" : "FAIL")}（播满即切，不再空转一整个心跳周期）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 队列存档自检 ──────────────────────────────────────
        // 上下文：挂课队列原来只活在内存里，一关程序就没了，每次都得重新挑课。
        // 存档往返必须无损，否则重开后看到的是"少了几门"或"多出一门重复课"。
        W("─── 队列存档自检 ───");
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"bw-selftest-queue-{Guid.NewGuid():N}.json");
            try
            {
                var store = new QueueStore(temp);
                const string user = "__selftest__";

                var items = new List<CourseItem>
                {
                    new()
                    {
                        CourseName = "自检课程甲", CourseNo = "SELFTEST-A", OlClassNo = "CLASS-1",
                        CenterCode = "C001", OlClassType = "ZE0",
                        RequiredDuration = 72, RequiredUnit = "分钟", LearnScore = 12.5, PassScore = 60,
                    },
                    new()
                    {
                        CourseName = "自检课程乙", CourseNo = "SELFTEST-B", OlClassNo = "CLASS-1",
                        CenterCode = "C001", OlClassType = "ZE0",
                    },
                    // 缺 courseNo/olClassNo 的脏数据：它挂不了，读回来只会变成一行死数据
                    new() { CourseName = "缺身份的记录" },
                };

                store.Save(user, items);
                var back = store.Load(user);
                var first = back.FirstOrDefault(x => x.CourseNo == "SELFTEST-A");

                var ok = back.Count == 2
                         && first is { CourseName: "自检课程甲", OlClassNo: "CLASS-1", CenterCode: "C001" }
                         && Math.Abs((first.RequiredDuration ?? 0) - 72) < 0.001
                         && Math.Abs((first.LearnScore ?? 0) - 12.5) < 0.001;

                // 另外一个账号读不到这份队列（换账号登录不会把别人的课挂上去）
                var other = store.Load("__selftest_other__");
                ok = ok && other.Count == 0;

                // 清空即删档
                store.Save(user, Array.Empty<CourseItem>());
                ok = ok && store.Load(user).Count == 0;

                if (ok) pass++; else fail++;

                W($"  存 3 门（含 1 门脏数据） → 读回 {back.Count} 门（应 2，缺身份的丢弃）");
                W($"  首门还原     : {first?.CourseName ?? "—"} / {first?.OlClassNo ?? "—"} / 要求 {first?.RequiredDuration:0.##} / 得分 {first?.LearnScore:0.##}");
                W($"  其他账号     : {other.Count} 门（应 0，账号之间互不干扰）");
                W($"  清空后读回   : {store.Load(user).Count} 门（应 0）");
                W($"  结论         : {(ok ? "PASS" : "FAIL")}（重开不丢队列、不串账号）");
            }
            finally
            {
                SafeDelete(temp);
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 账号存档自检 ──────────────────────────────────────
        // 上下文：「记住账户 / 保存密码 / 切换用户」都靠这份存档。
        // 密码必须密文落盘且能还原；关闭"保存密码"必须把已存密码清干净。
        W("─── 账号存档自检 ───");
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"bw-selftest-accounts-{Guid.NewGuid():N}.json");
            try
            {
                const string plain = "p@ssw0rd/测试";
                var store = new AccountStore(temp);
                store.SetSavePassword(true);
                store.Upsert("100001", "学员甲", plain);

                var saved = store.Accounts.FirstOrDefault(a => a.UserNo == "100001");
                var roundTrip = store.PasswordFor("100001") == plain;
                var cipher = saved?.ProtectedPassword ?? "";
                var notPlain = cipher.Length > 0
                               && !cipher.Contains("p@ssw0rd", StringComparison.Ordinal)
                               && !cipher.Contains("测试", StringComparison.Ordinal);

                // 关掉"保存密码"要清掉已存密码，但账号本身留着
                store.SetSavePassword(false);
                var cleared = store.Accounts.All(a => string.IsNullOrEmpty(a.ProtectedPassword))
                              && store.PasswordFor("100001") is null;
                var kept = store.Accounts.Any(a => a.UserNo == "100001");

                store.Remove("100001");
                var removed = store.Accounts.Count == 0;

                var ok = roundTrip && notPlain && cleared && kept && removed;
                if (ok) pass++; else fail++;

                W($"  密码往返     : {(roundTrip ? "密文存、明文还" : "异常")}（应能还原成原文）");
                W($"  落盘非明文   : {(notPlain ? "是" : "否")}（文件里不该出现密码原文）");
                W($"  关闭保存密码 : {(cleared ? "已清除" : "未清除")}（应清除，账号保留 = {kept}）");
                W($"  删除账号     : {(removed ? "已删除" : "仍在")}（下拉里的 ✕ 走的就是这条）");
                W($"  结论         : {(ok ? "PASS" : "FAIL")}（记住账户 / 保存密码 / 切换用户的基础）");
            }
            finally
            {
                SafeDelete(temp);
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 控制条按钮状态自检 ────────────────────────────
        // 用户反馈：「没开始就没有暂停和停止这一说，开始以后暂停和停止能用了，
        // 就不应该再能点开始挂课按钮」。四个按钮的可用性全部由引擎状态推导，
        // 推导抽成了纯函数 ControlBarStates，五种状态逐个验算。
        W("─── 控制条按钮状态自检 ───");
        try
        {
            // (Start, Pause, Stop, Save)
            var idleReady = QueueViewModel.ControlBarStates(EngineState.Idle, hasQueue: true);
            var idleEmpty = QueueViewModel.ControlBarStates(EngineState.Idle, hasQueue: false);
            var running = QueueViewModel.ControlBarStates(EngineState.Running, hasQueue: true);
            var paused = QueueViewModel.ControlBarStates(EngineState.Paused, hasQueue: true);
            var stopping = QueueViewModel.ControlBarStates(EngineState.Stopping, hasQueue: true);
            var stopped = QueueViewModel.ControlBarStates(EngineState.Stopped, hasQueue: true);

            var ok = idleReady == (true, false, false, false)     // 没开始：暂停/停止/保存全置灰
                     && idleEmpty == (false, false, false, false) // 队列还是空的：开始也点不了
                     && running == (false, true, true, true)      // 挂课中：开始挂课必须置灰
                     && paused == (false, true, true, true)       // 暂停中同上
                     && stopping == (false, false, false, false)  // 停止收尾中：全部置灰
                     && stopped == (true, false, false, false);   // 已停止：可以重新开始

            if (ok) pass++; else fail++;

            W($"  空闲+有队列 : {idleReady}（应 (True, False, False, False) —— 没开始就没有暂停/停止这一说）");
            W($"  空闲+空队列 : {idleEmpty}（应 (False, False, False, False)）");
            W($"  运行中      : {running}（应 (False, True, True, True) —— 不能重复开始）");
            W($"  已暂停      : {paused}（应 (False, True, True, True)）");
            W($"  停止收尾中  : {stopping}（应 (False, False, False, False)）");
            W($"  已停止      : {stopped}（应 (True, False, False, False) —— 可重新开始）");
            W($"  结论        : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 课程页筛选自检 ────────────────────────────────
        // 用户反馈：「课程页里帮我加个筛选功能，现在查找不方便」。
        // 匹配逻辑抽成了纯函数 MatchesFilter，另外验一遍
        // 「设置 SearchText 必须真的把 VisibleRows 重建了」—— 光有匹配函数、列表不刷新等于没做。
        W("─── 课程页筛选自检 ───");
        try
        {
            var classRow = new MyCourseRow(new MyCourseItem
            {
                OlClassName = "AI 学习专区", OlClassNo = "FZ-1", OlClassType = "ZE0",
            });
            var child = MyCourseRow.FromCourse(new CourseItem
            {
                CourseNo = "FC-1", CourseName = "大模型入门", OlClassNo = "FZ-1",
            }, classRow.Category, nested: true, parentKey: classRow.Key);
            classRow.Children.Add(child);

            var direct = new MyCourseRow(new MyCourseItem
            {
                CourseName = "自检课程·对外交往", CourseNo = "FC-2", OlClassNo = "FCL-2", OlClassType = "OCE",
            });

            // 纯函数层
            var fEmpty = CoursesViewModel.MatchesFilter(direct, null);          // 空关键字 → 全匹配
            var fTop = CoursesViewModel.MatchesFilter(direct, "对外交往");       // 顶层名命中
            var fMiss = !CoursesViewModel.MatchesFilter(direct, "查无此课");     // 不命中 → 滤掉
            var fChild = !CoursesViewModel.MatchesFilter(classRow, "对外交往")   // 合集名不中
                         && CoursesViewModel.MatchesFilter(classRow, "大模型");  // 但子课程命中 → 保留
            var fCase = CoursesViewModel.MatchesFilter(classRow, "ai");         // 大小写不敏感

            // VM 层：赋值 SearchText 必须触发 VisibleRows 重建
            var cvm = vm.Courses;
            var saved = cvm.AllRows.ToList();
            cvm.AllRows.Clear();
            cvm.AllRows.Add(classRow);
            cvm.AllRows.Add(direct);

            cvm.SearchText = "大模型";
            var hitChild = cvm.VisibleRows.Count;     // 应 1（只剩合集行）
            cvm.SearchText = "";
            var reset = cvm.VisibleRows.Count;        // 应 2（清空恢复全部）

            cvm.AllRows.Clear();
            foreach (var r in saved) cvm.AllRows.Add(r);   // 还原现场
            cvm.SearchText = "";

            var ok = fEmpty && fTop && fMiss && fChild && fCase && hitChild == 1 && reset == 2;

            if (ok) pass++; else fail++;

            W($"  空关键字     : {(fEmpty ? "全匹配" : "异常")}");
            W($"  顶层名命中   : {(fTop ? "是" : "否")} / 不命中滤掉 : {(fMiss ? "是" : "否")}");
            W($"  子课程命中保合集 : {(fChild ? "是" : "否")} / 大小写不敏感 : {(fCase ? "是" : "否")}");
            W($"  搜索「大模型」后可见 : {hitChild}（应 1 —— 命中的是合集里的子课程）");
            W($"  清空搜索后可见 : {reset}（应 2）");
            W($"  结论         : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 课程页全库搜索自检 ────────────────────────────
        // 用户实测反馈：「这个查询就是在下方列表当前显示的范围内查询，意义不大」——
        // 旧版搜索只看 AllRows，而它只是当前这一页（分页取回来的 20 行）。
        // 这一项把回归钉死：**只在第 2 页出现的课，全库索引就绪后必须搜得到**。
        W("─── 课程页全库搜索自检 ───");
        try
        {
            var cvm = vm.Courses;
            var savedAll = cvm.AllRows.ToList();

            // 第 1 页（当前页）两门课
            var page1 = new[]
            {
                new MyCourseRow(new MyCourseItem
                {
                    CourseName = "自检课程·第一页甲", CourseNo = "PG1-A", OlClassNo = "L1", OlClassType = "OCE",
                }),
                new MyCourseRow(new MyCourseItem
                {
                    CourseName = "自检课程·第一页乙", CourseNo = "PG1-B", OlClassNo = "L1", OlClassType = "OCE",
                }),
            };
            // 第 2 页（旧版搜索根本碰不到）的那门课
            var page2Row = new MyCourseRow(new MyCourseItem
            {
                CourseName = "自检课程·第二页信息安全法", CourseNo = "PG2-A", OlClassNo = "L1", OlClassType = "OCE",
            });

            cvm.AllRows.Clear();
            foreach (var r in page1) cvm.AllRows.Add(r);

            // ① 没有索引时：只在当前页里找 —— 第 2 页那门课找不到（这就是用户踩到的那件事）
            cvm.SearchText = "信息安全法";
            var beforeIndex = cvm.VisibleRows.Count;      // 应 0
            cvm.SearchText = "";
            cvm.ClearFullIndexForTest();

            // ② 索引就绪后：同一句话必须能找到第 2 页那门课
            cvm.SetFullIndexForTest(new[] { page1[0], page1[1], page2Row });
            cvm.SearchText = "信息安全法";
            var afterIndex = cvm.VisibleRows.Count;        // 应 1
            var afterHitIsPage2 = cvm.VisibleRows.Count == 1
                                  && ReferenceEquals(cvm.VisibleRows[0], page2Row);
            cvm.SearchText = "";
            var backToPage = cvm.VisibleRows.Count;        // 应 2（清空关键字 → 回到当前页）

            // ③ 纯函数层：什么时候该翻页、搜索该在哪个集合上做
            //   （注意 ReferenceEquals 比的是实例 —— 断言右边的集合必须先存变量，
            //    写成 `(IReadOnlyList<...>)new[]{...}` 会现场再分配一个数组、恒为 false）
            var idx = new[] { page2Row };
            var buildWhenNeeded = CoursesViewModel.ShouldBuildIndex("abc", true, false);     // True
            var noBuildOnArchive = !CoursesViewModel.ShouldBuildIndex("abc", false, false);  // 归档接口不用翻
            var noBuildWhenReady = !CoursesViewModel.ShouldBuildIndex("abc", true, true);    // 已就绪不重复翻
            var noBuildWhenEmpty = !CoursesViewModel.ShouldBuildIndex("  ", true, false);    // 空关键字不翻
            var pickIndex = ReferenceEquals(
                CoursesViewModel.PickSearchSource("x", page1, idx),
                (IReadOnlyList<MyCourseRow>)idx);                                            // 有关键字+有索引 → 整库
            var pickPage = ReferenceEquals(
                CoursesViewModel.PickSearchSource("x", page1, null),
                (IReadOnlyList<MyCourseRow>)page1);                                          // 没索引 → 退回当前页
            var pickPageOnEmpty = ReferenceEquals(
                CoursesViewModel.PickSearchSource("", page1, idx),
                (IReadOnlyList<MyCourseRow>)page1);                                          // 关键字清空 → 当前页

            cvm.ClearFullIndexForTest();
            cvm.AllRows.Clear();
            foreach (var r in savedAll) cvm.AllRows.Add(r);    // 还原现场

            var ok = beforeIndex == 0 && afterIndex == 1 && afterHitIsPage2 && backToPage == 2
                     && buildWhenNeeded && noBuildOnArchive && noBuildWhenReady && noBuildWhenEmpty
                     && pickIndex && pickPage && pickPageOnEmpty;

            if (ok) pass++; else fail++;

            W($"  建索引前搜「信息安全法」 : {beforeIndex} 条（应 0 —— 旧版只能在当前页里找）");
            W($"  索引就绪后             : {afterIndex} 条（应 1 —— 命中第 2 页那门课：{afterHitIsPage2}）");
            W($"  清空关键字             : {backToPage} 条（应 2 —— 回到当前页）");
            W($"  该不该翻页（需建/归档/已就绪/空关键字）: {buildWhenNeeded} / {!noBuildOnArchive} / {!noBuildWhenReady} / {!noBuildWhenEmpty}"
              + "（应 True/False/False/False）");
            W($"  搜索数据源（有索引/无索引/清空）: {pickIndex} / {pickPage} / {pickPageOnEmpty}（应 True/True/True）");
            W($"  结论                   : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 全库翻页取数自检 ──────────────────────────────
        // 全库索引靠按页循环取数。平台的脾气不止一种：①老实听 size ②把 size 截断
        // ③压根不回 total。抽成静态方法 + 假取页委托就是为了在这里把三种都验一遍，
        // 不打真接口、不用登录。翻页策略写错的话，用户会"搜不到第二页之后的课"且毫无察觉。
        W("─── 全库翻页取数自检 ───");
        try
        {
            static List<CourseItem> MakePool(int n) => Enumerable.Range(1, n)
                .Select(i => new CourseItem { CourseNo = $"AUTO-{i}", CourseName = $"批量课程 {i}", OlClassNo = "CL-1" })
                .ToList();

            var pool45 = MakePool(45);
            var requests = 0;

            // ① 平台老实听 size（我们一次要 500）→ 一趟取回全部 45
            requests = 0;
            var honest = await CourseService.FetchAllPagesAsync((page, size, ct) =>
            {
                requests++;
                var items = pool45.Skip((page - 1) * size).Take(size).ToList();
                return Task.FromResult(new PagedResult<CourseItem> { Items = items, Total = pool45.Count });
            });
            var honestReq = requests;

            // ② 平台把 size 截到 20（只回 20 条）→ 得按"实际返回条数"接着翻，否则第 1 页就收工
            requests = 0;
            var capped = await CourseService.FetchAllPagesAsync((page, size, ct) =>
            {
                requests++;
                var eff = Math.Min(size, 20);
                var items = pool45.Skip((page - 1) * eff).Take(eff).ToList();
                return Task.FromResult(new PagedResult<CourseItem> { Items = items, Total = pool45.Count });
            });
            var cappedReq = requests;

            // ③ 平台不回 total、也截 size → 只能靠"空页"判断到底
            requests = 0;
            var noTotal = await CourseService.FetchAllPagesAsync((page, size, ct) =>
            {
                requests++;
                var eff = Math.Min(size, 20);
                var items = pool45.Skip((page - 1) * eff).Take(eff).ToList();
                return Task.FromResult(new PagedResult<CourseItem> { Items = items });   // Total 恒 0
            });
            var noTotalReq = requests;

            // ④ 平台永远回满、永不报总数（病态）→ 必须靠上限刹住，绝不能无限翻页
            requests = 0;
            var runaway = await CourseService.FetchAllPagesAsync((page, size, ct) =>
            {
                requests++;
                var eff = Math.Min(size, 20);
                var items = Enumerable.Range(0, eff)
                    .Select(i => new CourseItem { CourseNo = $"RUN-{page}-{i}", OlClassNo = "CL-1" })
                    .ToList();
                return Task.FromResult(new PagedResult<CourseItem> { Items = items });
            }, maxRequests: 3);
            var runawayReq = requests;

            // ⑤ 去重：同样的 key 重复回也不该重复入列
            var dup = await CourseService.FetchAllPagesAsync((page, size, ct) =>
                Task.FromResult(new PagedResult<CourseItem>
                {
                    Items = page == 1
                        ? new List<CourseItem> { new() { CourseNo = "DUP-1", OlClassNo = "L" }, new() { CourseNo = "DUP-1", OlClassNo = "L" } }
                        : new List<CourseItem>(),
                    Total = 2,
                }));

            var ok = honest.Count == 45 && honestReq == 1
                     && capped.Count == 45 && cappedReq == 3
                     && noTotal.Count == 45 && noTotalReq == 4
                     && runaway.Count == 60 && runawayReq == 3
                     && dup.Count == 1;

            if (ok) pass++; else fail++;

            W($"  ① 老实听 size 500      : {honest.Count} 门 / {honestReq} 次请求（应 45 / 1）");
            W($"  ② size 被截到 20       : {capped.Count} 门 / {cappedReq} 次请求（应 45 / 3 —— 按实际条数接着翻）");
            W($"  ③ 不回 total           : {noTotal.Count} 门 / {noTotalReq} 次请求（应 45 / 4 —— 靠空页判到底）");
            W($"  ④ 永远回满且无 total   : {runaway.Count} 门 / {runawayReq} 次请求（应 60 / 3 —— 请求上限刹住）");
            W($"  ⑤ 重复记录去重         : {dup.Count} 条（应 1）");
            W($"  结论                   : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 平台检索（ES）路由与记录解析自检 ──────────────
        // v1.0.39 把两个浏览型页签的搜索改成问平台自带的 ES 检索（网页端那个大搜索框）。
        // 三件事必须钉死：
        //   ① 检索源跟着页签走 —— 专题班/培训班不在 ES 索引里，一律问 ES 会误判成"搜不到"；
        //   ② ES 记录是两层的（业务字段在 rawData 里），专区记录尤其不能把 subTitle 当 courseNo，
        //      否则专区行会被当成课程级行 —— 没复选框、没展开箭头，整行死掉；
        //   ③ 翻页控件与索引统计同占一格，判据不互斥就叠重影。
        W("─── 平台检索路由与记录解析自检 ───");
        try
        {
            const string CourseRecord = """
                {"dataType":"course","guid":"CS2025OCC00404","title":"示例课程标题","subTitle":"80788",
                 "hours":"2.0","centerCode":"C001","rawData":{"courseName":"示例课程标题",
                 "olClassNo":"1997868434762895360","courseNo":"80788","centerCode":"C001","courseHours":"2.0"}}
                """;
            const string ZoneRecord = """
                {"dataType":"zone","guid":"2093180642249543680","title":"某平台公开专区",
                 "subTitle":"2093180642249543680","hours":"14","centerCode":"C001",
                 "rawData":{"olClassType":"ZE0","olClassNo":"2093180642249543680",
                 "olClassName":"某平台公开专区","classHours":"14","courseNum":"21","centerCode":"C001"}}
                """;

            // ① 检索源跟着页签走
            var esOnCourse = CoursesViewModel.UsesEsSearch(CourseMenuKind.PublicCourse, "信息安全");
            var esOnZone = CoursesViewModel.UsesEsSearch(CourseMenuKind.StudyZone, "信息安全");
            var esOffTopic = !CoursesViewModel.UsesEsSearch(CourseMenuKind.MyOnlineTopic, "信息安全");
            var esOffTrain = !CoursesViewModel.UsesEsSearch(CourseMenuKind.MyTrainClass, "信息安全");
            var esOffEmpty = !CoursesViewModel.UsesEsSearch(CourseMenuKind.PublicCourse, "   ");

            // ② 同一个关键字只问一次：已出过结果、或已经失败过的，防抖不再重复问
            var askFresh = CoursesViewModel.ShouldRequestEsSearch(CourseMenuKind.PublicCourse, "信息安全", "", "");
            var skipApplied = !CoursesViewModel.ShouldRequestEsSearch(CourseMenuKind.PublicCourse, "信息安全", "信息安全", "");
            var skipFailed = !CoursesViewModel.ShouldRequestEsSearch(CourseMenuKind.PublicCourse, "信息安全", "", "信息安全");
            var askOtherTab = !CoursesViewModel.ShouldRequestEsSearch(CourseMenuKind.MyTrainClass, "信息安全", "", "");

            // ③ 记录映射（两条都是真实抓回来的样本）
            using (var cd = JsonDocument.Parse(CourseRecord))
            {
                var c = CourseService.ParseEsRecord(cd.RootElement);
                var courseOk = c.CourseNo == "80788"
                               && c.OlClassNo == "1997868434762895360"      // ★ 只在 rawData 里
                               && c.CourseName == "示例课程标题"
                               && c.CenterCode == "C001"
                               && Math.Abs((c.CourseHours ?? 0) - 2.0) < 0.001;
                var zoneOk = false;
                using (var zd = JsonDocument.Parse(ZoneRecord))
                {
                    var z = CourseService.ParseEsRecord(zd.RootElement);
                    // ★ 专区记录代表"合集"这一行：CourseNo 必须是空 —— 拿 subTitle 当 courseNo
                    //   会让它被当成课程级行（复选框与展开箭头一起消失）
                    zoneOk = z.CourseNo.Length == 0
                             && z.OlClassNo == "2093180642249543680"
                             && z.OlClassType == "ZE0"
                             && z.CourseName == "某平台公开专区"
                             && z.CourseNum == "21"
                             && Math.Abs((z.CourseHours ?? 0) - 14) < 0.001;
                }

                // ④ 翻页控件 / 索引统计互斥（同占一格）
                var cvm = vm.Courses;
                var savedMenu = cvm.SelectedMenu;
                var savedKw = cvm.SearchText;
                cvm.SelectedMenu = cvm.Menus[0];        // 公开课程：支持分页
                cvm.SearchText = "信息安全";
                cvm.SetEsResultsKeywordForTest("");     // 结果还没到手 → 翻页让位给统计
                var pendingPaging = cvm.PagingVisible;         // 应 False
                var pendingIndex = cvm.IsIndexSearchActive;    // 应 True
                cvm.SetEsResultsKeywordForTest("信息安全");    // 平台结果到手 → 翻页照常
                var esPaging = cvm.PagingVisible;              // 应 True
                var esIndex = cvm.IsIndexSearchActive;         // 应 False
                // ★ 还原顺序有讲究：先还原菜单（它会把防抖与平台结果一起作废），
                //   再还原关键字（空关键字会顺手把防抖停掉）—— 反过来的话，
                //   自检结束后可能还有一次"替自检那次搜索"的迟到请求。
                cvm.SetEsResultsKeywordForTest("");
                cvm.SelectedMenu = savedMenu;
                cvm.SearchText = savedKw;
                var mutexOk = !pendingPaging && pendingIndex && esPaging && !esIndex;

                // ⑤ 学习中心下拉：默认选中 + 中文名文案
                var centers = new List<CenterOption>
                {
                    new() { CenterCode = "C001", CenterName = "集团站点", Sort = 1 },
                    new() { CenterCode = "C007", CenterName = "示例中心", Sort = 12 },
                };
                var keepCurrent = ReferenceEquals(CoursesViewModel.PickDefaultCenter(centers, "C007"), centers[1]);
                var fallbackFirst = ReferenceEquals(CoursesViewModel.PickDefaultCenter(centers, "ZZZZ"), centers[0]);
                var emptyNull = CoursesViewModel.PickDefaultCenter(Array.Empty<CenterOption>(), "C001") is null;
                var displayOk = centers[0].Display == "集团站点（C001）"
                                && new CenterOption { CenterCode = "C009" }.Display == "C009";

                var ok = esOnCourse && esOnZone && esOffTopic && esOffTrain && esOffEmpty
                         && askFresh && skipApplied && skipFailed && askOtherTab
                         && courseOk && zoneOk && mutexOk
                         && keepCurrent && fallbackFirst && emptyNull && displayOk;

                if (ok) pass++; else fail++;

                W($"  检索源跟着页签（公开课/专区/专题班/培训班/空词）: {esOnCourse}/{esOnZone}/{esOffTopic}/{esOffTrain}/{esOffEmpty}（应 全 True）");
                W($"  同一关键字只问一次（新词/已出结果/已失败/非ES页签）: {askFresh}/{skipApplied}/{skipFailed}/{askOtherTab}（应 True/True/True/True）");
                W($"  课程记录映射（courseNo/olClassNo 取自 rawData）: {courseOk}");
                W($"  专区记录映射（CourseNo 必须为空，否则整行死掉）: {zoneOk}");
                W($"  翻页与索引统计互斥（未到货 {pendingIndex}/到货 {esPaging}）: {mutexOk}");
                W($"  中心下拉（沿用当前/回退首个/空表/中文名）: {keepCurrent}/{fallbackFirst}/{emptyNull}/{displayOk}（应 全 True）");
                W($"  结论                   : {(ok ? "PASS" : "FAIL")}");
            }
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 挂机行实时时长自检 ────────────────────────────
        // 用户反馈：面板上的时长在按秒涨，挂机列表里的时长却纹丝不动 ——
        // 行里显示的是平台回读值（CE002，只有结算落库才动），与面板的引擎实时账本
        // 不同源也不同钟。现在当前在挂行的时长由界面秒表写实时账本
        // （QueueRow.LiveDurationText，与面板同一个字符串），非当前行退回平台回读值。
        W("─── 挂机行实时时长自检 ───");
        try
        {
            var stCourse = new CourseItem
            {
                CourseNo = "ST-1", OlClassNo = "STC-1", CourseName = "实时时长自检课",
                RequiredDuration = 58, RequiredUnit = "分钟",
                CompletedDuration = 8.88, CompletedText = "8分53秒",
            };
            var stRow = new QueueRow(4, stCourse);

            // 1) 默认显示平台回读值（未在挂的行永远走这一路）
            var stFallback = stRow.DurationText == "8分53秒 / 58分钟";
            // 2) 在挂期间：实时账本接管，面板写什么行就显示什么（同源同钟）
            stRow.LiveDurationText = "9m03s / 58m00s";
            var stLive = stRow.DurationText == "9m03s / 58m00s";
            // 3) 换课 / 停止后：清掉实时值，退回平台回读值，不能残留旧数字
            stRow.LiveDurationText = null;
            var stRevert = stRow.DurationText == "8分53秒 / 58分钟";

            // 4) 视频占分：权重到位后行副标题要带出「视频占分 80%」
            //    （得分 = 时长比 × 权重，权重 80 的课挂满 58 分钟是 80 分，不是 100 分）
            stCourse.DurationScoreWeight = 80;
            var stWeight = stRow.SubtitleText.Contains("视频占分 80%");

            // 5) 成绩胶囊：行内那一行现在是结构化的（左时长 / 右占分 / 最右成绩），
            //    字段名全部去掉，靠位置识别 —— 原先把「时长 · 得分 · 分数线 · 视频占分」
            //    整串塞进两百多像素，必然折行（用户反馈原文：折行以后反倒不好看）。
            //    这里钉住胶囊的三种文案：分数+分数线 / 只有分数线 / 两者都没有。
            stCourse.LearnScore = 63.28;
            stCourse.PassScore = 60;
            var stChipFull = stRow.ScoreChipText == "63.28/60";
            stCourse.PassScore = null;
            var stChipNoLine = stRow.ScoreChipText == "63.28";
            stCourse.LearnScore = null;
            var stChipNone = stRow.ScoreChipText == "—";
            // 权重未知时 HasWeight 必须是 false —— XAML 用它控制那一小段整段显隐，
            // 用判空字符串的话，绑定到 null 的 TextBlock 仍会占位
            var stWeightFlag = stRow.HasWeight;
            stCourse.DurationScoreWeight = null;
            var stWeightFlagOff = !stRow.HasWeight;

            // 6) 已选/课程页的行副标题同样追加占分段
            var stMyRow = MyCourseRow.FromCourse(new CourseItem
            {
                CourseNo = "ST-2", OlClassNo = "STC-2", CourseName = "占分自检课",
                DurationScoreWeight = 70,
            }, "网络自学 · 公开课", nested: false);
            var stMyWeight = stMyRow.DetailText.Contains("视频占分 70%");

            var ok = stFallback && stLive && stRevert && stWeight && stMyWeight
                     && stChipFull && stChipNoLine && stChipNone
                     && stWeightFlag && stWeightFlagOff;
            if (ok) pass++; else fail++;

            W($"  默认平台值   : {(stFallback ? "是" : "否")}（应 8分53秒 / 58分钟）");
            W($"  实时账本接管 : {(stLive ? "是" : "否")}（应与面板课程时长完全一致）");
            W($"  退回平台值   : {(stRevert ? "是" : "否")}（清掉实时值后不能残留旧数字）");
            W($"  行副标题占分 : {(stWeight ? "是" : "否")}（悬停提示里应含「视频占分 80%」）");
            W($"  已选行占分   : {(stMyWeight ? "是" : "否")}（应含「视频占分 70%」）");
            W($"  成绩胶囊     : {(stChipFull ? "是" : "否")}（得分 63.28 / 分数线 60 → 63.28/60）");
            W($"  胶囊·无分数线: {(stChipNoLine ? "是" : "否")}（分数线未知就只显示得分）");
            W($"  胶囊·无成绩  : {(stChipNone ? "是" : "否")}（两者都没有时是「—」）");
            W($"  占分显隐开关 : {(stWeightFlag && stWeightFlagOff ? "是" : "否")}"
              + "（HasWeight 有→无 都要跟着变）");
            W($"  结论         : {(ok ? "PASS" : "FAIL")}（字段名去掉、信息一项不丢，稳定一行）");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        W("");

        // ── 皮肤与密度自检（v1.0.40）────────────────────────
        // 换肤是运行时往 Application.Resources 合并 ResourceInclude：
        // Source 没赋值、画刷键缺失、密度令牌没写上，都只有真跑一遍才暴露。
        // 这里把四套皮肤 + 两档密度各 Apply 一次，再核对关键资源是否到位，
        // 最后恢复到「设置里存的那套」，不让自检改掉用户现场。
        W("─── 皮肤与密度自检 ───");
        try
        {
            var settings = vm.CurrentSettings;
            var savedSkin = ThemeService.NormalizeSkinId(settings.SkinId);
            var savedDensity = ThemeService.NormalizeDensity(settings.UiDensity);

            string? BrushHex(string key)
            {
                if (Application.Current?.TryFindResource(key, out var res) != true) return null;
                return res is Avalonia.Media.IBrush brush ? brush.ToString() : res?.ToString();
            }

            bool HasKey(string key) =>
                Application.Current?.TryFindResource(key, out _) == true;

            var skinResults = new List<string>();
            var allSkinsOk = true;
            foreach (var skin in ThemeService.Skins)
            {
                ThemeService.Apply(skin.Id, savedDensity);
                // 必须 pump：ResourceInclude 是在 Add 到 MergedDictionaries 时 Loaded 的
                await PumpAsync();

                var hasBg = HasKey("BgBrush") && BrushHex("BgBrush") is not null;
                var hasAccent = HasKey("AccentBrush");
                var hasNav = HasKey("NavActiveBrush");
                var variant = Application.Current?.RequestedThemeVariant;
                var variantOk = variant == skin.Variant;
                var skinOk = hasBg && hasAccent && hasNav && variantOk;
                if (!skinOk) allSkinsOk = false;

                skinResults.Add($"{skin.DisplayName}={(skinOk ? "PASS" : "FAIL")}"
                                + (variantOk ? "" : $"[变体={variant}]"));
            }

            ThemeService.Apply(savedSkin, ThemeService.DensityCompact);
            await PumpAsync();
            var compactMargin = Application.Current?.TryFindResource("PageMargin", out var cm) == true
                ? cm?.ToString()
                : null;
            ThemeService.Apply(savedSkin, ThemeService.DensityComfortable);
            await PumpAsync();
            var comfortMargin = Application.Current?.TryFindResource("PageMargin", out var fm) == true
                ? fm?.ToString()
                : null;
            var densityOk = compactMargin != comfortMargin
                            && compactMargin is not null
                            && comfortMargin is not null;

            // 恢复用户现场，别让自检改掉设置
            ThemeService.Apply(savedSkin, savedDensity);

            var ok = allSkinsOk && densityOk;
            if (ok) pass++; else fail++;

            W($"  四套皮肤     : {string.Join(" / ", skinResults)}");
            W($"  紧凑 PageMargin : {compactMargin ?? "<null>"}");
            W($"  舒适 PageMargin : {comfortMargin ?? "<null>"}");
            W($"  密度可区分   : {(densityOk ? "是" : "否")}");
            W($"  恢复现场     : {savedSkin} / {(savedDensity == ThemeService.DensityCompact ? "紧凑" : "舒适")}");
            W($"  结论         : {(ok ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            fail++;
            W($"  FAIL → {ex.GetType().Name}: {ex.Message}");
        }
        // ── 更新通道（v1.0.41）：验签引擎 / 真实发布公钥 / 清单 / 端点链 / 换装脚本 ──
        W("─── 更新通道自检 ───");

        // (1) 验签器本身用 RFC 8032 官方测试向量（7.1 节 TEST 1）验一次。
        //     真实清单只有一条路径进验签器，官方向量保证这条路径的编码处理
        //     （hex 解析、消息字节、签名长度校验）本身没错。
        {
            var rfcPub = Convert.FromHexString(
                "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
            var rfcSig = Convert.FromHexString(
                "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490" +
                "1555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
            var ok = BaoWuLearn.Core.Update.Ed25519Verifier.Verify(rfcPub, [], rfcSig)
                     && !BaoWuLearn.Core.Update.Ed25519Verifier.Verify(rfcPub, [42], rfcSig);
            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 验签引擎（RFC 8032 官方向量，含拒篡改）");
        }

        // (2) 真实发布公钥端到端：固定样本清单 + publish 私钥产出的 detached 签名。
        //     样本字节必须与签名时一字不差（换发布钥后需同步更新本段两个常量）。
        {
            const string sampleJson =
                """{"schema":1,"version":"9.9.9","pubDate":"2026-09-01T00:00:00+08:00","notes":"自检样本","assets":{"win-x64":{"file":"x.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}},"mirrors":["https://example-mirror/"]}""";
            const string sampleSig =
                "d4a277998c31d6d88ec1555ac21f69a843164590c2874f359fd8c124bc6e3427" +
                "bbe190ffcd18995d72bda00c73901c7649e2937eb72a49116c7cdf845f502903";
            var bytes = Encoding.UTF8.GetBytes(sampleJson);
            var ok = BaoWuLearn.Core.Update.Ed25519Verifier.VerifyWithReleaseKey(bytes, sampleSig)
                     // 篡改版本号一个字符就必须拒
                     && !BaoWuLearn.Core.Update.Ed25519Verifier.VerifyWithReleaseKey(
                         Encoding.UTF8.GetBytes(sampleJson.Replace("9.9.9", "9.9.8")), sampleSig)
                     // 垃圾签名（非 hex / 长度不对）必须安静地拒，不能抛
                     && !BaoWuLearn.Core.Update.Ed25519Verifier.VerifyWithReleaseKey(bytes, "zz");
            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 发布公钥端到端（样本通过 + 篡改拒 + 垃圾拒）");
        }

        // (3) 清单解析 / 版本比较 / pubDate 防回滚 / 端点链构造
        {
            const string sampleJson =
                """{"schema":1,"version":"9.9.9","pubDate":"2026-09-01T00:00:00+08:00","notes":"自检样本","assets":{"win-x64":{"file":"x.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}},"mirrors":["https://example-mirror/"]}""";
            var ok = true;
            try
            {
                var m = BaoWuLearn.Core.Update.UpdateManifestParser.Parse(
                    Encoding.UTF8.GetBytes(sampleJson));
                ok &= m.Version == "9.9.9" && m.Schema == 1 && m.Mirrors.Count == 1;
                if (!m.Assets.TryGetValue("win-x64", out var asset))
                {
                    W($"  ✖ 清单自检：assets 数={m.Assets.Count}，JSON 长={Encoding.UTF8.GetByteCount(sampleJson)}");
                }
                ok &= asset?.FileName == "x.exe";
                ok &= BaoWuLearn.Core.Update.UpdateManifestParser.IsNewer(m.Version, "1.0.40");
                ok &= !BaoWuLearn.Core.Update.UpdateManifestParser.IsNewer("1.9.0", "1.10.0"); // 语义比较不是字符串比较
                ok &= !BaoWuLearn.Core.Update.UpdateManifestParser.IsNewer("1.0.40", "1.0.40");
                var pd = m.PubDate;
                ok &= BaoWuLearn.Core.Update.UpdateManifestParser.PubDateAccepts(pd, pd);
                ok &= !BaoWuLearn.Core.Update.UpdateManifestParser.PubDateAccepts(pd, pd.AddDays(1));
                var chain = BaoWuLearn.Core.Update.UpdateManifestParser.BuildEndpointChain(
                    "https://github.com/o/r/releases/download/latest/update.json",
                    ["https://m1", "bad-entry", "https://m1/", ""]);
                ok &= chain.Count == 2; // 直连 + m1（非法剔除、重复合并、自动补斜杠）
                ok &= chain[0].StartsWith("https://github.com", StringComparison.Ordinal);
                ok &= chain[1] == "https://m1/https://github.com/o/r/releases/download/latest/update.json";
                // 缺 schema 的 JSON 必须报 UpdateException 而不是崩
                try
                {
                    BaoWuLearn.Core.Update.UpdateManifestParser.Parse("{}"u8.ToArray());
                    ok = false;
                }
                catch (BaoWuLearn.Core.Update.UpdateException) { /* 正确姿势 */ }
            }
            catch (Exception ex)
            {
                ok = false;
                W($"  ✖ 清单自检抛异常：{ex.GetType().Name}: {ex.Message}");
            }
            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 清单解析/版本比较/防回滚/端点链");
        }

        // (4) 两平台换装脚本的形态断言（不真实执行——那是砸自己 .app 的事）
        {
            var ok = true;
            var bat = BaoWuLearn.Core.Update.UpdateService.BuildWindowsSwapScript(
                4321, @"C:\apps\宝武学习助手.exe", @"C:\apps\宝武学习助手.exe.new", @"C:\temp\bw.log");
            ok &= bat.Contains("Get-Process -Id 4321");            // 等主进程退出
            ok &= bat.Contains("'.exe'.bak") || bat.Contains(".bak"); // 旧版留备份
            ok &= bat.Contains("Start-Process");                   // 装完拉起新版
            ok &= bat.Contains("swap-fail-rolledback");            // 失败回滚路径存在
            ok &= bat.Contains("del \"%~f0\"");                    // 脚本自删

            var sh = BaoWuLearn.Core.Update.UpdateService.BuildMacSwapScript(
                4321, "/Applications/宝武学习助手.app", "/tmp/st/宝武学习助手.app",
                "/Applications/宝武学习助手.app.old", "/tmp/bw.log");
            ok &= sh.Contains("kill -0 4321");                     // 等主进程退出
            ok &= sh.IndexOf("app.old\"", StringComparison.Ordinal)
                < sh.IndexOf("open ", StringComparison.Ordinal);   // 先挪旧再拉起
            ok &= sh.Contains("swap-fail-rolledback");             // 失败回滚路径存在
            ok &= sh.Contains("rm -f \"$0\"");                     // 脚本自删
            ok &= sh.Contains("quarantine");                       // 新版免 Gatekeeper 拦截

            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 换装脚本（win bat / mac sh 关键步骤齐全）");
        }
        W("");

        // ── 多账号运行时池自检（v1.0.42）──────────────────
        // 用假工号走真实 Hub：Create/Remove 全程不发网络请求（token 只是字符串），
        // 队列存档键是假工号、移出时存档里也是空条目即删，不会污染真用户的档。
        W("─── 多账号运行时池自检 ───");

        // (1) 运行时隔离：token 各挂各的、Find/Active/Changed 事件正确
        {
            var hub = vm.Hub;
            var changedCount = 0;
            hub.Changed += () => changedCount++;

            var rtA = hub.Create("自检-甲", "自检甲", "TOKEN-A");
            var rtB = hub.Create("自检-乙", "自检乙", null);

            var ok = rtA.Api.Token == "TOKEN-A"
                     && rtB.Api.Token is null
                     && !ReferenceEquals(rtA.Api, rtB.Api)
                     && !ReferenceEquals(rtA.Engine, rtB.Engine)
                     && ReferenceEquals(hub.Active, rtB)
                     && ReferenceEquals(hub.Find("自检-甲"), rtA);

            hub.Remove(rtA);
            hub.Remove(rtB);
            // Changed 全程应广播 ≥4 次：两次 Create 换 Active + 两次 Remove 收尾各一次。
            // ★ 计数断言必须放在 Remove 之后 —— 提前断言是这条自检初版自己踩的坑。
            ok &= hub.All.Count == 0 && hub.Active is null && changedCount >= 4;
            if (!ok)
                W($"    tokenA={rtA.Api.Token} tokenB={rtB.Api.Token ?? "null"} " +
                  $"findA={ReferenceEquals(hub.Find("自检-甲"), rtA)} " +
                  $"changed={changedCount} left={hub.All.Count} activeNull={hub.Active is null}");

            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 运行时池：token 隔离 / 查找 / Active / 事件广播");
        }

        // (2) 「全部开始」错峰排期：严格递增、首个 8~20 秒、步进 55~240 秒、同种子确定性
        {
            var seed = 20260918;
            var d5 = BaoWuLearn.Desktop.Services.RuntimeHub.StartAllDelays(5, new Random(seed));
            var ok = d5.Count == 5
                     && d5[0] >= TimeSpan.FromSeconds(8)
                     && d5[0] <= TimeSpan.FromSeconds(20);
            for (var i = 1; i < d5.Count && ok; i++)
            {
                var step = d5[i] - d5[i - 1];
                ok &= step >= TimeSpan.FromSeconds(55) && step <= TimeSpan.FromSeconds(240);
            }
            ok &= BaoWuLearn.Desktop.Services.RuntimeHub.StartAllDelays(0, new Random(seed)).Count == 0;
            ok &= BaoWuLearn.Desktop.Services.RuntimeHub.StartAllDelays(1, new Random(seed))[0] == d5[0];

            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 全部开始错峰排期（严格递增 5 档 / 空表 / 确定性）");
        }

        // (3) 总览行状态徽章映射（过期优先于一切）
        {
            var ok = BaoWuLearn.Desktop.ViewModels.FleetViewModel.RowStatus(
                         BaoWuLearn.Core.Services.EngineState.Running, false, 3) == "● 挂机中"
                     && BaoWuLearn.Desktop.ViewModels.FleetViewModel.RowStatus(
                         BaoWuLearn.Core.Services.EngineState.Running, true, 3) == "⛔ 已过期"
                     && BaoWuLearn.Desktop.ViewModels.FleetViewModel.RowStatus(
                         BaoWuLearn.Core.Services.EngineState.Paused, false, 0) == "⏸ 已暂停"
                     && BaoWuLearn.Desktop.ViewModels.FleetViewModel.RowStatus(
                         BaoWuLearn.Core.Services.EngineState.Stopping, false, 0) == "◌ 停止中"
                     && BaoWuLearn.Desktop.ViewModels.FleetViewModel.RowStatus(
                         BaoWuLearn.Core.Services.EngineState.Idle, false, 0) == "○ 空闲"
                     && BaoWuLearn.Desktop.ViewModels.FleetViewModel.RowStatus(
                         BaoWuLearn.Core.Services.EngineState.Idle, false, 2) == "▷ 空闲可挂"
                     && BaoWuLearn.Desktop.ViewModels.FleetViewModel.RowStatus(
                         BaoWuLearn.Core.Services.EngineState.Stopped, false, 1) == "▷ 空闲可挂";

            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 总览行状态徽章映射");
        }

        // (4) 「挂后台」语义：只换 Active，运行时留在池里、引擎没被停、没被释放
        {
            var hub = vm.Hub;
            var rtC = hub.Create("自检-丙", "自检丙", "TOKEN-C");
            var stateBefore = rtC.Engine.State;

            hub.SetActive(null);   // 挂后台的核心动作就是这个
            var ok = hub.All.Count(r => r.UserNo == "自检-丙") == 1
                     && rtC.Engine.State == stateBefore
                     && rtC.Api.Token == "TOKEN-C"   // 还能用 = 没被 Dispose
                     && hub.AllIdle;                 // 全池空闲 = 更新安装门仍开

            hub.SetActive(rtC);
            ok &= ReferenceEquals(hub.Active, rtC);
            hub.Remove(rtC);
            ok &= hub.All.Count == 0;

            if (ok) pass++; else fail++;
            W($"  {(ok ? "✓" : "✖")} 挂后台不杀运行时（留池 / 引擎不动 / 移除才释放）");
        }
        W("");

        // 回到总览，避免自检结束时停在别的页面
        vm.NavigateCommand.Execute("dashboard");

        W("──────────────────────────────────────────");
        W($"总计：通过 {pass} 项，失败 {fail} 项");
        W(fail == 0
            ? ">>> 全部通过：验证码链路、页面渲染、皮肤与密度、更新通道、多账号运行时池均正常。"
            : ">>> 存在失败项，请把本文件内容发回以便排查。");
        W("==========================================");

        var report = sb.ToString();

        // 落盘一份，方便在没有控制台的 Windows 上取用
        try
        {
            var file = Path.Combine(AppPaths.LogDirectory, "selftest.txt");
            File.WriteAllText(file, report, Encoding.UTF8);
            Console.WriteLine($"自检报告已保存：{file}");
        }
        catch
        {
            // 写不了就算了，标准输出里已经有
        }

        return report;
    }

    /// <summary>删除自检产生的临时文件（删不掉也无所谓，落在系统临时目录里）。</summary>
    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}
