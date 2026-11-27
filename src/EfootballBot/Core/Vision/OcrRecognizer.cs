using EfootballBot.Core.Capture;
using System.IO;
using RapidOcrNet;
using SkiaSharp;

namespace EfootballBot.Core.Vision;

/// <summary>OCR 词块，坐标已归一化到 0~1。
/// <paramref name="Confidence"/> 为引擎给出的识别置信度(0~1)；默认 1.0 = 未知/最高可信，
/// 保证无 score 的后端(VLM)与既有测试构造无需改动。仅用于多候选择优，不做全局硬阈值过滤。</summary>
public sealed record OcrWord(string Text, double X, double Y, double W, double H, double Confidence = 1.0)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    private string? _norm;

    /// <summary>
    /// 规范化文本缓存（<see cref="OcrText.Norm"/> 的惰性结果）。
    /// Norm 要走 Unicode FormKC 归一 + 标点剔除 + 替换表，单次约 10µs；一帧 100+ 词、
    /// 每轮判定要跑十几个探针，同一个词会被反复规范化几十次（每帧数千次纯重复计算）。
    /// 对固定 Text 结果恒定，缓存不改变任何行为，仅省掉重复分配与字符串处理。
    /// </summary>
    public string NormText => _norm ??= OcrText.Norm(Text);
}

/// <summary>一条排版行：行内词按 X 排序后的规范化拼接文本 + 整行包围盒。</summary>
public sealed record OcrLine(string Text, double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}

/// <summary>归一化遮罩矩形（供静态帧哈希剔除外置性能监控 OSD 等局部高频跳变区）。</summary>
public readonly record struct OcrMaskRect(double X0, double Y0, double X1, double Y1);

public sealed record OcrResult(IReadOnlyList<OcrWord> Words, int Width, int Height)
{
    public static readonly OcrResult Empty = new(Array.Empty<OcrWord>(), 0, 0);

    /// <summary>识别并剔除的顶部外置性能监控（FPS/CPU/GPU 等）包围盒列表；无监控浮窗时恒为空。</summary>
    public IReadOnlyList<OcrMaskRect> OsdMaskRects { get; init; } = Array.Empty<OcrMaskRect>();

    private IReadOnlyList<OcrLine>? _lines;
    private List<List<OcrWord>>? _groupedLines;

    /// <summary>
    /// 按排版行聚类缓存（每行按 X 升序排好），供 Lines / MergedWords / JoinedText 共用，
    /// 避免同一帧内重复跑 3 遍 O(N²) 行聚类。
    /// </summary>
    private List<List<OcrWord>> GetGroupedLines()
    {
        if (_groupedLines is { } cached) return cached;
        var groups = new List<(List<OcrWord> Words, double SumY, double SumH)>();
        foreach (var w in Words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            int hit = -1;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                int n = g.Words.Count;
                double avgY = g.SumY / n;
                double avgH = g.SumH / n;
                if (Math.Abs(avgY - w.CenterY) < Math.Max(w.H, avgH) * 0.7)
                {
                    hit = i;
                    break;
                }
            }
            if (hit < 0)
            {
                groups.Add((new List<OcrWord> { w }, w.CenterY, w.H));
            }
            else
            {
                var g = groups[hit];
                g.Words.Add(w);
                groups[hit] = (g.Words, g.SumY + w.CenterY, g.SumH + w.H);
            }
        }
        var lines = new List<List<OcrWord>>(groups.Count);
        foreach (var g in groups)
        {
            g.Words.Sort((a, b) => a.X.CompareTo(b.X));
            lines.Add(g.Words);
        }
        _groupedLines = lines;
        return lines;
    }

    /// <summary>
    /// 判定词块是否属于 Windows 窗口顶部标题栏噪声（左上角图标、eFootball™ 标题、右上角控制按钮）。
    /// 窗口化 WGC 捕获整窗时（如 1282×759）顶部 ~31px 会被 OCR 读出，导致比赛中 OCR 降频失效、
    /// 加载帧误判 TitleScreen。只过滤 OCR 词块而不裁切图像，确保 YOLO 与像素探针坐标 100% 不变。
    /// </summary>
    internal static bool IsWindowTitlebarWord(OcrWord w)
    {
        if (!(w.Y < 0.035 && w.CenterY < 0.042 && w.H < 0.055)) return false;
        string norm = w.NormText;
        if (w.X < 0.30 &&
            (norm.Contains("footbal", StringComparison.OrdinalIgnoreCase) ||
             norm.Contains("ootbal", StringComparison.OrdinalIgnoreCase) ||
             norm.Contains("otball", StringComparison.OrdinalIgnoreCase) ||
             norm.Contains("efoot", StringComparison.OrdinalIgnoreCase) ||
             norm.Contains("实况足球", StringComparison.OrdinalIgnoreCase) ||
             (w.Text.Contains('™') && w.X < 0.25)))
            return true;

        if (w.X < 0.055 && w.W < 0.055 && norm.Length <= 2 && norm.All(ch => ch < 128))
            return true;

        if (w.X > 0.86 && w.W < 0.08)
        {
            if (norm.Length == 0) return true;
            if (norm is "x" or "X" or "一" or "口") return true;
            string trimmed = w.Text.Trim();
            if (trimmed is "-" or "—" or "_" or "□" or "×") return true;
        }
        return false;
    }

    /// <summary>剔除顶部 Windows 标题栏噪声词及可选的外置性能监控 OSD 词；无命中时零分配直接返回原实例。</summary>
    internal static OcrResult FilterWindowTitlebar(OcrResult result)
    {
        if (result.Words.Count == 0) return result;
        var (osdIndices, osdMasks) = DetectTopBandPerfOsd(result.Words);
        bool anyTitlebar = false;
        for (int i = 0; i < result.Words.Count; i++)
        {
            if (IsWindowTitlebarWord(result.Words[i]))
            {
                anyTitlebar = true;
                break;
            }
        }
        if (!anyTitlebar && osdIndices is null) return result;
        var filtered = new List<OcrWord>(result.Words.Count);
        for (int i = 0; i < result.Words.Count; i++)
        {
            var w = result.Words[i];
            if (IsWindowTitlebarWord(w)) continue;
            if (osdIndices is not null && osdIndices.Contains(i)) continue;
            filtered.Add(w);
        }
        return new OcrResult(filtered, result.Width, result.Height)
        {
            OsdMaskRects = osdMasks,
        };
    }

    /// <summary>
    /// 识别顶部外置性能监控（NVIDIA App / MSI Afterburner / Steam 等：FPS / CPU / GPU / 温度）区域：
    /// 仅当顶部带（Y &lt; 0.12）同一行真检测到性能监控组合特征时才剔除监控词并产出遮罩矩形；
    /// 对未开启任何外置监控的用户零影响、零假阳性（不误伤「比赛 / 对阵AI / 积分数字」）。
    /// </summary>
    private static (HashSet<int>? Indices, IReadOnlyList<OcrMaskRect> Masks) DetectTopBandPerfOsd(IReadOnlyList<OcrWord> words)
    {
        var topCandidates = new List<int>();
        bool anyPerfToken = false;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (!(w.Y < 0.12 && w.CenterY < 0.14 && w.H < 0.06)) continue;
            if (IsWindowTitlebarWord(w)) continue;
            topCandidates.Add(i);
            if (!anyPerfToken && HasPerfOsdKeyword(w.Text))
                anyPerfToken = true;
        }
        if (!anyPerfToken || topCandidates.Count == 0)
            return (null, Array.Empty<OcrMaskRect>());

        topCandidates.Sort((a, b) => words[a].CenterY.CompareTo(words[b].CenterY));
        var lines = new List<(List<int> Idxs, double SumY, double SumH)>();
        foreach (int idx in topCandidates)
        {
            var w = words[idx];
            int hit = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var g = lines[i];
                double avgY = g.SumY / g.Idxs.Count;
                double avgH = g.SumH / g.Idxs.Count;
                if (Math.Abs(avgY - w.CenterY) <= Math.Max(w.H, avgH) * 0.85)
                {
                    hit = i;
                    break;
                }
            }
            if (hit < 0)
                lines.Add((new List<int> { idx }, w.CenterY, w.H));
            else
            {
                var g = lines[hit];
                g.Idxs.Add(idx);
                lines[hit] = (g.Idxs, g.SumY + w.CenterY, g.SumH + w.H);
            }
        }

        HashSet<int>? stripped = null;
        var rawMasks = new List<OcrMaskRect>();
        foreach (var line in lines)
        {
            line.Idxs.Sort((a, b) => words[a].X.CompareTo(words[b].X));
            if (!IsPerfOsdLine(words, line.Idxs)) continue;

            var lineHit = new bool[line.Idxs.Count];
            for (int k = 0; k < line.Idxs.Count; k++)
            {
                string t = words[line.Idxs[k]].Text;
                if (IsPerfOsdToken(t)) lineHit[k] = true;
            }
            // 向右吸收紧邻性能标签后面的数值碎片（如 "CPU"|"75 C"、"GPU"|"71°("、"FPS"|"304"）
            for (int k = 1; k < line.Idxs.Count; k++)
            {
                if (!lineHit[k - 1] || lineHit[k]) continue;
                var prev = words[line.Idxs[k - 1]];
                var cur = words[line.Idxs[k]];
                double gap = cur.X - (prev.X + prev.W);
                if (gap <= 0.08 && IsTelemetryValueFragment(cur.Text, gap, EndsWithTelemetryLabel(prev.Text)))
                    lineHit[k] = true;
            }
            for (int k = 0; k < line.Idxs.Count; k++)
            {
                if (!lineHit[k]) continue;
                int wordIdx = line.Idxs[k];
                stripped ??= new HashSet<int>();
                stripped.Add(wordIdx);
                var w = words[wordIdx];
                rawMasks.Add(new OcrMaskRect(
                    Math.Clamp(w.X - 0.02, 0, 1),
                    Math.Clamp(w.Y - 0.01, 0, 1),
                    Math.Clamp(w.X + w.W + 0.02, 0, 1),
                    Math.Clamp(w.Y + w.H + 0.01, 0, 1)));
            }
        }

        if (stripped is null || rawMasks.Count == 0)
            return (null, Array.Empty<OcrMaskRect>());
        return (stripped, MergeOverlappingMasks(rawMasks));
    }

    private static bool HasPerfOsdKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string u = text.ToUpperInvariant();
        if (u.Contains("FPS", StringComparison.Ordinal) ||
            u.Contains("CPU", StringComparison.Ordinal) ||
            u.Contains("GPU", StringComparison.Ordinal) ||
            u.Contains('℃') ||
            u.Contains("°C", StringComparison.Ordinal))
            return true;
        return ContainsEpsWithDigits(u);
    }

    private static bool ContainsEpsWithDigits(string upper)
    {
        int idx = upper.IndexOf("EPS", StringComparison.Ordinal);
        while (idx >= 0)
        {
            for (int j = idx + 3; j < upper.Length && j < idx + 6; j++)
            {
                if (char.IsDigit(upper[j])) return true;
                if (upper[j] != ' ' && upper[j] != ':') break;
            }
            idx = upper.IndexOf("EPS", idx + 3, StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsPerfOsdToken(string text)
    {
        if (HasPerfOsdKeyword(text)) return true;
        string t = text.Trim().ToUpperInvariant();
        return t.Length <= 8 &&
               (t.Contains('°') && t.Any(char.IsDigit));
    }

    private static bool IsPerfOsdLine(IReadOnlyList<OcrWord> words, List<int> lineIdxs)
    {
        bool hasFps = false, hasCpu = false, hasGpu = false, hasTemp = false, hasDigits = false, hasLat = false;
        foreach (int i in lineIdxs)
        {
            string raw = words[i].Text;
            string u = raw.ToUpperInvariant();
            if (u.Contains("FPS", StringComparison.Ordinal) || ContainsEpsWithDigits(u)) hasFps = true;
            if (u.Contains("CPU", StringComparison.Ordinal)) hasCpu = true;
            if (u.Contains("GPU", StringComparison.Ordinal)) hasGpu = true;
            if (u.Contains('℃') || u.Contains("°C", StringComparison.Ordinal) ||
                (u.Contains('°') && u.Any(char.IsDigit)))
                hasTemp = true;
            if (u.Contains("MS", StringComparison.Ordinal) && u.Any(char.IsDigit)) hasLat = true;
            if (raw.Any(char.IsDigit)) hasDigits = true;
        }
        int categories = (hasFps ? 1 : 0) + (hasCpu ? 1 : 0) + (hasGpu ? 1 : 0) + (hasTemp ? 1 : 0) + (hasLat ? 1 : 0);
        if (categories >= 2 && hasDigits) return true;
        if (hasFps && hasDigits) return true;
        return (hasCpu || hasGpu) && hasTemp;
    }

    private static bool EndsWithTelemetryLabel(string text)
    {
        string u = text.TrimEnd(' ', ':', '|').ToUpperInvariant();
        return u.EndsWith("FPS", StringComparison.Ordinal) ||
               u.EndsWith("EPS", StringComparison.Ordinal) ||
               u.EndsWith("CPU", StringComparison.Ordinal) ||
               u.EndsWith("GPU", StringComparison.Ordinal) ||
               u.EndsWith("LAT", StringComparison.Ordinal);
    }

    private static bool IsTelemetryValueFragment(string text, double gap, bool prevEndedWithLabel)
    {
        string t = text.Trim();
        if (t.Length == 0 || t.Length > 10) return false;
        bool hasTempOrUnit = false, hasDigit = false;
        foreach (char c in t)
        {
            if (char.IsDigit(c)) { hasDigit = true; continue; }
            if (c is '°' or '℃' or '%' or 'C' or 'c' or 'F' or 'f' or 'M' or 'm' or 's' or 'S' or 'G' or 'g' or 'H' or 'h' or 'z' or 'Z')
            {
                if (c is '°' or '℃' or '%' or 'C' or 'c' or 'M' or 'm' or 's' or 'S') hasTempOrUnit = true;
                continue;
            }
            if (c is ' ' or '.' or ',' or ':' or '/' or '|' or '(' or ')' or '-') continue;
            return false;
        }
        if (!hasDigit && !hasTempOrUnit) return false;
        return hasTempOrUnit || (prevEndedWithLabel && gap <= 0.05);
    }

    private static IReadOnlyList<OcrMaskRect> MergeOverlappingMasks(List<OcrMaskRect> rects)
    {
        if (rects.Count <= 1) return rects;
        rects.Sort((a, b) => a.X0.CompareTo(b.X0));
        var merged = new List<OcrMaskRect>(rects.Count) { rects[0] };
        for (int i = 1; i < rects.Count; i++)
        {
            var r = rects[i];
            bool combined = false;
            for (int j = 0; j < merged.Count; j++)
            {
                var m = merged[j];
                bool yOverlap = Math.Min(m.Y1, r.Y1) - Math.Max(m.Y0, r.Y0) >= -0.02;
                bool xClose = r.X0 <= m.X1 + 0.02 && r.X1 >= m.X0 - 0.02;
                if (yOverlap && xClose)
                {
                    merged[j] = new OcrMaskRect(
                        Math.Min(m.X0, r.X0), Math.Min(m.Y0, r.Y0),
                        Math.Max(m.X1, r.X1), Math.Max(m.Y1, r.Y1));
                    combined = true;
                    break;
                }
            }
            if (!combined) merged.Add(r);
        }
        return merged;
    }

    /// <summary>
    /// 按排版行聚合（每个词属于唯一一行），行文本为行内词 Norm 拼接。
    /// 页面探针在此之上匹配：关键词在每条行至多命中一次，避免「原始词 + 包含它的合并行」
    /// 双重计分（MergedWords 同时含原始词与合并词，会把同行单词算两遍）。
    /// </summary>
    public IReadOnlyList<OcrLine> Lines => _lines ??= BuildLines();

    private IReadOnlyList<OcrLine> BuildLines()
    {
        var lines = GetGroupedLines();
        return lines.Select(ordered =>
        {
            double x0 = ordered.Min(w => w.X), y0 = ordered.Min(w => w.Y);
            double x1 = ordered.Max(w => w.X + w.W), y1 = ordered.Max(w => w.Y + w.H);
            return new OcrLine(string.Concat(ordered.Select(w => w.NormText)),
                x0, y0, x1 - x0, y1 - y0);
        }).ToList();
    }

    /// <summary>在排版行中找关键词子串（每条行至多一个命中）。</summary>
    public IEnumerable<OcrLine> FindLines(string normalizedKeyword)
    {
        foreach (var l in Lines)
            if (l.Text.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                yield return l;
    }

    private IReadOnlyList<OcrWord>? _merged;

    /// <summary>同一行的碎词合并成整行（Windows OCR 常把中文逐字拆开）。</summary>
    public IReadOnlyList<OcrWord> MergedWords => _merged ??= MergeLines();

    private IReadOnlyList<OcrWord> MergeLines()
    {
        var result = new List<OcrWord>(Words);
        foreach (var ordered in GetGroupedLines())
        {
            if (ordered.Count < 2) continue;
            AddMergedWord(result, ordered);

            // 若同一 Y 行内存在明显横向空隙（如左右两栏按钮），额外产出分段合并词，
            // 使碎词拥有紧凑包围盒而不被整行拉宽
            var segments = SplitByHorizontalGap(ordered, gapFactor: 2.5);
            if (segments.Count > 1)
            {
                foreach (var seg in segments)
                    if (seg.Count >= 2) AddMergedWord(result, seg);
            }
        }
        return result;
    }

    internal static List<List<OcrWord>> SplitByHorizontalGap(List<OcrWord> ordered, double gapFactor)
    {
        var segments = new List<List<OcrWord>>();
        if (ordered.Count == 0) return segments;
        var curSeg = new List<OcrWord> { ordered[0] };
        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var cur = ordered[i];
            double gap = cur.X - (prev.X + prev.W);
            double limit = Math.Max(prev.H, cur.H) * gapFactor;
            if (gap > limit)
            {
                segments.Add(curSeg);
                curSeg = new List<OcrWord> { cur };
            }
            else
            {
                curSeg.Add(cur);
            }
        }
        segments.Add(curSeg);
        return segments;
    }

    private static void AddMergedWord(List<OcrWord> target, List<OcrWord> group)
    {
        string text = string.Concat(group.Select(w => w.NormText));
        if (text.Length < 2) return;
        double x0 = group.Min(w => w.X), y0 = group.Min(w => w.Y);
        double x1 = group.Max(w => w.X + w.W), y1 = group.Max(w => w.Y + w.H);
        // 合并行置信度取最低：整行文本里只要有一个碎词可疑，合并结果就应降权（保守）
        double conf = group.Min(w => w.Confidence);
        target.Add(new OcrWord(text, x0, y0, x1 - x0, y1 - y0, conf));
    }

    public IEnumerable<OcrWord> FindAll(string normalizedKeyword)
    {
        foreach (var w in MergedWords)
            if (w.NormText.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                yield return w;
    }

    public bool Contains(string normalizedKeyword) => FindAll(normalizedKeyword).Any();

    private string? _joined;

    /// <summary>所有原始词按阅读顺序拼接并规范化（无分隔），用于匹配被逐字拆开的短词，
    /// 如「回」「放」实际是「回放」、「比」「赛」「回」「放」是「比赛回放」。
    /// 拼接前先按 Y 聚类成行：避免同一行某字 Y 微抖 1px 就被全局 Y 排序打乱词序。</summary>
    public string JoinedText => _joined ??= BuildJoinedText();

    private string BuildJoinedText()
    {
        return string.Concat(GetGroupedLines()
            .OrderBy(l => l.Average(w => w.CenterY))
            .SelectMany(l => l.Select(w => w.NormText)));
    }

    /// <summary>在全文拼接里找子串（不要求关键词落在同一个 OCR 词内）。</summary>
    public bool ContainsJoined(string normalizedKeyword)
        => JoinedText.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);

    public bool ContainsAny(IEnumerable<string> normalizedKeywords)
        => normalizedKeywords.Any(Contains);

    public OcrWord? FindFirst(string normalizedKeyword) => FindAll(normalizedKeyword).FirstOrDefault();

    /// <summary>
    /// 按词找按钮：纯拉丁字母短词（ok/skip/yes/no/next 等）要求整词精确相等，
    /// 避免「Opoku」「NGoloKante」含子串「ok」被误当按钮（真机 2026-10-06 故障）；
    /// 中文等关键词仍走子串包含（OCR 常把图标误识字拼到词尾，如「跳过參」）。
    /// </summary>
    public IEnumerable<OcrWord> FindButtonWords(string normalizedKeyword)
    {
        bool exact = normalizedKeyword.Length > 0 && normalizedKeyword.All(char.IsAsciiLetter);
        foreach (var w in MergedWords)
        {
            string t = w.NormText;
            bool hit = exact
                ? t.Equals(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
                : t.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);
            if (hit) yield return w;
        }
    }

    /// <summary>
    /// 按钮词命中（规则见 <see cref="FindButtonWords"/>）。多个命中时取置信度最高的那个：
    /// 同一关键词在画面出现多处时（如弹窗按钮 + 背景文字），引擎 score 更高的更可能是真按钮。
    /// MaxBy 在 score 相同时稳定返回首个命中，故单候选与无 score（默认 1.0）场景行为完全不变。
    /// </summary>
    public OcrWord? FindButton(string normalizedKeyword)
        => FindButtonWords(normalizedKeyword)
            .OrderByDescending(w => Math.Round(w.Confidence, 5))
            .ThenBy(w => w.W)
            .FirstOrDefault();
}

/// <summary>文字规范化，对抗 OCR 的空格 / 全半角 / 常见误识。</summary>
public static class OcrText
{
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new global::System.Text.StringBuilder(s.Length);
        foreach (char ch in s.Normalize(global::System.Text.NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            sb.Append(NormalizeChar(ch));
        }
        return sb.ToString();
    }

    private static char NormalizeChar(char ch) => ch switch
    {
        '賽' => '赛', '歷' => '历', '階' => '阶', '組' => '组', '別' => '别',
        '聯' => '联', '確' => '确', '認' => '认', '繼' => '继', '續' => '续',
        '勝' => '胜', '負' => '负', '積' => '积', '獎' => '奖', '勵' => '励',
        '開' => '开', '結' => '结', '進' => '进', '選' => '选', '設' => '设',
        '項' => '项', '頂' => '顶', '級' => '级', '員' => '员', '傳' => '传', '對' => '对',
        '戰' => '战', '點' => '点', '擊' => '击', '過' => '过', '畫' => '画',
        '試' => '试', '練' => '练', '訓' => '训', '動' => '动', '報' => '报',
        '領' => '领', '經' => '经', '驗' => '验', '關' => '关', '閉' => '闭',
        '準' => '准', '備' => '备',
        _ => ch,
    };
}

/// <summary>OCR 识别后端。</summary>
public enum OcrEngineKind
{
    /// <summary>RapidOCR PP-OCRv6-small（ONNX Runtime CPU）：本地离线深度学习，单模型通吃简繁英日。</summary>
    RapidV6,
    /// <summary>【已删除引擎，仅为旧配置编号兼容保留】3.3.0.1 起 Windows 内置 OCR 已移除，
    /// 读取到该值一律按 <see cref="SimdPaddle"/> 处理（其碎词/误识质量不达挂机判定线）。</summary>
    Windows = 1,
    /// <summary>SimdPaddleOCR（纯托管推理，Apache-2.0，默认引擎）：与 RapidV6 共用 models/v6 模型文件，
    /// 零原生依赖。加载失败自动回退 RapidV6。</summary>
    SimdPaddle = 2,
    /// <summary>Unlimited-OCR（百度 2026 文档解析 VLM，MIT）：官方仅提供 torch/vLLM/SGLang 部署，
    /// 本程序以 HTTP 客户端接入 OpenAI 兼容服务（见 <see cref="UnlimitedOcrBackend"/>）。
    /// 逐帧秒级~数十秒，只作精度参照/离线校准；服务不可达自动回退 SimdPaddle。</summary>
    UnlimitedOcr = 3,

    /// <summary>Rust sidecar（vision-svc）内的 ONNX Runtime 推理：帧经共享内存送过去，
    /// 与 SimdPaddle 共用同一套 models/v6 权重。不可用时自动回退 SimdPaddle。</summary>
    Sidecar = 4,
}

/// <summary>OCR 后端统一契约：输入一帧 BGRA，输出归一化坐标词块。</summary>
internal interface IOcrBackend : IDisposable
{
    /// <summary>引擎/语言标签，用于日志与环境检测展示。</summary>
    string Label { get; }

    Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default);
}

/// <summary>
/// OCR 识别器门面：按配置选择后端，对上保持 <see cref="OcrResult"/> 契约不变。
/// 默认 PP-OCRv6（<see cref="OcrEngineKind.RapidV6"/>），可回退 Windows 内置 OCR。
/// </summary>
public sealed class OcrRecognizer : IDisposable
{
    private IOcrBackend _backend;

    /// <summary>当前引擎类型。</summary>
    public OcrEngineKind EngineKind { get; private set; }

    /// <summary>引擎/语言标签（日志展示用）。</summary>
    public string LanguageTag => _backend.Label;

    /// <summary>非空 = 选定引擎加载失败已自动回退（内容为原因），日志须播报。</summary>
    public string? FallbackNote { get; private set; }

    /// <summary>旧签名兼容：默认使用当前默认引擎（离线命令行自检 --check-frame/--dump 走此入口）。
    /// preferredTag 曾用于选择 Windows OCR 语言包，该引擎已删除，参数保留仅防调用方破坏。</summary>
    public OcrRecognizer(string? preferredTag = null) : this(OcrEngineKind.SimdPaddle) { }

    /// <param name="downscaleLongSide">OCR 输入降采样上限（帧长边超过该值先等比缩小再识别）。
    /// 0 = 关闭。4K 级画面降采样到 1080p 后 det 像素量约为原来 1/4，UI 文字在 720p 实测帧即可读全；
    /// 词块坐标按归一化输出，降采样不影响 0~1 坐标契约。</param>
    /// <param name="preferInt8">true = small 识别模型优先加载 int8 量化版（缺失自动回退 fp32）。</param>
    /// <param name="tuning">难读文本调优（美术字/碎词化）。null = <see cref="OcrTuning.Off"/>，历史行为不变。</param>
    public OcrRecognizer(OcrEngineKind engine, string? preferredTag = null,
        int downscaleLongSide = DefaultDownscaleLongSide, bool preferInt8 = true,
        EfootballBot.Core.Config.UnlimitedOcrConfig? unlimitedOcr = null, OcrTuning? tuning = null,
        EfootballBot.Core.Config.VisionSidecarConfig? sidecar = null)
    {
        EngineKind = engine;
        _downscaleLongSide = downscaleLongSide;
        _preferInt8 = preferInt8;
        _tuning = tuning ?? OcrTuning.Off;
        switch (engine)
        {
            case OcrEngineKind.UnlimitedOcr:
                try
                {
                    _backend = new UnlimitedOcrBackend(unlimitedOcr ?? new EfootballBot.Core.Config.UnlimitedOcrConfig());
                }
                catch (Exception ex)
                {
                    // 外部服务随时可能没起：绝不因试验引擎断了挂机
                    FallbackNote = $"Unlimited-OCR 加载失败已回退 SimdPaddle：{ex.Message}";
                    try
                    {
                        _backend = new SimdPaddleOcrBackend(_tuning);
                    }
                    catch (Exception ex2)
                    {
                        FallbackNote += $"；SimdPaddle 亦失败，回退 RapidV6：{ex2.Message}";
                        _backend = new RapidV6OcrBackend(preferInt8);
                    }
                }
                break;
            case OcrEngineKind.Sidecar:
                try
                {
                    _backend = new SidecarOcrBackend(sidecar ?? new EfootballBot.Core.Config.VisionSidecarConfig());
                }
                catch (Exception ex)
                {
                    // sidecar 没构建/没启动：绝不因为试验引擎断了挂机
                    FallbackNote = $"vision-svc 后端加载失败已回退 SimdPaddle：{ex.Message}";
                    try
                    {
                        _backend = new SimdPaddleOcrBackend(_tuning);
                    }
                    catch (Exception ex2)
                    {
                        FallbackNote += $"；SimdPaddle 亦失败，回退 RapidV6：{ex2.Message}";
                        _backend = new RapidV6OcrBackend(preferInt8);
                    }
                }
                break;
            case OcrEngineKind.SimdPaddle:
            case OcrEngineKind.Windows: // 旧配置迁移：Windows 引擎已删除，一律落 SimdPaddle
                try
                {
                    _backend = new SimdPaddleOcrBackend(_tuning);
                }
                catch (Exception ex)
                {
                    // 纯托管引擎也可能因模型图形态不兼容失败：绝不因试验引擎断了挂机
                    FallbackNote = $"SimdPaddle 加载失败已回退 RapidV6：{ex.Message}";
                    _backend = new RapidV6OcrBackend(preferInt8);
                }
                break;
            default:
                _backend = new RapidV6OcrBackend(preferInt8);
                break;
        }
    }

    /// <summary>降采样上限默认值（1080p 长边）。</summary>
    public const int DefaultDownscaleLongSide = 1920;

    private readonly int _downscaleLongSide;
    private readonly bool _preferInt8;
    private readonly OcrTuning _tuning;

    /// <summary>
    /// 串行闸（P0-10）：引擎实例现在跨会话复用同一个 OCR 实例（EngineServices），
    /// 而"停止挂机 → 立刻重新开始"存在极短的窗口——旧引擎可能还在一次推理里，新引擎已经在跑。
    /// SimdPaddle/RapidOCR 的推理会话不是为并发调用设计的，这里用一把无争用成本的闸把它们排队，
    /// 从根上消除这类只在快速重启时才出现的偶发异常。
    /// </summary>
    private readonly global::System.Threading.SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private static readonly OcrTuning OnDemandFragmentTuning = new()
    {
        LineReread = true,
        LineRereadScale = 2,
        LineRereadMaxLines = 4,
        LineRereadRequireFragments = true,
    };

    /// <summary>最近一帧的行级重读统计（诊断用；未开启行级重读时恒为 default）。</summary>
    public LineReread.Stats LastRereadStats { get; private set; }

    /// <summary>
    /// 零空闲成本按需碎词重读：仅在画面连续未知/卡住时由上层调用，只对当前帧里的逐字碎词行
    /// 做最多 4 行的 ×2 裁切放大重读，正常帧零开销。
    /// </summary>
    public async Task<(OcrResult Result, LineReread.Stats Stats)> RereadFragmentsAsync(
        FrameData frame, OcrResult baseResult, CancellationToken ct = default)
    {
        if (baseResult.Words.Count < 2 || frame.Width <= 0 || frame.Height <= 0)
            return (baseResult, default);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var scaled = ImageScaling.DownscaleBilinear(frame, _downscaleLongSide);
            var (merged, stats) = await LineReread.ApplyAsync(
                    baseResult, scaled, _backend.RecognizeAsync, OnDemandFragmentTuning, ct)
                .ConfigureAwait(false);
            LastRereadStats = stats;
            if (merged.OsdMaskRects.Count == 0 && baseResult.OsdMaskRects.Count > 0)
                merged = merged with { OsdMaskRects = baseResult.OsdMaskRects };
            return (merged, stats);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var scaled = ImageScaling.DownscaleBilinear(frame, _downscaleLongSide);
            OcrResult result;
            try
            {
                result = await _backend.RecognizeAsync(scaled, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (_backend is SidecarOcrBackend)
            {
                // vision-svc 运行期熔断/断连：立即回退进程内引擎并重跑本帧，绝不把挂机卡死
                try { _backend.Dispose(); } catch { }
                try
                {
                    _backend = new SimdPaddleOcrBackend(_tuning);
                    EngineKind = OcrEngineKind.SimdPaddle;
                    FallbackNote = $"vision-svc 运行时异常已回退 SimdPaddle：{ex.Message}";
                }
                catch (Exception ex2)
                {
                    _backend = new RapidV6OcrBackend(_preferInt8);
                    EngineKind = OcrEngineKind.RapidV6;
                    FallbackNote = $"vision-svc 运行时异常且 SimdPaddle 失败，已回退 RapidV6：{ex.Message}；{ex2.Message}";
                }
                result = await _backend.RecognizeAsync(scaled, ct).ConfigureAwait(false);
            }
            result = OcrResult.FilterWindowTitlebar(result);
            if (!_tuning.LineReread || result.Words.Count == 0)
            {
                LastRereadStats = default;
                return result;
            }
            // 降采样后再重读：坐标口径始终是 scaled 帧，归一化结果与整帧一致
            var (merged, stats) = await LineReread.ApplyAsync(result, scaled, _backend.RecognizeAsync, _tuning, ct)
                .ConfigureAwait(false);
            LastRereadStats = stats;
            return merged.OsdMaskRects.Count == 0 && result.OsdMaskRects.Count > 0
                ? merged with { OsdMaskRects = result.OsdMaskRects }
                : merged;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _backend.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// RapidOCR PP-OCRv6-small 多语言后端（ONNX Runtime 本地推理，简繁英日一个模型）。
/// 模型随程序输出到 models/v6，文本行方向分类器复用包自带 v5 cls；游戏文字均正立，关闭方向分类省一次推理。
/// </summary>
internal sealed class RapidV6OcrBackend : IOcrBackend
{
    public const string LabelText = "PP-OCRv6-small";

    private readonly RapidOcrNet.RapidOcr _ocr;
    private readonly RapidOcrOptions _options;

    public string Label { get; }

    /// <param name="preferInt8">true = 优先加载 models/v6/int8 下的 int8 量化模型（CPU 推理约 2-4x）；
    /// int8 文件缺失或加载失败（损坏/ORT 不兼容）时自动回退 fp32，绝不因量化模型问题而启动失败。</param>
    public RapidV6OcrBackend(bool preferInt8 = true)
    {
        // 单文件发布时模型在临时解压目录；普通发布在 exe 同级 models，FindBundledFile 两种都兼容
        var fp32Det = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_det_small.onnx")
            ?? throw ModelMissing("models/v6/PP-OCRv6_det_small.onnx");
        var clsPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
            ?? throw ModelMissing("models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        // int8 降级阶梯：识别模型动态量化精度无损（实测文本逐字一致）优先启用；
        // 检测模型静态量化对 DBNet 概率图敏感（MinMax 校准实测大面积漏检），
        // det int8 只有在通过全量 RealFrame 回归后才随包发布。逐级尝试，全败落 fp32。
        var fp32Rec = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_rec_small.onnx")
            ?? throw ModelMissing("models/v6/PP-OCRv6_rec_small.onnx");
        var keysPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "ppocrv6_small_dict.txt")
            ?? throw ModelMissing("models/v6/ppocrv6_small_dict.txt");
        var recInt8 = preferInt8 ? EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "int8", "PP-OCRv6_rec_small_int8.onnx") : null;
        var detInt8 = preferInt8 ? EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "int8", "PP-OCRv6_det_small_int8.onnx") : null;
        static string Grade(string? d, string? r) =>
            d is null && r is null ? "" : $"（{(d is not null ? "det" : "")}{(d is not null && r is not null ? "+" : "")}{(r is not null ? "rec" : "")} int8）";

        string _lastCreateError = "";
        (string det, string rec, string grade)[] ladder =
        [
            (detInt8 ?? fp32Det, recInt8 ?? fp32Rec, Grade(detInt8, recInt8)),
            (fp32Det, recInt8 ?? fp32Rec, Grade(null, recInt8)),
            (fp32Det, fp32Rec, ""),
        ];

        RapidOcrNet.RapidOcr? loaded = null;
        string int8Grade = "";
        foreach (var (det, rec, g) in ladder)
        {
            try
            {
                loaded = CreateOcr(det, clsPath, rec, keysPath);
                int8Grade = g;
                break;
            }
            catch (Exception ex)
            {
                // 量化模型损坏/不兼容：降级到下一级（放弃 int8 只损失速度，不该损失可用性）
                _lastCreateError = ex.Message;
                global::System.Diagnostics.Debug.WriteLine($"[OCR] 模型加载失败（det={Path.GetFileName(det)} rec={Path.GetFileName(rec)}），降级重试：{ex.Message}");
            }
        }

        if (loaded is null)
        {
            // 所有 ladder 级别都失败：最后一级与 ladder 第3级参数完全相同，
            // 再试一次必然抛同样的异常且信息模糊（审查修复①）。直接抛清晰错误。
            throw new InvalidOperationException(
                "RapidV6 OCR 全部模型组合均加载失败（int8/fp32 det×rec 都已尝试）。" +
                "请检查 models/v6 下的 det/rec/cls 模型文件是否完整；" +
                $"最后一次失败：{_lastCreateError}");
        }
        _ocr = loaded;
        Label = LabelText + int8Grade;

        // PPOCRv6 预处理（短边自适应 736、不加白边）；游戏界面文字不会倒置，跳过 180° 分类
        _options = RapidOcrOptions.PPOCRv6 with { DoAngle = false };
    }

    private static RapidOcrNet.RapidOcr CreateOcr(string detPath, string clsPath, string recPath, string keysPath)
    {
        // RapidOcrNet 4.2.0 自管 InferenceSession（CPU，不接受外部 SessionOptions）；
        // GPU 加速仅覆盖 YOLO 检测（OnnxSessionFactory），OCR 走包内默认 CPU 会话
        var ocr = new RapidOcrNet.RapidOcr();
        try
        {
            ocr.InitModels(detPath, clsPath, recPath, keysPath);
            return ocr;
        }
        catch
        {
            // InitModels 失败时原生 ONNX session 已分配却无人释放（审查修复②）
            try { ocr.Dispose(); } catch { }
            throw;
        }
    }

    private static InvalidOperationException ModelMissing(string rel) => new(
        "PP-OCRv6 模型文件缺失：" + rel + "。请重新下载完整版程序（models 目录需与 exe 同级）。");

    public Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var words = new List<OcrWord>();
            unsafe
            {
                fixed (byte* pixels = frame.Bgra)
                {
                    // InstallPixels 零拷贝借用托管数组（fixed 固定），Detect 结束前不释放
                    using var bmp = new SKBitmap();
                    if (!bmp.InstallPixels(info, (IntPtr)pixels, info.RowBytes))
                        throw new InvalidOperationException("SKBitmap 像素装载失败。");

                    var result = _ocr.Detect(bmp, _options);
                    foreach (var block in result.TextBlocks)
                    {
                        string text = block.Text;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        var pts = block.BoxPoints;
                        float minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
                        for (int i = 1; i < pts.Length; i++)
                        {
                            minX = Math.Min(minX, pts[i].X);
                            maxX = Math.Max(maxX, pts[i].X);
                            minY = Math.Min(minY, pts[i].Y);
                            maxY = Math.Max(maxY, pts[i].Y);
                        }
                        words.Add(new OcrWord(
                            text,
                            Math.Clamp(minX / frame.Width, 0, 1),
                            Math.Clamp(minY / frame.Height, 0, 1),
                            Math.Clamp((maxX - minX) / frame.Width, 0, 1),
                            Math.Clamp((maxY - minY) / frame.Height, 0, 1),
                            block.BoxScore));
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            return new OcrResult(words, frame.Width, frame.Height);
        }, ct);

    public void Dispose() => _ocr.Dispose();
}

/// <summary>
/// SimdPaddleOCR 后端（sdcb 纯托管 PP-OCR 推理，Apache-2.0）：与 RapidV6 共用 models/v6 模型文件，
/// 零原生依赖的备选引擎。固定 CPU（不与游戏抢 GPU）；游戏界面文字均正立，关闭方向分类。
/// </summary>
internal sealed class SimdPaddleOcrBackend : IOcrBackend
{
    private readonly Sdcb.SimdPaddleOCR.PaddleOcrAll _ocr;

    public string Label { get; }

    public SimdPaddleOcrBackend(OcrTuning? tuning = null)
    {
        string det = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_det_small.onnx")
            ?? throw ModelFileMissing("models/v6/PP-OCRv6_det_small.onnx");
        string rec = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_rec_small.onnx")
            ?? throw ModelFileMissing("models/v6/PP-OCRv6_rec_small.onnx");
        string keys = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "ppocrv6_small_dict.txt")
            ?? throw ModelFileMissing("models/v6/ppocrv6_small_dict.txt");
        string cls = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
            ?? throw ModelFileMissing("models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        var t = tuning ?? OcrTuning.Off;
        // 选项属性是 init-only：先取一个默认实例读出 Sdcb 的默认值，null 时原样写回，
        // 保证未调优（生产默认）时与历史行为逐字段一致
        var dflt = new Sdcb.SimdPaddleOCR.PaddleOcrDetectorOptions();
        var detOpt = new Sdcb.SimdPaddleOCR.PaddleOcrDetectorOptions
        {
            Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu,
            BitmapThreshold = t.DetBitmapThreshold ?? dflt.BitmapThreshold,
            BoxThreshold = t.DetBoxThreshold ?? dflt.BoxThreshold,
            UnclipRatio = t.DetUnclipRatio ?? dflt.UnclipRatio,
            LimitSideLength = t.DetLimitSideLength ?? dflt.LimitSideLength,
            UseDilation = t.DetUseDilation ?? dflt.UseDilation,
        };

        _ocr = Sdcb.SimdPaddleOCR.PaddleOcrAll.Load(det, cls, rec, keys, new Sdcb.SimdPaddleOCR.PaddleOcrOptions
        {
            UseDirectionClassification = false,
            Detector = detOpt,
            Classifier = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
            Recognizer = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
        });
        Label = "PP-OCRv6-small（SimdPaddle）";
    }

    private static InvalidOperationException ModelFileMissing(string rel) => new(
        "SimdPaddleOCR 模型文件缺失：" + rel + "。请重新下载完整版程序（models 目录需与 exe 同级）。");

    public Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            // BGRA 原生支持，零转换直喂
            var result = _ocr.Run(frame.Bgra, frame.Width, frame.Height, frame.Width * 4,
                Sdcb.SimdPaddleOCR.ImagePixelFormat.Bgra32);
            var words = new List<OcrWord>(result.Lines.Length);
            foreach (var line in result.Lines)
            {
                string text = line.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var b = line.Box;
                float minX = Math.Min(Math.Min(b.X1, b.X2), Math.Min(b.X3, b.X4));
                float maxX = Math.Max(Math.Max(b.X1, b.X2), Math.Max(b.X3, b.X4));
                float minY = Math.Min(Math.Min(b.Y1, b.Y2), Math.Min(b.Y3, b.Y4));
                float maxY = Math.Max(Math.Max(b.Y1, b.Y2), Math.Max(b.Y3, b.Y4));
                words.Add(new OcrWord(
                    text,
                    Math.Clamp(minX / frame.Width, 0, 1),
                    Math.Clamp(minY / frame.Height, 0, 1),
                    Math.Clamp((maxX - minX) / frame.Width, 0, 1),
                    Math.Clamp((maxY - minY) / frame.Height, 0, 1),
                    line.RecognitionScore));
            }
            return new OcrResult(words, frame.Width, frame.Height);
        }, ct);

    public void Dispose() => _ocr.Dispose();
}
