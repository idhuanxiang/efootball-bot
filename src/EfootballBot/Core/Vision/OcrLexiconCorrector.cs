namespace EfootballBot.Core.Vision;

/// <summary>
/// 固定 UI 词表低置信度容错纠错（零日常开销）：
/// 1) 正常高置信度词（&gt;= 0.85）、短词（&lt;3 字）、过长词（&gt;8 字）或含 ASCII 字母/数字词直接 O(1) 原样返回；
/// 2) 仅对低置信度纯中文 UI 词做同长度汉明距离 == 1 的唯一词表匹配，存在多候选歧义时不猜测。
/// </summary>
public static class OcrLexiconCorrector
{
    public const float HighConfidenceCutoff = 0.85f;

    private static readonly string[] Lexicon =
    {
        "前往比赛", "下一场比赛", "赛事等级", "活动设置", "比赛计划",
        "自动控制", "弹性自动控制", "自动配置球员", "重视能力值",
        "球员卡限制", "无法进行比赛", "活动已完成", "挑战清单",
        "比赛历史", "横幅设置", "强力抢断", "对阵玩家", "巡回活动",
        "阵容挑战", "我的联赛", "关键比赛", "经验值", "主要奖励",
        "总活动积分", "胜场积分", "球员加分", "实时更新评价",
        "球员详细数据", "自定义锦标赛", "快速配对", "好友对战",
        "友谊赛", "下一步", "进入比赛", "选择地区", "选择球队",
        "活动列表", "比赛记录", "球队管理", "收件箱", "随机位置代币",
        "选择技巧代币", "随机加成代币",
    };

    private static readonly HashSet<string> ExactSet = new(Lexicon, StringComparer.Ordinal);

    /// <summary>绝对禁止被纠错改写的真实反义/邻近 UI 词（如「活动未完成」不能被改成「活动已完成」）。</summary>
    private static readonly HashSet<string> NeverRewrite = new(StringComparer.Ordinal)
    {
        "上一步", "活动未完成", "上一场比赛", "手动控制", "手动配置球员",
        "选择球员", "无法进入比赛", "取消比赛", "退出比赛",
    };

    private static readonly Dictionary<int, string[]> ByLength = Lexicon
        .GroupBy(s => s.Length)
        .ToDictionary(g => g.Key, g => g.ToArray());

    /// <summary>
    /// 对单个低置信度中文词做同长度 1 字容错纠错；高置信度、短词(&lt;4字)、含英文数字、精确命中或反义词黑名单时直接原样返回。
    /// </summary>
    public static string Correct(string normalizedText, float confidence)
    {
        if (confidence >= HighConfidenceCutoff) return normalizedText;
        if (string.IsNullOrEmpty(normalizedText)) return normalizedText;
        int len = normalizedText.Length;
        if (len < 4 || len > 8) return normalizedText;
        if (ExactSet.Contains(normalizedText) || NeverRewrite.Contains(normalizedText)) return normalizedText;

        for (int i = 0; i < len; i++)
        {
            if (normalizedText[i] < 0x4E00 || normalizedText[i] > 0x9FFF)
                return normalizedText;
        }

        if (!ByLength.TryGetValue(len, out var candidates))
            return normalizedText;

        string? match = null;
        foreach (var cand in candidates)
        {
            int diff = 0;
            for (int i = 0; i < len; i++)
            {
                if (normalizedText[i] != cand[i] && ++diff > 1)
                    break;
            }
            if (diff == 1)
            {
                if (match is not null) return normalizedText; // 歧义：不猜测
                match = cand;
            }
        }
        return match ?? normalizedText;
    }

    /// <summary>返回纠正后的词文本（若无需纠错则返回原 <see cref="OcrWord.Text"/>）。</summary>
    public static string CorrectRaw(string rawText, double confidence)
    {
        if (confidence >= HighConfidenceCutoff || string.IsNullOrEmpty(rawText))
            return rawText;
        string norm = OcrText.Norm(rawText);
        string fixedNorm = Correct(norm, (float)confidence);
        return string.Equals(fixedNorm, norm, StringComparison.Ordinal) ? rawText : fixedNorm;
    }
}
