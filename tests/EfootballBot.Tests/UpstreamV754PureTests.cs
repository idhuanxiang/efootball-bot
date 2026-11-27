using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Bot.Scenarios.Stud;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 上游 v7.5.4 纯逻辑与合成帧回归测试（不依赖外部 .png 实机帧）：
//   1) CnWeekly 控制方式闸（IsControlProbePage）
//   2) CnWeekly 单卡特殊奖励同分优先级（PlanLevelReward）
//   3) ResultCardProbe 赛后「球员评价」左侧白卡探针 + CnAdvance 落点/前台点击节流
public class UpstreamV754PureTests
{
    private readonly ITestOutputHelper _out;
    public UpstreamV754PureTests(ITestOutputHelper output) => _out = output;

    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    private static YoloDetection Box(string cls, double score, double cx, double cy)
        => new(cls, 0, (float)score, cx - 0.05, cy - 0.03, 0.10, 0.06);

    private static FrameData Frame(int w, int h,
        params (double X0, double Y0, double X1, double Y1)[] whites)
        => FrameOn(w, h, dark: true, whites);

    private static FrameData FrameOn(int w, int h, bool dark,
        params (double X0, double Y0, double X1, double Y1)[] whites)
    {
        var bgra = new byte[w * h * 4];
        byte baseL = dark ? (byte)24 : (byte)246;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = baseL;
            bgra[i + 1] = baseL;
            bgra[i + 2] = (byte)(dark ? baseL + 4 : baseL);
            bgra[i + 3] = 255;
        }
        foreach (var (x0, y0, x1, y1) in whites)
            for (int y = (int)(h * y0); y < (int)(h * y1); y++)
                for (int x = (int)(w * x0); x < (int)(w * x1); x++)
                {
                    int i = (y * w + x) * 4;
                    bgra[i] = 250;
                    bgra[i + 1] = 250;
                    bgra[i + 2] = 253;
                }
        return new FrameData(bgra, w, h, 0);
    }

    // ---------------- CnWeekly 控制方式闸 ----------------

    [Theory]
    [InlineData(CwScreen.Bracket, MatchScreen.LeaguePage, true)]      // 实机这一页
    [InlineData(CwScreen.ChallengeCardHome, MatchScreen.LeaguePage, true)]
    [InlineData(CwScreen.EventHome, MatchScreen.LeaguePage, true)]
    [InlineData(CwScreen.EventsList, MatchScreen.LeaguePage, true)]
    [InlineData(CwScreen.None, MatchScreen.LeaguePage, true)]         // 主菜单/联赛主页
    [InlineData(CwScreen.None, MatchScreen.HasConfirm, false)]        // 弹窗覆盖层：不点，别点穿
    [InlineData(CwScreen.None, MatchScreen.Unknown, false)]
    public void ControlProbeAppliesToMenuPagesOnly(CwScreen cw, MatchScreen ml, bool expected)
        => Assert.Equal(expected, CnWeeklyBrain.IsControlProbePage(cw, ml));

    // ---------------- CnWeekly 单卡特殊奖励同评分优先级 ----------------

    [Fact]
    public void SameRating_FallsBackToForwardPriority()
    {
        var panels = new[] { 0.221, 0.499, 0.777 };
        var words = new List<OcrWord>();
        for (int i = 0; i < 3; i++)
            words.Add(new OcrWord("84", panels[i] - 0.031, 0.380, 0.026, 0.033));
        words.Add(new OcrWord("前场球员", 0.180, 0.252, 0.082, 0.046));
        words.Add(new OcrWord("中场球员", 0.459, 0.252, 0.082, 0.046));
        words.Add(new OcrWord("后场球员", 0.737, 0.252, 0.080, 0.046));
        var ocr = new OcrResult(words, 1280, 720);

        var choice = CnWeeklyBrain.PlanLevelReward(ocr);
        Assert.NotNull(choice);
        _out.WriteLine($"同分 → 选 {choice!.Slot} 值={choice.Value} 明细={choice.Detail}");
        Assert.Equal(CwRewardSlot.Forward, choice.Slot);
    }

    // ---------------- ① ResultCardProbe: OCR 读到卡面文字：点字 ----------------

    [Fact]
    public void Locate_OCR读到卡面下一步_点在字上()
    {
        var ocr = Ocr(
            W("全场", 0.615, 0.072),
            W("球员评价", 0.616, 0.290),
            W("下一步", 0.132, 0.500));
        var hit = ResultCardProbe.Locate(ocr, null, attempt: 3);
        Assert.NotNull(hit);
        _out.WriteLine($"hit = ({hit!.Value.X:F3},{hit.Value.Y:F3}) [{hit.Value.Where}]");
        Assert.Equal("白卡「下一步」文字", hit.Value.Where);
        Assert.InRange(hit.Value.X, 0.05, 0.25);
        Assert.InRange(hit.Value.Y, 0.40, 0.60);
    }

    [Fact]
    public void Locate_合并长行不许抢卡面按钮()
    {
        var ocr = Ocr(
            W("球员评价", 0.616, 0.290),
            W("下一步球员评价", 0.400, 0.500));
        var hit = ResultCardProbe.Locate(ocr, null, attempt: 1);
        Assert.NotNull(hit);
        Assert.Equal("白卡标定落点", hit!.Value.Where);
        Assert.InRange(hit.Value.X, 0.05, 0.25);
    }

    // ---------------- ② ResultCardProbe: 像素探针 ----------------

    [Fact]
    public void FindCard_左三分之一的白卡必须认出来()
    {
        var frame = Frame(1280, 720, (0.026, 0.019, 0.242, 0.881));
        var card = ResultCardProbe.FindCard(frame);
        Assert.NotNull(card);
        _out.WriteLine($"card = {card!.Value}");
        Assert.InRange(card.Value.X0, 0.015, 0.05);
        Assert.InRange(card.Value.X1, 0.22, 0.27);
        Assert.InRange(card.Value.Y0, 0.005, 0.05);
        Assert.InRange(card.Value.Y1, 0.85, 0.92);
    }

    [Fact]
    public void FindCard_版式A短卡同样认得出()
    {
        var frame = Frame(1280, 720, (0.028, 0.031, 0.239, 0.444));
        var card = ResultCardProbe.FindCard(frame);
        Assert.NotNull(card);
        Assert.InRange(card!.Value.Y0, 0.01, 0.06);
        Assert.InRange(card.Value.Y1, 0.40, 0.48);
    }

    [Fact]
    public void Locate_整帧漏读按钮字_像素探针点卡内箭头()
    {
        var frame = Frame(1280, 720, (0.026, 0.019, 0.242, 0.881));
        var hit = ResultCardProbe.Locate(Ocr(), frame, attempt: 150);
        Assert.NotNull(hit);
        _out.WriteLine($"hit = ({hit!.Value.X:F3},{hit.Value.Y:F3}) [{hit.Value.Where}]");
        Assert.Equal("白卡像素探针", hit.Value.Where);
        Assert.InRange(hit.Value.X, 0.10, 0.16);
        Assert.InRange(hit.Value.Y, 0.36, 0.48);
        Assert.False(hit.Value.X > 0.8 && hit.Value.Y > 0.8);
    }

    [Fact]
    public void FindCard_白底文档页不认()
    {
        var frame = FrameOn(1280, 720, dark: false);
        Assert.Null(ResultCardProbe.FindCard(frame));
    }

    [Fact]
    public void FindCard_白横条与中下部白块都不认()
    {
        var band = Frame(1280, 720, (0.02, 0.02, 0.28, 0.10));
        Assert.Null(ResultCardProbe.FindCard(band));
        var low = Frame(1280, 720, (0.02, 0.50, 0.24, 0.92));
        Assert.Null(ResultCardProbe.FindCard(low));
    }

    // ---------------- ③ ResultCardProbe: 标定落点轮换 ----------------

    [Fact]
    public void 标定落点_三档轮换且都在左侧白卡范围内()
    {
        var seen = new HashSet<(double, double)>();
        for (int a = 0; a < ResultCardProbe.FallbackPoints.Length; a++)
        {
            var p = ResultCardProbe.FallbackPoints[Math.Abs(a) % ResultCardProbe.FallbackPoints.Length];
            Assert.InRange(p.X, 0.05, 0.25);
            Assert.InRange(p.Y, 0.15, 0.60);
            seen.Add(p);
        }
        Assert.Equal(ResultCardProbe.FallbackPoints.Length, seen.Count);
    }

    [Fact]
    public void Locate_既无字也无帧_不抢非结算页()
    {
        var ocr = Ocr(W("前往比赛", 0.14, 0.25), W("设置", 0.20, 0.93));
        Assert.Null(ResultCardProbe.Locate(ocr, null, attempt: 5));
    }

    // ---------------- ④ CnAdvance 的档位顺序 ----------------

    [Fact]
    public void LocateClickPoint_按钮锚点优先()
    {
        var ocr = Ocr(W("RT 下一步", 0.900, 0.925), W("球员评价", 0.616, 0.290));
        var p = CnAdvance.LocateClickPoint(ocr, null, null, attempt: 3, frame: null);
        Assert.Equal("按钮位置", p.Where);
        Assert.True(p.X > 0.8 && p.Y > 0.9);
    }

    [Fact]
    public void LocateClickPoint_页面兜底坐标优先于白卡()
    {
        var frame = Frame(1280, 720, (0.026, 0.019, 0.242, 0.881));
        var p = CnAdvance.LocateClickPoint(Ocr(), null, (0.850, 0.920), attempt: 3, frame: frame);
        Assert.Equal("兜底坐标", p.Where);
        Assert.Equal(0.850, p.X, 3);
    }

    [Fact]
    public void LocateClickPoint_赛后球员评价页_落点在左侧白卡而不是右下角()
    {
        var ocr = Ocr(W("全场", 0.615, 0.072), W("球员评价", 0.616, 0.290), W("下一步", 0.132, 0.500));
        var p = CnAdvance.LocateClickPoint(ocr, null, null, attempt: 150, frame: null);
        _out.WriteLine($"p = ({p.X:F3},{p.Y:F3}) [{p.Where}]");
        Assert.NotEqual("国服右下兜底坐标", p.Where);
        Assert.InRange(p.X, 0.05, 0.25);
        Assert.InRange(p.Y, 0.40, 0.60);
    }

    [Fact]
    public void LocateClickPoint_真没线索时仍是右下兜底()
    {
        var p = CnAdvance.LocateClickPoint(Ocr(), Array.Empty<YoloDetection>(), null, attempt: 9, frame: null);
        Assert.Equal("国服右下兜底坐标", p.Where);
        Assert.Equal(CnAdvance.DefaultX, p.X, 3);
        Assert.Equal(CnAdvance.DefaultY, p.Y, 3);
    }

    [Fact]
    public void LocateClickPoint_Yolo右下的confirm框仍然管用()
    {
        var dets = new[] { Box("btn_confirm", 0.93, 0.88, 0.90) };
        var p = CnAdvance.LocateClickPoint(Ocr(), dets, null, attempt: 3, frame: null);
        Assert.Equal("按钮位置", p.Where);
        Assert.True(p.X > 0.8 && p.Y > 0.8);
    }

    // ---------------- ⑤ 后台点不动时的前台真实点击（最后一档） ----------------

    [Fact]
    public void UseForegroundClick_前七轮只走后台_第八轮起每六轮一次()
    {
        for (int a = 1; a < CnAdvance.ForegroundClickFromAttempt; a++)
            Assert.False(CnAdvance.UseForegroundClick(a), $"第 {a} 轮不该抢前台");
        Assert.True(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt));
        Assert.False(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt + 1));
        Assert.False(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt + CnAdvance.ForegroundClickEvery - 1));
        Assert.True(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt + CnAdvance.ForegroundClickEvery));
        Assert.True(CnAdvance.ForegroundClickEvery >= 3);
    }

    // ---------------- 全库回归自查补充用例 ----------------

    [Fact]
    public void PopupBrain_TruncatedBgra_DoesNotThrow()
    {
        var truncated = new FrameData(new byte[16], 1280, 720, 0);
        Assert.False(PopupBrain.IsReconnectDialog(truncated, OcrResult.Empty));
        var ocr = Ocr(W("取消", 0.50, 0.54), W("重试", 0.50, 0.64));
        var action = PopupBrain.DecideReconnect(truncated, ocr, out _);
        Assert.Equal(PopupAction.DownThenConfirm, action);
    }

    [Fact]
    public void CnAdvance_FindButton_SameRowPrefersRightEvenIfLeftSlightlyLower()
    {
        // 同排（|Δy| = 0.005 < 0.02），右侧按钮先遍历到、左侧词 y 略大 0.005，仍必须选右侧
        var ocr = Ocr(W("确定", 0.88, 0.845), W("确定", 0.20, 0.850));
        var hit = CnAdvance.FindButton(ocr, null, "确定");
        Assert.NotNull(hit);
        Assert.True(hit!.Value.X > 0.80, $"同排应优先右侧，实际 X={hit.Value.X:F3}");
    }

    [Fact]
    public void CnAdvance_LocateClickPoint_DoesNotLetEnterWordsStealResultCard()
    {
        // 赛后白卡结算页若下半屏出现「进入/开始」等 EnterWords，不应抢走白卡落点
        var ocr = Ocr(
            W("全场", 0.615, 0.072),
            W("球员评价", 0.616, 0.290),
            W("进入下一赛季活动说明", 0.550, 0.700),
            W("下一步", 0.132, 0.500));
        var p = CnAdvance.LocateClickPoint(ocr, null, null, attempt: 3, frame: null);
        Assert.InRange(p.X, 0.05, 0.25);
        Assert.Equal("白卡「下一步」文字", p.Where);
    }

    [Fact]
    public void MatchBrain_HasLiveMatchHud_FullWidthDigits_DoesNotThrow()
    {
        var ocr = Ocr(W("３８２８", 0.27, 0.055), W("FRA", 0.06, 0.055), W("ALB", 0.20, 0.055));
        Assert.False(MatchBrain.HasLiveMatchHud(ocr));
    }

    [Fact]
    public void MatchBrain_QuickAdvance_DoesNotTriggerOnNonMatchUnknownOrWatermark()
    {
        var menuUnknown = Ocr(W("继续", 0.50, 0.60));
        var a1 = MatchBrain.Decide(MatchScreen.Unknown, menuUnknown, 0, inMatchRecently: false);
        Assert.DoesNotContain("立刻点", a1.Target);

        var watermark = Ocr(W("实况足球在线", 0.50, 0.50), W("11.1.0.1", 0.50, 0.55));
        // 即使右下角有蓝色像素，水印加载页也不得走快通道
        var blueCorner = Frame(1280, 720);
        for (int y = (int)(720 * 0.86); y < (int)(720 * 0.96); y++)
            for (int x = (int)(1280 * 0.74); x < (int)(1280 * 0.98); x++)
            {
                int i = (y * 1280 + x) * 4;
                blueCorner.Bgra[i] = 240; blueCorner.Bgra[i + 1] = 80; blueCorner.Bgra[i + 2] = 20;
            }
        var a2 = MatchBrain.Decide(MatchScreen.Unknown, watermark, 0, inMatchRecently: true, blueCorner);
        Assert.DoesNotContain("立刻点", a2.Target);
    }

    [Fact]
    public void CnWeeklyBrain_Panels_IgnoresFullRowMergedLine()
    {
        // 模拟同一行三个面板标题被 OCR 拼成一整行长词（W=0.75, CenterX=0.50），
        // 以及拆词后的「前锋」「捆绑包」：Panels 不得把整行中心 0.50 赋给前锋
        var words = new[]
        {
            new OcrWord("前锋", 0.17, 0.256, 0.05, 0.03),
            new OcrWord("捆绑包", 0.22, 0.256, 0.06, 0.03),
            new OcrWord("中场捆绑包", 0.45, 0.256, 0.10, 0.03),
            new OcrWord("后场捆绑包", 0.73, 0.256, 0.10, 0.03),
        };
        var ocr = new OcrResult(words, 1280, 720);
        var panels = CnWeeklyBrain.Panels(ocr, "捆绑包");
        Assert.InRange(panels[0].X, 0.15, 0.30);
        Assert.InRange(panels[1].X, 0.45, 0.55);
        Assert.InRange(panels[2].X, 0.73, 0.83);
    }

    [Fact]
    public void MatchBrain_QuickAdvance_DoesNotStealInMatchSkipOrOutOfMatchPopup()
    {
        // 1) InMatch 若已出现「跳过比赛」横幅且允许跳过，不得被右下角「继续/下一步」快通道抢占
        var skipOcr = Ocr(
            W("对局获胜", 0.50, 0.25),
            W("跳过比赛", 0.50, 0.30),
            W("继续", 0.88, 0.92));
        var a1 = MatchBrain.Decide(MatchScreen.InMatch, skipOcr, 0, inMatchRecently: true, allowMatchSkip: true);
        Assert.Equal(MatchActionKind.PadStartPress, a1.Kind);

        // 2) 赛外 HasConfirm 若 PopupBrain 识别到按钮框（或非赛中），不得走 200ms 立刻点快通道
        var frame = Frame(1280, 720, (0.42, 0.52, 0.58, 0.58));
        var popupOcr = Ocr(W("确定", 0.50, 0.55), W("继续", 0.50, 0.45));
        var a2 = MatchBrain.Decide(MatchScreen.HasConfirm, popupOcr, 0, inMatchRecently: false, frame: frame);
        Assert.DoesNotContain("立刻点", a2.Target);
    }

    [Fact]
    public void HubTileLocator_TruncatedBgra_DoesNotThrow()
    {
        var truncated = new FrameData(new byte[16], 1280, 720, 0);
        var ocr = Ocr(
            W("eFootball™联赛", 0.30, 0.37),
            W("AI比赛", 0.60, 0.37));
        Assert.Null(HubTileLocator.IsFocusedOnTarget(ocr, truncated, "我的联赛"));
    }

    [Fact]
    public void StudTrainingBrain_ReadVisiblePlayerCount_HandlesSlashAnd4DigitCap()
    {
        Assert.Equal(9, StudTrainingBrain.ReadVisiblePlayerCount(Ocr(W("数量:9/1000", 0.88, 0.08))));
        Assert.Equal(12, StudTrainingBrain.ReadVisiblePlayerCount(Ocr(W("数量:", 0.84, 0.08), W("12/900", 0.91, 0.08))));
        Assert.Equal(9, StudTrainingBrain.ReadVisiblePlayerCount(Ocr(W("球员数量9900储备球员", 0.88, 0.08))));
        // 4 位容量 + 斜杠被吞：「9/1000」→「91000」，末 3 位切片旧算法会读成 91（放大 10 倍）
        Assert.Equal(9, StudTrainingBrain.ReadVisiblePlayerCount(Ocr(W("数量91000", 0.88, 0.08))));
        Assert.Equal(12, StudTrainingBrain.ReadVisiblePlayerCount(Ocr(W("数量12900", 0.88, 0.08))));
    }
}
