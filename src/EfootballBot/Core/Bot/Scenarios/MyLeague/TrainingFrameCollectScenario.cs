using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

// ===========================================================================
// 架构锁 · 模块层 Core/Bot/Scenarios（v7.2）
// 本场景只借用共享层 MatchBrain / HubTileLocator 的判据与 Pad 的按键，
// 不引用任何别的 *Scenario 类型，符合「模块之间不许互相引用」约束。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>
/// 训练帧主动采集场景（v8.2.1，开发者选项专用）：
/// 从当前页收敛回主菜单，然后按现有导航逻辑走遍模型最需要的几类主要页面；
/// **每页停留 10 秒**，期间约每秒抓 1 帧（画面签名去重，只留变化的帧），
/// 让同一页收集到不同时刻/焦点/动画状态的多张样本，全部采完自动 Stop。
///
/// 状态机（目标队列驱动，每个目标两阶段）：
///   ToHome（回主菜单）→ 对每个目标：[Navigate（导航到达）→ CaptureBatch（批量采集10s）] → Done
///   Navigate 按目标不同：主菜单=已在场直接抓、比赛页=从主菜单按 A、子页=比赛页点磁贴+焦点确认
///   CaptureBatch 结束一律 B 回比赛页（最后一个目标直接结束）
///
/// 导航复用共享能力：HubTileLocator.FindMainMenuMatchOffset / FindTileOffset /
/// IsFocusedOnTarget（白框焦点确认）+ Pad 饱和归位序列。抓图用 BotEngine.DumpTrainingFrame
/// （写 data\trainingframes\日期\，不受可疑帧开关/节流限制）。
///
/// 导航有超时/扫描预算，抓不到跳过下一个绝不卡死；总时限 15 分钟兜底。
/// </summary>
public sealed class TrainingFrameCollectScenario : IScenario
{
    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    private readonly long _startTick = Environment.TickCount64;
    private readonly HashSet<string> _done = new(StringComparer.Ordinal); // 已处理目标（成功/跳过）

    /// <summary>目标页：Tag=判据/抓图标记；Tile=比赛页要点的磁贴（null=无需点磁贴）。</summary>
    private static readonly (string Tag, string? Tile, string Label)[] Targets =
    {
        ("train_main_menu", null, "主菜单"),
        ("train_match_hub", null, "比赛选择页"),
        ("train_league_home", "我的联赛", "我的联赛主页"),
        ("train_tournament_cards", "锦标赛", "锦标赛卡片页"),
        ("train_weekly_cards", "活动", "周活动卡片页"),
    };

    private enum Phase { ToHome, Navigate, CaptureBatch, Done }
    private Phase _phase = Phase.ToHome;
    private int _targetIndex;          // 当前目标序号
    private int _rounds;               // 导航等待轮次
    private int _backAttempts;         // 回主页 B 次数
    private int _tileScanMoves;        // 磁贴逐格扫描步数

    // ---- 每页批量采集 ----
    private const int BatchDurationMs = 10_000;   // 每页停留 10 秒
    private const int BatchMinIntervalMs = 900;   // 两帧最小间隔（约 1 秒 1 张）
    private long _batchStartTick;
    private long _lastCaptureTick;
    private string? _lastFrameSignature;
    private int _batchFrames;
    private int _batchTotalFrames;                // 全部页累计抓帧数

    public string Name => "训练帧采集";

    public TrainingFrameCollectScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (_phase == Phase.Done) return 1000; // 防御重入

        // 总时限兜底：15 分钟（5 页 × 10 秒采集 + 导航余量）
        if (Environment.TickCount64 - _startTick > 15 * 60 * 1000)
        {
            Finish("达到 15 分钟总时限");
            return 1000;
        }

        var obs = await _engine.ObserveAsync(ct);
        var ocr = obs.Ocr;
        var dets = obs.Detections;
        var screen = MatchBrain.Classify(dets, ocr);
        bool isMainMenu = screen == MatchScreen.MainMenu || MatchBrain.HasMainMenuNavBar(ocr);
        bool isHub = screen == MatchScreen.MatchHub;

        switch (_phase)
        {
            // ---------- ① 收敛回主菜单 ----------
            case Phase.ToHome:
                if (isMainMenu)
                {
                    _log("[训练帧] 已回主菜单，开始逐页采集（每页停留 10 秒）", LogLevel.Success);
                    _targetIndex = 0;
                    _phase = Phase.Navigate;
                    _rounds = 0;
                    return 400;
                }
                if (++_backAttempts > 8)
                {
                    _log("[训练帧] B 退 8 次仍未回主菜单，重置预算继续（不会抓非主菜单帧）", LogLevel.Warn);
                    _engine.DumpSuspiciousFrame(obs.Frame, "train_home_stuck",
                        $"B 退 8 次仍未回主菜单（当前 {screen}）");
                    _backAttempts = 0;
                    return 1200;
                }
                await _engine.Pad.Back();
                return 2400;

            // ---------- ② 导航到当前目标页 ----------
            case Phase.Navigate:
                if (_targetIndex >= Targets.Length) { Finish("全部目标已处理"); return 1000; }
                var target = Targets[_targetIndex];
                string tag = target.Tag;

                if (tag == "train_main_menu")
                {
                    // 主菜单：已在主页（ToHome 保证），直接进批量采集
                    if (!isMainMenu) { _phase = Phase.ToHome; _backAttempts = 0; return 400; }
                    StartBatch();
                    return 0;
                }
                if (tag == "train_match_hub")
                {
                    if (isHub) { StartBatch(); return 0; }
                    // 从主菜单 → 下×5 → 定位「比赛」磁贴 → 左×6 → 右×offset → A
                    if (_rounds == 0)
                    {
                        await MatchActionExecutor.EnterMatchFromMainMenuAsync(_engine, obs, _log, "训练帧");
                    }
                    if (++_rounds > 10)
                    {
                        _log("[训练帧] 等比赛页超时，B 退回重导航", LogLevel.Warn);
                        await _engine.Pad.Back();
                        _phase = Phase.ToHome; _backAttempts = 0; _rounds = 0;
                        return 2400;
                    }
                    return 1800;
                }

                // 子页（我的联赛/锦标赛/周活动）：从比赛页点磁贴 + 白框焦点确认
                if (!isHub)
                {
                    // 不在比赛页：若已在目标子页就直接采集，否则 B 退回比赛页
                    var here = MatchBrain.CollectTrainingFrame(ocr, dets);
                    if (here is { } h && h.Tag == tag) { StartBatch(); return 0; }
                    await _engine.Pad.Back();
                    _rounds = 0;
                    return 2400;
                }
                int? tileOff = HubTileLocator.FindTileOffset(ocr, target.Tile!);
                if (tileOff is int to)
                {
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    await _engine.Pad.DpadRepeat(PadDir.Right, to);
                    await _engine.Pad.Confirm();
                    _log($"[训练帧] 比赛页 → {target.Label}（「{target.Tile}」第 {to + 1} 磁贴）", LogLevel.Info);
                    _rounds = 0;
                    return 2800;
                }
                // 标题读不到：归位最左后逐格右移切焦点
                if (_tileScanMoves == 0) await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                if (_tileScanMoves < 6)
                {
                    _tileScanMoves++;
                    await _engine.Pad.Dpad(PadDir.Right);
                    return 1500;
                }
                _log($"[训练帧] 未找到「{target.Tile}」磁贴，跳过 {target.Label}", LogLevel.Warn);
                _engine.DumpSuspiciousFrame(obs.Frame, "train_tile_not_found",
                    $"比赛页右移 6 格仍未读到「{target.Tile}」");
                _done.Add(tag);
                _targetIndex++;
                _tileScanMoves = 0; _rounds = 0;
                return 500;

            // ---------- ③ 批量采集当前页 10 秒 ----------
            case Phase.CaptureBatch:
                var cur = Targets[_targetIndex];
                long nowTick = Environment.TickCount64;
                // 每 ~1 秒抓一帧，且画面签名有变化（避免重复帧）
                if (nowTick - _lastCaptureTick >= BatchMinIntervalMs)
                {
                    string sig = FrameSignature(ocr, dets);
                    if (sig != _lastFrameSignature)
                    {
                        _engine.DumpTrainingFrame(obs.Frame, cur.Tag,
                            $"主动采集：{cur.Label}（{cur.Tag}）第 {_batchFrames + 1} 帧");
                        _lastFrameSignature = sig;
                        _lastCaptureTick = nowTick;
                        _batchFrames++;
                        _batchTotalFrames++;
                    }
                }
                if (nowTick - _batchStartTick >= BatchDurationMs)
                {
                    _log($"[训练帧] {cur.Label} 采集完成：{_batchFrames} 帧 / 10 秒", LogLevel.Success);
                    _done.Add(cur.Tag);
                    _targetIndex++;
                    _tileScanMoves = 0; _rounds = 0;
                    if (_targetIndex >= Targets.Length) { Finish("全部目标已采集"); return 1000; }
                    // 只有「子页采完→下一个子页」才需要 B 回比赛页；
                    // 主菜单采完→导航阶段自己按 A 进比赛页，比赛页采完→导航阶段直接点磁贴，
                    // 这两种都不能 B（B 会退回标题/离开比赛页，打乱导航）。
                    bool needBack = cur.Tile is not null && Targets[_targetIndex].Tile is not null;
                    _phase = Phase.Navigate;
                    if (needBack) { await _engine.Pad.Back(); return 2600; }
                    return 500;
                }
                return 900; // 约每秒一轮

            case Phase.Done:
                return 1000;
        }
        return 1000;
    }

    /// <summary>进入批量采集：重置本页计数与计时。</summary>
    private void StartBatch()
    {
        _phase = Phase.CaptureBatch;
        _batchStartTick = Environment.TickCount64;
        _lastCaptureTick = 0;              // 保证第一帧立即抓
        _lastFrameSignature = null;
        _batchFrames = 0;
        _rounds = 0;
        var t = Targets[_targetIndex];
        _log($"[训练帧] 到达 {t.Label}，开始 10 秒批量采集…", LogLevel.Info);
    }

    /// <summary>画面签名：OCR 词数 + 检测类别计数 + 前几个框的量化坐标。有变化才抓，避免重复帧。</summary>
    private static string FrameSignature(OcrResult ocr, IReadOnlyList<YoloDetection> dets)
    {
        var counts = dets.Where(d => d.Score >= MatchBrain.MinScore)
                         .GroupBy(d => d.ClassName)
                         .OrderBy(g => g.Key)
                         .Select(g => $"{g.Key}{g.Count()}");
        var boxes = dets.Where(d => d.Score >= MatchBrain.MinScore).Take(6)
                        .Select(d => $"{(int)(d.X * 20)},{(int)(d.Y * 20)}");
        return $"{ocr.Words.Count}|{string.Join(",", counts)}|{string.Join(";", boxes)}";
    }

    private void Finish(string reason)
    {
        var got = string.Join("、", Targets.Where(t => _done.Contains(t.Tag)).Select(t => t.Label));
        _log($"[训练帧] 采集结束（{reason}）：处理 [{(_done.Count == 0 ? "无" : got)}]" +
             $" {_done.Count}/{Targets.Length}，共抓 {_batchTotalFrames} 帧。" +
             "已保存到 data\\trainingframes\\日期\\", LogLevel.Success);
        _phase = Phase.Done;
        _engine.Stop(); // 场景自然结束，GUI 的 ScenarioRunning 轮询会自动复位按钮
    }
}
