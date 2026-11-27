using System.Runtime.InteropServices;
using System.Text;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Modules;
using EfootballBot.Core.Bot.Sampling;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.Sampling;

/// <summary>
/// 手动操作智能采样场景（不接管手柄、不自动点击、不隐藏窗口）：
/// 1) 监听用户物理手柄（PadBackendProbe）与游戏前台键盘按键上升沿；
/// 2) 当检测到用户按键或界面切换时，自动抓取当前/落地界面，经过黑屏/重复/反标清洗后落盘；
/// 3) 记录「来源页 --[操作键]--> 目标页」路径与未知页提示词，停止时生成 navigation_graph.md。
/// </summary>
public sealed class ManualSampleScenario : IScenario, IModuleLifecycle
{
    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    private readonly bool _isCn;

    private ushort _lastPadButtons;
    private bool _lastPadLt;
    private bool _lastPadRt;
    private readonly HashSet<int> _lastKeysDown = new();

    private string? _currentPageId;
    private HashSet<string>? _lastSampledOcrWords;
    private byte[]? _lastSampledGrid;
    private (string FromPage, string ActionLabel, long Tick)? _pendingAction;
    private long _lastObserveTick;
    private int _followUpStage; // 0=无, 1=待600ms追拍, 2=待1400ms追拍
    private bool _stoppedHandled;

    private static readonly (ushort Mask, string Label)[] PadWatch =
    {
        (0x1000, "Pad:A"), (0x2000, "Pad:B"), (0x4000, "Pad:X"), (0x8000, "Pad:Y"),
        (0x0001, "Pad:Up"), (0x0002, "Pad:Down"), (0x0004, "Pad:Left"), (0x0008, "Pad:Right"),
        (0x0010, "Pad:Start"), (0x0020, "Pad:Back"),
        (0x0100, "Pad:LB"), (0x0200, "Pad:RB"),
    };

    private static readonly (int Vk, string Label)[] KeyWatch =
    {
        (0x0D, "Key:Enter"), (0x1B, "Key:Esc"), (0x20, "Key:Space"), (0x08, "Key:Backspace"),
        (0x26, "Key:Up"), (0x28, "Key:Down"), (0x25, "Key:Left"), (0x27, "Key:Right"),
        (0x57, "Key:W"), (0x53, "Key:S"), (0x41, "Key:A"), (0x44, "Key:D"),
        (0x09, "Key:Tab"), (0x51, "Key:Q"), (0x45, "Key:E"),
        (0x5A, "Key:Z"), (0x58, "Key:X"), (0x43, "Key:C"),
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public string Name => _isCn ? "国服手动采样" : "国际服手动采样";

    public ManualSampleScenario(BotEngine engine, Action<string, LogLevel> log, bool isCn)
    {
        _engine = engine;
        _log = log;
        _isCn = isCn;
        _log($"[手动采样] 已就绪（{(_isCn ? "国服" : "国际服")}）：不接管手柄、不自动点击，请正常手动操作游戏，样本与路径将自动保存至 data\\samples\\", LogLevel.Success);
    }

    public void OnGameRestarted(bool success)
    {
        _currentPageId = null;
        _lastSampledOcrWords = null;
        _lastSampledGrid = null;
        _pendingAction = null;
        _followUpStage = 0;
    }

    public void OnModuleStopped()
    {
        if (_stoppedHandled) return;
        _stoppedHandled = true;
        var w = _engine.SampleWriter;
        w.WriteNavigationGraph();
        _log($"[手动采样] 本次采样结束：保留 {w.SavedCount} 帧（剔除黑屏 {w.FilteredBlackCount} / 重复 {w.FilteredDuplicateCount} / 反标干扰 {w.FilteredMislabelCount}），发现未知页 {w.UnknownPageCount} 类，记录路径 {w.TransitionCount} 条。目录：{w.RootDir}", LogLevel.Success);
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        long now = Environment.TickCount64;
        string? inputAction = PollUserInput();

        if (inputAction is not null)
        {
            string from = _currentPageId ?? "Start";
            _pendingAction = (from, inputAction, now);
            _followUpStage = 1;
            _log($"[手动采样] 捕获操作：{inputAction}（当前页：{from}）", LogLevel.Debug);
        }

        bool shouldObserve = false;
        bool forceOcr = false;
        if (_followUpStage == 1 && _pendingAction is { } p1 && now - p1.Tick >= 600)
        {
            _followUpStage = 2;
            shouldObserve = true;
            forceOcr = true;
        }
        else if (_followUpStage == 2 && _pendingAction is { } p2 && now - p2.Tick >= 1400)
        {
            _followUpStage = 0;
            shouldObserve = true;
            forceOcr = true;
        }
        else if (now - _lastObserveTick >= 400)
        {
            shouldObserve = true;
        }

        if (!shouldObserve)
            return 100;

        _lastObserveTick = now;
        var obs = await _engine.ObserveAsync(ct, forceOcr: forceOcr);
        var screen = MatchBrain.Classify(obs.Detections, obs.Ocr,
            ScreenProbe.CornerHasContent(obs.Frame),
            allowCornerInMatch: false,
            ScreenProbe.IsMostlyLightPage(obs.Frame),
            pitchLike: ScreenProbe.LooksLikePitch(obs.Frame));
        var currGrid = FrameQualityFilter.ComputeLumaGrid(obs.Frame);
        var focusShift = FrameQualityFilter.DetectFocusShift(_lastSampledGrid, currGrid, obs.Ocr);
        var decision = OcrSampleTrigger.Evaluate(
            obs.Ocr,
            obs.Detections,
            screen,
            _isCn,
            _currentPageId,
            _lastSampledOcrWords,
            _pendingAction is not null,
            focusShift);

        if (!decision.ShouldSample)
        {
            if (_followUpStage == 0 && _pendingAction is { } skipped && now - skipped.Tick >= 1400)
                _pendingAction = null;
            return 100;
        }

        string targetPageId = decision.PageId;
        string? transitionDesc = null;
        bool pageChanged = _currentPageId is not null && !string.Equals(_currentPageId, targetPageId, StringComparison.Ordinal);
        // 跨页切换本身会造成全屏亮度网格剧变，不能把跨页帧算成页内光标移动
        var effectiveFocusShift = pageChanged ? new FocusShiftResult(false, 0, 0, 0, 0, "", null, 0) : focusShift;

        if (_pendingAction is { } pending && (pageChanged || effectiveFocusShift.Moved || _followUpStage == 0))
        {
            transitionDesc = $"{pending.FromPage} --[{pending.ActionLabel}]--> {targetPageId}";
        }
        else if (pageChanged)
        {
            transitionDesc = $"{_currentPageId} --[OCR/Auto]--> {targetPageId}";
        }

        string sampleNote = effectiveFocusShift.Moved
            ? $"光标移动({effectiveFocusShift.Direction}){(effectiveFocusShift.FocusedText is { } ft ? $"→「{ft}」" : "")}"
            : (_pendingAction is { } pa
                ? $"操作 {pa.ActionLabel} 后 OCR 自动采样（{decision.Reason}）"
                : $"OCR 识别自动采样（{decision.Reason}）");

        var res = _engine.SampleWriter.TryRecordFrame(
            obs.Frame,
            obs.Ocr,
            obs.Detections,
            screen,
            _isCn,
            sourceMode: "manual_ocr",
            pageTagOverride: decision.PageTagOverride,
            transitionSummary: transitionDesc,
            note: sampleNote,
            focusShift: effectiveFocusShift);

        if (res.Saved)
        {
            _lastSampledOcrWords = decision.CurrentWords;
            _lastSampledGrid = currGrid;
            if (transitionDesc is not null)
            {
                if (_pendingAction is { } p)
                {
                    // Stage 1 仅在确实发生页面切换或光标移动时才消费挂起操作；若 600ms 仍在过渡中则留给 Stage 2 (1400ms) 捕获
                    if (pageChanged || effectiveFocusShift.Moved || _followUpStage == 0)
                    {
                        _engine.SampleWriter.RecordTransition(p.FromPage, p.ActionLabel, res.PageId, screen == MatchScreen.Unknown, obs.Frame.Width, obs.Frame.Height, effectiveFocusShift);
                        _pendingAction = null;
                        _followUpStage = 0;
                    }
                }
                else if (_currentPageId is not null)
                {
                    _engine.SampleWriter.RecordTransition(_currentPageId, "OCR/Auto", res.PageId, screen == MatchScreen.Unknown, obs.Frame.Width, obs.Frame.Height, effectiveFocusShift);
                }
            }
            else if (effectiveFocusShift.Moved && !pageChanged)
            {
                _engine.SampleWriter.RecordTransition(res.PageId, $"Cursor:{effectiveFocusShift.Direction}", res.PageId, screen == MatchScreen.Unknown, obs.Frame.Width, obs.Frame.Height, effectiveFocusShift);
            }

            if (res.Bucket == "unknown")
            {
                _log($"[手动采样] OCR 发现未知页 {res.PageId}（{decision.Reason}，{obs.Frame.Width}x{obs.Frame.Height}），已生成提示词与无损样本（累计 {_engine.SampleWriter.SavedCount} 帧）", LogLevel.Info);
            }
            else if (effectiveFocusShift.Moved)
            {
                string focusDesc = effectiveFocusShift.FocusedText is { } ftxt ? $"「{ftxt}」" : $"({effectiveFocusShift.NewX:F2},{effectiveFocusShift.NewY:F2})";
                _log($"[手动采样] 光标移动({effectiveFocusShift.Direction}) → {focusDesc}，已采样 [{res.Bucket}] {res.PageId}（累计 {_engine.SampleWriter.SavedCount} 帧）", LogLevel.Info);
            }
            else
            {
                _log($"[手动采样] OCR 自动采样 [{res.Bucket}] {res.PageId}（{decision.Reason}，{obs.Frame.Width}x{obs.Frame.Height}，累计 {_engine.SampleWriter.SavedCount} 帧）", LogLevel.Debug);
            }
        }

        // 仅在样本保留，或未落盘但未触发过滤（非黑屏/反标过滤）时推进当前页，防止 filtered 返回的 "Unknown" 污染当前页状态
        if (res.Saved || (pageChanged && res.Bucket != "filtered"))
            _currentPageId = res.PageId;

        if (_followUpStage == 0 && _pendingAction is { } done && now - done.Tick >= 1400)
            _pendingAction = null;

        return 100;
    }

    private string? PollUserInput()
    {
        // ① 物理手柄上升沿（无需窗口必须在前台，手柄即用户操作）
        ushort currentButtons = 0;
        bool ltNow = false;
        bool rtNow = false;
        for (int slot = 0; slot < 4; slot++)
        {
            var state = PadBackendProbe.TryRead(slot);
            if (state is null) continue;
            if (state.Value.wButtons == 0 && state.Value.bLeftTrigger < 128 && state.Value.bRightTrigger < 128)
                continue;
            currentButtons = state.Value.wButtons;
            ltNow = state.Value.bLeftTrigger >= 128;
            rtNow = state.Value.bRightTrigger >= 128;
            break;
        }

        ushort rising = (ushort)(currentButtons & ~_lastPadButtons);
        bool ltRising = ltNow && !_lastPadLt;
        bool rtRising = rtNow && !_lastPadRt;
        _lastPadButtons = currentButtons;
        _lastPadLt = ltNow;
        _lastPadRt = rtNow;

        if (rtRising) return "Pad:RT";
        if (ltRising) return "Pad:LT";
        foreach (var (mask, label) in PadWatch)
            if ((rising & mask) != 0) return label;

        // ② 前台游戏窗口键盘上升沿
        if (!GameIsForeground())
        {
            _lastKeysDown.Clear();
            return null;
        }

        string? keyHit = null;
        foreach (var (vk, label) in KeyWatch)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down)
            {
                if (_lastKeysDown.Add(vk) && keyHit is null)
                    keyHit = label;
            }
            else
            {
                _lastKeysDown.Remove(vk);
            }
        }
        return keyHit;
    }

    private static bool GameIsForeground()
    {
        var sb = new StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        string title = sb.ToString();
        return title.Contains("eFootball", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("实况足球在线", StringComparison.OrdinalIgnoreCase);
    }
}
