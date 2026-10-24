using System.Runtime.InteropServices;
using System.Windows;
using ClipboardManager.ViewModels;

namespace ClipboardManager.Services;

public sealed class ClipboardCaptureService : IDisposable
{
    private const int ReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly ClipboardDatabase _database;
    private readonly MainViewModel _viewModel;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private bool _disposed;

    public ClipboardCaptureService(ClipboardDatabase database, MainViewModel viewModel)
    {
        _database = database;
        _viewModel = viewModel;
    }

    public bool IsEnabled { get; set; } = true;

    public async Task CaptureCurrentClipboardAsync()
    {
        if (_disposed || !IsEnabled || !_captureGate.Wait(0))
            return;

        try
        {
            string? text = null;
            for (var attempt = 1; attempt <= ReadAttempts; attempt++)
            {
                try
                {
                    if (!System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
                        return;
                    text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
                    break;
                }
                catch (COMException ex) when (attempt < ReadAttempts)
                {
                    AppLogger.Error($"Clipboard read attempt {attempt} failed; retrying.", ex);
                    await Task.Delay(RetryDelay);
                }
                catch (COMException ex)
                {
                    AppLogger.Error("Clipboard remained locked after all read attempts.", ex);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                return;
            if (text.Length > ClipboardDatabase.MaxTextLength)
            {
                AppLogger.Error($"Clipboard text exceeded the {ClipboardDatabase.MaxTextLength:N0}-character storage limit and was skipped.");
                return;
            }

            var item = await _database.AddAsync(text);
            if (item is not null)
                _viewModel.AddItem(item);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Clipboard capture failed.", ex);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The UI thread can be shutting down while an asynchronous clipboard capture is finishing.
    }
}
