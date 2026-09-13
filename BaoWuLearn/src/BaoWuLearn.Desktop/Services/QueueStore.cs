using System.Text.Json;
using System.Text.Json.Serialization;
using BaoWuLearn.Core.Models;

namespace BaoWuLearn.Desktop.Services;

/// <summary>
/// 挂课队列的本地存档。
///
/// 背景：队列原来只活在内存里，程序一关就没了 —— 每次重开都要到课程页把课重新挑一遍、
/// 重新排序，非常烦。这里把队列落到配置目录（与 settings.json 同处）。
///
/// <b>按账号分开存</b>：多个同事共用一台机器时，各自的队列互不干扰；
/// 换账号登录只会看到自己的那一份，不会把别人的课挂上去。
///
/// 只存"重建队列所必需的字段"。学时 / 成绩这类会变的数据也一并存下，
/// 这样恢复到列表里立刻就有数可看，随后再由正常的成绩回读覆盖成最新值。
/// </summary>
public sealed class QueueStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _sync = new();

    /// <param name="pathOverride">
    /// 存档文件路径。默认放用户配置目录；自检传临时文件，避免碰用户真实数据。
    /// </param>
    public QueueStore(string? pathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(pathOverride))
        {
            _path = pathOverride;
            var overrideDir = Path.GetDirectoryName(pathOverride);
            if (!string.IsNullOrEmpty(overrideDir)) Directory.CreateDirectory(overrideDir);
            return;
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BaoWuLearn");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "queue.json");
    }

    public string StorePath => _path;

    /// <summary>读取某个账号上次保存的队列（没有则返回空列表）。</summary>
    public List<CourseItem> Load(string userNo)
    {
        if (string.IsNullOrWhiteSpace(userNo)) return new List<CourseItem>();

        try
        {
            lock (_sync)
            {
                var file = ReadFile();
                if (file is null || !file.Users.TryGetValue(userNo, out var entry) || entry is null)
                    return new List<CourseItem>();

                return entry.Items.Select(ToCourseItem).Where(x => x is not null).Select(x => x!).ToList();
            }
        }
        catch
        {
            // 存档损坏或不可读都不该影响启动
            return new List<CourseItem>();
        }
    }

    /// <summary>保存某个账号的队列（传空列表即相当于清掉该账号的存档）。</summary>
    public void Save(string userNo, IEnumerable<CourseItem> items)
    {
        if (string.IsNullOrWhiteSpace(userNo)) return;

        try
        {
            var list = items.ToList();

            lock (_sync)
            {
                var file = ReadFile() ?? new QueueFile();
                if (list.Count == 0) file.Users.Remove(userNo);
                else file.Users[userNo] = new UserQueue
                {
                    SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Items = list.Select(ToEntry).ToList(),
                };

                File.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOpts));
            }
        }
        catch
        {
            // 写入失败（目录只读等）不应打断挂课
        }
    }

    /// <summary>存档里的账号清单（调试用）。</summary>
    public IReadOnlyList<string> SavedUsers()
    {
        try
        {
            lock (_sync) return (ReadFile()?.Users.Keys.ToList() ?? new List<string>());
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private QueueFile? ReadFile()
    {
        if (!File.Exists(_path)) return null;
        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<QueueFile>(json, JsonOpts);
    }

    private static QueueEntry ToEntry(CourseItem c) => new()
    {
        CourseName = c.CourseName,
        Guid = c.Guid,
        CenterCode = c.CenterCode,
        CenterName = c.CenterName,
        CourseNo = c.CourseNo,
        OlClassNo = c.OlClassNo,
        OlClassType = c.OlClassType,
        LearnStatus = c.LearnStatus,
        CourseHours = c.CourseHours,
        RequiredDuration = c.RequiredDuration,
        RequiredUnit = c.RequiredUnit,
        CompletedDuration = c.CompletedDuration,
        CompletedText = c.CompletedText,
        DurationScoreWeight = c.DurationScoreWeight,
        LearnScore = c.LearnScore,
        PassScore = c.PassScore,
    };

    private static CourseItem? ToCourseItem(QueueEntry e)
    {
        // courseNo / olClassNo 是队列的身份键，缺了就等于挂不了，直接丢弃
        if (string.IsNullOrEmpty(e.CourseNo) || string.IsNullOrEmpty(e.OlClassNo)) return null;

        return new CourseItem
        {
            CourseName = e.CourseName ?? "",
            Guid = e.Guid ?? "",
            CenterCode = e.CenterCode ?? "",
            CenterName = e.CenterName ?? "",
            CourseNo = e.CourseNo,
            OlClassNo = e.OlClassNo,
            OlClassType = e.OlClassType ?? "",
            LearnStatus = e.LearnStatus,
            CourseHours = e.CourseHours,
            RequiredDuration = e.RequiredDuration,
            RequiredUnit = e.RequiredUnit,
            CompletedDuration = e.CompletedDuration,
            CompletedText = e.CompletedText,
            DurationScoreWeight = e.DurationScoreWeight,
            LearnScore = e.LearnScore,
            PassScore = e.PassScore,
        };
    }

    // ── 存档结构 ──────────────────────────────────────────

    private sealed class QueueFile
    {
        public Dictionary<string, UserQueue> Users { get; set; } = new();
    }

    private sealed class UserQueue
    {
        public string? SavedAt { get; set; }
        public List<QueueEntry> Items { get; set; } = new();
    }

    private sealed class QueueEntry
    {
        public string? CourseName { get; set; }
        public string? Guid { get; set; }
        public string? CenterCode { get; set; }
        public string? CenterName { get; set; }
        public string? CourseNo { get; set; }
        public string? OlClassNo { get; set; }
        public string? OlClassType { get; set; }
        public string? LearnStatus { get; set; }
        public double? CourseHours { get; set; }
        public double? RequiredDuration { get; set; }
        public string? RequiredUnit { get; set; }
        public double? CompletedDuration { get; set; }
        public string? CompletedText { get; set; }
        public double? DurationScoreWeight { get; set; }
        public double? LearnScore { get; set; }
        public double? PassScore { get; set; }
    }
}
