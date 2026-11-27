using System.Globalization;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Sampling;

/// <summary>单条清洗后的 YOLO 标注。</summary>
public sealed record CleanYoloBox(
    int ClassId,
    string ClassName,
    float Score,
    double Cx,
    double Cy,
    double W,
    double H)
{
    public string ToYoloLine() => string.Create(CultureInfo.InvariantCulture,
        $"{ClassId} {Cx:F6} {Cy:F6} {W:F6} {H:F6}");
}

/// <summary>被质量/反标门控拦截的伪标签记录。</summary>
public sealed record RejectedBoxRecord(
    string ClassName,
    float Score,
    double Cx,
    double Cy,
    double W,
    double H,
    string Reason);

/// <summary>单帧自动标注校验结果。</summary>
public sealed record LabelAuditResult(
    IReadOnlyList<CleanYoloBox> CleanBoxes,
    IReadOnlyList<YoloDetection> ReviewBoxes,
    IReadOnlyList<RejectedBoxRecord> RejectedBoxes,
    bool DropEntireFrame,
    string? DropReason,
    bool NeedsReview,
    bool IsOcrHardCase,
    IReadOnlyList<string> OcrHardReasons);

/// <summary>
/// 自动标注质量门控与反标伪标签清洗：
/// 1) 严格对齐 models/yolo/classes.txt 类别表；
/// 2) 拦截并剔除「过场动画右下角跳过键被误标为 btn_back」等反标干扰样本；
/// 3) 低置信度（0.25~0.65）框隔离到 review 目录，绝不混入自动训练集；
/// 4) 识别低置信度/难例 OCR 帧，辅助 OCR 微调采样。
/// </summary>
public static class AutoLabelGuard
{
    public const float AcceptThreshold = 0.65f;
    public const float ReviewThreshold = 0.25f;

    /// <summary>与 models/yolo/classes.txt 完全一致的类别顺序。</summary>
    public static readonly string[] YoloClasses =
    {
        "player_card",
        "btn_back",
        "btn_confirm",
        "coin_bar",
        "main_menu",
        "item_row",
    };

    private static readonly Dictionary<string, int> ClassMap = BuildClassMap();

    private static Dictionary<string, int> BuildClassMap()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < YoloClasses.Length; i++)
            map[YoloClasses[i]] = i;
        return map;
    }

    private static readonly string[] SkipTokens =
    {
        "跳过", "skip", "快进", "按住跳过", "长按跳过", "長押し", "スキップ",
    };

    private static readonly string[] MenuGuardTokens =
    {
        "返回", "取消", "上一步", "后退", "back", "cancel", "关闭", "close",
    };

    /// <summary>
    /// 审核一帧的 YOLO 检测框与 OCR 结果，剔除反标伪标签与低置信度干扰。
    /// </summary>
    public static LabelAuditResult Audit(
        OcrResult ocr,
        IReadOnlyList<YoloDetection> detections,
        MatchScreen screen)
    {
        var clean = new List<CleanYoloBox>();
        var review = new List<YoloDetection>();
        var rejected = new List<RejectedBoxRecord>();
        bool hasSkipWordBottomRight = HasSkipWordInBottomRight(ocr);
        bool hasMenuBackWord = HasMenuBackWord(ocr);

        foreach (var d in detections)
        {
            if (d.Score < ReviewThreshold) continue;
            if (!ClassMap.TryGetValue(d.ClassName, out int classId)) continue;

            double w = Math.Clamp(d.W, 0.0, 1.0);
            double h = Math.Clamp(d.H, 0.0, 1.0);
            double cx = Math.Clamp(d.CenterX, 0.0, 1.0);
            double cy = Math.Clamp(d.CenterY, 0.0, 1.0);
            if (w <= 0.002 || h <= 0.002) continue;

            // 反标拦截：过场动画 / 赛中 / 加载页右下角「跳过」按钮被误检为 btn_back
            if (IsCutsceneSkipMislabel(d.ClassName, cx, cy, screen, hasSkipWordBottomRight, hasMenuBackWord))
            {
                rejected.Add(new RejectedBoxRecord(
                    d.ClassName, d.Score, cx, cy, w, h, "mislabel_skip_as_btn_back"));
                continue;
            }

            if (d.Score >= AcceptThreshold)
                clean.Add(new CleanYoloBox(classId, d.ClassName, d.Score, cx, cy, w, h));
            else
                review.Add(d);
        }

        // 若拦截到了右下角「跳过」反标，且当前帧没有其他可靠菜单 UI（纯过场动画），整帧丢弃
        bool hasSkipMislabel = rejected.Any(r => r.Reason == "mislabel_skip_as_btn_back");
        bool dropFrame = hasSkipMislabel && clean.Count == 0 && !hasMenuBackWord &&
                         screen is MatchScreen.InMatch or MatchScreen.Unknown;

        var (isOcrHard, ocrReasons) = InspectOcrHardCase(ocr);
        bool needsReview = !dropFrame && (review.Count > 0 || hasSkipMislabel);

        return new LabelAuditResult(
            clean,
            review,
            rejected,
            dropFrame,
            dropFrame ? "mislabel_skip_as_btn_back" : null,
            needsReview,
            isOcrHard,
            ocrReasons);
    }

    private static bool IsCutsceneSkipMislabel(
        string className,
        double cx,
        double cy,
        MatchScreen screen,
        bool hasSkipWordBottomRight,
        bool hasMenuBackWord)
    {
        if (!string.Equals(className, "btn_back", StringComparison.Ordinal))
            return false;

        // 过场动画「跳过」与页面确认按钮固定位于右下角，而真「返回(btn_back)」只会在左下角或左上角；
        // 即使同页左下角存在「返回」，右下角的 btn_back 检测框也必为误检反标，直接拦截。
        if (cx >= 0.70 && cy >= 0.78)
            return true;

        _ = screen;
        _ = hasSkipWordBottomRight;
        _ = hasMenuBackWord;
        return false;
    }

    private static bool HasSkipWordInBottomRight(OcrResult ocr)
    {
        foreach (var w in ocr.Words)
        {
            if (w.CenterX < 0.65 || w.CenterY < 0.72) continue;
            var norm = OcrText.Norm(w.Text);
            foreach (var tok in SkipTokens)
            {
                if (norm.Contains(tok, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool HasMenuBackWord(OcrResult ocr)
    {
        foreach (var w in ocr.Words)
        {
            var norm = OcrText.Norm(w.Text);
            foreach (var tok in MenuGuardTokens)
            {
                if (norm.Contains(tok, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 检测是否包含 OCR 难例特征（低置信度、比分冒号/斜杠容量、过长合行或高频关键数字）。
    /// </summary>
    public static (bool IsHard, IReadOnlyList<string> Reasons) InspectOcrHardCase(OcrResult ocr)
    {
        if (ocr.Words.Count == 0) return (false, Array.Empty<string>());

        var reasons = new List<string>();
        int lowConfCount = 0;
        bool hasRatioOrScore = false;
        bool hasLongLine = false;

        foreach (var w in ocr.Words)
        {
            if (w.Confidence > 0 && w.Confidence < 0.78 && w.Text.Trim().Length >= 2)
                lowConfCount++;

            if (!hasRatioOrScore && (w.Text.Contains('/') || w.Text.Contains(':')))
            {
                if (w.Text.Any(char.IsDigit))
                    hasRatioOrScore = true;
            }

            if (!hasLongLine && w.Text.Length >= 24)
                hasLongLine = true;
        }

        if (lowConfCount > 0) reasons.Add($"low_conf:{lowConfCount}");
        if (hasRatioOrScore && lowConfCount > 0) reasons.Add("hard_digits_low_conf");
        if (hasLongLine && lowConfCount > 0) reasons.Add("long_line_low_conf");

        return (reasons.Count > 0, reasons);
    }
}
