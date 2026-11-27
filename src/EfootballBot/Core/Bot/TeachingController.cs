using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using System.Runtime.InteropServices;
using System.Text;

namespace EfootballBot.Core.Bot;

/// <summary>教学/复现模式。默认 Off；只有用户在配置里显式开启才生效。</summary>
public enum TeachingMode
{
    Off,
    Record,
    ReplaySequential,
    ReplayRuleBook,
}

/// <summary>
/// 自学习第一步：录制用户手柄操作 → 状态签名 → 门控复现；第二步的规则库用
/// <see cref="TeachingMode.ReplayRuleBook"/>（任意步匹配，而不是严格按顺序）。
///
/// 隐私与安全门控（用户 2026-11-23 明确授权的范围）：
///   · 默认关闭，必须显式开启；只监听物理手柄的白名单按键，不碰键盘/不录任何文本；
///   · 录制只在游戏窗口处于前台时生效（GetForegroundWindow + 标题含 eFootball）；
///   · 轨迹只存「OCR 词 + YOLO 计数 + 按键」到 data\traces\，不存像素、不联网、不入库；
///   · 复现时画面不匹配就停手，绝不盲按；连续多次不匹配自动退出，交回正常挂机；
///   · 任何时刻原有的全局暂停/停止热键都能中断。
/// </summary>
public sealed class TeachingController
{
    // XInput 白名单：方向键 / Start / 肩键 / A B X Y。其余位（Back、摇杆按下、扳机）一律不录。
    private const ushort WhiteMask = 0x000F | 0x0010 | 0x0100 | 0x0200 | 0xF000;

    private static readonly (ushort Mask, PadButton Button)[] Priority =
    {
        (0x1000, PadButton.A), (0x2000, PadButton.B), (0x4000, PadButton.X), (0x8000, PadButton.Y),
        (0x0001, PadButton.DpadUp), (0x0002, PadButton.DpadDown),
        (0x0004, PadButton.DpadLeft), (0x0008, PadButton.DpadRight),
        (0x0010, PadButton.Start), (0x0100, PadButton.LeftShoulder), (0x0200, PadButton.RightShoulder),
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private readonly Action<string, LogLevel> _log;
    private readonly double _threshold;
    private readonly int _maxMisses;
    private readonly bool _enabled;
    private readonly string _traceName;
    private ActionTrace? _trace;
    private ushort _lastButtons;
    private bool _lastRightTrigger;
    private DateTime _lastStepAt;
    private long _lastSaveTick;
    private bool _dirty;
    private int _nextIndex;
    private int _missStreak;

    public TeachingMode Mode { get; private set; }
    public string TraceName => _traceName;
    public int StepCount => _trace?.Steps.Count ?? 0;

    public TeachingController(LearningConfig cfg, Action<string, LogLevel> log)
    {
        _log = log;
        _enabled = cfg.TeachingEnabled;
        _threshold = Math.Clamp(cfg.TeachingSimilarity, 0.1, 1.0);
        _maxMisses = Math.Max(1, cfg.TeachingMaxMisses);
        _traceName = cfg.TeachingTraceName;
        Mode = _enabled ? ParseMode(cfg.TeachingMode) : TeachingMode.Off;
        if (Mode == TeachingMode.Off) return;

        if (Mode == TeachingMode.Record)
        {
            _trace = ActionTrace.Create(_traceName);
            _lastStepAt = DateTime.Now;
            _log($"[教学] 录制已开启：轨迹 {_traceName}（只录手柄白名单按键，游戏窗口前台才生效）", LogLevel.Info);
        }
        else
        {
            _trace = ActionTrace.Load(_traceName);
            if (_trace is null || _trace.Steps.Count == 0)
            {
                _log($"[教学] 轨迹 {_traceName} 不存在或为空，复现未启动", LogLevel.Warn);
                Mode = TeachingMode.Off;
            }
            else
            {
                _log($"[教学] 复现已开启：{Mode}，{_trace.Steps.Count} 步，相似度阈值 {_threshold:F2}", LogLevel.Info);
            }
        }
    }

    private static TeachingMode ParseMode(string s) => s?.Trim().ToLowerInvariant() switch
    {
        "record" => TeachingMode.Record,
        "replay" or "sequential" or "replaysequential" => TeachingMode.ReplaySequential,
        "rulebook" or "replayrulebook" => TeachingMode.ReplayRuleBook,
        _ => TeachingMode.Off,
    };

    /// <summary>游戏窗口是否在前台（支持国际服 eFootball 与国服 实况足球在线）。</summary>
    private static bool GameIsForeground()
    {
        var sb = new StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        string title = sb.ToString();
        return title.Contains("eFootball", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("实况足球在线", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>轮询物理手柄，返回本次新按下的一枚白名单按键或 RT 扳机；无变化返回 (null, false)。</summary>
    private (PadButton? Button, bool RightTrigger) PollRisingInput()
    {
        ushort current = 0;
        bool rtNow = false;
        for (int slot = 0; slot < 4; slot++)
        {
            var state = PadBackendProbe.TryRead(slot);
            if (state is null) continue;
            ushort held = (ushort)(state.Value.wButtons & WhiteMask);
            bool rt = state.Value.bRightTrigger >= 128;
            if (held == 0 && !rt) continue;   // 虚拟手柄空闲时是全 0，跳过
            current = held;
            rtNow = rt;
            break;
        }
        ushort rising = (ushort)(current & ~_lastButtons);
        bool rtRising = rtNow && !_lastRightTrigger;
        _lastButtons = current;
        _lastRightTrigger = rtNow;
        if (rtRising) return (null, true);
        foreach (var (mask, button) in Priority)
            if ((rising & mask) != 0) return (button, false);
        return (null, false);
    }

    /// <summary>将尚未落盘的教学录制轨迹立即写入磁盘。</summary>
    public void Flush()
    {
        if (!_dirty || _trace is null) return;
        _trace.Save();
        _dirty = false;
        _lastSaveTick = Environment.TickCount64;
    }

    /// <summary>旧签名兼容。</summary>
    public bool Tick(OcrResult ocr, IReadOnlyList<YoloDetection> dets,
        out PadButton? toPress, out int delayMs, out string reason)
        => Tick(ocr, dets, out toPress, out _, out delayMs, out reason);

    /// <summary>
    /// 每观察一帧调用一次。返回 true 表示本轮由教学接管（引擎应跳过场景决策）；
    /// <paramref name="toPress"/> 或 <paramref name="pressRt"/> 为真时由引擎发送对应按键。
    /// </summary>
    public bool Tick(OcrResult ocr, IReadOnlyList<YoloDetection> dets,
        out PadButton? toPress, out bool pressRt, out int delayMs, out string reason)
    {
        toPress = null;
        pressRt = false;
        delayMs = 300;
        reason = "";
        if (Mode == TeachingMode.Off) return false;

        if (Mode == TeachingMode.Record)
        {
            if (!GameIsForeground())
            {
                Flush();
                reason = "游戏窗口不在前台，暂停录制";
                return true;
            }
            var (pressedBtn, pressedRt) = PollRisingInput();
            if (pressedBtn is null && !pressedRt)
            {
                if (_dirty && Environment.TickCount64 - _lastSaveTick >= 2000) Flush();
                return true;
            }
            var now = DateTime.Now;
            int gap = (int)Math.Clamp((now - _lastStepAt).TotalMilliseconds, 100, 10000);
            _lastStepAt = now;
            string keyName = pressedRt ? "RT" : pressedBtn!.Value.ToString();
            _trace!.Add(TraceSignature.Capture(ocr, dets), keyName, gap);
            _dirty = true;
            if (Environment.TickCount64 - _lastSaveTick >= 2000) Flush();
            reason = $"已录第 {_trace.Steps.Count} 步：{keyName}";
            _log($"[教学] {reason}", LogLevel.Info);
            return true;
        }

        // 复现：状态签名不匹配就停手，绝不盲按
        var sig = TraceSignature.Capture(ocr, dets);
        TraceStep? hit = null;
        if (Mode == TeachingMode.ReplaySequential)
        {
            if (_nextIndex < _trace!.Steps.Count)
            {
                var step = _trace.Steps[_nextIndex];
                if (step.Signature.SimilarityTo(sig) >= _threshold) hit = step;
            }
        }
        else
        {
            hit = _trace!.Steps
                .Select(s => (Step: s, Score: s.Signature.SimilarityTo(sig)))
                .Where(x => x.Score >= _threshold)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Step)
                .FirstOrDefault();
        }

        if (hit is null)
        {
            _missStreak++;
            reason = $"画面与轨迹第 {_nextIndex + 1} 步不匹配（连续 {_missStreak}/{_maxMisses}），停手";
            if (_missStreak >= _maxMisses)
            {
                Mode = TeachingMode.Off;
                _log($"[教学] 连续 {_maxMisses} 次不匹配，退出复现并交回正常挂机", LogLevel.Warn);
                return false;   // 交回场景决策
            }
            return true;        // 停手但不交回，等下一帧
        }

        _missStreak = 0;
        if (string.Equals(hit.Button, "RT", StringComparison.OrdinalIgnoreCase))
        {
            pressRt = true;
        }
        else if (Enum.TryParse(hit.Button, out PadButton btn))
        {
            toPress = btn;
        }
        else
        {
            Mode = TeachingMode.Off;
            _log($"[教学] 轨迹含未知按键 {hit.Button}，退出复现", LogLevel.Warn);
            return false;
        }
        delayMs = Math.Clamp(hit.DelayMs, 100, 5000);
        _nextIndex = Mode == TeachingMode.ReplaySequential ? _nextIndex + 1 : _nextIndex;
        reason = $"复现第 {hit.Index + 1} 步：{hit.Button}";
        _log($"[教学] {reason}", LogLevel.Info);
        if (Mode == TeachingMode.ReplaySequential && _nextIndex >= _trace.Steps.Count)
        {
            _log("[教学] 流程回放完毕，退出复现", LogLevel.Success);
            Mode = TeachingMode.Off;
        }
        return true;
    }
}
