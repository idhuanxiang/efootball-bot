using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 全局热键（低级钩子实现）：WH_KEYBOARD_LL + WH_MOUSE_LL，支持键盘任意键与鼠标键
/// （左/右/中/侧键），可不带修饰键——RegisterHotKey 做不到的能力。
/// 主热键语义（调用方实现）：第一次按=暂停，暂停中再按=彻底停止；恢复热键=从暂停恢复。
/// 钩子须在带消息循环的线程（WPF UI 线程）上 Register。
/// </summary>
public sealed class SmartHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONUP = 0x020C;

    private const int VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    internal enum HotkeyAction { None, PauseStop, Resume }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // 必须用 GetAsyncKeyState 读全局物理按键状态：GetKeyState 只读当前线程消息队列，
    // 当助手在后台/游戏在前台时，Ctrl/Shift 读回来恒为 0，导致带修饰键的热键完全失效。
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private IntPtr _kbHook, _msHook;
    // 委托必须强引用，否则被 GC 回收后钩子失效
    private readonly LowLevelProc _kbProc, _msProc;
    private volatile Config.HotkeyConfig? _cfg;
    // 防按住不放时的键盘自动连发：同一主键未松开前不重复触发，避免"按一次暂停变暂停+停止"
    private int _heldMainVk;
    // 鼠标键同样要去抖（审查修复）：旧实现每个 WM_*BUTTONDOWN 都 Fire，
    // 按住鼠标侧键会连发，双击则"暂停→立即停止"。按下记键、抬起清零。
    private int _heldMouseBtn;

    /// <summary>主热键触发（暂停/停止语义由调用方实现）。钩子线程回调，UI 操作需封送。</summary>
    public event Action? PauseStopTriggered;
    /// <summary>恢复热键触发。</summary>
    public event Action? ResumeTriggered;

    public bool IsRegistered { get; private set; }

    public SmartHotkey()
    {
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
    }

    /// <summary>兼容旧 API：低级钩子不需要窗口句柄，空实现。</summary>
    public void Attach(object window) { }

    /// <summary>注册热键（按需安装低级钩子，任一失败自动回滚不泄漏）。失败返回 false。</summary>
    public bool Register(Config.HotkeyConfig cfg)
    {
        if (!cfg.Enabled) return false;
        Unregister();
        _cfg = cfg;
        bool needKb = !cfg.IsMouse || (!cfg.ResumeIsMouse && cfg.ResumeVirtualKey != 0);
        bool needMs = cfg.IsMouse || (cfg.ResumeIsMouse && cfg.ResumeVirtualKey != 0);
        if (!needKb && !needMs) return false;

        var modHandle = GetModuleHandle(null);
        if (needKb && _kbHook == IntPtr.Zero)
        {
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, modHandle, 0);
            if (_kbHook == IntPtr.Zero) { Unregister(); return false; }
        }
        if (needMs && _msHook == IntPtr.Zero)
        {
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, modHandle, 0);
            if (_msHook == IntPtr.Zero) { Unregister(); return false; }
        }
        IsRegistered = true;
        return true;
    }

    public void Unregister()
    {
        if (_kbHook != IntPtr.Zero) { try { UnhookWindowsHookEx(_kbHook); } catch { } _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { try { UnhookWindowsHookEx(_msHook); } catch { } _msHook = IntPtr.Zero; }
        _cfg = null;
        _heldMainVk = 0;
        _heldMouseBtn = 0;
        IsRegistered = false;
    }

    private static int CurrentMods()
    {
        int m = 0;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) m |= 2;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) m |= 4;
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) m |= 1;
        if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) m |= 8;
        return m;
    }

    private static bool IsModifierVk(int vk) =>
        vk is VK_CONTROL or VK_SHIFT or VK_MENU or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    internal static HotkeyAction MatchHotkey(Config.HotkeyConfig? cfg, int code, int mods, bool isMouse)
    {
        if (cfg is null || !cfg.Enabled) return HotkeyAction.None;
        if (cfg.VirtualKey != 0 && cfg.IsMouse == isMouse && code == cfg.VirtualKey && mods == cfg.Modifiers)
            return HotkeyAction.PauseStop;
        if (cfg.ResumeVirtualKey != 0 && cfg.ResumeIsMouse == isMouse && code == cfg.ResumeVirtualKey && mods == cfg.ResumeModifiers)
            return HotkeyAction.Resume;
        return HotkeyAction.None;
    }

    private void Fire(int code, int mods, bool isMouse)
    {
        switch (MatchHotkey(_cfg, code, mods, isMouse))
        {
            case HotkeyAction.PauseStop:
                PauseStopTriggered?.Invoke();
                break;
            case HotkeyAction.Resume:
                ResumeTriggered?.Invoke();
                break;
        }
    }

    internal static bool ShouldAcceptKeyDown(int vk, ref int heldMainVk, Func<int, bool> isKeyPhysicallyDown)
    {
        if (IsModifierVk(vk)) return false;
        // 丢过 WM_KEYUP（UAC/提权窗口切换等）时，GetAsyncKeyState 会报告实际已松开，立刻清零自愈
        if (heldMainVk != 0 && !isKeyPhysicallyDown(heldMainVk))
            heldMainVk = 0;
        if (vk == heldMainVk) return false;
        heldMainVk = vk;
        return true;
    }

    /// <summary>鼠标按下是否应触发：同一键未抬起前不重复触发（纯函数便于单测）。</summary>
    internal static bool ShouldAcceptMouseDown(int button, ref int heldButton)
        => ShouldAcceptMouseDown(button, ref heldButton, static _ => true);

    /// <summary>鼠标按下是否应触发（带物理按键状态自愈，防止丢失 UP 消息后卡死）。</summary>
    internal static bool ShouldAcceptMouseDown(int button, ref int heldButton, Func<int, bool> isButtonPhysicallyDown)
    {
        if (button == 0) return false;
        if (heldButton != 0 && !isButtonPhysicallyDown(heldButton))
            heldButton = 0;
        if (button == heldButton) return false;
        heldButton = button;
        return true;
    }

    internal static int MouseButtonToVk(int btn) => btn switch
    {
        1 => 0x01, // VK_LBUTTON
        2 => 0x02, // VK_RBUTTON
        4 => 0x04, // VK_MBUTTON
        5 => 0x05, // VK_XBUTTON1
        6 => 0x06, // VK_XBUTTON2
        _ => 0,
    };

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long msg = wParam.ToInt64();
            int vk = Marshal.ReadInt32(lParam, 0); // KBDLLHOOKSTRUCT.vkCode
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (ShouldAcceptKeyDown(vk, ref _heldMainVk, static k => (GetAsyncKeyState(k) & 0x8000) != 0))
                    Fire(vk, CurrentMods(), isMouse: false);
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                if (vk == _heldMainVk) _heldMainVk = 0;
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long msg = wParam.ToInt64();
            int btn = msg switch
            {
                WM_LBUTTONDOWN => 1,
                WM_RBUTTONDOWN => 2,
                WM_MBUTTONDOWN => 4,
                WM_XBUTTONDOWN => ((uint)Marshal.ReadInt32(lParam, 8) >> 16) == 1 ? 5 : 6, // mouseData 高字
                _ => 0,
            };
            if (btn != 0)
            {
                if (ShouldAcceptMouseDown(btn, ref _heldMouseBtn, static b =>
                    {
                        int vk = MouseButtonToVk(b);
                        return vk == 0 || (GetAsyncKeyState(vk) & 0x8000) != 0;
                    }))
                    Fire(btn, CurrentMods(), isMouse: true);
            }
            else
            {
                int upBtn = msg switch
                {
                    WM_LBUTTONUP => 1,
                    WM_RBUTTONUP => 2,
                    WM_MBUTTONUP => 4,
                    WM_XBUTTONUP => ((uint)Marshal.ReadInt32(lParam, 8) >> 16) == 1 ? 5 : 6,
                    _ => 0,
                };
                if (upBtn != 0 && upBtn == _heldMouseBtn)
                    _heldMouseBtn = 0;
            }
        }
        return CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
    }
}
