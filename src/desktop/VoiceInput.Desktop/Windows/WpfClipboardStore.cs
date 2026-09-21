using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VoiceInput.Desktop.Windows;

public sealed class WpfClipboardStore(Dispatcher dispatcher) : IClipboardStore
{
    public object? Capture() => InvokeWithRetry(() =>
    {
        var source = System.Windows.Clipboard.GetDataObject();
        if (source is null)
        {
            return null;
        }

        var copy = new System.Windows.DataObject();
        foreach (var format in source.GetFormats(false))
        {
            var data = source.GetData(format, false);
            if (data is not null)
            {
                copy.SetData(format, data);
            }
        }

        return new ClipboardSnapshot(copy);
    });

    public void SetText(string text) =>
        InvokeWithRetry(() => System.Windows.Clipboard.SetText(text));

    public void Restore(object? snapshot) => InvokeWithRetry(() =>
    {
        if (snapshot is null)
        {
            System.Windows.Clipboard.Clear();
            return;
        }

        if (snapshot is not ClipboardSnapshot clipboardSnapshot)
        {
            throw new InvalidOperationException("Unknown clipboard snapshot.");
        }

        System.Windows.Clipboard.SetDataObject(clipboardSnapshot.Data, true);
    });

    private T InvokeWithRetry<T>(Func<T> action) =>
        dispatcher.CheckAccess()
            ? Retry(action)
            : dispatcher.Invoke(() => Retry(action));

    private void InvokeWithRetry(Action action)
    {
        _ = InvokeWithRetry(() =>
        {
            action();
            return true;
        });
    }

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException) when (attempt < 4)
            {
                Thread.Sleep(20);
            }
        }
    }

    private sealed record ClipboardSnapshot(System.Windows.DataObject Data);
}
