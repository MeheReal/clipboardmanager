using System.Runtime.InteropServices;
using System.Windows;
using ClipboardManager.Models;

namespace ClipboardManager.Services;

public sealed class ClipboardPasteService
{
    private const int ClipboardWriteAttempts = 3;

    public async Task<bool> SetClipboardTextAsync(ClipboardItem item)
    {
        for (var attempt = 1; attempt <= ClipboardWriteAttempts; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(item.TextContent, System.Windows.TextDataFormat.UnicodeText);
                return true;
            }
            catch (COMException ex) when (attempt < ClipboardWriteAttempts)
            {
                AppLogger.Error($"Clipboard write attempt {attempt} failed; retrying.", ex);
                await Task.Delay(50);
            }
            catch (COMException ex)
            {
                AppLogger.Error("Clipboard write failed after all attempts.", ex);
                return false;
            }
        }
        return false;
    }

    public static bool TryPasteInto(nint targetWindow)
    {
        if (!NativeMethods.TryRestoreForegroundWindow(targetWindow))
        {
            AppLogger.Error("Could not restore the previously active window; Ctrl+V was not sent.");
            return false;
        }
        if (!NativeMethods.SendCtrlV())
        {
            AppLogger.Error("SendInput failed while simulating Ctrl+V.");
            return false;
        }
        return true;
    }
}

