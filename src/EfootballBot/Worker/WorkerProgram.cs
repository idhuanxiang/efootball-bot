using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Modules;
using EfootballBot.Core.Config;
using EfootballBot.Core.Logging;
using EfootballBot.Core.System;
using EfootballBot.Ipc;

namespace EfootballBot.Worker;

/// <summary>
/// 无头工作进程：接收 GUI 命令，运行 BotEngine，事件通过命名管道回传。
/// 管道名：EfootballBot-Worker-{pid}
/// </summary>
public static class WorkerProgram
{
    /// <summary>
    /// 打印模块注册表（按服分组）+ 自检结果。返回值 0 = 自检通过，2 = 注册表有结构性问题。
    /// 用于「某个模块点了没反应」的现场核对：这条命令能一眼看出该模块在不在表里、
    /// 属于哪个服、是不是场景、能不能被接力。
    /// </summary>
    private static int ListModules()
    {
        Console.OutputEncoding = Encoding.UTF8;
        foreach (var region in new[] { ServerRegion.Intl, ServerRegion.Cn })
        {
            Console.WriteLine($"== {region.Display()}（{region.Key()}）==");
            foreach (var d in BotModuleCatalog.By(region))
                Console.WriteLine($"  {d.Id,-14} {d.DisplayName,-12} " +
                                  $"{(d.IsScenario ? "场景" : "工具")}  " +
                                  $"{(d.HandoffEligible ? "可接力" : "      ")}  {d.Summary}");
        }
        var problems = BotModuleCatalog.SelfCheck();
        Console.WriteLine(problems.Count == 0
            ? $"-- 注册表自检通过（{BotModuleCatalog.All.Count} 个模块）"
            : "-- 注册表自检未通过：" + string.Join("；", problems));
        return problems.Count == 0 ? 0 : 2;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        // 诊断入口：打印模块注册表并退出（不建会话、不接手柄、不写游戏）。
        // 用法：EfootballBot.exe --worker --list-modules
        if (args.Contains("--list-modules")) return ListModules();

        // 崩溃自动抓 minidump，越早装越好（挂机中 Worker 被原生崩溃打死是最常见的事故）
        CrashReporter.Install("worker");

        // 解析参数：--worker <mode> [--pipe <name>] [--console]
        string mode = args.Length > 1 ? args[1] : EngineModes.Observe;
        bool console = args.Contains("--console");

        int pid = Environment.ProcessId;
        // --parent：GUI 进程号。GUI 崩溃/被强杀时本进程据它自主收尾并退出，
        // 不留孤儿（孤儿 Worker 会继续跑场景、占着玩家 1 并让真实手柄保持禁用态）。
        int parentIdx = Array.IndexOf(args, "--parent");
        int parentPid = parentIdx >= 0 && parentIdx + 1 < args.Length
            && int.TryParse(args[parentIdx + 1], out int pp) && pp > 0 ? pp : 0;
        int pipeIdx = Array.IndexOf(args, "--pipe");
        // 管道名优先用 GUI 传入的（GUID，无 PID 猜测）；缺省退回 PID 约定
        string pipeName = pipeIdx >= 0 && pipeIdx + 1 < args.Length
            ? args[pipeIdx + 1]
            : $"EfootballBot-Worker-{pid}";

        // 初始化文件日志（Worker 独立进程，需自己初始化）
        // 老版 %APPDATA%\EfootballBot 数据先迁移到 exe 旁 data\（幂等），配置/日志全在程序目录内
        AppPaths.MigrateLegacyData();
        AppConfig cfg;
        try { cfg = AppConfig.Load(); }
        catch (Exception ex)
        {
            // 配置损坏此前在日志初始化之前抛出 → Worker 秒退且零日志（v1.1.19 修复）：
            // 先用默认配置把日志立起来，把异常写进会话文件再继续
            FileLogger.Instance.Initialize(enabled: true, "Debug", maxFiles: 10, retentionDays: 7);
            FileLogger.Instance.Write($"配置加载失败（用默认配置继续）：{ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            cfg = new AppConfig();
        }
        FileLogger.Instance.Initialize(cfg.FileLogEnabled, cfg.FileLogLevel, cfg.MaxLogFiles, cfg.LogRetentionDays);
        FileLogger.Instance.Write($"Worker 进程初始化完成（PID {pid}），创建管道服务器…", LogLevel.Info);

        // 双显卡独显直连自愈（幂等）：正常流程 GUI 启动时已写入，本进程创建时即已按独显
        // 运行；单独 --console 调试/旧 GUI 场景下兜底补写（下次进程生效）
        try { GpuPreference.EnsureHighPerformance(); } catch { }

        if (console)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine($"[Worker] PID={pid} mode={mode} pipe={pipeName}");
            Console.WriteLine("[Worker] 等待 GUI 连接…");
        }

        BotEngine? engine = null;
        int previewOn = 0;
        CancellationTokenSource? previewCts = null;
        Task? previewTask = null;
        NamedPipeServerStream? pipe = null;
        CancellationTokenSource? readCts = null;

        // ---- 会话标记：GUI 下次启动据此清理上代异常退出遗留的 Worker ----
        string sessionFile = Path.Combine(AppPaths.DataDir, "worker-session.txt");
        void WriteSessionMarker()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(sessionFile, $"{pid}|{parentPid}|{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch { /* 标记失败不影响运行 */ }
        }
        void DeleteSessionMarker()
        {
            try
            {
                // 只删自己写的（多开档案各自 data 目录，仍防并发覆盖）
                if (File.Exists(sessionFile) && File.ReadAllText(sessionFile).StartsWith($"{pid}|"))
                    File.Delete(sessionFile);
            }
            catch { }
        }
        WriteSessionMarker();

        // ---- GUI 看门狗：GUI 异常退出（崩溃/被强杀/更新重启）时自主收尾后退出 ----
        // 没有它：GUI 一死 Worker 仍跑着场景并占着玩家 1、真实手柄也保持禁用，
        // 用户重开程序点「开始」会被判"手柄抢不到"反复失败（2026-10-23 用户报障）。
        if (parentPid > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    await parent.WaitForExitAsync();
                }
                catch { /* 父进程早已不在：直接收尾 */ }
                try { FileLogger.Instance.Write("GUI 进程已退出：Worker 自主收尾（停引擎 / 恢复真实手柄 / 还原停靠窗口）", LogLevel.Warn); } catch { }
                StopPreviewLoop();
                var dying = Interlocked.Exchange(ref engine, null);
                if (dying is not null)
                {
                    // 先等主循环退出再释放：GUI 没了更要保证手柄/捕获干净收尾
                    try { await dying.StopAsync(1500); } catch { }
                    try { dying.Dispose(); } catch { }
                }
                try { EfootballBot.Core.Input.PhysicalPadControl.RestoreAll(); } catch { }
                try { EfootballBot.Core.System.BackgroundParker.TryRestoreFromRecord(); } catch { }
                DeleteSessionMarker();
                Environment.Exit(0);
            });
        }

        var writeLock = new SemaphoreSlim(1, 1);
        int previewIntervalMs = PreviewFrameIntervalMs;

        async Task SendAsync(PipeProtocol.Msg msg)
        {
            var p = pipe;
            if (p is null || !p.IsConnected) return;
            // 写超时（审查修复⑮）：GUI 假死/管道缓冲区满时，预览帧写会永久独占 writeLock，
            // 让 EvStopped 永远拿不到锁、GUI 只能靠 Kill 兜底（打断手柄恢复）。
            // 预览帧用 1.5s 短超时丢弃即可，控制类消息用 10s 保证可靠送达。
            bool isPreview = msg.Ev == "preview";
            int timeoutMs = isPreview ? 1500 : 10000;
            // ★写入本身不能带取消令牌（复查修复）：PipeStream.WriteAsync 非原子，
            // 超时取消可能只写了半条 JSON 且没有换行，后续所有消息都会解析错位、整条丢弃，
            // 连 EvStopped 都收不到——比原问题更严重。只对"等锁"限时，写入要么完整成功、要么管道断开。
            // 预览帧丢了无所谓；控制消息（尤其 EvStopped）不能静默丢——旧实现等锁超时直接 return，
            // GUI 等不到停止确认只能 Kill 进程，打断手柄恢复。这里再等一轮，仍拿不到锁说明
            // 写入线程已被不读管道的 GUI 永久卡死：主动断管，让 GUI 读循环立刻感知并走断开回收。
            bool acquired = await writeLock.WaitAsync(timeoutMs);
            if (!acquired && !isPreview) acquired = await writeLock.WaitAsync(timeoutMs);
            if (!acquired)
            {
                if (!isPreview) { try { p.Dispose(); } catch { } }
                return;
            }
            try
            {
                if (p.IsConnected)
                    await PipeProtocol.WriteLineAsync(p, msg);
            }
            catch { /* 管道断开：整条消息未写完，但连接已断无所谓 */ }
            finally
            {
                writeLock.Release();
            }
        }

        void BridgeEvents(BotEngine e)
        {
            // 代际守卫（审查修复）：旧引擎被替换/释放后，它的迟到事件（Log/Stats/Screen/捕获步骤）
            // 不能串进新会话的界面——否则会看到上一局的分数/画面摘要，甚至覆盖新会话状态。
            bool stale() => !ReferenceEquals(Volatile.Read(ref engine), e);
            e.Log += (msg, lv) =>
            {
                if (stale()) return;
                // 不落盘：同一批消息由 GUI 的 AppendLog 统一写日志文件（Worker 只拥有
                // 自己的生命周期行）。此前两边各写一份逐字相同的文件，反馈包里同一次
                // 会话有两份副本，磁盘与打包体积白白翻倍。
                if (console) Console.WriteLine($"[{lv}] {msg}");
                _ = SendAsync(PipeProtocol.EvLog(lv.ToString(), msg));
            };
            e.StatsChanged += s =>
            {
                if (stale()) return;
                _ = SendAsync(PipeProtocol.EvStats(s.MatchesDone, s.KeyMatches, s.PlayersMaxed, s.StartedAt, s.LastError,
                    s.SkippedMatches, s.PerfNote, s.AiLadderRating, s.AiLadderDivision));
            };
            // 感知摘要事件（Screen 已是摘要文本："OCR N 词 / 检测 N 框"）
            e.ScreenChanged += s =>
            {
                if (stale()) return;
                _ = SendAsync(PipeProtocol.EvScreen(s.Screen, s.Score,
                    string.Join(",", s.Evidence)));
            };
            e.Capture.StepLog += s =>
            {
                if (stale()) return;
                if (console) Console.WriteLine($"[cap] {s}");
                _ = SendAsync(PipeProtocol.EvLog("Info", $"[捕获] {s}"));
            };
            e.Stopped += reason =>
            {
                // 若已由 cmd == "stop" 摘除 engine 引用，说明是 GUI 主动停止（后续会发 EvStopped），不再补发 EvExited
                if (!ReferenceEquals(Volatile.Read(ref engine), e)) return;
                StopPreviewLoop();
                _ = SendAsync(PipeProtocol.EvExited(reason));
            };
        }

        void StartPreviewLoop()
        {
            var cts = new CancellationTokenSource();
            var ct = cts.Token;
            var prev = Interlocked.Exchange(ref previewCts, cts);
            if (prev is not null)
            {
                try { prev.Cancel(); } catch { }
                try { prev.Dispose(); } catch { }
            }
            long lastFrameStamp = -1;
            previewTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && Volatile.Read(ref previewOn) != 0)
                {
                    try
                    {
                        var f = Volatile.Read(ref engine)?.LatestFrame;
                        // 帧去重：引擎停止/重建期间 LatestFrame 不更新，重复发同帧无意义
                        if (f is not null && f.TimestampMs != lastFrameStamp)
                        {
                            lastFrameStamp = f.TimestampMs;
                            // 降采样到 640×360 后 JPEG 编码（PNG→JPEG 快 5~10 倍、体积小一个数量级）
                            string b64 = EncodePreviewJpeg(f.Bgra, f.Width, f.Height);
                            await SendAsync(PipeProtocol.EvPreview(b64));
                        }
                        await Task.Delay(Math.Clamp(Volatile.Read(ref previewIntervalMs), 100, 2000), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch { }
                }
            }, ct);
        }

        void StopPreviewLoop()
        {
            Volatile.Write(ref previewOn, 0);
            var prev = Interlocked.Exchange(ref previewCts, null);
            if (prev is not null)
            {
                try { prev.Cancel(); } catch { }
                try { prev.Dispose(); } catch { }
            }
        }

        async Task StopPreviewLoopAsync()
        {
            var pt = Interlocked.Exchange(ref previewTask, null);
            StopPreviewLoop();
            if (pt is not null)
            {
                try { await pt.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
            }
        }

        // 监听管道连接（循环，GUI 断开后等待重连）
        while (true)
        {
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                // 管道创建失败此前直接炸出进程，日志只有一行会话头（v1.1.19 修复）：先落盘再抛
                FileLogger.Instance.Write($"创建命名管道服务器失败（{pipeName}）：{ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                throw;
            }

            try
            {
                if (console) Console.WriteLine("[Worker] 等待连接…");
                FileLogger.Instance.Write("Worker 管道服务器已建立，等待 GUI 连接…", LogLevel.Info);
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await pipe.WaitForConnectionAsync(connectCts.Token);
                if (console) Console.WriteLine("[Worker] GUI 已连接");
                FileLogger.Instance.Write("GUI 已连接到 Worker 管道", LogLevel.Info);
            }
            catch (OperationCanceledException)
            {
                if (console) Console.WriteLine("[Worker] 60s 无重连，退出进程。");
                FileLogger.Instance.Write("60s 无 GUI 连接，Worker 退出", LogLevel.Warn);
                pipe.Dispose();
                break; // 退出 while(true)，进程结束
            }
            catch { pipe.Dispose(); continue; }

            // 上一轮的 readCts 已 Cancel 且 pipe 已 Dispose（ReadLoop 退出），此处安全释放，
            // 避免 GUI 反复断开重连时每次泄漏一个 CTS（2026-11-26 审查）
            try { readCts?.Dispose(); } catch { }
            readCts = new CancellationTokenSource();

            // 首条消息握手标记：客户端冷启动期超时残留的 CreateFile 可能被幽灵授予本实例，
            // 表现为双方"已连接"但收不到任何字节。5s 内无首条消息 → 回收本实例重新监听。
            var firstMsgTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 读循环：处理 GUI 命令
            var readTask = PipeProtocol.ReadLoopAsync(pipe, async msg =>
            {
                firstMsgTcs.TrySetResult();
                if (console) Console.WriteLine($"[Worker] 收到命令: {PipeProtocol.Serialize(msg)}");

                if (msg.Cmd == "ping")
                {
                    await SendAsync(PipeProtocol.EvPong());
                    // 紧跟在 pong 之后发 PID：GUI 的握手只读第一行 pong，这一条留给读循环。
                    // 子会话模式下 GUI 靠它 Process.GetProcessById 反查 Worker。
                    await SendAsync(PipeProtocol.EvPid(pid));
                }
                else if (msg.Cmd == "start" && msg.Mode is not null)
                {
                    FileLogger.Instance.Write($"收到 start 命令：mode={msg.Mode}", LogLevel.Info);
                    if (engine is { IsRunning: true })
                    {
                        await SendAsync(PipeProtocol.EvLog("Warn", "场景已在运行中，忽略重复 start"));
                        return;
                    }
                    try
                    {
                        // 每次 start 重建引擎（干净状态）
                        engine?.Dispose();
                        var freshCfg = AppConfig.Load(); // 每次 start 读最新配置
                        Volatile.Write(ref previewIntervalMs, freshCfg.PreviewIntervalMs);
                        // 瘦身发布（ExternalModels）：模型可能不在包里 → 首次开始前按需下载一次。
                        // 失败不阻断启动：OCR 会给出明确的缺失提示，用户也可手动放 models 目录。
                        await ModelPack.EnsureAsync((m, lv) =>
                        {
                            if (console) Console.WriteLine($"[{lv}] {m}");
                            _ = SendAsync(PipeProtocol.EvLog(lv.ToString(), m));
                        });
                        engine = new BotEngine(freshCfg);
                        BridgeEvents(engine);
                        await engine.StartAsync(msg.Mode);
                        if (console) Console.WriteLine($"[Worker] 场景已启动: {msg.Mode}");
                        // 预览开关若处于开启状态：引擎重建后重新启动推送循环
                        //（每次 start 都重建引擎，旧循环虽引用同一变量但重启更稳）
                        if (Volatile.Read(ref previewOn) != 0) StartPreviewLoop();
                    }
                    catch (Exception ex)
                    {
                        // 启动失败的引擎里可能残留已开启的捕获会话等资源，整套释放防僵尸抓帧
                        var failed = Interlocked.Exchange(ref engine, null);
                        failed?.Dispose();
                        FileLogger.Instance.Write($"启动场景失败：{ex}", LogLevel.Error);
                        await SendAsync(PipeProtocol.EvLog("Error", $"启动失败：{ex.Message}"));
                        await SendAsync(PipeProtocol.EvExited($"启动失败：{ex.Message}"));
                        // 手柄未让路：记下退出码，GUI 据此弹窗且不再自动重启（用户不动手也是白跑）
                        if (ex is EfootballBot.Core.Input.PadTakeoverBlockedException)
                            Environment.ExitCode = EfootballBot.Core.Input.PadTakeoverBlockedException.ExitCode;
                    }
                }
                else if (msg.Cmd == "stop")
                {
                    await StopPreviewLoopAsync();
                    var finishingEngine = Interlocked.Exchange(ref engine, null);
                    if (finishingEngine is not null)
                    {
                        // ① 硬松手先行（毫秒级）：先断连撤代理 ACTIVE / 拆 ViGEm + 全键释放，再停循环并立即回确认
                        try { finishingEngine.Pad360.Disconnect(); } catch { }
                        try { finishingEngine.Pad360.ResetAll(); } catch { }
                        try
                        {
                            if (finishingEngine.IsRunning)
                                await finishingEngine.StopAsync(1500);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Instance.Write($"Worker stop 停止引擎异常：{ex.Message}", LogLevel.Warn);
                        }
                        // ② 慢清理放确认之前：确保进程内物理手柄恢复与 DisabledPadDevs 落盘完成，再发 EvStopped 通知 GUI 回收进程
                        try { finishingEngine.Dispose(); } catch { }
                        try { await finishingEngine.WaitPendingRestoreAsync(1200); } catch { }
                        try { await SendAsync(PipeProtocol.EvLog("Info", "已停止")); } catch { }
                        await SendAsync(PipeProtocol.EvStopped());
                    }
                    else
                    {
                        await SendAsync(PipeProtocol.EvStopped());
                    }
                }
                else if (msg.Cmd == "pause")
                {
                    engine?.SetPaused(true);
                }
                else if (msg.Cmd == "resume")
                {
                    engine?.SetPaused(false);
                }
                else if (msg.Cmd == "step")
                {
                    engine?.RequestStep();
                }
                else if (msg.Cmd == "swapTest")
                {
                    engine?.RequestSwapInspection();
                }
                else if (msg.Cmd == "preview")
                {
                    bool on = msg.On == true;
                    Volatile.Write(ref previewOn, on ? 1 : 0);
                    if (on) StartPreviewLoop();
                    else StopPreviewLoop();
                    if (console) Console.WriteLine($"[Worker] 预览={(on ? "开" : "关")}");
                }
                else if (msg.Cmd == "reloadConfig")
                {
                    // M1 简化：仅记录日志，下次 start 时自动读最新配置
                    await SendAsync(PipeProtocol.EvLog("Info", "配置将在下次启动场景时生效"));
                }
            }, readCts.Token);

            // 握手窗口：5s 内必须收到首条字节，否则判定为幽灵连接，丢弃重建实例
            await Task.WhenAny(firstMsgTcs.Task, readTask, Task.Delay(5000));

            if (!firstMsgTcs.Task.IsCompleted)
            {
                if (console) Console.WriteLine("[Worker] 5s 内无首条消息，判定幽灵连接，回收实例");
                FileLogger.Instance.Write("连接建立后 5s 内无首条消息（幽灵连接），回收管道实例重新监听", LogLevel.Warn);
                readCts.Cancel();
                pipe.Dispose();
                pipe = null;
                continue;
            }

            // 等待读循环退出（GUI 关闭/崩溃）
            try
            {
                await readTask;
            }
            catch { }

            if (console) Console.WriteLine("[Worker] GUI 断开，等待重连…");

            // 管道断开后停止场景（避免无头 Worker 空跑）。
            // 引擎一律整套释放：即使场景未在运行，捕获会话等资源也不能留（防僵尸抓帧）。
            var toDispose = Interlocked.Exchange(ref engine, null);
            if (toDispose is not null)
            {
                try { await toDispose.StopAsync(1500); } catch { }
                try { toDispose.Dispose(); } catch { }
                if (console) Console.WriteLine("[Worker] 因 GUI 断开而停止并释放引擎");
            }
            await StopPreviewLoopAsync();
            readCts.Cancel();
            pipe.Dispose();
            pipe = null;
        }

        try { readCts?.Dispose(); } catch { }
        DeleteSessionMarker();
        return 0;
    }

    /// <summary>
    /// 预览帧间隔（毫秒）。挂机感知本身只需 ≤300ms 粒度，但右侧实时画面是给人看的：
    /// 100ms ≈ 10fps，配合 JPEG(q70) 后单帧约 15~20KB，管道带宽 ~200KB/s、GUI 解码 ~2ms/帧，
    /// 在"看得清动作"与"不抢引擎 CPU"之间取平衡。旧实现是 500ms（2fps）+ PNG，观感像卡住的截图。
    /// </summary>
    private const int PreviewFrameIntervalMs = 100;

    /// <summary>
    /// 将 BGRA 帧降采样到 640×360、按 UI 背景色补边、JPEG(q70) 编码后 base64。
    /// 相比旧实现（480×270 PNG + Dispatcher.Invoke 回 UI 线程编码）：
    ///   1) JPEG 编码比 PNG 快 5~10 倍，输出体积小一个数量级（管道与 GUI 解码同步变轻）；
    ///   2) BitmapSource.Create 产出可 Freeze 的对象，后台线程直接编码，
    ///      省掉每帧一次 Application.Dispatcher.Invoke 跨线程往返。
    /// 返回的 base64 由 GUI 侧 BitmapImage 解码（自动识别 JPEG，无需改协议）。
    /// </summary>
    private static byte[]? s_previewBuf;
    private static readonly object s_previewLock = new();

    private static string EncodePreviewJpeg(byte[] bgra, int sw, int sh)
    {
        const int pw = 640, ph = 360;
        int cw = pw, ch = (int)Math.Round((double)pw * sh / sw);
        if (ch > ph) { ch = ph; cw = (int)Math.Round((double)ph * sw / sh); }
        int offX = (pw - cw) / 2, offY = (ph - ch) / 2;

        lock (s_previewLock)
        {
            // 复用 900KB 预览缓冲，避免每 100~300ms 分配一次进 LOH 引发长期挂机 Gen2 GC
            var buf = s_previewBuf ??= new byte[pw * ph * 4];
            for (int i = 0; i < buf.Length; i += 4)
            {
                buf[i] = 0x14; buf[i + 1] = 0x0F; buf[i + 2] = 0x0B; buf[i + 3] = 0xFF;
            }

            for (int y = 0; y < ch; y++)
            {
                int srow = (y * sh / ch) * sw * 4;
                int drow = ((y + offY) * pw + offX) * 4;
                for (int x = 0; x < cw; x++)
                {
                    int si = srow + (x * sw / cw) * 4;
                    int di = drow + x * 4;
                    buf[di] = bgra[si];
                    buf[di + 1] = bgra[si + 1];
                    buf[di + 2] = bgra[si + 2];
                    buf[di + 3] = 0xFF;
                }
            }

            var source = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, buf, pw * 4);
            source.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = 70 };
            enc.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
    }
}

