using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EfootballBot.Core;
using EfootballBot.Core.Bot;
using EfootballBot.Core.System;

namespace EfootballBot;

/// <summary>
/// 「挂机时静音游戏」：只把游戏进程的音频会话静音（Windows 音量合成器里单独给游戏静音的效果），
/// 系统音量与其他软件完全不受影响。
/// <para>
/// 各游戏模块页的「本次运行」栏各有一个开关（共用同一配置 <see cref="Core.Config.AudioConfig"/>），
/// 点它随时开关游戏声音：挂机中点一下就立刻生效，不用停止再开始。
/// 挂机开始自动静音，停止自动恢复；游戏中途重启会新建音频会话（静音不继承），
/// 挂机期间按秒级巡检重放静音。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>挂机中重放静音的巡检间隔（游戏重启 → 新会话，需要重新静音）。</summary>
    private readonly DispatcherTimer _muteTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    /// <summary>当前是否由本程序把游戏静音（用于日志去重与停止时恢复）。</summary>
    private bool _muteApplied;

    /// <summary>批量回填/同步所有模块的复选框时屏蔽事件，避免互相触发写配置。</summary>
    private bool _syncingMuteUi;

    private CheckBox[] MuteCheckboxes() => new[]
    {
        ChkMuteMyLeague, ChkMuteWeekly, ChkMuteAiLadder, ChkMuteStud,
        ChkMuteCn, ChkMuteCnWeekly, ChkMuteCnMyLeague,
    };

    /// <summary>
    /// 启动期初始化：复选框回填配置 + 巡检计时器。
    /// 巡检同时负责「挂机已结束但还没恢复声音」（场景自己跑完/异常退出）的兜底恢复。
    /// </summary>
    private void InitGameMute()
    {
        _syncingMuteUi = true;
        foreach (var c in MuteCheckboxes()) c.IsChecked = _cfg.Audio.MuteGame;
        _syncingMuteUi = false;

        _muteTimer.Tick += (_, _) =>
        {
            bool running = _workerHost is { ScenarioRunning: true };
            if (running && _cfg.Audio.MuteGame)
                SetGameMuteAsync(true, userAction: false);   // 幂等重放：游戏重启后的新会话也会被静音
            else if (!running && _muteApplied)
                SetGameMuteAsync(false, userAction: false);  // 场景自行结束（跑完/异常）也要恢复声音
            else if (!running && !_muteApplied)
                _muteTimer.Stop();
        };
    }

    private bool _muteFailLogged;
    private readonly SemaphoreSlim _muteGate = new(1, 1);
    private long _muteOpGen;

    /// <summary>任一模块页的开关变动：写配置 + 同步其余 5 个复选框 + 立刻生效。</summary>
    private void ChkMuteGame_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingMuteUi || _cfg is null) return;
        bool on = sender is CheckBox { IsChecked: true };
        _cfg.Audio.MuteGame = on;
        PersistConfigSafe("保存静音设置");

        _syncingMuteUi = true;
        foreach (var c in MuteCheckboxes())
            if ((c.IsChecked == true) != on) c.IsChecked = on;
        _syncingMuteUi = false;

        bool running = _workerHost is { ScenarioRunning: true };
        if (on && !running)
        {
            AppendLog("[设置] 挂机时静音游戏：已开启（开始挂机后自动生效）", LogLevel.Info);
            return;
        }
        if (on && running && !_muteTimer.IsEnabled) _muteTimer.Start();
        SetGameMuteAsync(on, userAction: true);
    }

    /// <summary>挂机启动成功后调用（配置开着就立刻静音并启动巡检）。</summary>
    private void MuteGameOnScenarioStarted()
    {
        _muteFailLogged = false;
        if (_cfg.Audio.MuteGame)
        {
            if (!_muteTimer.IsEnabled) _muteTimer.Start();
            SetGameMuteAsync(true, userAction: false);
        }
    }

    /// <summary>挂机停止时调用：恢复游戏声音并停掉巡检。</summary>
    private void MuteGameOnScenarioStopped()
    {
        _muteFailLogged = false;
        _muteTimer.Stop();
        // 即使 _muteApplied 尚未置位，也可能有在途的 Mute(true) 正在执行，强制排队一次 Mute(false) 串行兜底
        SetGameMuteAsync(false, userAction: false, force: true);
    }

    /// <summary>
    /// 后台线程串行执行 COM 音频会话静音，避免阻塞 UI 线程与并发竞态。
    /// </summary>
    private void SetGameMuteAsync(bool mute, bool userAction, bool force = false)
    {
        if (!mute && !_muteApplied && !userAction && !force) return;
        long opGen = Interlocked.Increment(ref _muteOpGen);

        _ = Task.Run(async () =>
        {
            await _muteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 若已有更新的静音/恢复请求排队，跳过过时的非强制旧请求
                if (!force && opGen != Volatile.Read(ref _muteOpGen)) return;
                int sessions = GameAudioMute.Apply(mute, out string error);
                _ = Dispatcher.BeginInvoke(() => ApplyMuteResult(mute, userAction, sessions, error, opGen));
            }
            finally
            {
                _muteGate.Release();
            }
        });
    }

    private void ApplyMuteResult(bool mute, bool userAction, int sessions, string error, long opGen)
    {
        if (!string.IsNullOrEmpty(error))
        {
            if (userAction || !_muteFailLogged)
            {
                AppendLog($"[音频] 静音游戏失败：{error}", LogLevel.Warn);
                if (!userAction) _muteFailLogged = true;
            }
            if (!mute || sessions <= 0) return;
        }
        if (mute)
        {
            if (opGen != Volatile.Read(ref _muteOpGen)) return;
            if (sessions > 0)
            {
                _muteFailLogged = false;
                if (!_muteApplied)
                    AppendLog($"[音频] 已静音游戏（{sessions} 个音频会话），系统音量与其他软件不受影响", LogLevel.Success);
                _muteApplied = true;
                if (!userAction && _workerHost is not { ScenarioRunning: true })
                    SetGameMuteAsync(false, userAction: false, force: true);
            }
            else if (userAction)
            {
                AppendLog("[音频] 没找到游戏的音频会话（游戏没启动或还没出过声）——挂机中会自动重试", LogLevel.Warn);
            }
        }
        else
        {
            if (_muteApplied || sessions > 0) AppendLog("[音频] 已恢复游戏声音", LogLevel.Info);
            _muteApplied = false;
        }
    }
}

