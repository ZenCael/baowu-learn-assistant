using System.Text.Json.Serialization;

namespace BaoWuLearn.Core.Download;

public enum DownloadState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// 一条下载任务（一个课件 = 一条任务，多视频课件天然是课程下的多条）。
/// 字段面向持久化：重启后 Queued/Running/Paused/Failed 都还原成可续状态。
/// </summary>
public sealed class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // 归属（展示与目录组织用）
    public string CourseNo { get; set; } = "";
    public string CourseTitle { get; set; } = "";
    public string WareId { get; set; } = "";
    public string WareName { get; set; } = "";

    public WareSourceKind Kind { get; set; }

    /// <summary>HLS=主清单 URL；直链=文件 URL；预览=wareCode（二段解析）。</summary>
    public string Source { get; set; } = "";

    /// <summary>落盘完整路径（含目录）。运行完成后文件就在这里。</summary>
    public string TargetPath { get; set; } = "";

    public DownloadState State { get; set; } = DownloadState.Queued;
    public string? Error { get; set; }

    [JsonIgnore]
    public long TotalBytes;
    [JsonIgnore]
    public long DoneBytes;
    [JsonIgnore]
    public int SegmentsTotal;
    [JsonIgnore]
    public int SegmentsDone;

    /// <summary>进度文本（直链按字节、HLS 按分片）。</summary>
    [JsonIgnore]
    public string ProgressText
    {
        get
        {
            if (Kind == WareSourceKind.HlsStream && SegmentsTotal > 0)
                return $"{SegmentsDone}/{SegmentsTotal} 分片";
            if (TotalBytes > 0)
                return $"{Fmt(DoneBytes)} / {Fmt(TotalBytes)}";
            return DoneBytes > 0 ? Fmt(DoneBytes) : "—";
        }
    }

    [JsonIgnore]
    public double? Fraction
    {
        get
        {
            if (State == DownloadState.Completed) return 1;
            if (Kind == WareSourceKind.HlsStream && SegmentsTotal > 0)
                return (double)SegmentsDone / SegmentsTotal;
            if (TotalBytes > 0) return Math.Min(1.0, (double)DoneBytes / TotalBytes);
            return null;
        }
    }

    private static string Fmt(long bytes)
    {
        double b = bytes;
        foreach (var u in new[] { "B", "KB", "MB", "GB" })
        {
            if (b < 1024 || u == "GB") return $"{b:0.#}{u}";
            b /= 1024;
        }
        return bytes + "B";
    }
}
