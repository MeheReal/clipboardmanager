using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;

namespace ClipboardManager;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\ClipboardManager.PowerUser.Singleton";

    private Mutex? _instanceMutex;
    private ClipboardDatabase? _database;
    private ClipboardCaptureService? _captureService;
    private NativeMessageHost? _messageHost;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private readonly GitHubUpdateService _updateService = new();
    private int _updateCheckInProgress;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);
            _instanceMutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            AppLogger.Initialize();
            _trayIcon = new TrayIconService(ShowOverlay, ToggleCapture, CheckForUpdatesAsync, ShutdownApplication);

            try
            {
                _database = new ClipboardDatabase();
                await _database.InitializeAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Clipboard database initialization failed.", ex);
                throw;
            }

            _viewModel = new MainViewModel(_database);
            _mainWindow = new MainWindow(_viewModel);
            _mainWindow.SourceInitialized += MainWindow_SourceInitialized;
            await _viewModel.LoadHistoryAsync();
            _captureService = new ClipboardCaptureService(_database, _viewModel);

            // Create the hidden window's HWND now. SourceInitialized runs before this
            // returns, and is the only place where listener/hotkey registration occurs.
            _mainWindow.EnsureNativeWindowHandle();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Application startup failed.", ex);
            ShowExceptionDialog("Clipboard Manager could not start.", ex);
            Shutdown(-1);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (_messageHost is not null)
            return;

        _messageHost = new NativeMessageHost(
            onClipboardUpdate: () => _captureService is { IsEnabled: true } captureService
                ? captureService.CaptureCurrentClipboardAsync()
                : Task.CompletedTask,
            onHotKey: ShowOverlay);

        if (!_messageHost.ClipboardListenerRegistered)
        {
            AppLogger.Error("AddClipboardFormatListener failed; clipboard capture is unavailable.");
            _trayIcon?.ShowBalloon("Clipboard Manager", "Clipboard monitoring could not be started.");
        }

        if (!_messageHost.HotKeyRegistered)
        {
            AppLogger.Error("RegisterHotKey for Alt+V failed. The shortcut may already be in use.");
            _trayIcon?.ShowBalloon("Clipboard Manager", "Alt+V is unavailable. Another app may already use this shortcut.");
        }
    }

    private void ShowOverlay()
    {
        if (_mainWindow is not null)
            _mainWindow.ShowOverlay(NativeMethods.GetForegroundWindow());
    }

    private void ToggleCapture(bool enabled)
    {
        if (_captureService is null)
        {
            _trayIcon?.ShowBalloon("Clipboard Manager", "Clipboard capture is unavailable because history storage did not start.");
            return;
        }

        _captureService.IsEnabled = enabled;
    }

    private async Task CheckForUpdatesAsync()
    {
        if (Interlocked.Exchange(ref _updateCheckInProgress, 1) != 0)
            return;

        try
        {
            var check = await _updateService.CheckForUpdatesAsync();
            if (check.Release is null)
            {
                System.Windows.MessageBox.Show(
                    $"Clipboard Manager is up to date (version {check.CurrentVersion}).",
                    "Clipboard Manager updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var release = check.Release;
            var choice = System.Windows.MessageBox.Show(
                $"Version {release.TagName} is available (you have {check.CurrentVersion}).\n\n" +
                "Download and install it now? Clipboard Manager will close briefly and reopen when the update is complete.",
                "Clipboard Manager update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (choice != MessageBoxResult.Yes)
                return;

            _trayIcon?.ShowBalloon("Clipboard Manager", $"Downloading version {release.TagName}...");
            var downloadedPath = await _updateService.DownloadAndVerifyAsync(release);

            System.Windows.MessageBox.Show(
                "The update was downloaded and verified. Clipboard Manager will now restart to install it.",
                "Clipboard Manager update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                throw new InvalidOperationException("Windows did not provide the running executable path.");

            _updateService.LaunchInstaller(downloadedPath, processPath, Process.GetCurrentProcess().Id, release.Sha256);
            Shutdown();
        }
        catch (Exception ex)
        {
            AppLogger.Error("The update check or installation failed.", ex);
            System.Windows.MessageBox.Show(
                $"Clipboard Manager could not complete the update.\n\n{ex.Message}",
                "Clipboard Manager update",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckInProgress, 0);
        }
    }
    private void ShutdownApplication() => Shutdown();

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        var details = exception?.ToString() ?? e.ExceptionObject?.ToString() ?? "No exception details were provided.";
        AppLogger.Error("An unhandled application-domain exception occurred.", exception);
        ShowErrorDialog("Clipboard Manager encountered an unhandled error.", details);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        AppLogger.Error("An unobserved background task exception occurred.", e.Exception);
        ShowExceptionDialog("A background task failed.", e.Exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppLogger.Error("An unhandled UI exception occurred.", e.Exception);
        ShowExceptionDialog("Clipboard Manager encountered an unexpected error.", e.Exception);
        Shutdown(-1);
    }

    private static void ShowExceptionDialog(string context, Exception exception) =>
        ShowErrorDialog(context, $"{exception.Message}{Environment.NewLine}{Environment.NewLine}{exception}");

    private static void ShowErrorDialog(string title, string details)
    {
        try
        {
            System.Windows.MessageBox.Show(details, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not display the application error dialog.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeSafely(_messageHost, "native message host");
        DisposeSafely(_trayIcon, "tray icon");
        DisposeSafely(_captureService, "clipboard capture service");
        DisposeSafely(_database, "clipboard database");

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void DisposeSafely(IDisposable? resource, string name)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Could not dispose the {name} cleanly.", ex);
        }
    }
}

