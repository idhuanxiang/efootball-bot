using EfootballBot.Core.Config;

namespace EfootballBot.Core.Input;

/// <summary>手柄语义动作：全部对应 eFootball 国际服默认键位（下一步=A，返回=B，方向=方向键）。</summary>
public sealed class Pad
{
    private readonly IVirtualPad _vg;
    private readonly TimingConfig _t;
    private readonly HumanPacer? _pace;
    private CancellationToken _sessionCt;
    // 暂停中断：按键序列用 session+pause 链接令牌，暂停时取消 pause 即中断正在执行的连按
    // （2026-11-26 实机：组别页暂停时正在跑的「左×4+上×6」没停，游戏还在动）
    private CancellationTokenSource? _pauseCts;
    private CancellationTokenSource? _linkedCts;
    private readonly object _ctsGate = new();
    // 加锁读取：RebuildLinked 在 Worker 管道线程执行，按键在 bot 主循环线程读 ActionCt，
    // 无锁会读到已 Dispose 的 CTS.Token 抛 ObjectDisposedException（审查修复）
    private CancellationToken ActionCt
    {
        get { lock (_ctsGate) return _linkedCts?.Token ?? _sessionCt; }
    }

    /// <summary>当前是否处于暂停取消态（供主循环区分“暂停中断”与“其他取消异常”）。</summary>
    public bool IsPauseCanceled
    {
        get { lock (_ctsGate) return _pauseCts?.IsCancellationRequested == true; }
    }

    // ---- 性能埋点（P1-14）：场景一秒钟到底花多少在"按键"上 ----
    // 只在 Pad 的公有动作入口计时：内部 Task.Delay（按住/间隔）也算动作时间——
    // 那本来就是场景为了"让游戏吃到这一下"必须付出的墙钟时间，隐藏它反而看不出瓶颈。
    private long _busyMs;
    private long _actionCount;

    /// <summary>累计花在按键/摇杆动作上的时间（毫秒）。主循环按轮取差值记进 PerfTrace。</summary>
    public long BusyMs => Interlocked.Read(ref _busyMs);

    /// <summary>累计动作次数。</summary>
    public long ActionCount => Interlocked.Read(ref _actionCount);

    public Pad(IVirtualPad vg, TimingConfig timing)
    {
        _vg = vg;
        _t = timing;
        _pace = timing.HumanPacingEnabled ? new HumanPacer() : null;
    }

    /// <summary>绑定当前挂机会话的取消令牌：用户停止后中断连按与等待，并确保松手。</summary>
    public void SetSessionToken(CancellationToken ct)
    {
        _sessionCt = ct;
        RebuildLinked();
    }

    /// <summary>
    /// 暂停时取消当前按键令牌并保持"取消态"——正在执行的连按/等待立刻中断，
    /// 且 DpadRepeatFast 每次循环重读 ActionCt 时也仍拿到已取消令牌（不会继续发键）。
    /// ★不能在这里 RebuildLinked：那样会造出新令牌让连按停不下来；
    ///   令牌只由 ResumeActions（恢复）重建。
    /// </summary>
    public void PauseActions()
    {
        lock (_ctsGate) _pauseCts?.Cancel(); // 不 Dispose：可能有异步动作正持有它的 Token
    }

    public void ResumeActions() => RebuildLinked();

    private void RebuildLinked()
    {
        CancellationTokenSource? oldLinked;
        CancellationTokenSource? oldPause;
        lock (_ctsGate)
        {
            oldLinked = _linkedCts;
            oldPause = _pauseCts;
            _pauseCts = new CancellationTokenSource();
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCt, _pauseCts.Token);
        }
        // 在锁外释放旧 CTS：ActionCt 仅在锁内读取 .Token，故不会在已释放对象上读 Token；
        // 同时避免旧 _linkedCts 长期挂在 _sessionCt 回调链表上造成整会话内存累积。
        try { oldLinked?.Dispose(); } catch { }
        try { oldPause?.Dispose(); } catch { }
    }

    /// <summary>动作计时包装：次数 +1，耗时累加（异常也计，便于看出"卡在提交上"）。</summary>
    private async Task Timed(Func<Task> action)
    {
        Interlocked.Increment(ref _actionCount);
        long t0 = Environment.TickCount64;
        try { await action().ConfigureAwait(false); }
        finally { Interlocked.Add(ref _busyMs, Environment.TickCount64 - t0); }
    }

    private int Jitter(int baseMs)
    {
        if (_pace is not null) return _pace.Sample(baseMs);
        int j = Math.Max(0, Math.Min(200, _t.HumanJitterMs));
        int d = j == 0 ? 0 : Random.Shared.Next(-j, j + 1);
        return Math.Max(40, baseMs + d);
    }

    /// <summary>小概率插入一次「犹豫」停顿（真人被手机/弹窗打断的瞬间），仅高频语义键。</summary>
    private async Task MaybeHesitate()
    {
        if (_pace is null || !_pace.RollHesitation()) return;
        await Task.Delay(_pace.HesitationMs(), ActionCt);
    }

    /// <summary>下一步 / 确认（手柄 A）。</summary>
    public Task Confirm() => Timed(async () =>
    {
        await MaybeHesitate();
        await _vg.TapAsync(PadButton.A, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt);
    });

    /// <summary>返回（手柄 B）。</summary>
    public Task Back() => Timed(async () =>
    {
        await MaybeHesitate();
        await _vg.TapAsync(PadButton.B, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt);
    });

    /// <summary>按任意指定按钮（供声明式导航图使用）。</summary>
    public Task Press(PadButton b) => Timed(async () =>
    {
        await MaybeHesitate();
        await _vg.TapAsync(b, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt);
    });

    public Task Dpad(PadDir dir) => Timed(async () =>
    {
        await MaybeHesitate();
        await _vg.TapDpadAsync(dir, Jitter(_t.DpadHoldMs), Jitter(_t.TapGapMs), ActionCt);
    });

    public Task X() => Timed(() => _vg.TapAsync(PadButton.X, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt));
    public Task Y() => Timed(() => _vg.TapAsync(PadButton.Y, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt));
    public Task LB() => Timed(() => _vg.TapAsync(PadButton.LeftShoulder, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt));
    public Task RB() => Timed(() => _vg.TapAsync(PadButton.RightShoulder, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt));
    public Task R3() => Timed(() => _vg.TapAsync(PadButton.RightThumb, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt));
    public Task L3() => Timed(() => _vg.TapAsync(PadButton.LeftThumb, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt));
    public Task Start() => Timed(() => _vg.TapAsync(PadButton.Start, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs), ActionCt));

    /// <summary>右扳机 RT（个别赛前确认页右下角提示「RT 确定」）。</summary>
    public Task RT() => Timed(async () =>
    {
        ActionCt.ThrowIfCancellationRequested();
        _vg.SetRightTrigger(255);
        try { await Task.Delay(Jitter(_t.TapHoldMs), ActionCt); }
        finally { PadRelease.Safe(() => _vg.SetRightTrigger(0), _vg.ResetAll); }
        await Task.Delay(Jitter(_t.TapGapMs), ActionCt);
    });

    /// <summary>右摇杆轻推（联赛主页右侧“使用的物品”翻页）。</summary>
    public Task RsFlick(PadDir dir) => Timed(() => _vg.FlickRightStickAsync(dir, Jitter(160), Jitter(200), ActionCt));

    public Task DpadRepeat(PadDir dir, int count)
        => RepeatAsync(count, () => Dpad(dir), ActionCt);

    /// <summary>
    /// 快速连按方向键（换人巡检等状态机的批量移光标用）：保持/间隔减半、跳过拟人犹豫。
    /// 机械导航不承担拟人语义——真人推方向键本就是连续快按；单步 Dpad 仍走完整拟人节奏。
    /// </summary>
    public Task DpadRepeatFast(PadDir dir, int count) => Timed(async () =>
    {
        int hold = Jitter(Math.Max(60, _t.DpadHoldMs / 2));
        int gap = Jitter(Math.Max(80, _t.TapGapMs / 2));
        for (int i = 0; i < count; i++)
        {
            ActionCt.ThrowIfCancellationRequested();
            await _vg.TapDpadAsync(dir, hold, gap, ActionCt);
        }
    });

    /// <summary>同时受外部 ct 与暂停令牌约束的延时，确保暂停时能立即打断等待。</summary>
    private async Task DelayWithPauseAsync(int ms, CancellationToken ct)
    {
        var act = ActionCt;
        if (!ct.CanBeCanceled || ct == _sessionCt)
        {
            await Task.Delay(ms, act);
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, act);
        await Task.Delay(ms, linked.Token);
    }

    /// <summary>结算 / 奖励页：缓慢连按 A。</summary>
    public async Task ConfirmMany(int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ActionCt.ThrowIfCancellationRequested();
            await Confirm();
            await DelayWithPauseAsync(Jitter(260), ct);
        }
    }

    public static async Task RepeatAsync(int count, Func<Task> action, CancellationToken ct = default)
    {
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await action();
        }
    }

    public Task Wait(int ms, CancellationToken ct) => DelayWithPauseAsync(Jitter(ms), ct);

    /// <summary>
    /// 拟人随机等待（闭区间 [minMs, maxMs]），用于非必须立即的连点场景
    /// （比赛中挂机按 A、赛后弹窗确认），避免固定节奏被识别为脚本。
    /// 拟人节奏开启时取对数正态簇状分布（真人操作成簇、有疏密），
    /// 关闭时退回均匀分布。
    /// </summary>
    public Task HumanWait(int minMs, int maxMs, CancellationToken ct)
    {
        int lo = Math.Clamp(Math.Min(minMs, maxMs), 0, 600_000);
        int hi = Math.Clamp(Math.Max(minMs, maxMs), lo, 600_000);
        int ms = _pace is not null ? _pace.SampleRange(lo, hi) : Random.Shared.Next(lo, hi + 1);
        return DelayWithPauseAsync(ms, ct);
    }
}
