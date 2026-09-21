using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VoiceInput.Desktop.Hud;

public partial class HudWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const long NoActivateStyle = 0x08000000L;
    private const long ToolWindowStyle = 0x00000080L;
    private static readonly System.Windows.Media.Brush ListeningBrush = new SolidColorBrush(
        System.Windows.Media.Color.FromRgb(55, 214, 122));
    private static readonly System.Windows.Media.Brush ErrorBrush = new SolidColorBrush(
        System.Windows.Media.Color.FromRgb(241, 95, 95));
    private readonly PartialTranscriptCoalescer _partialCoalescer = new(
        TimeSpan.FromMilliseconds(75));
    private readonly DispatcherTimer _partialTimer;

    public HudWindow()
    {
        InitializeComponent();
        _partialTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(75),
        };
        _partialTimer.Tick += OnPartialTimer;
        SourceInitialized += OnSourceInitialized;
    }

    public void ShowListening(string detail = "Speak now")
    {
        ResetPartialDisplay();
        AccentDot.Fill = ListeningBrush;
        StatusText.Text = "Listening";
        TranscriptText.Text = detail;
        ShowWithoutActivation();
    }

    public void ShowConnecting()
    {
        ShowListening("Connecting; wait for Speak now");
        StatusText.Text = "Connecting";
    }

    public void ShowPartial(string text)
    {
        var update = _partialCoalescer.Submit(text, DateTimeOffset.UtcNow);
        if (update.ImmediateText is not null)
        {
            _partialTimer.Stop();
            RenderPartial(update.ImmediateText);
            return;
        }

        if (update.FlushAfter is { } flushAfter)
        {
            _partialTimer.Stop();
            _partialTimer.Interval = flushAfter;
            _partialTimer.Start();
        }
    }

    private void RenderPartial(string text)
    {
        AccentDot.Fill = ListeningBrush;
        StatusText.Text = "Listening";
        TranscriptText.Text = text;
        ShowWithoutActivation();
    }

    public void ShowError(string message)
    {
        ResetPartialDisplay();
        AccentDot.Fill = ErrorBrush;
        StatusText.Text = "Error";
        TranscriptText.Text = message;
        ShowWithoutActivation();
    }

    public void HideHud()
    {
        ResetPartialDisplay();
        Hide();
    }

    private void OnPartialTimer(object? sender, EventArgs e)
    {
        _partialTimer.Stop();
        var text = _partialCoalescer.Flush(DateTimeOffset.UtcNow);
        if (text is not null)
        {
            RenderPartial(text);
        }
    }

    private void ResetPartialDisplay()
    {
        _partialTimer.Stop();
        _partialCoalescer.Reset();
    }

    private void ShowWithoutActivation()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Bottom - Height - 32;
        if (!IsVisible)
        {
            Show();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        _ = SetWindowLongPtr(
            handle,
            ExtendedStyleIndex,
            new nint(style | NoActivateStyle | ToolWindowStyle));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint windowHandle,
        int index,
        nint newValue);
}
