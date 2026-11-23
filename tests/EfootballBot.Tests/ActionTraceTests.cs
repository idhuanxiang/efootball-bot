using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>自学习（录制/复现）核心逻辑回归：签名、相似度、轨迹存取、门控回放。</summary>
public class ActionTraceTests
{
    private static OcrResult Ocr(params string[] words)
        => new(words.Select((t, i) => new OcrWord(t, 0.10 + i * 0.01, 0.30, 0.10, 0.04)).ToList(), 1280, 720);

    private static IReadOnlyList<YoloDetection> Dets(params (string Name, float Score)[] items)
        => items.Select((x, i) => new YoloDetection(x.Name, i, x.Score, 0.1, 0.1, 0.1, 0.1)).ToList();

    private static string UniqueName() => "test_trace_" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void 签名_只保留归一化词与高置信度类别计数()
    {
        var ocr = Ocr("前往比赛", "排名", "比赛记录", "x");   // x 长度 1 会被丢掉
        var dets = Dets(("btn_back", 0.9f), ("btn_back", 0.8f), ("item_row", 0.2f));
        var sig = TraceSignature.Capture(ocr, dets);
        Assert.Equal(3, sig.Words.Length);
        Assert.Equal(2, sig.YoloCounts["btn_back"]);
        Assert.False(sig.YoloCounts.ContainsKey("item_row"));  // 0.2 < MinScore
    }

    [Fact]
    public void 相似度_相同为1_完全不同为0()
    {
        var ocr = Ocr("前往比赛", "排名", "比赛记录");
        var a = TraceSignature.Capture(ocr, Dets(("btn_back", 0.9f)));
        Assert.Equal(1.0, a.SimilarityTo(a), 3);
        var b = TraceSignature.Capture(Ocr("合约", "特殊球员名单"), Dets(("main_menu", 0.9f)));
        Assert.Equal(0.0, a.SimilarityTo(b), 3);
    }

    [Fact]
    public void 轨迹_SaveLoad往返_且非法名被拒()
    {
        string name = UniqueName();
        try
        {
            var trace = ActionTrace.Create(name);
            trace.Add(TraceSignature.Capture(Ocr("前往比赛", "排名"), Dets()), "A", 350);
            trace.Save();
            var loaded = ActionTrace.Load(name);
            Assert.NotNull(loaded);
            Assert.Single(loaded!.Steps);
            Assert.Equal("A", loaded.Steps[0].Button);
            Assert.Equal(350, loaded.Steps[0].DelayMs);
            Assert.Contains(name, ActionTrace.ListNames());
            Assert.Throws<ArgumentException>(() => ActionTrace.Load("../evil"));
        }
        finally
        {
            string p = Path.Combine(ActionTrace.TracesDir, name + ".json");
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static TeachingController Replay(string name, string mode, double threshold = 0.7)
        => new(new LearningConfig
        {
            TeachingEnabled = true,
            TeachingMode = mode,
            TeachingTraceName = name,
            TeachingSimilarity = threshold,
            TeachingMaxMisses = 3,
        }, (_, _) => { });

    [Fact]
    public void 顺序复现_画面匹配才发键_走完自动退出()
    {
        string name = UniqueName();
        try
        {
            var ocr = Ocr("前往比赛", "排名", "比赛记录");
            var dets = Dets(("btn_back", 0.9f));
            var trace = ActionTrace.Create(name);
            trace.Add(TraceSignature.Capture(ocr, dets), "A", 400);
            trace.Save();

            var ctl = Replay(name, "ReplaySequential");
            Assert.True(ctl.Tick(ocr, dets, out PadButton? btn, out int delay, out _));
            Assert.Equal(PadButton.A, btn);
            Assert.Equal(400, delay);
            Assert.Equal(TeachingMode.Off, ctl.Mode);   // 单步轨迹走完即退出
        }
        finally
        {
            string p = Path.Combine(ActionTrace.TracesDir, name + ".json");
            if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void 规则库复现_可命中任意一步_不要求按顺序()
    {
        string name = UniqueName();
        try
        {
            var first = Ocr("前往比赛", "排名", "比赛记录");
            var second = Ocr("合约", "特殊球员名单", "组合包");
            var trace = ActionTrace.Create(name);
            trace.Add(TraceSignature.Capture(first, Dets()), "A", 300);
            trace.Add(TraceSignature.Capture(second, Dets()), "B", 300);
            trace.Save();

            var ctl = Replay(name, "ReplayRuleBook");
            Assert.True(ctl.Tick(second, Dets(), out PadButton? btn, out _, out _));
            Assert.Equal(PadButton.B, btn);   // 直接命中第 2 步
        }
        finally
        {
            string p = Path.Combine(ActionTrace.TracesDir, name + ".json");
            if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void 画面不匹配_停手不发键_连续三次自动交回挂机()
    {
        string name = UniqueName();
        try
        {
            var trace = ActionTrace.Create(name);
            trace.Add(TraceSignature.Capture(Ocr("前往比赛", "排名"), Dets()), "A", 300);
            trace.Save();

            var ctl = Replay(name, "ReplaySequential");
            var other = Ocr("完全不同的", "另一页");
            for (int i = 0; i < 2; i++)
            {
                Assert.True(ctl.Tick(other, Dets(), out PadButton? none, out _, out _));
                Assert.Null(none);
            }
            Assert.False(ctl.Tick(other, Dets(), out PadButton? stillNone, out _, out _));
            Assert.Null(stillNone);
            Assert.Equal(TeachingMode.Off, ctl.Mode);
        }
        finally
        {
            string p = Path.Combine(ActionTrace.TracesDir, name + ".json");
            if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void 默认关闭时_不接管任何一轮()
    {
        var ctl = new TeachingController(new LearningConfig(), (_, _) => { });
        Assert.Equal(TeachingMode.Off, ctl.Mode);
        Assert.False(ctl.Tick(Ocr("前往比赛"), Dets(), out PadButton? btn, out _, out _));
        Assert.Null(btn);
    }
}
