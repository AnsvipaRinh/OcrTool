using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OcrTool.Core;

namespace OcrTool.App;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly string _hotkeyString;
    private readonly HotkeyWindow _window;
    private readonly int _id;
    private bool _registered;
    private bool _disposed;
    private static int s_nextId = 0x4700;

    public event EventHandler? HotkeyPressed;
    public bool IsRegistered => _registered;
    public int LastError { get; private set; }
    public string HotkeyString => _hotkeyString;

    public GlobalHotkey(string hotkeyString)
    {
        _hotkeyString = hotkeyString;
        _id = ++s_nextId;
        _window = new HotkeyWindow(() => HotkeyPressed?.Invoke(this, EventArgs.Empty));

        if (!ParseHotkey(hotkeyString, out var modifiers, out var key))
        {
            LastError = 87;
            Logger.Log($"RegisterHotKey: invalid hotkey string '{hotkeyString}'");
            return;
        }

        _registered = RegisterHotKey(_window.Handle, _id, modifiers, (uint)(int)key);
        if (_registered)
        {
            Logger.Log($"RegisterHotKey SUCCESS: {hotkeyString} id=0x{_id:X}");
        }
        else
        {
            LastError = Marshal.GetLastWin32Error();
            Logger.Log($"RegisterHotKey FAILED: {hotkeyString}, error={LastError}");
        }
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
        if (_disposed) return;
        _disposed = true;
        if (_registered)
        {
            UnregisterHotKey(_window.Handle, _id);
            _registered = false;
        }
        if (_window.Handle != IntPtr.Zero)
        {
            _window.DestroyHandle();
        }
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action _callback;

        public HotkeyWindow(Action callback)
        {
            _callback = callback;
            var cp = new CreateParams
            {
                Caption = "OcrToolHotkey",
                Parent = new IntPtr(-3)
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                if (m.Msg == WM_HOTKEY)
                {
                    _callback();
                }
                base.WndProc(ref m);
            }
            catch (Exception ex)
            {
                Logger.Log("Hotkey WndProc failed: " + ex);
            }
        }
    }

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
