using System.Numerics;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Sampling;

/// <summary>光标/焦点移动识别结果。</summary>
public sealed record FocusShiftResult(
    bool Moved,
    double OldX,
    double OldY,
    double NewX,
    double NewY,
    string Direction,
    string? FocusedText,
    int ChangedCells);

/// <summary>
/// 分辨率自适应画面质量门控：
/// 1) 剔除黑屏/过暗过渡帧与纯色过渡帧；
/// 2) 生成 9x8 归一化网格 dHash，用于同类画面快速去重；
/// 3) 结合 OCR 词集 Jaccard 相似度判定重复帧，避免训练集中堆满静止重复样本。
/// </summary>
public static class FrameQualityFilter
{
    private const int GridW = 32;
    private const int GridH = 18;
    private const int HashW = 9;
    private const int HashH = 8;

    /// <summary>
    /// 分辨率自适应检查帧是否值得采样（兼容 720p / 1080p / 2K / 超宽屏，零额外内存分配）。
    /// </summary>
    public static (bool Ok, string Reason) CheckUsable(FrameData frame)
    {
        if (frame.Width < 320 || frame.Height < 180 || frame.Bgra.Length < frame.Width * frame.Height * 4)
            return (false, "invalid_frame");

        var bgra = frame.Bgra;
        int stride = frame.Width * 4;
        int total = GridW * GridH;
        int darkCount = 0;
        double sum = 0;
        double sumSq = 0;
        double gradSum = 0;
        int gradCount = 0;

        Span<double> prevRow = stackalloc double[GridW];
        for (int gy = 0; gy < GridH; gy++)
        {
            int py = (int)((gy + 0.5) * frame.Height / GridH);
            int rowOffset = py * stride;
            double prevLum = 0;

            for (int gx = 0; gx < GridW; gx++)
            {
                int px = (int)((gx + 0.5) * frame.Width / GridW);
                int idx = rowOffset + px * 4;
                // BT.601 近似亮度（BGRA）
                double lum = (bgra[idx + 2] * 299 + bgra[idx + 1] * 587 + bgra[idx] * 114) / 1000.0;
                if (lum < 16.0) darkCount++;
                sum += lum;
                sumSq += lum * lum;

                if (gx > 0)
                {
                    gradSum += Math.Abs(lum - prevLum);
                    gradCount++;
                }
                if (gy > 0)
                {
                    gradSum += Math.Abs(lum - prevRow[gx]);
                    gradCount++;
                }
                prevLum = lum;
                prevRow[gx] = lum;
            }
        }

        double darkRatio = (double)darkCount / total;
        double mean = sum / total;
        if (darkRatio >= 0.92 || mean < 14.0)
            return (false, "black_transition");

        double variance = Math.Max(0, sumSq / total - mean * mean);
        double stdDev = Math.Sqrt(variance);
        double avgGrad = gradCount > 0 ? gradSum / gradCount : 0;
        if (stdDev < 8.0 && avgGrad < 3.5)
            return (false, "flat_transition");

        return (true, "ok");
    }

    /// <summary>
    /// 基于 9x8 归一化灰度网格计算 64 位 dHash（分辨率自适应）。
    /// </summary>
    public static ulong ComputeDHash(FrameData frame)
    {
        if (frame.Width < HashW || frame.Height < HashH || frame.Bgra.Length < frame.Width * frame.Height * 4)
            return 0;

        var bgra = frame.Bgra;
        int stride = frame.Width * 4;
        ulong hash = 0;
        int bit = 0;

        for (int gy = 0; gy < HashH; gy++)
        {
            int py = (int)((gy + 0.5) * frame.Height / HashH);
            int rowOffset = py * stride;
            int prevLum = 0;
            for (int gx = 0; gx < HashW; gx++)
            {
                int px = (int)((gx + 0.5) * frame.Width / HashW);
                int idx = rowOffset + px * 4;
                int lum = (bgra[idx + 2] * 299 + bgra[idx + 1] * 587 + bgra[idx] * 114) / 1000;
                if (gx > 0)
                {
                    if (lum > prevLum)
                        hash |= 1UL << bit;
                    bit++;
                }
                prevLum = lum;
            }
        }
        return hash;
    }

    /// <summary>计算两个 64 位 dHash 的汉明距离。</summary>
    public static int HammingDistance(ulong a, ulong b)
        => BitOperations.PopCount(a ^ b);

    /// <summary>计算两帧 OCR 规范化词集的 Jaccard 相似度（0~1）。</summary>
    public static double OcrJaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        if (a.Count == 0 || b.Count == 0) return 0.0;
        int inter = 0;
        foreach (var w in a)
            if (b.Contains(w)) inter++;
        int union = a.Count + b.Count - inter;
        return union > 0 ? (double)inter / union : 0.0;
    }

    /// <summary>提取归一化 OCR 词集用于去重与比较。</summary>
    public static HashSet<string> ExtractWordSet(OcrResult ocr)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in ocr.Words)
        {
            var norm = OcrText.Norm(w.Text);
            if (!string.IsNullOrEmpty(norm))
                set.Add(norm);
        }
        return set;
    }

    /// <summary>
    /// 判断当前帧是否与最近样本高度重复：图像汉明距离 &lt;= maxHamming 且 OCR 词集相似度 &gt;= 0.90。
    /// </summary>
    public static bool IsDuplicate(
        ulong hash,
        IReadOnlyCollection<ulong> recentHashes,
        double ocrJaccard,
        int maxHamming = 4)
    {
        if (ocrJaccard < 0.90) return false;
        foreach (var prev in recentHashes)
        {
            if (HammingDistance(hash, prev) <= maxHamming)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 按 32x18 归一化网格采样亮度（576 字节，分辨率自适应），用于局部光标/焦点移动检测。
    /// </summary>
    public static byte[] ComputeLumaGrid(FrameData frame)
    {
        var grid = new byte[GridW * GridH];
        if (frame.Width < GridW || frame.Height < GridH || frame.Bgra.Length < frame.Width * frame.Height * 4)
            return grid;

        var bgra = frame.Bgra;
        int stride = frame.Width * 4;
        for (int gy = 0; gy < GridH; gy++)
        {
            int py = (int)((gy + 0.5) * frame.Height / GridH);
            int rowOffset = py * stride;
            for (int gx = 0; gx < GridW; gx++)
            {
                int px = (int)((gx + 0.5) * frame.Width / GridW);
                int idx = rowOffset + px * 4;
                int lum = (bgra[idx + 2] * 299 + bgra[idx + 1] * 587 + bgra[idx] * 114) / 1000;
                grid[gy * GridW + gx] = (byte)Math.Clamp(lum, 0, 255);
            }
        }
        return grid;
    }

    /// <summary>
    /// 检测同页面内的局部高亮焦点/光标移动，并关联距离新焦点最近的 OCR 文本。
    /// </summary>
    public static FocusShiftResult DetectFocusShift(byte[]? prevGrid, byte[] currGrid, OcrResult ocr)
    {
        if (prevGrid is null || prevGrid.Length != GridW * GridH || currGrid.Length != GridW * GridH)
            return new FocusShiftResult(false, 0, 0, 0, 0, "", null, 0);

        int brightCount = 0, darkCount = 0;
        double brightSumX = 0, brightSumY = 0;
        double darkSumX = 0, darkSumY = 0;

        for (int gy = 0; gy < GridH; gy++)
        {
            double ny = (gy + 0.5) / GridH;
            for (int gx = 0; gx < GridW; gx++)
            {
                double nx = (gx + 0.5) / GridW;
                int i = gy * GridW + gx;
                int diff = currGrid[i] - prevGrid[i];
                if (diff >= 24)
                {
                    brightCount++;
                    brightSumX += nx;
                    brightSumY += ny;
                }
                else if (diff <= -24)
                {
                    darkCount++;
                    darkSumX += nx;
                    darkSumY += ny;
                }
            }
        }

        int changed = brightCount + darkCount;
        // 整屏切换或全局动画变化（>260 格）不算光标移动；极小噪声（<6 格）忽略
        if (changed < 6 || changed > 260 || (brightCount < 3 && darkCount < 3))
            return new FocusShiftResult(false, 0, 0, 0, 0, "", null, changed);

        double newX = brightCount > 0 ? brightSumX / brightCount : darkSumX / darkCount;
        double newY = brightCount > 0 ? brightSumY / brightCount : darkSumY / darkCount;
        double oldX = darkCount > 0 ? darkSumX / darkCount : newX;
        double oldY = darkCount > 0 ? darkSumY / darkCount : newY;

        double dx = newX - oldX;
        double dy = newY - oldY;
        if (brightCount >= 3 && darkCount >= 3 && Math.Sqrt(dx * dx + dy * dy) < 0.03)
            return new FocusShiftResult(false, oldX, oldY, newX, newY, "", null, changed);

        string dir;
        if (brightCount == 0 || darkCount == 0 || Math.Sqrt(dx * dx + dy * dy) < 0.03)
            dir = "局部切换";
        else if (Math.Abs(dy) >= Math.Abs(dx))
            dir = dy > 0 ? "下" : "上";
        else
            dir = dx > 0 ? "右" : "左";

        string? focusedText = null;
        double bestDist = 0.18;
        foreach (var w in ocr.Words)
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            double dist = Math.Sqrt(Math.Pow(w.CenterX - newX, 2) + Math.Pow(w.CenterY - newY, 2));
            if (dist <= bestDist)
            {
                bestDist = dist;
                focusedText = w.Text.Trim();
            }
        }

        return new FocusShiftResult(true, oldX, oldY, newX, newY, dir, focusedText, changed);
    }
}
