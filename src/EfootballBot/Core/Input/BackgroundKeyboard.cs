using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 面向窗口句柄的后台键盘模拟（PostMessage，不动全局键盘状态、不抢前台）。
///
/// 为什么需要：国服客户端个别页面是「网页层」UI，只认它自己印在按钮上的键盘提示。
/// 实机 2026-11-07 段位积分页右下按钮写的是「C 下一步」（不是手柄的 A、也不是 RT）——
/// 同一帧 A 连按 5 次、B 连按 6 次画面一动不动。UE/Slate 的 UI 走标准 WM_KEYDOWN 消息，
/// 后台投递即可触发绑定动作，与背景鼠标（<see cref="BackgroundMouse"/>）同一路数。
/// </summary>
public sealed class BackgroundKeyboard
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private readonly Action<string>? _log;

    /// <summary>log：可选告警回调（投递失败/抬起重试失败时用；null = 静默）。</summary>
    public BackgroundKeyboard(Action<string>? log = null) => _log = log;

    private const int KeyUpAttempts = 3;

    /// <summary>向指定窗口投递一次按键（按下 → 保持 holdMs → 抬起）。</summary>
    public async Task TapAsync(IntPtr hWnd, int virtualKey, int holdMs = 60, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!PostMessage(hWnd, WM_KEYDOWN, (IntPtr)virtualKey, KeyLParam(virtualKey, up: false)))
        {
            // 按下没进队 = 键没被按住，无需释放；只是这次按键无效（审查修复：以前静默吞掉）
            _log?.Invoke($"[后台键盘] WM_KEYDOWN 投递失败（VK=0x{virtualKey:X}，窗口可能已关闭），本次按键无效");
            return;
        }
        try
        {
            await Task.Delay(holdMs, ct);
        }
        finally
        {
            ReleaseKey(hWnd, virtualKey);
        }
    }

    /// <summary>抬起按键：失败重试，全失败才告警——WM_KEYUP 丢了会让这个键在游戏里永久保持按下。</summary>
    private void ReleaseKey(IntPtr hWnd, int virtualKey)
    {
        for (int attempt = 1; attempt <= KeyUpAttempts; attempt++)
        {
            if (PostMessage(hWnd, WM_KEYUP, (IntPtr)virtualKey, KeyLParam(virtualKey, up: true))) return;
            if (attempt < KeyUpAttempts) Thread.Sleep(20);
        }
        _log?.Invoke($"[后台键盘] WM_KEYUP 连续 {KeyUpAttempts} 次投递失败（VK=0x{virtualKey:X}），" +
                     "该键可能停留在按下态，请检查游戏窗口是否仍在");
    }

    /// <summary>
    /// WM_KEYDOWN / WM_KEYUP 的 lParam 组装：重复次数=1、第 16~23 位放扫描码
    /// （MapVirtualKey 换算，缺了它部分引擎识别不出按键）、抬起事件再置第 30/31 位。
    /// 纯函数，便于单测锁定位域。
    /// </summary>
    internal static IntPtr KeyLParam(int virtualKey, bool up)
    {
        uint scan = MapVirtualKey((uint)virtualKey, 0);
        int lp = 1 | ((int)scan << 16);
        // 扩展键（方向键/Ins/Del/Home/End/PgUp/PgDn/右 Ctrl·Alt/NumLock 等）必须置 bit24(KF_EXTENDED)：
        // 缺了它，按扫描码区分左右/主键区的引擎会把方向键当成小键盘键 → "发了键没反应"（审查修复）。
        if (IsExtendedKey(virtualKey)) lp |= 1 << 24;
        if (up) lp |= (1 << 30) | (1 << 31);
        return (IntPtr)lp;
    }

    /// <summary>该虚拟键是否属于扩展键（lParam bit24 = KF_EXTENDED）。纯函数便于单测。</summary>
    internal static bool IsExtendedKey(int virtualKey) => virtualKey switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // PgUp PgDn End Home ← ↑ → ↓
        0x2C or 0x2D or 0x2E => true,                                          // PrintScreen Insert Delete
        0x5B or 0x5C or 0x5D => true,                                          // 左/右 Win、菜单键
        0x6F or 0x90 => true,                                                  // 小键盘 /、NumLock
        0xA3 or 0xA5 => true,                                                  // 右 Ctrl、右 Alt
        _ => false,
    };
}
