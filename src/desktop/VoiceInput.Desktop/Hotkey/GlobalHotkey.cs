using System.ComponentModel;
using System.Windows.Forms;

namespace VoiceInput.Desktop.Hotkey;

public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x5649;
    private const int WmHotkey = 0x0312;
    private const uint NoRepeat = 0x4000;
    private readonly HotkeyWindow _window;
    private bool _registered;

    public GlobalHotkey(HotkeyGesture gesture)
    {
        _window = new HotkeyWindow();
        _window.Pressed += OnPressed;
        var modifiers = (uint)gesture.Modifiers | NoRepeat;
        if (!RegisterHotKey(_window.Handle, HotkeyId, modifiers, gesture.VirtualKey))
        {
            _window.Pressed -= OnPressed;
            _window.Dispose();
            throw new Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                $"Global hotkey '{gesture}' is already in use or unavailable.");
        }

        _registered = true;
    }

    public event EventHandler? Pressed;

    public void Dispose()
    {
        if (_registered)
        {
            _ = UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }

        _window.Pressed -= OnPressed;
        _window.Dispose();
    }

    private void OnPressed(object? sender, EventArgs e) => Pressed?.Invoke(this, e);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool RegisterHotKey(
        nint windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "VoiceInputHotkeyWindow",
            });
        }

        public event EventHandler? Pressed;

        public void Dispose() => DestroyHandle();

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyId)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }

            base.WndProc(ref message);
        }
    }
}
