using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Match;

/// <summary>一次到位磁贴移动计划：Delta &gt; 0 右移，&lt; 0 左移，0 直接按 A。</summary>
public sealed record HubTileMovePlan(int Delta, bool TargetVisible, string FocusedTitle);

/// <summary>
/// 比赛页（大活动页）磁贴行的 OCR 动态定位（共享层）：从画面文字认出各磁贴标题并按 x 排序，
/// 返回目标磁贴是第几格（0 起）。
///
/// 背景：磁贴行会随服务器活动增减（用户实机 2026-11-22：活动结束后第一格消失，
/// 「eFootball联赛」从第 3 卡左移到第 2 卡），任何写死"第 N 卡"的导航都会在活动轮换后按错。
/// 周活动/AI天梯都靠这个共享能力实时定位，读不全时返回 null，调用方退回原扫描，绝不瞎猜。
/// </summary>
public static class HubTileLocator
{
    /// <summary>磁贴标题（大字印在卡片下半部；描述句不是整词，不参与）。</summary>
    private static readonly string[] Titles = { "锦标赛", "活动", "我的联赛" };

    /// <summary>比赛页国际服板块从左到右的标准物理顺序（目标卡被滚出视野时用于估算跨屏位移）。</summary>
    private static readonly string[] CanonicalOrder = { "锦标赛", "活动", "eFootball联赛", "我的联赛" };

    /// <summary>
    /// 目标磁贴在磁贴行的序号（0 起，按屏幕从左到右）。读不全/找不到返回 null。
    /// </summary>
    /// <param name="target">目标标题，如「活动」「eFootball联赛」（归一后的形式）。</param>
    public static int? FindTileOffset(OcrResult ocr, string target)
    {
        var tiles = CollectTiles(ocr);
        if (tiles.Count < 2) return null; // 读不全不猜格位
        tiles.Sort((a, b) => a.X.CompareTo(b.X));
        int idx = tiles.FindIndex(t => t.Title == target);
        return idx >= 0 ? idx : null;
    }

    /// <summary>
    /// 焦点是否确实落在目标磁贴上（v8.2.0 修复「标题可见≠光标选中」）：
    /// 游戏用**闪烁白框**包住选中卡（用户 2026-11-23 实机：卡片四周边框近白，
    /// 5~10s 闪一次、亮约 1s）。做法：按卡片边界采样**边框环**的近白像素——
    /// 8 张用户实机帧标定：聚焦卡边框 151~591，未聚焦卡 0~41（含闪烁灭相位/白框完全不亮帧）。
    /// 白框熄灭且聚焦卡图案不亮时（用户「焦点活动没闪」帧）全员 &lt;100 → 返回 null 让调用方继续等，
    /// 绝不在读不出时误判为「焦点在目标卡」。
    /// </summary>
    /// <returns>
    /// true = 焦点确认在 target 卡（可安全按 A）；
    /// false = 焦点明确在别的卡（应继续右移换焦点，绝不按 A）；
    /// null = 本帧读不出（白框全灭/卡面反光），调用方保持导航不误判。
    /// </returns>
    public static bool? IsFocusedOnTarget(OcrResult ocr, FrameData frame, string target)
    {
        var detected = DetectFocusedTiles(ocr, frame);
        return detected is null ? null : detected.Value.FocusedTitle == target;
    }

    /// <summary>
    /// 一次到位磁贴移动规划：同帧结合可见磁贴顺序与当前白框焦点所在卡，算出相对偏移量 Delta
    /// （省掉盲目「左×6」与逐格重跑 OCR）。目标卡在屏上时直接返回精确差值；被滚出视野时按
    /// 标准物理顺序计算方向与步数；白框灭相读不出时返回 null 交由调用方走回退扫描。
    /// </summary>
    public static HubTileMovePlan? PlanOneShotMove(OcrResult ocr, FrameData frame, string target)
    {
        var detected = DetectFocusedTiles(ocr, frame);
        if (detected is null) return null;
        var (orderedTitles, focusedIdx, focusedTitle) = detected.Value;
        int targetIdx = orderedTitles.FindIndex(t => t == target);
        if (targetIdx >= 0)
            return new HubTileMovePlan(targetIdx - focusedIdx, TargetVisible: true, focusedTitle);

        int curCanonical = Array.IndexOf(CanonicalOrder, focusedTitle);
        int dstCanonical = Array.IndexOf(CanonicalOrder, target);
        if (curCanonical >= 0 && dstCanonical >= 0)
            return new HubTileMovePlan(dstCanonical - curCanonical, TargetVisible: false, focusedTitle);

        return null;
    }

    private static (List<string> OrderedTitles, int FocusedIdx, string FocusedTitle)? DetectFocusedTiles(
        OcrResult ocr, FrameData frame)
    {
        var tiles = CollectTilesForFocus(ocr);
        if (tiles.Count < 2 || frame.Width < 100 || frame.Height < 100) return null;

        var sorted = tiles.OrderBy(t => t.X).ToList();
        double[][] slots =
        {
            new[] { 0.050, 0.340 },  // 第 1 格（锦标赛消失后是活动）
            new[] { 0.355, 0.645 },  // 第 2 格
            new[] { 0.650, 0.950 },  // 第 3 格
        };
        const double cardTop = 0.22, cardBottom = 0.78;

        var scores = new List<(string Title, int White)>();
        for (int i = 0; i < slots.Length; i++)
        {
            double x0 = slots[i][0], x1 = slots[i][1];
            var inSlot = sorted.Where(t => t.X >= x0 && t.X < x1).ToList();
            if (inSlot.Count == 0) continue;
            int white = CountWhiteRing(frame, x0, x1, cardTop, cardBottom, 0.005);
            scores.Add((inSlot[0].Title, white));
        }
        if (scores.Count == 0) return null;

        int bestIdx = 0;
        for (int i = 1; i < scores.Count; i++)
            if (scores[i].White > scores[bestIdx].White) bestIdx = i;

        var best = scores[bestIdx];
        int restMax = scores.Where((_, i) => i != bestIdx).Select(s => s.White)
                            .DefaultIfEmpty(0).Max();
        if (best.White < FocusRingMin || best.White < restMax * 2)
            return null;
        return (scores.Select(s => s.Title).ToList(), bestIdx, best.Title);
    }

    /// <summary>卡片矩形边框环上的近白像素数（r/g/b 都高且彼此接近 = 白色亮边）。</summary>
    private static int CountWhiteRing(FrameData f, double x0, double x1, double y0, double y1, double ring)
    {
        if (f.Width <= 0 || f.Height <= 0 || f.Bgra.Length < f.Width * f.Height * 4)
            return 0;
        int count = 0;
        int xa = Math.Clamp((int)(f.Width * x0), 0, f.Width);
        int xb = Math.Clamp((int)(f.Width * x1), 0, f.Width);
        int ya = Math.Clamp((int)(f.Height * y0), 0, f.Height);
        int yb = Math.Clamp((int)(f.Height * y1), 0, f.Height);
        int rx = Math.Max(1, (int)(f.Width * ring));
        int ry = Math.Max(1, (int)(f.Height * ring));
        for (int y = ya; y < yb; y++)
            for (int x = xa; x < xb; x++)
            {
                bool onRing = x < xa + rx || x >= xb - rx || y < ya + ry || y >= yb - ry;
                if (!onRing) continue;
                int i = (y * f.Width + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx >= 215 && mx - mn <= 35) count++;
            }
        return count;
    }

    /// <summary>焦点检测专用标题收集：与 CollectTiles 同样归一标题，但 y 下限放宽到 0.30
    /// （聚焦卡滚入后标题大字在卡面中部 y≈0.37，常规 CollectTiles 的 0.40 下限会漏掉）。</summary>
    private static List<(double X, string Title)> CollectTilesForFocus(OcrResult ocr)
    {
        var tiles = new List<(double X, string Title)>();
        foreach (var w in ocr.Words)
        {
            if (w.CenterY is < 0.30 or > 0.90 || w.CenterX > 0.99) continue;
            string? title = NormalizeTitle(w.NormText);
            if (title is null) continue;
            if (tiles.Any(t => Math.Abs(t.X - w.CenterX) < 0.02)) continue;
            tiles.Add((w.CenterX, title));
        }
        return tiles;
    }

    // 焦点白框阈值（8 张用户实机帧标定：聚焦卡边框环 151~591，未聚焦卡 0~41）
    private const int FocusRingMin = 100;

    /// <summary>
    /// 这一帧认出几枚磁贴标题。用途：判断"画面真的是比赛页磁贴行"——从别的页（如锦标赛子页）
    /// 按 B 退回时，单看分类可能仍判 MatchHub，但那一页只有一枚「锦标赛」标题，凑不出磁贴行。
    /// </summary>
    public static int VisibleTitleCount(OcrResult ocr) => CollectTiles(ocr).Count;

    /// <summary>
    /// 确认当前真的是多卡比赛页（至少读出 2 枚磁贴标题，或含「我的联赛」磁贴词），
    /// 避免单卡「锦标赛」子页被共享分类误判成 MatchHub 提前终止退回引导。
    /// </summary>
    public static bool IsRealMatchHub(MatchScreen screen, OcrResult ocr) =>
        screen == MatchScreen.MatchHub &&
        (VisibleTitleCount(ocr) >= 2 ||
         ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("我的联赛")));

    /// <summary>磁贴行里能认出的标题（x 升序未排序；同一格的碎词/合并整行只留第一次命中）。</summary>
    private static List<(double X, string Title)> CollectTiles(OcrResult ocr)
    {
        var tiles = new List<(double X, string Title)>();
        foreach (var w in ocr.Words)
        {
            if (w.CenterY is < 0.40 or > 0.90 || w.CenterX > 0.99) continue;
            string? title = NormalizeTitle(w.NormText);
            if (title is null) continue;
            // 同一格的碎词/合并整行只保留第一次命中
            if (tiles.Any(t => Math.Abs(t.X - w.CenterX) < 0.02)) continue;
            tiles.Add((w.CenterX, title));
        }
        return tiles;
    }

    /// <summary>磁贴标题归一：「eFootball™联赛」被拆写时也归为「eFootball联赛」。</summary>
    private static string? NormalizeTitle(string norm)
    {
        if (Titles.Contains(norm)) return norm;
        if (norm.Contains("eFootball", StringComparison.OrdinalIgnoreCase) && norm.Contains("联赛"))
            return "eFootball联赛";
        return null;
    }

    // ---------------- 主菜单底部磁贴行定位 ----------------
    /// <summary>主菜单底部卡片行的固定标签词（用户实机 2026-11-23 截图：
    /// 比赛 / 我的球队 / 合约 / 任务 / 每日小游戏，横向排列，文字在图标下方）。</summary>
    private static readonly string[] MainMenuLabels =
        { "比赛", "我的球队", "合约", "任务", "每日小游戏" };

    /// <summary>
    /// 底部磁贴行是否已进入视野（宽松版，供「按需下移」迭代使用）：
    /// 在 y≈0.62~0.92 认出 ≥1 个固定标签即认为已到底部行，不必读全 5 个。
    /// 与严格版 FindMainMenuMatchOffset 的分工：本函数只判断"到了没"，格位仍交给严格版。
    /// </summary>
    public static bool IsMainMenuBottomRowVisible(OcrResult ocr)
    {
        foreach (var w in ocr.Words)
        {
            if (w.CenterY is < 0.62 or > 0.92) continue;
            string norm = w.NormText.Replace(" ", "");
            if (MainMenuLabels.Any(l => norm == l)) return true;
        }
        return false;
    }

    /// <summary>
    /// 主菜单「比赛」磁贴在底部卡片行的格位（0 起，按屏幕从左到右）。
    /// 只在底部行 y≈0.62~0.92 且认出 ≥3 个不同标签时才可信；否则返回 null 让调用方
    /// 回退原「下×2→左×6」饱和归位——主菜单标签 OCR 偶发读不全，绝不瞎猜格位。
    /// </summary>
    public static int? FindMainMenuMatchOffset(OcrResult ocr)
    {
        var tiles = new List<(double X, string Label)>();
        foreach (var w in ocr.Words)
        {
            // 底部卡片行：标签文字在图标下方，实机 y≈0.75~0.88；放宽到 0.62~0.92
            if (w.CenterY is < 0.62 or > 0.92) continue;
            string norm = w.NormText.Replace(" ", "");
            // 精确匹配固定标签（"比赛"须整词，避免"比赛记录/前往比赛"混入——那些 y 更高）
            string? label = MainMenuLabels.FirstOrDefault(l => norm == l);
            if (label is null) continue;
            if (tiles.Any(t => Math.Abs(t.X - w.CenterX) < 0.02)) continue;
            tiles.Add((w.CenterX, label));
        }
        // 交叉验证：至少认出 3 个不同标签，才认为这帧的底部行读全了
        if (tiles.Select(t => t.Label).Distinct().Count() < 3) return null;
        tiles.Sort((a, b) => a.X.CompareTo(b.X));
        int idx = tiles.FindIndex(t => t.Label == "比赛");
        return idx >= 0 ? idx : null;
    }
}
