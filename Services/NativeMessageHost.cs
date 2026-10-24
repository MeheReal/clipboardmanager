using System.Windows.Interop;

namespace ClipboardManager.Services;

/// <summary>
/// Installs the clipboard listener and global hotkey. The app creates this host
/// only from MainWindow.SourceInitialized, after the WPF window HWND is valid.
/// </summary>
public sealed class NativeMessageHost : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private const int HotKeyId = 0xC1A0;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyV = 0x56;
    private readonly Action _onHotKey;
    private readonly Func<Task> _onClipboardUpdate;
    private readonly HwndSource _source;
    private bool _disposed;

    public NativeMessageHost(Func<Task> onClipboardUpdate, Action onHotKey)
    {
        _onClipboardUpdate = onClipboardUpdate;
        _onHotKey = onHotKey;

        var parameters = new HwndSourceParameters("ClipboardManager.NativeMessageHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new nint(-3) // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowProcedure);

        var clipboardListenerRegistered = false;
        try
        {
            clipboardListenerRegistered = NativeMethods.AddClipboardFormatListener(_source.Handle);
        }
        catch (Exception ex)
        {
            AppLogger.Error("AddClipboardFormatListener threw while registering the clipboard listener.", ex);
        }

        var hotKeyRegistered = false;
        try
        {
            hotKeyRegistered = NativeMethods.RegisterHotKey(
                _source.Handle,
                HotKeyId,
                ModAlt | ModNoRepeat,
                VirtualKeyV);
        }
        catch (Exception ex)
        {
            // A missing Win32 entry point or unavailable hotkey must not abort startup.
            AppLogger.Error("RegisterHotKey threw while registering Alt+V.", ex);
        }

        ClipboardListenerRegistered = clipboardListenerRegistered;
        HotKeyRegistered = hotKeyRegistered;
    }

    public bool ClipboardListenerRegistered { get; }
    public bool HotKeyRegistered { get; }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            handled = true;
            _ = _onClipboardUpdate();
        }
        else if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            _onHotKey();
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ClipboardListenerRegistered)
            NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        if (HotKeyRegistered)
            NativeMethods.UnregisterHotKey(_source.Handle, HotKeyId);
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }
}
