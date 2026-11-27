using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Sampling;

/// <summary>OCR 驱动采样判定结果。</summary>
public sealed record OcrSampleDecision(
    bool ShouldSample,
    string PageId,
    string? PageTagOverride,
    string Reason,
    HashSet<string> CurrentWords);

/// <summary>
/// 基于 OCR 识别结果驱动手动/半自动采样决策：
/// 1) 过滤无文字且无 UI 框的过场/草皮帧，避免把非黑屏过场误存为未知页；
/// 2) 优先用 OCR 精细标签（主菜单/弹窗/周活动/锦标赛/比赛页/联赛主页）归类；
/// 3) 当 OCR 语义发生变化、页面切换、命中 OCR 难例或重点训练页时自动触发采样。
/// </summary>
public static class OcrSampleTrigger
{
    public static OcrSampleDecision Evaluate(
        OcrResult ocr,
        IReadOnlyList<YoloDetection> dets,
        MatchScreen screen,
        bool isCn,
        string? lastPageId,
        IReadOnlySet<string>? lastOcrWords,
        bool hasPendingAction,
        FocusShiftResult? focusShift = null)
    {
        var currentWords = FrameQualityFilter.ExtractWordSet(ocr);
        bool hasUiBox = dets.Any(d => d.Score >= AutoLabelGuard.ReviewThreshold);

        if (currentWords.Count == 0 && !hasUiBox)
        {
            return new OcrSampleDecision(false, "empty_scene", null, "no_ocr_or_ui", currentWords);
        }

        var training = MatchBrain.CollectTrainingFrame(ocr, dets);
        string? tagOverride = training?.Tag;
        string pageId = tagOverride is not null
            ? tagOverride
            : (screen == MatchScreen.Unknown
                ? UnknownPagePromptBuilder.BuildUnknownId(ocr, isCn)
                : screen.ToString());

        if (lastOcrWords is null)
            return new OcrSampleDecision(true, pageId, tagOverride, "initial_ocr", currentWords);

        if (!string.Equals(pageId, lastPageId, StringComparison.Ordinal))
            return new OcrSampleDecision(true, pageId, tagOverride, "page_changed", currentWords);

        double jaccard = FrameQualityFilter.OcrJaccard(currentWords, lastOcrWords);
        if (jaccard < 0.85)
            return new OcrSampleDecision(true, pageId, tagOverride, "ocr_changed", currentWords);

        if (focusShift is { Moved: true })
            return new OcrSampleDecision(true, pageId, tagOverride, "cursor_moved", currentWords);

        if (hasPendingAction)
            return new OcrSampleDecision(true, pageId, tagOverride, "user_action", currentWords);

        return new OcrSampleDecision(false, pageId, tagOverride, "ocr_unchanged", currentWords);
    }
}
