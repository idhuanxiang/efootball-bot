using System.IO;
using System.Text;
using System.Text.Json;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using SkiaSharp;

namespace EfootballBot.Core.Bot.Sampling;

/// <summary>单次采样落盘结果。</summary>
public sealed record SampleWriteResult(
    bool Saved,
    string Bucket,
    string PageId,
    string? ImagePath,
    string Reason);

/// <summary>界面跳转边记录（用于路径导航建模，含可选光标移动方向与焦点词）。</summary>
public sealed record PageTransitionRecord(
    string Timestamp,
    string FromPage,
    string InputAction,
    string ToPage,
    bool IsUnknownTarget,
    int Width,
    int Height,
    string? FocusDirection = null,
    string? FocusedText = null,
    double? FocusX = null,
    double? FocusY = null);

/// <summary>
/// 统一智能数据集采样与路径记录器：
/// - 统一输出无损 PNG + 标准 YOLO `.txt` + `.meta.json` + 未知页 `prompt.md` + 导航图 `navigation_graph.md`；
/// - 落盘前自动经过 <see cref="FrameQualityFilter"/>（剔除黑屏/纯色/重复帧）与 <see cref="AutoLabelGuard"/>（剔除反标伪标签）；
/// - 后台异步写盘，调用线程仅做像素克隆与轻量过滤，不影响主循环实时性。
/// </summary>
public sealed class DatasetSampleWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOpts = new() { WriteIndented = false };

    private readonly string _rootDir;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private Task _writeChain = Task.CompletedTask;
    private readonly Dictionary<string, Queue<(ulong Hash, HashSet<string> Words)>> _recentByBucket = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownPrompts = new(StringComparer.Ordinal);
    private readonly List<PageTransitionRecord> _transitions = new();
    private readonly Dictionary<string, (string Summary, int Count)> _unknownSummaries = new(StringComparer.Ordinal);
    private bool _classesWritten;
    /// <summary>上一次落盘用的毫秒时间戳：同一毫秒内的连续采样靠它顺延，避免同名 PNG 互相覆盖。</summary>
    private long _lastStampMs;

    public int SavedCount { get; private set; }
    public int FilteredBlackCount { get; private set; }
    public int FilteredDuplicateCount { get; private set; }
    public int FilteredMislabelCount { get; private set; }
    public int UnknownPageCount => _unknownSummaries.Count;
    public int TransitionCount => _transitions.Count;
    public string RootDir => _rootDir;

    /// <summary>因触达单日帧数上限而没落盘的帧数（0 说明配额没起过作用）。</summary>
    public int DroppedByQuotaCount { get; private set; }

    /// <summary>单日帧数上限；0 或负数表示不限。</summary>
    private readonly int _maxFramesPerDay;
    /// <summary>当日已落盘帧数缓存；-1 表示还没校准过。</summary>
    private int _dayFrameCount = -1;

    public DatasetSampleWriter(string? rootDir = null, int maxFramesPerDay = 0)
    {
        _rootDir = rootDir ?? Path.Combine(AppPaths.DataDir, "samples", DateTime.Now.ToString("yyyyMMdd"));
        _maxFramesPerDay = maxFramesPerDay;
    }

    /// <summary>配额预占：调用方必须持有 _gate。首次调用时枚举当日目录校准计数（只在挂机早期发生一次）。</summary>
    private bool TryReserveDaySlotLocked()
    {
        if (_maxFramesPerDay <= 0) return true;
        if (_dayFrameCount < 0) _dayFrameCount = CountDayFrames();
        if (_dayFrameCount >= _maxFramesPerDay) return false;
        _dayFrameCount++;
        return true;
    }

    private int CountDayFrames()
    {
        try
        {
            if (!Directory.Exists(_rootDir)) return 0;
            return Directory.EnumerateFiles(_rootDir, "*.png", SearchOption.AllDirectories).Count();
        }
        catch { return 0; }
    }

    /// <summary>
    /// 审核并异步落盘一帧高质量样本（无损 PNG + 元数据 + 清洗后 YOLO 标注 + 未知页提示词）。
    /// </summary>
    public SampleWriteResult TryRecordFrame(
        FrameData frame,
        OcrResult ocr,
        IReadOnlyList<YoloDetection>? detections,
        MatchScreen screen,
        bool isCn,
        string sourceMode,
        string? pageTagOverride = null,
        string? transitionSummary = null,
        string? note = null,
        FocusShiftResult? focusShift = null)
    {
        var dets = detections ?? Array.Empty<YoloDetection>();

        // ① 画面质量门控：剔除黑屏/过暗过渡帧与纯色过渡帧
        var (usable, qualityReason) = FrameQualityFilter.CheckUsable(frame);
        if (!usable)
        {
            lock (_gate) FilteredBlackCount++;
            return new SampleWriteResult(false, "filtered", pageTagOverride ?? screen.ToString(), null, qualityReason);
        }

        // ② 反标伪标签清洗：剔除过场「跳过」误标 btn_back，隔离低置信度框
        var audit = AutoLabelGuard.Audit(ocr, dets, screen);
        if (audit.DropEntireFrame)
        {
            lock (_gate) FilteredMislabelCount++;
            return new SampleWriteResult(false, "filtered", pageTagOverride ?? screen.ToString(), null,
                audit.DropReason ?? "mislabel_dropped");
        }

        // ③ 页面归类（已知页面 / 未知聚类页面 / 待复核 / OCR 难例）
        bool isUnknown = screen == MatchScreen.Unknown && string.IsNullOrWhiteSpace(pageTagOverride);
        string pageId = !string.IsNullOrWhiteSpace(pageTagOverride)
            ? SanitizeName(pageTagOverride)
            : (isUnknown ? UnknownPagePromptBuilder.BuildUnknownId(ocr, isCn) : screen.ToString());

        string bucket;
        string subDir;
        if (isUnknown)
        {
            bucket = "unknown";
            subDir = Path.Combine("unknown", pageId);
        }
        else if (audit.NeedsReview)
        {
            bucket = "review";
            subDir = "review";
        }
        else if (audit.IsOcrHardCase)
        {
            bucket = "ocr_hard";
            subDir = "ocr_hard";
        }
        else
        {
            bucket = "known";
            subDir = Path.Combine("known", pageId);
        }

        // ④ 重复帧去重（按桶内最近 8 帧 dHash + OCR Jaccard）
        ulong hash = FrameQualityFilter.ComputeDHash(frame);
        var wordSet = FrameQualityFilter.ExtractWordSet(ocr);
        string dedupKey = $"{bucket}:{pageId}";
        lock (_gate)
        {
            if (!_recentByBucket.TryGetValue(dedupKey, out var queue))
            {
                queue = new Queue<(ulong, HashSet<string>)>();
                _recentByBucket[dedupKey] = queue;
            }
            foreach (var prev in queue)
            {
                double jaccard = FrameQualityFilter.OcrJaccard(wordSet, prev.Words);
                if (focusShift is not { Moved: true } &&
                    FrameQualityFilter.HammingDistance(hash, prev.Hash) <= 4 &&
                    jaccard >= 0.90)
                {
                    FilteredDuplicateCount++;
                    return new SampleWriteResult(false, "filtered", pageId, null, "duplicate_frame");
                }
            }
            queue.Enqueue((hash, wordSet));
            while (queue.Count > 8) queue.Dequeue();

            // 容量上限：日志有保留天数/条数清理，样本目录以前完全没有。样本只是辅助数据，
            // 长期挂机不能让它把磁盘吃满——到达上限就停止写入（保留已有样本，不做删除，
            // 免得把待标注的好帧一起清掉）。0 或负数表示不限。
            if (!TryReserveDaySlotLocked())
            {
                DroppedByQuotaCount++;
                return new SampleWriteResult(false, "filtered", pageId, null, "quota_exceeded");
            }

            SavedCount++;
            if (audit.RejectedBoxes.Count > 0)
                FilteredMislabelCount += audit.RejectedBoxes.Count;

            if (isUnknown)
            {
                string summary = string.Join(" | ", ocr.Words.Select(w => w.Text).Take(5));
                if (_unknownSummaries.TryGetValue(pageId, out var cur))
                    _unknownSummaries[pageId] = (cur.Summary, cur.Count + 1);
                else
                    _unknownSummaries[pageId] = (summary, 1);
            }
        }

        // ⑤ 拷贝像素并在后台异步写盘（无损 PNG + YOLO .txt + .meta.json + 未知页 prompt.md）
        var pixels = (byte[])frame.Bgra.Clone();
        int width = frame.Width;
        int height = frame.Height;
        // 文件名靠毫秒戳区分：批量回放或并发调用时同一毫秒可能连落两帧，同名 PNG 会互相覆盖
        // （写盘是异步的，存在性检查不可靠）。这里让戳在进程内严格单调递增，撞了就顺延 1ms。
        string stamp;
        lock (_gate)
        {
            long nowMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            if (nowMs <= _lastStampMs) nowMs = _lastStampMs + 1;
            _lastStampMs = nowMs;
            stamp = new DateTime(nowMs * TimeSpan.TicksPerMillisecond).ToString("HHmmss_fff");
        }
        string fileStem = $"{stamp}_{pageId}_{width}x{height}";
        string targetDir = Path.Combine(_rootDir, subDir);
        string imgDir = bucket is "unknown" ? targetDir : Path.Combine(targetDir, "images");
        string lblDir = bucket is "unknown" ? targetDir : Path.Combine(targetDir, "labels");
        string metaDir = bucket is "unknown" ? targetDir : Path.Combine(targetDir, "meta");
        string imgPath = Path.Combine(imgDir, fileStem + ".png");

        bool writePrompt = false;
        string? promptText = null;
        if (isUnknown)
        {
            lock (_gate)
            {
                if (_knownPrompts.Add(pageId))
                {
                    writePrompt = true;
                    promptText = UnknownPagePromptBuilder.BuildPromptMarkdown(
                        pageId, isCn, width, height, sourceMode, transitionSummary, ocr, dets, audit);
                }
            }
        }

        var metaObj = new
        {
            page_id = pageId,
            bucket,
            region = isCn ? "cn" : "global",
            screen = screen.ToString(),
            source_mode = sourceMode,
            width,
            height,
            dhash = $"0x{hash:X16}",
            transition = transitionSummary,
            note,
            focus_shift = focusShift is { Moved: true } fs
                ? new { fs.Direction, fs.OldX, fs.OldY, fs.NewX, fs.NewY, fs.FocusedText, fs.ChangedCells }
                : null,
            captured_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ocr_hard_reasons = audit.OcrHardReasons,
            rejected_boxes = audit.RejectedBoxes,
            review_boxes = audit.ReviewBoxes.Select(r => new { r.ClassName, r.Score, r.CenterX, r.CenterY, r.W, r.H }),
            ocr_words = ocr.Words.Select(w => new { w.Text, w.Confidence, w.X, w.Y, w.W, w.H }),
        };
        string metaJson = JsonSerializer.Serialize(metaObj, JsonOpts);
        string? yoloTxt = audit.CleanBoxes.Count > 0
            ? string.Join("\n", audit.CleanBoxes.Select(b => b.ToYoloLine())) + "\n"
            : null;

        EnqueueWrite(() =>
        {
            try
            {
                EnsureClassesFile();
                Directory.CreateDirectory(imgDir);
                Directory.CreateDirectory(metaDir);
                WriteLosslessPng(pixels, width, height, imgPath);
                File.WriteAllText(Path.Combine(metaDir, fileStem + ".meta.json"), metaJson, Utf8NoBom);
                if (yoloTxt is not null)
                {
                    Directory.CreateDirectory(lblDir);
                    File.WriteAllText(Path.Combine(lblDir, fileStem + ".txt"), yoloTxt, Utf8NoBom);
                }
                if (writePrompt && promptText is not null)
                {
                    File.WriteAllText(Path.Combine(targetDir, "prompt.md"), promptText, Utf8NoBom);
                }
            }
            catch { /* 后台写盘失败静默，绝不中断主循环 */ }
        });

        return new SampleWriteResult(true, bucket, pageId, imgPath, note ?? "saved");
    }

    /// <summary>记录一条「来源页 --[操作]--> 目标页」导航跳转边（可附带光标移动方向与焦点词）。</summary>
    public void RecordTransition(
        string fromPage,
        string inputAction,
        string toPage,
        bool isUnknownTarget,
        int width,
        int height,
        FocusShiftResult? focusShift = null)
    {
        if (string.IsNullOrWhiteSpace(fromPage) || string.IsNullOrWhiteSpace(toPage)) return;
        bool hasFocus = focusShift is { Moved: true };
        var rec = new PageTransitionRecord(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            fromPage,
            inputAction,
            toPage,
            isUnknownTarget,
            width,
            height,
            hasFocus ? focusShift!.Direction : null,
            hasFocus ? focusShift!.FocusedText : null,
            hasFocus ? Math.Round(focusShift!.NewX, 4) : null,
            hasFocus ? Math.Round(focusShift!.NewY, 4) : null);
        string jsonLine = JsonSerializer.Serialize(rec, JsonLineOpts);

        lock (_gate)
            _transitions.Add(rec);

        EnqueueWrite(() =>
        {
            try
            {
                Directory.CreateDirectory(_rootDir);
                File.AppendAllText(Path.Combine(_rootDir, "transitions.jsonl"), jsonLine + "\n", Utf8NoBom);
            }
            catch { }
        });
    }

    /// <summary>等待所有排队中的后台 PNG / JSON / 路径写盘任务完成。</summary>
    public void Flush(int timeoutMs = 3000)
    {
        Task pending;
        lock (_writeGate)
            pending = _writeChain;
        try { pending.Wait(timeoutMs); } catch { }
    }

    private void EnqueueWrite(Action action)
    {
        lock (_writeGate)
        {
            _writeChain = _writeChain.ContinueWith(
                _ => action(),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>生成并更新导航路径图（navigation_graph.md，含 Mermaid 流程图与未知页汇总）。</summary>
    public void WriteNavigationGraph()
    {
        Flush(3000);
        List<PageTransitionRecord> edges;
        List<(string Id, string Summary, int Count)> unknowns;
        int saved, black, dup, mislabel;
        lock (_gate)
        {
            edges = _transitions.ToList();
            unknowns = _unknownSummaries.Select(kv => (kv.Key, kv.Value.Summary, kv.Value.Count)).ToList();
            saved = SavedCount;
            black = FilteredBlackCount;
            dup = FilteredDuplicateCount;
            mislabel = FilteredMislabelCount;
        }

        try
        {
            Directory.CreateDirectory(_rootDir);
            EnsureClassesFile();
            var sb = new StringBuilder();
            sb.AppendLine("# 界面导航路径与采样汇总");
            sb.AppendLine();
            sb.AppendLine($"- **统计**：已保存 `{saved}` 帧无损 PNG | 剔除黑屏/过渡 `{black}` 帧 | 剔除重复 `{dup}` 帧 | 拦截反标干扰 `{mislabel}` 次 | 未知页面 `{unknowns.Count}` 类");
            sb.AppendLine();
            sb.AppendLine("## 1. 界面跳转路径图");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine("flowchart LR");
            var seenEdges = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in edges)
            {
                string edgeLabel = e.InputAction;
                if (!string.IsNullOrWhiteSpace(e.FocusedText))
                    edgeLabel = $"{e.InputAction} @ 「{e.FocusedText}」";
                string key = $"{e.FromPage}|{edgeLabel}|{e.ToPage}";
                if (!seenEdges.Add(key)) continue;
                string fromNode = SanitizeMermaidId(e.FromPage);
                string toNode = SanitizeMermaidId(e.ToPage);
                string label = edgeLabel.Replace("\"", "'");
                sb.AppendLine($"    {fromNode}[\"{e.FromPage}\"] -->|\"{label}\"| {toNode}[\"{e.ToPage}\"]");
            }
            if (seenEdges.Count == 0)
                sb.AppendLine("    Start[\"等待采集路径\"]");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## 2. 未知页面聚类清单");
            sb.AppendLine();
            if (unknowns.Count == 0)
            {
                sb.AppendLine("- 本次未发现未知页面。");
            }
            else
            {
                sb.AppendLine("| 未知页 ID | 采样数 | 提示词路径 | 典型文本摘要 |");
                sb.AppendLine("|---|---:|---|---|");
                foreach (var u in unknowns)
                    sb.AppendLine($"| `{u.Id}` | {u.Count} | `unknown/{u.Id}/prompt.md` | {u.Summary.Replace("|", "/")} |");
            }

            var cursorMoves = edges
                .Where(e => e.FromPage == e.ToPage && !string.IsNullOrWhiteSpace(e.FocusDirection))
                .GroupBy(e => (e.ToPage, e.InputAction, e.FocusDirection, Text: e.FocusedText ?? "-"))
                .Select(g => (g.Key.ToPage, g.Key.InputAction, g.Key.FocusDirection, g.Key.Text, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();
            sb.AppendLine();
            sb.AppendLine("## 3. 页内光标移动路径");
            sb.AppendLine();
            if (cursorMoves.Count == 0)
            {
                sb.AppendLine("- 本次未记录到光标位移事件。");
            }
            else
            {
                sb.AppendLine("| 页面 | 触发方式 | 移动方向 | 焦点词 | 次数 |");
                sb.AppendLine("|---|---|---|---|---:|");
                foreach (var c in cursorMoves)
                    sb.AppendLine($"| `{c.ToPage}` | `{c.InputAction}` | `{c.FocusDirection}` | {c.Text.Replace("|", "/")} | {c.Count} |");
            }

            File.WriteAllText(Path.Combine(_rootDir, "navigation_graph.md"), sb.ToString(), Utf8NoBom);
        }
        catch { }
    }

    private void EnsureClassesFile()
    {
        if (_classesWritten) return;
        lock (_gate)
        {
            if (_classesWritten) return;
            Directory.CreateDirectory(_rootDir);
            string path = Path.Combine(_rootDir, "classes.txt");
            if (!File.Exists(path))
                File.WriteAllText(path, string.Join("\n", AutoLabelGuard.YoloClasses) + "\n", Utf8NoBom);
            _classesWritten = true;
        }
    }

    private static void WriteLosslessPng(byte[] bgra, int width, int height, string path)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = bgra)
            {
                using var bmp = new SKBitmap();
                if (!bmp.InstallPixels(info, (nint)p, info.RowBytes)) return;
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(path);
                data.SaveTo(fs);
            }
        }
    }

    private static string SanitizeName(string raw)
    {
        var chars = raw.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return chars.Length == 0 ? "sample" : new string(chars);
    }

    private static string SanitizeMermaidId(string raw)
        => "N_" + SanitizeName(raw);
}
