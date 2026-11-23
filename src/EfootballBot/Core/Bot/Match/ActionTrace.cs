// ===========================================================================
// 架构锁 · 共享层 Core/Bot/Match（v7.2）
// 本层职责：所有模块共用的能力 —— 界面分类、共享决策、赛中匹配、流程记忆、教学轨迹。
// 教学轨迹只存「状态签名 + 动作」，不存截图像素、不录键盘文本、不联网。
// ===========================================================================
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using System.Text.Json;

namespace EfootballBot.Core.Bot.Match;

/// <summary>一帧的状态签名：OCR 归一化词集合 + 各类别 YOLO 命中数（不含像素）。</summary>
public sealed record TraceSignature(string[] Words, Dictionary<string, int> YoloCounts)
{
    /// <summary>归一化后只保留长度≥2 的词，去掉数字/标点噪声，最多 60 个，排序后去重。</summary>
    public static TraceSignature Capture(OcrResult ocr, IReadOnlyList<YoloDetection> dets)
    {
        var words = ocr.Words
            .Select(w => OcrText.Norm(w.Text))
            .Where(t => t.Length >= 2)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .Take(60)
            .ToArray();
        var counts = dets.Where(d => d.Score >= MatchBrain.MinScore)
            .GroupBy(d => d.ClassName)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new TraceSignature(words, counts);
    }

    /// <summary>0~1 相似度：OCR 词 Jaccard 占 0.8，YOLO 类别计数一致性占 0.2。</summary>
    public double SimilarityTo(TraceSignature other)
    {
        var a = Words.ToHashSet(StringComparer.Ordinal);
        var b = other.Words.ToHashSet(StringComparer.Ordinal);
        double jaccard = a.Count == 0 && b.Count == 0
            ? 1.0
            : (double)a.Intersect(b, StringComparer.Ordinal).Count() /
              Math.Max(1, a.Union(b, StringComparer.Ordinal).Count());

        var keys = YoloCounts.Keys.Union(other.YoloCounts.Keys, StringComparer.Ordinal).ToList();
        double yolo = keys.Count == 0
            ? 1.0
            : (double)keys.Count(k => YoloCounts.GetValueOrDefault(k) == other.YoloCounts.GetValueOrDefault(k)) /
              keys.Count;
        return 0.8 * jaccard + 0.2 * yolo;
    }
}

/// <summary>教学轨迹里的一步：当时画面 + 用户按了什么 + 距上一步多久。</summary>
public sealed record TraceStep(int Index, TraceSignature Signature, string Button, int DelayMs);

/// <summary>一条可保存/加载的教学轨迹。</summary>
public sealed record ActionTrace(string Name, DateTime CreatedAt, List<TraceStep> Steps)
{
    /// <summary>轨迹统一放 data\traces\（不入库、不上传）。</summary>
    public static string TracesDir => global::System.IO.Path.Combine(
        EfootballBot.Core.System.AppPaths.DataDir, "traces");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>只允许安全文件名，防路径穿越。</summary>
    private static string SafePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))
            throw new ArgumentException("轨迹名只允许字母/数字/下划线/连字符", nameof(name));
        return global::System.IO.Path.Combine(TracesDir, name + ".json");
    }

    public static ActionTrace Create(string name)
        => new(name, DateTime.Now, new List<TraceStep>());

    public void Add(TraceSignature sig, string button, int delayMs)
        => Steps.Add(new TraceStep(Steps.Count, sig, button, delayMs));

    public void Save()
    {
        global::System.IO.Directory.CreateDirectory(TracesDir);
        global::System.IO.File.WriteAllText(SafePath(Name), JsonSerializer.Serialize(this, JsonOptions));
    }

    public static ActionTrace? Load(string name)
    {
        string path = SafePath(name);
        return global::System.IO.File.Exists(path)
            ? JsonSerializer.Deserialize<ActionTrace>(global::System.IO.File.ReadAllText(path), JsonOptions)
            : null;
    }

    public static IReadOnlyList<string> ListNames()
    {
        if (!global::System.IO.Directory.Exists(TracesDir)) return Array.Empty<string>();
        return global::System.IO.Directory.GetFiles(TracesDir, "*.json")
            .Select(global::System.IO.Path.GetFileNameWithoutExtension)
            .Where(n => n is not null).Select(n => n!).OrderBy(n => n).ToArray();
    }
}
