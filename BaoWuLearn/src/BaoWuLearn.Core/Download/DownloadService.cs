using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;

namespace BaoWuLearn.Core.Download;

/// <summary>
/// 课件下载引擎（v1.0.44+）。
///
/// 三形态分流（见 <see cref="WareSourceResolver"/>）：
///  - 直链/静态区：自有 HttpClient（零鉴权，长超时，Range 断点续传）；
///  - HLS：master 选最高档 → 分片 4 路并发 → AES-128 就地解密 → 顺序合并；
///  - 预览通道（PDF/附件）：经调用方注入的 <see cref="ApiClient"/> 鉴权，
///    二段解析 fileId 后走文件下载。
///
/// 设计约束：
///  - 与挂机完全解耦 —— 静态区流量不走 ApiClient，不给账号审计添新特征；
///  - 任务表落盘（download.json），重启恢复；运行中任务还原为 Paused 等用户点续；
///  - 并发上限 2（课程级带宽友好）；任何失败必须带可读原因，绝不静默。
/// </summary>
public sealed class DownloadService : IDisposable
{
    private readonly string _storePath;
    private readonly Func<ApiClient?> _apiProvider;
    private readonly Func<string> _rootProvider;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lane = new(2, 2);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancels = new();
    private readonly List<DownloadTask> _tasks = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>任务列表/状态变化广播（任意线程；UI 侧自行 marshal）。</summary>
    public event Action? Changed;
    /// <summary>进度变化广播（任意线程）。</summary>
    public event Action? Progressed;

    public DownloadService(Func<ApiClient?> apiProvider, Func<string> downloadRoot, string storePath)
    {
        _apiProvider = apiProvider;
        _rootProvider = downloadRoot;
        _storePath = storePath;

        // 独立 HttpClient：不设总超时（大文件），取消全靠 CTS；
        // 不解压（AutomaticDecompression 留默认 None），保证二进制原样落盘。
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");

        Load();
    }

    public IReadOnlyList<DownloadTask> Snapshot()
    {
        lock (_gate) return _tasks.ToList();
    }

    // ── 入队 ─────────────────────────────────────────────

    /// <summary>把课件排入下载队列。不可下载形态抛 <see cref="DownloadException"/>（带原因）。</summary>
    public DownloadTask Enqueue(CourseItem course, WareItem ware)
    {
        var source = WareSourceResolver.Resolve(ware);
        if (source.Kind == WareSourceKind.NotDownloadable)
            throw new DownloadException("该课件没有可用的下载地址");

        var ext = source.SuggestedExtension ?? (source.Kind == WareSourceKind.HlsStream ? ".ts" : ".bin");
        var target = BuildTargetPath(course, ware, ext, source.Kind);

        var task = new DownloadTask
        {
            CourseNo = course.CourseNo,
            CourseTitle = WareSourceResolver.SanitizeFileName(course.CourseName, "未命名课程"),
            WareId = ware.WareId,
            WareName = WareSourceResolver.SanitizeFileName(ware.WareName),
            Kind = source.Kind,
            Source = source.Kind == WareSourceKind.PreviewFile ? ware.WareId : source.Url!,
            TargetPath = target,
        };

        lock (_gate)
        {
            // 去重：同课件在队（未完成态）就不重复排
            var dup = _tasks.FirstOrDefault(t =>
                t.WareId == task.WareId && t.CourseNo == task.CourseNo &&
                t.State is DownloadState.Queued or DownloadState.Running or DownloadState.Paused);
            if (dup is not null) return dup;

            _tasks.Add(task);
        }

        Save();
        Changed?.Invoke();
        _ = RunAsync(task.Id);
        return task;
    }

    private string BuildTargetPath(CourseItem course, WareItem ware, string ext, WareSourceKind kind)
    {
        var courseDir = Path.Combine(_rootProvider(), "宝武学习助手",
            WareSourceResolver.SanitizeFileName(course.CourseName, "未命名课程"));
        var baseName = WareSourceResolver.SanitizeFileName(ware.WareName);
        // 同名课件（重名章节很常见）用编号尾段消歧
        var name = File.Exists(Path.Combine(courseDir, baseName + ext)) ||
                   _tasks.Any(t => t.TargetPath == Path.Combine(courseDir, baseName + ext))
            ? $"{baseName}_{ware.WareId[^Math.Min(6, ware.WareId.Length)]}"
            : baseName;
        return Path.Combine(courseDir, name + ext);
    }

    // ── 控制指令 ─────────────────────────────────────────

    public void Pause(string id) => CancelTo(id, DownloadState.Paused);
    public void Cancel(string id) => CancelTo(id, DownloadState.Failed, "已取消");

    private void CancelTo(string id, DownloadState to, string? err = null)
    {
        if (_cancels.TryGetValue(id, out var cts)) cts.Cancel();
        lock (_gate)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t is null || t.State == DownloadState.Completed) return;
            t.State = to;
            if (err is not null) t.Error = err;
        }
        Save();
        Changed?.Invoke();
    }

    public void Resume(string id)
    {
        lock (_gate)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t is null || t.State is DownloadState.Completed) return;
            t.State = DownloadState.Queued;
            t.Error = null;
        }
        Save();
        Changed?.Invoke();
        _ = RunAsync(id);
    }

    public void Remove(string id)
    {
        CancelTo(id, DownloadState.Failed, "已移除");
        lock (_gate) _tasks.RemoveAll(t => t.Id == id);
        Save();
        Changed?.Invoke();
    }

    public void PauseAll()
    {
        List<string> ids;
        lock (_gate)
            ids = _tasks.Where(t => t.State is DownloadState.Queued or DownloadState.Running)
                        .Select(t => t.Id).ToList();
        foreach (var id in ids) Pause(id);
    }

    /// <summary>启动时把上次被进程退出打断的任务复位成可续状态。</summary>
    public void ReviveInterrupted()
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var t in _tasks.Where(t => t.State == DownloadState.Running))
            {
                t.State = DownloadState.Paused;
                t.Error = "上次下载被中断";
                changed = true;
            }
        }
        if (changed) { Save(); Changed?.Invoke(); }
    }

    private async Task RunAsync(string id)
    {
        await _lane.WaitAsync();
        DownloadTask task;
        lock (_gate)
        {
            task = _tasks.FirstOrDefault(t => t.Id == id)!;
            if (task is null || task.State != DownloadState.Queued)
            {
                _lane.Release();
                return;
            }
            task.State = DownloadState.Running;
            task.Error = null;
        }
        Changed?.Invoke();

        var cts = new CancellationTokenSource();
        _cancels[id] = cts;
        try
        {
            switch (task.Kind)
            {
                case WareSourceKind.DirectFile:
                    await DownloadDirectAsync(task, cts.Token);
                    break;
                case WareSourceKind.HlsStream:
                    await DownloadHlsAsync(task, cts.Token);
                    break;
                case WareSourceKind.PreviewFile:
                    await DownloadPreviewAsync(task, cts.Token);
                    break;
            }
            lock (_gate) task.State = DownloadState.Completed;
        }
        catch (OperationCanceledException)
        {
            // Pause/Cancel 已写好状态，这里不覆盖
        }
        catch (DownloadException ex)
        {
            lock (_gate) { task.State = DownloadState.Failed; task.Error = ex.Message; }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                task.State = DownloadState.Failed;
                task.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        finally
        {
            _cancels.TryRemove(id, out _);
            cts.Dispose();
            _lane.Release();
            Save();
            Changed?.Invoke();
        }
    }

    // ── 直链 ─────────────────────────────────────────────

    private async Task DownloadDirectAsync(DownloadTask task, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(task.TargetPath)!);
        var part = task.TargetPath + ".part";

        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, task.Source);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(part);   // 失效断点就地清掉，重试即从头
            throw new DownloadException("断点已失效（源文件可能更新过），重试该任务即可从头下载");
        }
        resp.EnsureSuccessStatusCode();

        var append = resp.StatusCode == HttpStatusCode.PartialContent;
        task.TotalBytes = append
            ? existing + (resp.Content.Headers.ContentLength ?? 0)
            : resp.Content.Headers.ContentLength ?? 0;
        task.DoneBytes = append ? existing : 0;

        await using (var fs = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buf = new byte[96 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                task.DoneBytes += n;
                Progressed?.Invoke();
            }
        }
        SwapIn(part, task.TargetPath);
    }

    // ── HLS ──────────────────────────────────────────────

    private async Task DownloadHlsAsync(DownloadTask task, CancellationToken ct)
    {
        var masterText = await GetTextAsync(task.Source, ct);
        var variants = M3u8.ParseMaster(masterText, task.Source);

        var mediaUrl = M3u8.PickHighest(variants)?.Uri ?? task.Source;
        var mediaText = await GetTextAsync(mediaUrl, ct);
        var media = M3u8.ParseMedia(mediaText, mediaUrl);

        if (media.Segments.Count == 0)
            throw new DownloadException("清单里没有分片（可能是直播流或加密方案不支持）");

        // 产物形态：fMP4（有 init 段）→ .mp4；纯 TS → .ts。改扩展名以内容为准。
        var ext = media.IsFmp4 ? ".mp4" : ".ts";
        var finalPath = Path.ChangeExtension(task.TargetPath, ext.TrimStart('.'));
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var segDir = finalPath + ".seg";
        Directory.CreateDirectory(segDir);

        var keyCache = new Dictionary<string, byte[]>();
        async Task<byte[]> GetKeyAsync(HlsKey key, CancellationToken ct2)
        {
            if (key.Uri is null) throw new DownloadException("AES 加密清单但没给 key URI");
            if (keyCache.TryGetValue(key.Uri, out var cached)) return cached;
            var bytes = await _http.GetByteArrayAsync(key.Uri, ct2);
            if (bytes.Length != 16) throw new DownloadException($"AES key 长度异常（{bytes.Length} 字节）");
            return keyCache[key.Uri] = bytes;
        }

        task.SegmentsTotal = media.Segments.Count + (media.InitSegment is null ? 0 : 1);
        task.SegmentsDone = 0;
        Progressed?.Invoke();

        // init 段（若有）先取
        if (media.InitSegment is not null)
        {
            var initPath = Path.Combine(segDir, "seg_000000.tmp");
            if (!File.Exists(initPath))
                await DownloadFileToAsync(media.InitSegment, initPath, ct);
            task.SegmentsDone++;
            Progressed?.Invoke();
        }

        var offset = media.InitSegment is null ? 0 : 1;
        var lane = new SemaphoreSlim(4);   // 分片 4 路并发
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var jobs = Enumerable.Range(0, media.Segments.Count).Select(async i =>
        {
            await lane.WaitAsync(ct);
            try
            {
                var segPath = Path.Combine(segDir, $"seg_{i + offset:D6}.tmp");
                if (File.Exists(segPath))
                {
                    task.SegmentsDone++;
                    Progressed?.Invoke();
                    return;   // 断点：已下过就跳过（tmp 存的是解密后的明文）
                }
                var seg = media.Segments[i];
                var bytes = await _http.GetByteArrayAsync(seg.Uri, ct);
                if (seg.Key is { IsAes128: true } k)
                {
                    var key = await GetKeyAsync(k, ct);
                    var iv = k.Iv ?? DefaultIv(i);
                    bytes = Aes128CbcDecrypt(bytes, key, iv);
                }
                File.WriteAllBytes(segPath, bytes);
                task.SegmentsDone++;
                Progressed?.Invoke();
            }
            finally { lane.Release(); }
        }).ToList();

        try { await Task.WhenAll(jobs); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Enqueue(ex.Message); }

        if (errors.Count > 0)
            throw new DownloadException($"分片下载失败：{errors.First()}（可重试，已下分片会保留）");

        if (!media.EndList)
        {
            var done = Directory.GetFiles(segDir, "*.tmp").Length;
            if (done < media.Segments.Count + (media.InitSegment is null ? 0 : 1))
                throw new DownloadException("流未完结（缺 ENDLIST）且分片不全");
        }

        // 顺序合并
        var part = finalPath + ".part";
        await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
        {
            if (media.InitSegment is not null)
                await using (var init = File.OpenRead(Path.Combine(segDir, "seg_000000.tmp")))
                    await init.CopyToAsync(fs, ct);
            for (var i = 0; i < media.Segments.Count; i++)
            {
                await using var seg = File.OpenRead(Path.Combine(segDir, $"seg_{i + offset:D6}.tmp"));
                await seg.CopyToAsync(fs, ct);
                ct.ThrowIfCancellationRequested();
            }
        }
        SwapIn(part, finalPath);
        Directory.Delete(segDir, true);   // 分片垃圾清干净

        task.TargetPath = finalPath;
    }

    /// <summary>HLS 规范：无显式 IV 时 IV = 前 8 字节 0 + 分片序号 8 字节大端（iv[8..15]）。</summary>
    public static byte[] DefaultIv(long sequence)
    {
        var iv = new byte[16];
        iv[8] = (byte)(sequence >> 56);
        iv[9] = (byte)(sequence >> 48);
        iv[10] = (byte)(sequence >> 40);
        iv[11] = (byte)(sequence >> 32);
        iv[12] = (byte)(sequence >> 24);
        iv[13] = (byte)(sequence >> 16);
        iv[14] = (byte)(sequence >> 8);
        iv[15] = (byte)sequence;
        return iv;
    }

    /// <summary>HLS AES-128 解密（CBC + PKCS7）。自检用固定向量断言。</summary>
    public static byte[] Aes128CbcDecrypt(byte[] cipher, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private async Task DownloadFileToAsync(string url, string path, CancellationToken ct)
    {
        var bytes = await _http.GetByteArrayAsync(url, ct);
        var tmp = path + ".writing";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
    }

    // ── 预览通道（PDF/附件，要鉴权）──────────────────────

    private async Task DownloadPreviewAsync(DownloadTask task, CancellationToken ct)
    {
        var api = _apiProvider()
            ?? throw new DownloadException("未登录 —— PDF/附件下载需要登录会话");

        using var doc = await api.PostRawAsync(
            ApiEndpoints.FilePreviewUrl, WareSourceResolver.PreviewPayload(task.WareId), ct: ct)
            ?? throw new DownloadException("预览文件查询没有返回内容");

        var data = ApiResponseReader.Data(doc.RootElement);
        if (data is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0)
            throw new DownloadException("预览文件为空（课件可能被平台撤下）");

        var first = arr[0];
        var fileId = TryStr(first, "fileId") ?? TryStr(first, "id")
            ?? throw new DownloadException("预览响应里找不到 fileId");

        // 文件名以服务端为准（带真扩展名）；拿不到就维持原猜测
        var nameHint = TryStr(first, "fileName") ?? TryStr(first, "name");
        var hintExt = nameHint is null ? null : Path.GetExtension(nameHint);
        var finalPath = !string.IsNullOrEmpty(hintExt) && hintExt.Length is >= 2 and <= 6
            ? Path.ChangeExtension(task.TargetPath, hintExt.TrimStart('.'))
            : task.TargetPath;

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var url = WareSourceResolver.FileDownloadUrl(fileId);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(api.Token))
            req.Headers.TryAddWithoutValidation("token", api.Token);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        task.TotalBytes = resp.Content.Headers.ContentLength ?? 0;
        task.DoneBytes = 0;

        var part = finalPath + ".part";
        await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buf = new byte[96 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                task.DoneBytes += n;
                Progressed?.Invoke();
            }
        }

        // 网关可能把鉴权失效藏回 JSON 错误体（文件接口同样如此），查一下
        if (task.DoneBytes < 4096 && await LooksLikeErrorBodyAsync(part))
        {
            File.Delete(part);
            throw new DownloadException("下载被拒绝：登录会话可能已过期，重新登录后重试");
        }

        SwapIn(part, finalPath);
        task.TargetPath = finalPath;
    }

    private static async Task<bool> LooksLikeErrorBodyAsync(string partPath)
    {
        try
        {
            var head = new byte[4096];
            await using var fs = File.OpenRead(partPath);
            var n = await fs.ReadAsync(head);
            var text = Encoding.UTF8.GetString(head, 0, n);
            return text.StartsWith('{') &&
                   (text.Contains("code", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("token", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string? TryStr(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── 工具 ─────────────────────────────────────────────

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>.part → 目标名原子交换（半新半旧不允许存在，同更新换装口径）。</summary>
    private static void SwapIn(string part, string target)
    {
        var bak = target + ".old";
        if (File.Exists(target)) File.Move(target, bak, overwrite: true);
        File.Move(part, target);
        if (File.Exists(bak)) File.Delete(bak);
    }

    // ── 持久化 ───────────────────────────────────────────

    private sealed record Persisted(
        string Id, string CourseNo, string CourseTitle, string WareId, string WareName,
        WareSourceKind Kind, string Source, string TargetPath, DownloadState State, string? Error);

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var list = JsonSerializer.Deserialize<List<Persisted>>(
                File.ReadAllText(_storePath), ApiClient.JsonOpts) ?? new();
            lock (_gate)
            {
                foreach (var p in list)
                    _tasks.Add(new DownloadTask
                    {
                        Id = p.Id, CourseNo = p.CourseNo, CourseTitle = p.CourseTitle,
                        WareId = p.WareId, WareName = p.WareName, Kind = p.Kind,
                        Source = p.Source, TargetPath = p.TargetPath,
                        State = p.State == DownloadState.Running ? DownloadState.Paused : p.State,
                        Error = p.Error,
                    });
            }
        }
        catch
        {
            // 任务表损坏不拦启动：当作空表，下轮 Save 会重写
        }
    }

    private void Save()
    {
        try
        {
            List<Persisted> list;
            lock (_gate)
            {
                // 完成态太多会拖慢读写：只保最近 300 条历史
                _tasks.RemoveAll(t => t.State == DownloadState.Completed
                                      && _tasks.Count(x => x.State == DownloadState.Completed) > 300);
                list = _tasks.Select(t => new Persisted(t.Id, t.CourseNo, t.CourseTitle,
                    t.WareId, t.WareName, t.Kind, t.Source, t.TargetPath, t.State, t.Error)).ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath,
                JsonSerializer.Serialize(list, ApiClient.JsonOpts));
        }
        catch { /* 尽力而为 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _cancels.Values) cts.Cancel();
        _http.Dispose();
    }
}

/// <summary>下载业务异常（消息直达 UI，必须是人话）。</summary>
public sealed class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
    public DownloadException(string message, Exception inner) : base(message, inner) { }
}
