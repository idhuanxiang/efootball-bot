using System.Security.Cryptography;
using System.Text;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Sampling;

/// <summary>
/// 未知页面聚类指纹与结构化提示词生成器：
/// 同一类未知界面按顶部/核心 OCR 关键词聚类到同一个文件夹，并生成带分辨率、
/// 跳转来源、区域 OCR 原文与 YOLO 检测状况的 prompt.md，方便直接标注与扩展导航路径。
/// </summary>
public static class UnknownPagePromptBuilder
{
    /// <summary>
    /// 为未知界面生成稳定聚类 ID（格式：unk_cn_xxxxxx 或 unk_intl_xxxxxx）。
    /// </summary>
    public static string BuildUnknownId(OcrResult ocr, bool isCn)
    {
        string prefix = isCn ? "unk_cn_" : "unk_intl_";
        var topTokens = ocr.Words
            .Where(w => w.CenterY <= 0.35 || w.CenterY >= 0.75)
            .Select(w => OcrText.Norm(w.Text))
            .Where(t => t.Length >= 2 && !t.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (topTokens.Length == 0)
        {
            topTokens = ocr.Words
                .Select(w => OcrText.Norm(w.Text))
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal)
                .Take(6)
                .ToArray();
        }

        string seed = topTokens.Length > 0 ? string.Join("|", topTokens) : $"empty_{ocr.Words.Count}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        string hex = Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
        return prefix + hex;
    }

    /// <summary>
    /// 生成未知页面的结构化提示词文档（prompt.md）。
    /// </summary>
    public static string BuildPromptMarkdown(
        string unknownId,
        bool isCn,
        int width,
        int height,
        string sourceMode,
        string? transitionSummary,
        OcrResult ocr,
        IReadOnlyList<YoloDetection> dets,
        LabelAuditResult audit)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 未知页面提示词：{unknownId}");
        sb.AppendLine();
        sb.AppendLine($"- **服区**：{(isCn ? "国服 (eFootballOnline)" : "国际服 (eFootball)")}");
        sb.AppendLine($"- **原始分辨率**：`{width} x {height}`（YOLO 标注已按归一化坐标适配）");
        sb.AppendLine($"- **采集模式**：{sourceMode}");
        if (!string.IsNullOrWhiteSpace(transitionSummary))
            sb.AppendLine($"- **导航来源路径**：`{transitionSummary}`");
        sb.AppendLine($"- **记录时间**：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("## 1. 画面分区 OCR 文本摘要");
        AppendZone(sb, "顶部区域 (Y < 0.30)", ocr.Words.Where(w => w.CenterY < 0.30));
        AppendZone(sb, "中部区域 (0.30 <= Y <= 0.72)", ocr.Words.Where(w => w.CenterY is >= 0.30 and <= 0.72));
        AppendZone(sb, "底部区域 (Y > 0.72)", ocr.Words.Where(w => w.CenterY > 0.72));
        sb.AppendLine();

        sb.AppendLine("## 2. YOLO 检测与反标清洗摘要");
        if (dets.Count == 0)
        {
            sb.AppendLine("- 无 YOLO 检出框。");
        }
        else
        {
            foreach (var d in dets.Where(x => x.Score >= AutoLabelGuard.ReviewThreshold))
                sb.AppendLine($"- `{d.ClassName}` conf={d.Score:F2} cx={d.CenterX:F3} cy={d.CenterY:F3} w={d.W:F3} h={d.H:F3}");
        }
        if (audit.RejectedBoxes.Count > 0)
        {
            sb.AppendLine("- **已拦截反标干扰框**：");
            foreach (var r in audit.RejectedBoxes)
                sb.AppendLine($"  - `{r.ClassName}` conf={r.Score:F2} reason=`{r.Reason}`");
        }
        sb.AppendLine();

        sb.AppendLine("## 3. 人工/模型标注与路径扩展提示词");
        sb.AppendLine("```text");
        sb.AppendLine($"[页面ID] {unknownId}");
        sb.AppendLine("[页面名称] 请填写该界面在游戏中的中文名（如：赛季奖励弹窗 / 阵容调整确认页）");
        sb.AppendLine("[核心特征词] 请从上方分区 OCR 中挑选 2~3 个稳定必现词");
        sb.AppendLine("[建议操作键] 请填写离开或推进该界面的按键（如：A确认 / B返回 / RT推进 / 方向键移动）");
        sb.AppendLine("[目标跳转页] 请填写按下建议按键后会进入的页面");
        sb.AppendLine("[YOLO标注要点] 若画面含 btn_confirm / btn_back / player_card / coin_bar / main_menu / item_row 请补充矩形框；右下角过场「跳过」不要标为 btn_back");
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static void AppendZone(StringBuilder sb, string title, IEnumerable<OcrWord> words)
    {
        var list = words
            .OrderBy(w => w.Y)
            .ThenBy(w => w.X)
            .Select(w => $"{w.Text}({w.Confidence:F2})")
            .Take(20)
            .ToArray();
        sb.AppendLine($"- **{title}**：{(list.Length == 0 ? "（无文字）" : string.Join(" | ", list))}");
    }
}
