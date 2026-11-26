using System.IO;
using System.Diagnostics;
using EfootballBot.Core.System;

// ===========================================================================
// 架构锁 · 工具层 Core/Tools（v7.2）
// 本层职责：本地工具（赛前设置 / 打开路径 / 窗口防卡死 …），与挂机模块完全解耦。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用挂机层（EfootballBot.Core.Bot）—— 共享件放 Core/**，不要借挂机的件
//   · 工具 id 字面量只许在 ToolIds.cs，具体工具只许在 ToolCatalog 里实例化
//   · 界面层（MainWindow*.cs）不许直接 Process.Start：起进程/打开路径一律走 ToolCatalog
// 新增工具：ToolIds 加常量 + ToolCatalog 加一条 + 本目录一个 *Tool.cs 实现。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Tools;

/// <summary>
/// 赛前快捷设置（工具模块，两服各自一份路径记忆）：
/// <list type="bullet">
/// <item><b>启动游戏</b>：已在运行只提示；国际服优先 Steam 正规链路（steam://rungameid/…，
/// 不依赖安装位置、Steam 未启动会自动带起），Steam 不在时回退定位到的 exe 直启；国服只走 exe 直启。</item>
/// <item><b>打开设置工具</b>：定位 Settings.exe 并启动（定位链路见 <see cref="ToolPaths"/>）。</item>
/// <item><b>打开游戏目录</b>：借 Settings.exe 定位安装目录，用资源管理器打开。</item>
/// </list>
/// 全部逻辑在工具层实现，界面只负责把 <see cref="ToolResult.Message"/> 贴到状态行。
/// </summary>
public sealed class PreMatchTool : ITool
{
    public string Id => ToolIds.PreMatch;
    public string DisplayName => "赛前快捷设置";
    public string Summary => "启动游戏本体 / 打开游戏自带设置工具 / 打开游戏安装目录（两服各自路径记忆）";
    public bool ServerScoped => true;

    public IReadOnlyList<ToolAction> Actions { get; } = new[]
    {
        ToolAction.LaunchGame, ToolAction.OpenSettingsTool, ToolAction.OpenGameDirectory,
    };

    public Task<ToolResult> ExecuteAsync(ToolAction action, ToolContext ctx, CancellationToken ct = default)
        => action switch
        {
            ToolAction.LaunchGame => LaunchGameAsync(ctx, ct),
            ToolAction.OpenSettingsTool => Task.FromResult(OpenSettingsTool(ctx)),
            ToolAction.OpenGameDirectory => Task.FromResult(OpenGameDirectory(ctx)),
            _ => Task.FromResult(ToolResult.Fail($"赛前快捷设置不支持动作 {action}")),
        };

    private static async Task<ToolResult> LaunchGameAsync(ToolContext ctx, CancellationToken ct)
    {
        var profile = ToolPaths.Of(ctx.Server);
        string tag = $"[{ToolPaths.Of(ctx.Server).Label}]";
        try
        {
            var existing = Process.GetProcessesByName(profile.ProcessName);
            bool alreadyRunning = existing.Length > 0;
            foreach (var p in existing) p.Dispose();
            if (alreadyRunning)
            {
                ctx.Log($"[赛前快捷设置]{tag} 游戏已在运行，未重复启动。", LogLevel.Info);
                return ToolResult.Success($"{profile.Label}游戏已在运行，无需重复启动。");
            }

            // 国际服：Steam 正规链路优先（URI 未注册 = 没装 Steam → 落到 exe 直启）
            if (profile.SteamAppId is { } appId)
            {
                try
                {
                    using var sp = Process.Start(new ProcessStartInfo
                    {
                        FileName = $"steam://rungameid/{appId}",
                        UseShellExecute = true,
                    });
                    // steam:// 只代表"交给协议处理器"，Steam 未登录/被拦/游戏校验失败时同样会走到这里。
                    // 先等进程真的出现再报"已启动"，否则用户对着黑屏干等、日志还留着一条假的 Success（审查修复）。
                    if (await WaitForProcessAsync(profile.ProcessName, TimeSpan.FromSeconds(8), ct))
                    {
                        ctx.Log($"[赛前快捷设置]{tag} 已通过 Steam 启动国际服游戏（eFootball）。", LogLevel.Success);
                        return ToolResult.Success("已通过 Steam 启动国际服游戏（eFootball），窗口出现需等待片刻。");
                    }
                    ctx.Log($"[赛前快捷设置]{tag} 已请求 Steam 启动，但 8 秒内未见游戏进程——" +
                            "可能是 Steam 未登录/正在更新，或启动被安全软件拦截。请留意 Steam 窗口提示。", LogLevel.Warn);
                    return ToolResult.Success("已请求 Steam 启动国际服游戏。若 1 分钟内未出现游戏窗口，" +
                                             "请检查 Steam 是否已登录/正在更新（也可手动启动游戏后再点开始挂机）。");
                }
                catch
                {
                    // 无 Steam（URI 协议未注册）→ 回退定位 exe 直启
                }
            }

            string? exe = ToolPaths.LocateGameExe(ctx.Config, ctx.Server);
            if (exe is null)
            {
                string hint = ctx.Server == ServerRegion.Cn
                    ? "未找到国服游戏本体（eFootballOnline.exe）——请先点一次「打开设置工具」形成路径记忆（或启动国服游戏）后再试。"
                    : "未定位到国际服游戏本体（eFootball.exe）——请确认已安装 Steam 版 eFootball。";
                ctx.Log($"[赛前快捷设置]{tag} 未定位到游戏本体，未启动。", LogLevel.Warn);
                return ToolResult.Fail(hint);
            }

            using var gp = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
            ctx.Log($"[赛前快捷设置]{tag} 已启动游戏：{exe}", LogLevel.Success);
            return ToolResult.Success(exe, exe);
        }
        catch (Exception ex)
        {
            ctx.Log($"[赛前快捷设置]{tag} 启动游戏失败：{ex.Message}", LogLevel.Error);
            return ToolResult.Fail($"启动失败：{ex.Message}");
        }
    }

    /// <summary>轮询等待游戏进程出现（Steam 链路验证用）。取消/超时返回 false，不抛异常。</summary>
    private static async Task<bool> WaitForProcessAsync(string processName, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var found = Process.GetProcessesByName(processName);
                foreach (var p in found) p.Dispose();
                if (found.Length > 0) return true;
            }
            catch { /* 枚举失败按未出现处理，继续等 */ }
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static ToolResult OpenSettingsTool(ToolContext ctx)
    {
        string label = ToolPaths.Of(ctx.Server).Label;
        try
        {
            string? exe = ToolPaths.LocateSettingsTool(ctx.Config, ctx.Server, ctx.Log);
            if (exe is null)
            {
                string hint = ctx.Server == ServerRegion.Cn
                    ? "未找到国服 Settings.exe——请先启动国服游戏（保持运行）再点一次；定位成功一次后会记住路径，之后无需游戏在运行。"
                    : "未找到 Settings.exe——请先启动国际服游戏（保持运行）再点一次，或到游戏安装目录手动打开。";
                ctx.Log($"[赛前快捷设置] 未定位到{label}游戏设置工具（Settings.exe）：请先启动游戏再试，或到游戏安装目录手动打开。",
                    LogLevel.Warn);
                return ToolResult.Fail(hint);
            }
            using var p = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            ctx.Log($"[赛前快捷设置] 已打开{label}游戏设置工具：{exe}", LogLevel.Success);
            return ToolResult.Success(exe, exe);
        }
        catch (Exception ex)
        {
            ctx.Log($"[赛前快捷设置] 打开{label}设置工具失败：{ex.Message}", LogLevel.Error);
            return ToolResult.Fail($"打开失败：{ex.Message}");
        }
    }

    private static ToolResult OpenGameDirectory(ToolContext ctx)
    {
        string label = ToolPaths.Of(ctx.Server).Label;
        try
        {
            string? dir = ToolPaths.GameDirectory(ctx.Config, ctx.Server, ctx.Log);
            if (dir is null)
            {
                ctx.Log($"[赛前快捷设置] 未定位到{label}游戏安装目录：请先启动游戏再试。", LogLevel.Warn);
                return ToolResult.Fail($"未定位到{label}游戏安装目录——请先启动游戏再点一次。");
            }
            using var _ = Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            ctx.Log($"[赛前快捷设置] 已打开{label}游戏目录：{dir}", LogLevel.Success);
            return ToolResult.Success(dir, dir);
        }
        catch (Exception ex)
        {
            ctx.Log($"[赛前快捷设置] 打开{label}游戏目录失败：{ex.Message}", LogLevel.Error);
            return ToolResult.Fail($"打开失败：{ex.Message}");
        }
    }
}

