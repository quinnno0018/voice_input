namespace VoiceInput.Desktop.Windows;

public interface IClipboardStore
{
    object? Capture();

    void SetText(string text);

    void Restore(object? snapshot);
}

public interface IPasteSender
{
    bool SendPaste();
}

public sealed record TextInjectionResult(bool Success, string? Error = null);

public sealed class ClipboardTextInjector(
    IForegroundTargetDetector targetDetector,
    IClipboardStore clipboard,
    IPasteSender pasteSender,
    TimeSpan restoreDelay)
{
    public async Task<TextInjectionResult> InsertAsync(
        ForegroundTargetSnapshot target,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextInjectionResult(false, "Stable transcript is empty.");
        }

        if (!targetDetector.IsActive(target))
        {
            return new TextInjectionResult(
                false,
                "The original input target is no longer active.");
        }

        object? snapshot;
        try
        {
            snapshot = clipboard.Capture();
        }
        catch (Exception exception)
        {
            return new TextInjectionResult(
                false,
                $"Clipboard could not be preserved: {exception.Message}");
        }

        var clipboardChanged = false;
        var result = new TextInjectionResult(false, "Paste failed.");
        try
        {
            clipboard.SetText(text);
            clipboardChanged = true;

            if (!targetDetector.IsActive(target))
            {
                result = new TextInjectionResult(
                    false,
                    "The input target changed before paste.");
            }
            else if (!pasteSender.SendPaste())
            {
                result = new TextInjectionResult(false, "Windows rejected the paste input.");
            }
            else
            {
                await Task.Delay(restoreDelay, cancellationToken);
                result = new TextInjectionResult(true);
            }
        }
        catch (OperationCanceledException)
        {
            result = new TextInjectionResult(false, "Text injection was cancelled.");
        }
        catch (Exception exception)
        {
            result = new TextInjectionResult(false, exception.Message);
        }
        finally
        {
            if (clipboardChanged)
            {
                try
                {
                    clipboard.Restore(snapshot);
                }
                catch (Exception exception)
                {
                    result = new TextInjectionResult(
                        false,
                        $"Clipboard restore failed: {exception.Message}");
                }
            }
        }

        return result;
    }
}
