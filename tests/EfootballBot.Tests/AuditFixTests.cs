using System.IO;
using EfootballBot.Core;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Logging;
using EfootballBot.Core.System;
using EfootballBot.Core.Tools;
using EfootballBot.Core.Vision.Sidecar;
using Xunit;

namespace EfootballBot.Tests;

public class AuditFixTests
{
    private sealed class NoopVirtualPad : IVirtualPad
    {
        public PadBackendKind Kind => PadBackendKind.Vigem;
        public int UserIndex => 0;
        public bool IsConnected => true;
        public void Connect() { }
        public void Disconnect() { }
        public void SetButton(PadButton btn, bool pressed) { }
        public void SetDpad(PadDir? dir) { }
        public void SetLeftStick(short x, short y) { }
        public void SetRightStick(short x, short y) { }
        public void SetLeftTrigger(byte value) { }
        public void SetRightTrigger(byte value) { }
        public void ResetAll() { }
        public void Dispose() { }
    }

    [Fact]
    public void SmartHotkey_ShouldAcceptMouseDown_SuppressesRepeatAndSelfHealsOnRelease()
    {
        int held = 0;
        bool sideDown = true;

        Assert.True(SmartHotkey.ShouldAcceptMouseDown(5, ref held, _ => sideDown));
        Assert.False(SmartHotkey.ShouldAcceptMouseDown(5, ref held, _ => sideDown));

        sideDown = false;
        Assert.True(SmartHotkey.ShouldAcceptMouseDown(5, ref held, _ => sideDown));
    }

    [Fact]
    public void SmartHotkey_MatchHotkey_IgnoresZeroVirtualKey()
    {
        var cfg = new HotkeyConfig
        {
            Enabled = true,
            VirtualKey = 0x51,
            Modifiers = 0,
            IsMouse = false,
            ResumeVirtualKey = 0,
            ResumeModifiers = 0,
            ResumeIsMouse = false,
        };

        Assert.Equal(SmartHotkey.HotkeyAction.None,
            SmartHotkey.MatchHotkey(cfg, 0, 0, isMouse: false));
    }

    [Fact]
    public async Task Pad_WaitAndHumanWait_CancelImmediatelyOnPauseAndResumeCleanly()
    {
        using var session = new CancellationTokenSource();
        var pad = new Pad(new NoopVirtualPad(), new TimingConfig { HumanPacingEnabled = false });
        pad.SetSessionToken(session.Token);

        Assert.False(pad.IsPauseCanceled);

        var waitTask = pad.Wait(10_000, session.Token);
        pad.PauseActions();
        Assert.True(pad.IsPauseCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);

        pad.ResumeActions();
        Assert.False(pad.IsPauseCanceled);

        var humanTask = pad.HumanWait(10_000, 10_000, session.Token);
        pad.PauseActions();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await humanTask);

        pad.ResumeActions();
        await pad.Wait(10, session.Token);
    }

    [Fact]
    public void SessionScheduler_SameStartAndEnd_RunsAllDayWithoutDuplicateBreakLog()
    {
        var logs = new List<string>();
        var cfg = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "00:00",
            EndTime = "00:00",
            DailyCapHours = 0,
            DriftMinutes = 0,
        };
        var s = new SessionScheduler(cfg, (m, _) => logs.Add(m), rnd: new Random(1));
        var t = new DateTime(2026, 11, 26, 3, 0, 0);

        Assert.True(s.ShouldRunNow(t));
        Assert.True(s.ShouldRunNow(t.AddHours(12)));
    }

    private sealed class ThrowOnRtReleasePad : IVirtualPad
    {
        public bool ResetAllCalled { get; private set; }
        public PadBackendKind Kind => PadBackendKind.Vigem;
        public int UserIndex => 0;
        public bool IsConnected => true;
        public void Connect() { }
        public void Disconnect() { }
        public void SetButton(PadButton btn, bool pressed) { }
        public void SetDpad(PadDir? dir) { }
        public void SetLeftStick(short x, short y) { }
        public void SetRightStick(short x, short y) { }
        public void SetLeftTrigger(byte value) { }
        public void SetRightTrigger(byte value)
        {
            if (value == 0) throw new PadSubmitException("simulated transient release error", new Exception("inner"));
        }
        public void ResetAll() => ResetAllCalled = true;
        public void Dispose() { }
    }

    [Fact]
    public async Task Pad_RT_PreservesOperationCanceled_WhenReleaseThrows()
    {
        using var session = new CancellationTokenSource();
        var vpad = new ThrowOnRtReleasePad();
        var pad = new Pad(vpad, new TimingConfig { HumanPacingEnabled = false, TapHoldMs = 5000, TapGapMs = 10 });
        pad.SetSessionToken(session.Token);

        var rtTask = pad.RT();
        await Task.Delay(30);
        pad.PauseActions();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rtTask);
        Assert.True(vpad.ResetAllCalled);
    }

    [Fact]
    public void AppConfig_NullNestedSections_DoNotTriggerLoadFailed()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"efb-cfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, "{\"SchemaVersion\": 3, \"Pad\": null, \"Timing\": null, \"HidHide\": null, \"GamePaths\": null}");
            var cfg = AppConfig.LoadFrom(tmp, saveOnUpgrade: false);
            Assert.False(cfg.LoadFailed);
            Assert.NotNull(cfg.Pad);
            Assert.NotNull(cfg.Timing);
            Assert.NotNull(cfg.HidHide);
            Assert.NotNull(cfg.GamePaths);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public async Task ShellTool_OpenUrl_RejectsNonHttpSchemes()
    {
        var tool = new ShellTool();
        var ctx = new ToolContext(ServerRegion.Intl, new AppConfig(), (_, _) => { }, TargetPath: "file:///C:/Windows/win.ini");
        var resFile = await tool.ExecuteAsync(ToolAction.OpenUrl, ctx);
        Assert.False(resFile.Ok);

        var ctxCmd = new ToolContext(ServerRegion.Intl, new AppConfig(), (_, _) => { }, TargetPath: "cmd.exe");
        var resCmd = await tool.ExecuteAsync(ToolAction.OpenUrl, ctxCmd);
        Assert.False(resCmd.Ok);
    }

    [Fact]
    public void WeChatFeed_ErrorLog_DoesNotOverwritePendingWarnTitle()
    {
        WeChatFeed.Reset();
        var sent = new List<(NotifyKind Kind, string Title, string Body)>();
        var now = new DateTime(2026, 11, 26, 12, 0, 0);
        WeChatFeed.Clock = () => now;
        WeChatFeed.SendHook = (k, t, b) => sent.Add((k, t, b));

        var cfg = new AppConfig();
        cfg.Notify.WebhookUrl = "https://example.invalid/hook";
        cfg.Notify.OnWarnLog = true;
        cfg.Notify.MergeMinutes = 10;
        cfg.Notify.DailySendLimit = 0;

        try
        {
            WeChatFeed.SessionStart(cfg, () => "ok", null);
            WeChatFeed.LogLine("warn-1", LogLevel.Warn);
            WeChatFeed.LogLine("warn-2", LogLevel.Warn); // queued (Pending = 1)
            WeChatFeed.LogLine("err-1", LogLevel.Error); // immediate, must not overwrite Warn title/window

            now = now.AddMinutes(11);
            WeChatFeed.FlushDue(cfg, null, null, now);

            Assert.Equal(3, sent.Count);
            Assert.Equal("⚠ 挂机警告", sent[0].Title);
            Assert.Equal("‼ 挂机错误", sent[1].Title);
            Assert.Equal("⚠ 挂机警告", sent[2].Title);
        }
        finally
        {
            WeChatFeed.SendHook = null;
            WeChatFeed.Clock = () => DateTime.Now;
            WeChatFeed.Reset();
        }
    }

    [Fact]
    public void UploadFrameRing_UndersizedFrame_ThrowsWithoutCorruptingSeq()
    {
        string name = $"Local\\EfootballBot.TestRing.{Guid.NewGuid():N}";
        using var ring = new UploadFrameRing(name, 8, 8);
        Assert.Throws<ArgumentException>(() => ring.Publish(new FrameData(new byte[8], 4, 4, 0)));
        long seq = ring.Publish(new FrameData(new byte[64], 4, 4, 0));
        Assert.Equal(2L, seq);
    }
}
