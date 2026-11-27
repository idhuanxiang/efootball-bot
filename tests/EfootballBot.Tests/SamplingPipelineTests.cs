using System.IO;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Modules;
using EfootballBot.Core.Bot.Sampling;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

public sealed class SamplingPipelineTests
{
    private static FrameData MakeFrame(int w, int h, byte r, byte g, byte b, bool gradient = false)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (gradient)
                {
                    bgra[i] = (byte)((x * 210 / Math.Max(1, w - 1) + 20) & 0xFF);
                    bgra[i + 1] = (byte)((y * 180 / Math.Max(1, h - 1) + 30) & 0xFF);
                    bgra[i + 2] = (byte)(((x + y) * 90 / Math.Max(1, w + h - 2) + 40) & 0xFF);
                }
                else
                {
                    bgra[i] = b;
                    bgra[i + 1] = g;
                    bgra[i + 2] = r;
                }
                bgra[i + 3] = 255;
            }
        }
        return new FrameData(bgra, w, h, 0);
    }

    private static OcrResult MakeOcr(params (string Text, double Cx, double Cy, float Conf)[] items)
    {
        var words = items.Select(x => new OcrWord(
            x.Text,
            Math.Max(0, x.Cx - 0.05),
            Math.Max(0, x.Cy - 0.02),
            0.10,
            0.04,
            x.Conf)).ToArray();
        return new OcrResult(words, 1280, 720);
    }

    [Fact]
    public void FrameQualityFilter_RejectsBlackAndFlat_AcceptsRichMultiResolution()
    {
        var black = MakeFrame(1280, 720, 3, 4, 5);
        var (okBlack, rBlack) = FrameQualityFilter.CheckUsable(black);
        Assert.False(okBlack);
        Assert.Equal("black_transition", rBlack);

        var flat = MakeFrame(1920, 1080, 100, 100, 100);
        var (okFlat, rFlat) = FrameQualityFilter.CheckUsable(flat);
        Assert.False(okFlat);
        Assert.Equal("flat_transition", rFlat);

        var rich720 = MakeFrame(1280, 720, 0, 0, 0, gradient: true);
        var rich1080 = MakeFrame(1920, 1080, 0, 0, 0, gradient: true);
        Assert.True(FrameQualityFilter.CheckUsable(rich720).Ok);
        Assert.True(FrameQualityFilter.CheckUsable(rich1080).Ok);

        // 分辨率自适应：同构渐变画面在 720p 与 1080p 下的 dHash 应极其接近
        ulong h720 = FrameQualityFilter.ComputeDHash(rich720);
        ulong h1080 = FrameQualityFilter.ComputeDHash(rich1080);
        Assert.True(FrameQualityFilter.HammingDistance(h720, h1080) <= 4);
    }

    [Fact]
    public void AutoLabelGuard_FiltersCutsceneSkipMislabelAsBtnBack()
    {
        var ocrSkip = MakeOcr(("跳过", 0.90, 0.92, 0.95f));
        var dets = new[]
        {
            new YoloDetection("btn_back", 1, 0.88f, 0.84, 0.86, 0.10, 0.08),
        };

        var audit = AutoLabelGuard.Audit(ocrSkip, dets, MatchScreen.InMatch);
        Assert.Empty(audit.CleanBoxes);
        Assert.Single(audit.RejectedBoxes);
        Assert.Equal("mislabel_skip_as_btn_back", audit.RejectedBoxes[0].Reason);
        Assert.True(audit.DropEntireFrame);
    }

    [Fact]
    public void AutoLabelGuard_KeepsNormalBtnBackAndIsolatesLowConfidence()
    {
        var ocrMenu = MakeOcr(("返回", 0.10, 0.90, 0.96f), ("确认", 0.82, 0.85, 0.95f));
        var dets = new[]
        {
            new YoloDetection("btn_back", 1, 0.85f, 0.05, 0.86, 0.10, 0.07),
            new YoloDetection("btn_confirm", 2, 0.45f, 0.78, 0.82, 0.12, 0.08),
        };

        var audit = AutoLabelGuard.Audit(ocrMenu, dets, MatchScreen.HasConfirm);
        Assert.False(audit.DropEntireFrame);
        Assert.Single(audit.CleanBoxes);
        Assert.Equal(1, audit.CleanBoxes[0].ClassId);
        Assert.Single(audit.ReviewBoxes);
        Assert.True(audit.NeedsReview);
    }

    [Fact]
    public void UnknownPagePromptBuilder_GeneratesStableIdAndPrompt()
    {
        var ocr = MakeOcr(("特别活动", 0.25, 0.12, 0.92f), ("确认领取", 0.80, 0.88, 0.91f));
        string id1 = UnknownPagePromptBuilder.BuildUnknownId(ocr, isCn: true);
        string id2 = UnknownPagePromptBuilder.BuildUnknownId(ocr, isCn: true);

        Assert.Equal(id1, id2);
        Assert.StartsWith("unk_cn_", id1);

        var audit = AutoLabelGuard.Audit(ocr, Array.Empty<YoloDetection>(), MatchScreen.Unknown);
        string md = UnknownPagePromptBuilder.BuildPromptMarkdown(
            id1, isCn: true, 1280, 720, "manual", "MainMenu --[Pad:A]--> " + id1,
            ocr, Array.Empty<YoloDetection>(), audit);

        Assert.Contains(id1, md);
        Assert.Contains("1280 x 720", md);
        Assert.Contains("MainMenu --[Pad:A]-->", md);
        Assert.Contains("特别活动", md);
    }

    [Fact]
    public async Task DatasetSampleWriter_WritesLosslessPngMetadataPromptAndNavigationGraph()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "efootball_sample_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new DatasetSampleWriter(tempRoot);
            var frame = MakeFrame(1280, 720, 0, 0, 0, gradient: true);
            var ocrUnknown = MakeOcr(("未知活动页", 0.30, 0.15, 0.90f), ("继续", 0.80, 0.85, 0.93f));

            var res = writer.TryRecordFrame(
                frame, ocrUnknown, Array.Empty<YoloDetection>(), MatchScreen.Unknown,
                isCn: false, sourceMode: "manual", transitionSummary: "MainMenu --[Key:Enter]--> unknown");
            Assert.True(res.Saved);
            Assert.Equal("unknown", res.Bucket);

            writer.RecordTransition("MainMenu", "Key:Enter", res.PageId, isUnknownTarget: true, 1280, 720);
            writer.WriteNavigationGraph();

            Assert.True(File.Exists(res.ImagePath));
            Assert.True(File.Exists(Path.Combine(tempRoot, "classes.txt")));
            Assert.True(File.Exists(Path.Combine(tempRoot, "unknown", res.PageId, "prompt.md")));
            Assert.True(File.Exists(Path.Combine(tempRoot, "navigation_graph.md")));
            string nav = File.ReadAllText(Path.Combine(tempRoot, "navigation_graph.md"));
            Assert.Contains("flowchart LR", nav);
            Assert.Contains(res.PageId, nav);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BotModuleCatalog_RegistersManualSamplingWithoutGamepadTakeover()
    {
        var intl = BotModuleCatalog.Find(ModuleIds.IntlManualSample);
        var cn = BotModuleCatalog.Find(ModuleIds.CnManualSample);

        Assert.NotNull(intl);
        Assert.NotNull(cn);
        Assert.False(intl!.RequiresGamepad);
        Assert.False(cn!.RequiresGamepad);
        Assert.False(BotModuleCatalog.RequiresGamepad(ModuleIds.IntlManualSample));
        Assert.False(BotModuleCatalog.RequiresGamepad(ModuleIds.CnManualSample));
        Assert.True(BotModuleCatalog.RequiresGamepad(ModuleIds.IntlMyLeague));
        Assert.Empty(BotModuleCatalog.SelfCheck());
    }

    [Fact]
    public void OcrSampleTrigger_FiltersEmptyCutsceneAndTriggersOnOcrChangeOrHardCase()
    {
        // ① 无 OCR 且无 UI 检测框（过场动画/纯草皮）：不采样
        var emptyDecision = OcrSampleTrigger.Evaluate(
            OcrResult.Empty, Array.Empty<YoloDetection>(), MatchScreen.Unknown,
            isCn: true, lastPageId: null, lastOcrWords: null, hasPendingAction: false);
        Assert.False(emptyDecision.ShouldSample);
        Assert.Equal("no_ocr_or_ui", emptyDecision.Reason);

        // ② 首次读到有效 OCR 文本：自动采样
        var ocr1 = MakeOcr(("特别赛事", 0.25, 0.12, 0.94f), ("前往比赛", 0.80, 0.86, 0.92f));
        var d1 = OcrSampleTrigger.Evaluate(
            ocr1, Array.Empty<YoloDetection>(), MatchScreen.Unknown,
            isCn: true, lastPageId: null, lastOcrWords: null, hasPendingAction: false);
        Assert.True(d1.ShouldSample);
        Assert.Equal("initial_ocr", d1.Reason);

        // ③ 同页同词无变化且非难例：跳过不采样
        var dSame = OcrSampleTrigger.Evaluate(
            ocr1, Array.Empty<YoloDetection>(), MatchScreen.Unknown,
            isCn: true, lastPageId: d1.PageId, lastOcrWords: d1.CurrentWords, hasPendingAction: false);
        Assert.False(dSame.ShouldSample);
        Assert.Equal("ocr_unchanged", dSame.Reason);

        // ④ OCR 词发生变化：自动触发采样
        var ocr2 = MakeOcr(("赛事奖励详情", 0.25, 0.12, 0.94f), ("领取奖励", 0.80, 0.86, 0.92f));
        var dChanged = OcrSampleTrigger.Evaluate(
            ocr2, Array.Empty<YoloDetection>(), MatchScreen.Unknown,
            isCn: true, lastPageId: d1.PageId, lastOcrWords: d1.CurrentWords, hasPendingAction: false);
        Assert.True(dChanged.ShouldSample);

        // ⑤ 同页低置信度 OCR 难例不再静态重复触发（避免停留不动时刷屏）
        var ocrHard = MakeOcr(("特别赛事", 0.25, 0.12, 0.62f), ("9/1000", 0.80, 0.86, 0.58f));
        var dHard = OcrSampleTrigger.Evaluate(
            ocrHard, Array.Empty<YoloDetection>(), MatchScreen.HasConfirm,
            isCn: true, lastPageId: "HasConfirm", lastOcrWords: FrameQualityFilter.ExtractWordSet(ocrHard), hasPendingAction: false);
        Assert.False(dHard.ShouldSample);
        Assert.Equal("ocr_unchanged", dHard.Reason);

        // ⑥ 同页同词但检测到光标移动：触发 cursor_moved 采样
        var shift = new FocusShiftResult(true, 0.50, 0.30, 0.50, 0.55, "下", "传奇", 18);
        var dCursor = OcrSampleTrigger.Evaluate(
            ocr1, Array.Empty<YoloDetection>(), MatchScreen.Unknown,
            isCn: true, lastPageId: d1.PageId, lastOcrWords: d1.CurrentWords, hasPendingAction: false, focusShift: shift);
        Assert.True(dCursor.ShouldSample);
        Assert.Equal("cursor_moved", dCursor.Reason);
    }

    [Fact]
    public void FrameQualityFilter_DetectsCursorMovementAndFocusedOcrText()
    {
        var f1 = MakeFrame(1280, 720, 40, 40, 40);
        var f2 = MakeFrame(1280, 720, 40, 40, 40);

        // 构造：原焦点在顶部 (y~0.25) 变暗，新焦点在中部 (y~0.58) 变亮
        PaintRect(f1, 0.40, 0.22, 0.60, 0.32, 200);
        PaintRect(f2, 0.40, 0.52, 0.60, 0.62, 200);

        var g1 = FrameQualityFilter.ComputeLumaGrid(f1);
        var g2 = FrameQualityFilter.ComputeLumaGrid(f2);
        var ocr = MakeOcr(("常规", 0.50, 0.27, 0.95f), ("传奇", 0.50, 0.57, 0.95f));

        var shift = FrameQualityFilter.DetectFocusShift(g1, g2, ocr);
        Assert.True(shift.Moved);
        Assert.Equal("下", shift.Direction);
        Assert.Equal("传奇", shift.FocusedText);
    }

    [Fact]
    public void AutoLabelGuard_RejectsBottomRightBtnBackEvenOnLeaguePageWithBackWord()
    {
        var ocr = MakeOcr(("返回", 0.12, 0.93, 0.95f), ("前往比赛", 0.87, 0.93, 0.95f));
        var dets = new[]
        {
            new YoloDetection("btn_back", 1, 0.88f, 0.78 - 0.09, 0.91 - 0.03, 0.18, 0.06),
            new YoloDetection("btn_back", 1, 0.92f, 0.03, 0.88, 0.18, 0.06),
        };

        var audit = AutoLabelGuard.Audit(ocr, dets, MatchScreen.LeaguePage);
        Assert.Single(audit.RejectedBoxes);
        Assert.Equal("mislabel_skip_as_btn_back", audit.RejectedBoxes[0].Reason);
        Assert.Single(audit.CleanBoxes);
        Assert.True(audit.CleanBoxes[0].Cx < 0.2);
    }

    [Fact]
    public void OcrTextNorm_FixesMinedCjkMisspellings()
    {
        Assert.Equal("比赛", OcrText.Norm("北赛"));
        Assert.Equal("横幅设置", OcrText.Norm("黄幅设置"));
        Assert.Equal("结束", OcrText.Norm("结引"));
        Assert.Equal("设置", OcrText.Norm("设量"));
        Assert.Equal("强力抢断", OcrText.Norm("强力种断"));
        Assert.Equal("强力抢断", OcrText.Norm("强力抢斯"));
        Assert.Equal("期间", OcrText.Norm("环间"));
    }

    [Fact]
    public void DatasetSampleWriter_RecordsFocusShiftIntoTransitionsAndNavGraph()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "efootball_nav_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var writer = new DatasetSampleWriter(tmp);
            var shift = new FocusShiftResult(true, 0.50, 0.30, 0.50, 0.55, "下", "前往比赛", 18);
            writer.RecordTransition("LeaguePage", "Pad:Down", "LeaguePage", false, 1282, 759, shift);
            writer.RecordTransition("LeaguePage", "Pad:A", "MatchStartPage", false, 1282, 759, shift);
            writer.WriteNavigationGraph();

            var jsonLines = File.ReadAllLines(Path.Combine(tmp, "transitions.jsonl"));
            Assert.Equal(2, jsonLines.Length);
            using var doc = System.Text.Json.JsonDocument.Parse(jsonLines[0]);
            Assert.Equal("下", doc.RootElement.GetProperty("FocusDirection").GetString());
            Assert.Equal("前往比赛", doc.RootElement.GetProperty("FocusedText").GetString());

            var navMd = File.ReadAllText(Path.Combine(tmp, "navigation_graph.md"));
            Assert.Contains("Pad:A @ 「前往比赛」", navMd);
            Assert.Contains("## 3. 页内光标移动路径", navMd);
            // Section 3 只收录同页光标移动（LeaguePage->LeaguePage），跨页切换（LeaguePage->MatchStartPage）不得混入
            var sec3 = navMd.Split("## 3. 页内光标移动路径")[1];
            Assert.Contains("`Pad:Down`", sec3);
            Assert.DoesNotContain("`Pad:A`", sec3);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void PadTakeoverBlockedException_StripCode_HandlesPrefix()
    {
        var raw = "启动失败：[PAD_TAKEOVER_BLOCKED] 手柄接管失败：请先拔掉物理手柄";
        Assert.True(EfootballBot.Core.Input.PadTakeoverBlockedException.IsBlocked(raw));
        Assert.Equal("启动失败： 手柄接管失败：请先拔掉物理手柄",
            EfootballBot.Core.Input.PadTakeoverBlockedException.StripCode(raw));
        Assert.DoesNotContain(EfootballBot.Core.Input.PadTakeoverBlockedException.Code,
            EfootballBot.Core.Input.PadTakeoverBlockedException.StripCode(raw));
    }

    [Fact]
    public void Pad_PauseEpoch_IncrementsMonotonicallyAcrossPauseResume()
    {
        var pad = new EfootballBot.Core.Input.Pad(
            new EfootballBot.Core.Input.DryRunPad(null!),
            new EfootballBot.Core.Config.TimingConfig());
        long e0 = pad.PauseEpoch;
        pad.PauseActions();
        long e1 = pad.PauseEpoch;
        pad.ResumeActions();
        long e2 = pad.PauseEpoch;
        pad.PauseActions();
        long e3 = pad.PauseEpoch;
        Assert.True(e1 > e0);
        Assert.Equal(e1, e2);
        Assert.True(e3 > e2);
    }

    [Fact]
    public void OcrLexiconCorrector_FixesLowConfidenceUiTyposWithoutTouchingNormalReads()
    {
        Assert.Equal("前往比赛", OcrLexiconCorrector.Correct("前往北赛", 0.72f));
        Assert.Equal("自动配置球员", OcrLexiconCorrector.Correct("自动配量球员", 0.68f));
        Assert.Equal("活动已完成", OcrLexiconCorrector.Correct("活动已完战", 0.75f));

        // 高置信度、短词、英文/球员名、含数字均不改动
        Assert.Equal("前往北赛", OcrLexiconCorrector.Correct("前往北赛", 0.95f));
        Assert.Equal("确定", OcrLexiconCorrector.Correct("确定", 0.60f));
        Assert.Equal("Mbappe", OcrLexiconCorrector.Correct("Mbappe", 0.50f));
        Assert.Equal("100GP", OcrLexiconCorrector.Correct("100GP", 0.50f));

        // CorrectRaw 同步保留原样或纠错后的中文词
        Assert.Equal("自动配置球员", OcrLexiconCorrector.CorrectRaw("自动配量 球员", 0.70));
        Assert.Equal("自动配量 球员", OcrLexiconCorrector.CorrectRaw("自动配量 球员", 0.92));
    }

    private static void PaintRect(FrameData f, double x0, double y0, double x1, double y1, byte lum)
    {
        int ix0 = (int)(x0 * f.Width), ix1 = (int)(x1 * f.Width);
        int iy0 = (int)(y0 * f.Height), iy1 = (int)(y1 * f.Height);
        for (int y = iy0; y < iy1; y++)
        {
            for (int x = ix0; x < ix1; x++)
            {
                int i = (y * f.Width + x) * 4;
                f.Bgra[i] = lum;
                f.Bgra[i + 1] = lum;
                f.Bgra[i + 2] = lum;
            }
        }
    }
}
