using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

public sealed class AuditAndOcrUpgradeTests
{
    [Theory]
    [InlineData("前往比賽", "前往比赛")]
    [InlineData("確 認 ！", "确认")]
    [InlineData("繼 續", "继续")]
    [InlineData("頂級球員", "顶级球员")]
    [InlineData("聯賽積分", "联赛积分")]
    [InlineData("開賽準備", "开赛准备")]
    [InlineData("領取獎勵", "领取奖励")]
    public void OcrText_Norm_SinglePass_NormalizesTcAndPunctuation(string raw, string expected)
    {
        Assert.Equal(expected, OcrText.Norm(raw));
    }

    [Fact]
    public void OcrResult_MergeLines_SplitsDistantColumns_AndFindButtonPrefersTightBox()
    {
        var words = new[]
        {
            new OcrWord("取", 0.20, 0.70, 0.02, 0.03, 0.95),
            new OcrWord("消", 0.225, 0.70, 0.02, 0.03, 0.95),
            new OcrWord("确", 0.65, 0.70, 0.02, 0.03, 0.95),
            new OcrWord("认", 0.675, 0.70, 0.02, 0.03, 0.95),
        };
        var ocr = new OcrResult(words, 1280, 720);

        var btn = ocr.FindButton("确认");
        Assert.NotNull(btn);
        Assert.True(btn!.W < 0.10, $"Expected tight segment box, got W={btn.W}");
        Assert.InRange(btn.CenterX, 0.65, 0.70);
    }

    [Fact]
    public void LineReread_GroupLines_SplitsDistantColumnsOnSameRow()
    {
        var words = new[]
        {
            new OcrWord("前", 0.10, 0.80, 0.02, 0.03),
            new OcrWord("往", 0.125, 0.80, 0.02, 0.03),
            new OcrWord("比", 0.15, 0.80, 0.02, 0.03),
            new OcrWord("赛", 0.175, 0.80, 0.02, 0.03),
            new OcrWord("取", 0.80, 0.80, 0.02, 0.03),
            new OcrWord("消", 0.825, 0.80, 0.02, 0.03),
        };
        var groups = LineReread.GroupLines(words);
        Assert.Equal(2, groups.Count);
        Assert.Equal(4, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
    }

    [Fact]
    public void ImageScaling_DownscaleBilinear_PrecomputedXTable_MatchesGradient()
    {
        int w = 400, h = 200;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bgra[i + 0] = (byte)(x * 255 / (w - 1));
                bgra[i + 1] = (byte)(y * 255 / (h - 1));
                bgra[i + 2] = 128;
                bgra[i + 3] = 255;
            }
        }
        var src = new FrameData(bgra, w, h, 1);
        var dst = ImageScaling.DownscaleBilinear(src, 200);
        Assert.Equal(200, dst.Width);
        Assert.Equal(100, dst.Height);
        // Top-left near 0, bottom-right near 255
        Assert.InRange(dst.Bgra[0], 0, 5);
        int last = ((dst.Height - 1) * dst.Width + (dst.Width - 1)) * 4;
        Assert.InRange(dst.Bgra[last + 0], 250, 255);
        Assert.InRange(dst.Bgra[last + 1], 250, 255);
        Assert.Equal(128, dst.Bgra[last + 2]);
        Assert.Equal(255, dst.Bgra[last + 3]);
    }

    [Fact]
    public void PerfTrace_ExcludesThrottledZeroOcr_AndTracksPeaks()
    {
        var perf = new PerfTrace();
        perf.Record(10, 5, 100, 20, 5, 140);
        perf.Record(10, 5, 0, 20, 5, 40);
        perf.Record(10, 5, 0, 20, 5, 40);
        perf.Record(10, 5, 200, 20, 5, 240);

        // First non-zero OCR=100, second non-zero OCR=200 => OcrInferMs = 100 + 0.2*(200-100) = 120
        Assert.Equal(120, perf.OcrInferMs, 2);
        Assert.Equal(200, perf.MaxOcrMs);
        Assert.Equal(240, perf.MaxRoundMs);

        perf.Reset();
        Assert.Equal(0, perf.Rounds);
        Assert.Equal(0, perf.OcrInferMs);
        Assert.Equal(0, perf.MaxOcrMs);
        Assert.Equal(0, perf.MaxRoundMs);
    }
}
