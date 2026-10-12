using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OcrTool.Core;

namespace OcrTool.App;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly int _id;
    private bool _registered;
    private IntPtr _hookHandle;
    private readonly LowLevelKeyboardProc _proc;
    private readonly string _hotkeyString;
    private GCHandle _procHandle;

    public event EventHandler? HotkeyPressed;
    public bool IsRegistered => _registered;
    public int LastError { get; private set; }
    public string HotkeyString => _hotkeyString;

    public GlobalHotkey(string hotkeyString)
    {
        _hotkeyString = hotkeyString;
        _id = GetHashCode() & 0xFFFF;
        _proc = HookCallback;
        _procHandle = GCHandle.Alloc(_proc);
        InstallHook();
    }

    private void InstallHook()
    {
        _procHandle = GCHandle.Alloc(_proc);
        _hookHandle = SetHook(_proc);
        if (_hookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            LastError = error;
            _registered = false;
            Logger.Log($"SetWindowsHookEx FAILED: error=0x{error:X} ({error})");
        }
        else
        {
            _registered = true;
            Logger.Log($"SetWindowsHookEx SUCCESS: handle=0x{_hookHandle.ToInt64():X}, registered={_registered}");
        }
    }

    public void Reinstall()
    {
        Dispose();
        var handle = GCHandle.Alloc(_proc);
        _procHandle = handle;
        _hookHandle = SetHook(_proc);
        _registered = _hookHandle != IntPtr.Zero;
        Logger.Log($"SetWindowsHookEx reinstalled: handle=0x{_hookHandle.ToInt64():X}, registered={_registered}");
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var hMod = GetModuleHandle(curModule.ModuleName);
        Logger.Log($"GetModuleHandle({curModule.ModuleName})=0x{hMod.ToInt64():X}");
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, hMod, 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (IsHotkeyMatch(vkCode))
            {
                Logger.Log($"Hotkey matched: vk=0x{vkCode:X}");
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool IsHotkeyMatch(int vkCode)
    {
        if (!ParseHotkey(_hotkeyString, out var modifiers, out var key))
            return false;

        bool ctrlPressed = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool shiftPressed = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool altPressed = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

        if (((modifiers & MOD_CONTROL) != 0) != ctrlPressed) return false;
        if (((modifiers & MOD_SHIFT) != 0) != shiftPressed) return false;
        if (((modifiers & MOD_ALT) != 0) != altPressed) return false;

        return vkCode == (int)key;
    }

    private static bool ParseHotkey(string hotkeyString, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (Enum.TryParse<Keys>(part, true, out var k))
                    {
                        key = k;
                    }
                    break;
            }
        }

        return key != Keys.None;
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _registered = false;
        }
        if (_procHandle.IsAllocated)
        {
            _procHandle.Free();
        }
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}