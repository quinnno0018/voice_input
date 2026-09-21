using System.Windows;
using VoiceInput.Desktop.Config;

namespace VoiceInput.Desktop;

public partial class App : System.Windows.Application
{
    private ShellController? _shell;
    private Tsf.TsfAsrHost? _tsf;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (e.Args.Contains("--tsf-asr", StringComparer.Ordinal))
            {
                _tsf = new Tsf.TsfAsrHost(AppConfig.Load(), Dispatcher);
                _tsf.Start();
            }
            else
            {
                _shell = new ShellController(AppConfig.Load(), Dispatcher);
                _shell.Start();
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Voice Input startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        _tsf?.Dispose();
        base.OnExit(e);
    }
}
