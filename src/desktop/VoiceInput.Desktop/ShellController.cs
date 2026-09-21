using System.Diagnostics;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using VoiceInput.Desktop.Asr;
using VoiceInput.Desktop.Audio;
using VoiceInput.Desktop.Config;
using VoiceInput.Desktop.Hotkey;
using VoiceInput.Desktop.Hud;
using VoiceInput.Desktop.Windows;

namespace VoiceInput.Desktop;

public sealed class ShellController : IDisposable
{
    private static readonly TimeSpan WarmSessionLifetime = TimeSpan.FromSeconds(30);
    private readonly AppConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly HudWindow _hud;
    private readonly IForegroundTargetDetector _targetDetector;
    private readonly ClipboardTextInjector _textInjector;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _toggleMenu;
    private readonly SemaphoreSlim _toggleLock = new(1, 1);
    private GlobalHotkey? _hotkey;
    private InputSession? _session;
    private WarmAsrSession? _warmSession;
    private bool _disposed;

    public ShellController(AppConfig config, Dispatcher dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;
        _hud = new HudWindow();
        _targetDetector = new ForegroundTargetDetector();
        _textInjector = new ClipboardTextInjector(
            _targetDetector,
            new WpfClipboardStore(dispatcher),
            new SendInputPasteSender(),
            TimeSpan.FromMilliseconds(180));

        _toggleMenu = new Forms.ToolStripMenuItem("Start listening");
        _toggleMenu.Click += OnToggleRequested;
        var exitMenu = new Forms.ToolStripMenuItem("Exit");
        exitMenu.Click += OnExitRequested;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_toggleMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitMenu);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Voice Input",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += OnToggleRequested;
    }

    public void Start()
    {
        try
        {
            _hotkey = new GlobalHotkey(_config.Hotkey);
            _hotkey.Pressed += OnToggleRequested;
            ShowTrayMessage(
                "Voice Input is running",
                $"Press {_config.Hotkey} to start or stop listening.");
            BeginWarmup();
        }
        catch (Exception exception)
        {
            ShowTrayMessage("Global hotkey unavailable", exception.Message, true);
            System.Windows.MessageBox.Show(
                exception.Message,
                "Voice Input hotkey conflict",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Cancellation.Cancel();
        _session?.Audio.Stop();
        var warmSession = Interlocked.Exchange(ref _warmSession, null);
        if (warmSession is not null)
        {
            try
            {
                warmSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
        _hotkey?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _hud.Close();
        _toggleLock.Dispose();
    }

    private async void OnToggleRequested(object? sender, EventArgs e)
    {
        await ToggleAsync();
    }

    private async Task ToggleAsync()
    {
        await _toggleLock.WaitAsync();
        try
        {
            if (_session is null)
            {
                await StartListeningAsync();
            }
            else
            {
                await StopListeningAsync();
            }
        }
        catch (Exception exception)
        {
            _hud.ShowError(exception.Message);
            ShowTrayMessage("Voice input error", exception.Message, true);
        }
        finally
        {
            _toggleLock.Release();
        }
    }

    private async Task StartListeningAsync()
    {
        var target = _targetDetector.Capture();
        if (target is null)
        {
            _hud.ShowError("No foreground input target was found.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _hud.ShowError("Set DASHSCOPE_API_KEY before starting voice input.");
            return;
        }

        IStreamingAsrProvider? provider = null;
        var warmSession = Interlocked.Exchange(ref _warmSession, null);
        var cancellation = new CancellationTokenSource();
        var audio = new MicrophoneCapture(
            _config.Audio,
            _config.AudioDeviceIndex);
        var audioDebugLog = AudioDebugLog.TryCreate(audio);
        try
        {
            _hud.ShowListening("Connecting...");
            provider = await AudioSessionStartup.StartCaptureThenAcquireProviderAsync(
                audio,
                warmSession,
                CreateProvider,
                cancellation.Token);
            audioDebugLog?.RecordProviderReady();

            var session = new InputSession(
                target,
                provider,
                audio,
                audioDebugLog,
                cancellation);
            _session = session;
            _toggleMenu.Text = "Stop listening";
            session.RunTask = RunSessionAsync(session);
            _hud.ShowListening();
        }
        catch
        {
            cancellation.Cancel();
            audio.Dispose();
            audioDebugLog?.Dispose();
            if (provider is not null)
            {
                await provider.DisposeAsync();
            }
            cancellation.Dispose();
            throw;
        }
    }

    private async Task StopListeningAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        session.Stopping = true;
        session.Audio.Stop();
        if (session.SenderTask is not null)
        {
            await IgnoreCancellation(session.SenderTask);
        }

        using var finishTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await session.Provider.CompleteAsync(finishTimeout.Token);
            if (session.RunTask is not null)
            {
                await session.RunTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (TimeoutException)
        {
            session.Cancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            session.Cancellation.Cancel();
        }

        if (session.RunTask is not null)
        {
            await IgnoreCancellation(session.RunTask);
        }
    }

    private async Task RunSessionAsync(InputSession session)
    {
        session.SenderTask = PumpAudioAsync(session);
        var receiver = ProcessEventsAsync(session);

        try
        {
            var first = await Task.WhenAny(session.SenderTask, receiver);
            if (first == receiver || !session.Stopping)
            {
                session.Cancellation.Cancel();
                session.Audio.Stop();
            }

            await Task.WhenAll(session.SenderTask, receiver);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _hud.ShowError(exception.Message);
                ShowTrayMessage("Voice input error", exception.Message, true);
            });
        }
        finally
        {
            session.Cancellation.Cancel();
            session.Audio.Dispose();
            session.AudioDebugLog?.RecordSummary(session.Audio.DroppedChunks);
            session.AudioDebugLog?.Dispose();
            await session.Provider.DisposeAsync();
            session.Cancellation.Dispose();

            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    _toggleMenu.Text = "Start listening";
                }

                if (session.Stopping)
                {
                    _hud.HideHud();
                }
            });

            BeginWarmup();
        }
    }

    private static async Task PumpAudioAsync(InputSession session)
    {
        await foreach (var chunk in session.Audio.Chunks.ReadAllAsync(
                           session.Cancellation.Token))
        {
            session.Audio.Options.ValidatePcmChunk(chunk);
            session.AudioDebugLog?.RecordCapturedChunk(chunk);
            var sendStartedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            await session.Provider.SendAudioAsync(
                chunk,
                session.Cancellation.Token);
            stopwatch.Stop();
            session.AudioDebugLog?.RecordAudioSent(
                chunk.Length,
                sendStartedAt,
                stopwatch.Elapsed);
        }
    }

    private async Task ProcessEventsAsync(InputSession session)
    {
        await foreach (var asrEvent in session.Provider.ReadEventsAsync(
                           session.Cancellation.Token))
        {
            switch (asrEvent)
            {
                case PartialTranscript partial:
                    await _dispatcher.InvokeAsync(
                        () => _hud.ShowPartial(partial.Text));
                    break;
                case StableTranscript stable:
                    var result = await _textInjector.InsertAsync(
                        session.Target,
                        stable.Text,
                        session.Cancellation.Token);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (result.Success)
                        {
                            _hud.ShowListening();
                        }
                        else
                        {
                            _hud.ShowError(result.Error ?? "Text injection failed.");
                        }
                    });
                    if (!result.Success)
                    {
                        session.Cancellation.Cancel();
                        return;
                    }
                    break;
                case AsrError error:
                    throw new AsrProviderException(error.Message);
            }
        }
    }

    private IStreamingAsrProvider CreateProvider() =>
        _config.Provider switch
        {
            AppConfig.ParaformerProvider => new ParaformerRealtimeProvider(_config),
            _ => throw new AsrProviderException(
                $"Unsupported desktop ASR provider '{_config.Provider}'."),
        };

    private void BeginWarmup()
    {
        if (_disposed
            || string.IsNullOrWhiteSpace(_config.ApiKey)
            || Volatile.Read(ref _warmSession) is not null)
        {
            return;
        }

        var warmSession = new WarmAsrSession(
            CreateProvider(),
            _config.Audio,
            WarmSessionLifetime);
        if (Interlocked.CompareExchange(
                ref _warmSession,
                warmSession,
                null) is not null)
        {
            _ = DisposeWarmSessionAsync(warmSession);
            return;
        }

        _ = PrepareWarmSessionAsync(warmSession);
    }

    private async Task PrepareWarmSessionAsync(WarmAsrSession warmSession)
    {
        try
        {
            await warmSession.PrepareAsync(CancellationToken.None);
        }
        catch
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _warmSession,
                        null,
                        warmSession),
                    warmSession))
            {
                await DisposeWarmSessionAsync(warmSession);
            }
        }
    }

    private static async Task DisposeWarmSessionAsync(
        WarmAsrSession warmSession)
    {
        try
        {
            await warmSession.DisposeAsync();
        }
        catch
        {
        }
    }

    private async void OnExitRequested(object? sender, EventArgs e)
    {
        await _toggleLock.WaitAsync();
        try
        {
            if (_session is not null)
            {
                await StopListeningAsync();
            }
        }
        finally
        {
            _toggleLock.Release();
        }

        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowTrayMessage(string title, string message, bool error = false)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = error
            ? Forms.ToolTipIcon.Error
            : Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(4000);
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class InputSession(
        ForegroundTargetSnapshot target,
        IStreamingAsrProvider provider,
        MicrophoneCapture audio,
        AudioDebugLog? audioDebugLog,
        CancellationTokenSource cancellation)
    {
        public ForegroundTargetSnapshot Target { get; } = target;
        public IStreamingAsrProvider Provider { get; } = provider;
        public MicrophoneCapture Audio { get; } = audio;
        public AudioDebugLog? AudioDebugLog { get; } = audioDebugLog;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool Stopping { get; set; }
        public Task? SenderTask { get; set; }
        public Task? RunTask { get; set; }
    }
}
