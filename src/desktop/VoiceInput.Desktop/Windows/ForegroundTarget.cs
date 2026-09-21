using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceInput.Desktop.Windows;

public sealed record ForegroundTargetSnapshot(
    nint WindowHandle,
    uint ProcessId,
    string ProcessName,
    string WindowTitle);

public interface IForegroundTargetDetector
{
    ForegroundTargetSnapshot? Capture();

    bool IsActive(ForegroundTargetSnapshot target);
}

public sealed class ForegroundTargetDetector : IForegroundTargetDetector
{
    public ForegroundTargetSnapshot? Capture()
    {
        var window = GetForegroundWindow();
        if (window == nint.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return null;
        }

        var title = new StringBuilder(512);
        _ = GetWindowText(window, title, title.Capacity);

        string processName;
        try
        {
            processName = Process.GetProcessById(checked((int)processId)).ProcessName;
        }
        catch (Exception)
        {
            processName = "unknown";
        }

        return new ForegroundTargetSnapshot(
            window,
            processId,
            processName,
            title.ToString());
    }

    public bool IsActive(ForegroundTargetSnapshot target) =>
        target.WindowHandle != nint.Zero
        && GetForegroundWindow() == target.WindowHandle;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        nint windowHandle,
        StringBuilder text,
        int maximumCount);
}
