using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClipboardManager.Services;

public sealed class TrayIconService : IDisposable
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClipboardManager";
    private static readonly Color SurfaceColor = Color.FromArgb(24, 27, 36);
    private static readonly Color BorderColor = Color.FromArgb(45, 55, 72);
    private static readonly Color AccentColor = Color.FromArgb(0, 120, 212);
    private static readonly Color TextColor = Color.FromArgb(245, 247, 250);

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Action<bool> _toggleCapture;
    private readonly Func<Task> _checkForUpdates;
    private readonly Forms.ToolStripMenuItem _captureMenuItem;
    private readonly Forms.ToolStripMenuItem _autoStartMenuItem;
    private readonly Forms.ToolStripMenuItem _checkForUpdatesMenuItem;
    private readonly RoundedContextMenuStrip _menu;
    private readonly Icon _ownedIcon;
    private readonly nint _iconHandle;
    private bool _disposed;

    public TrayIconService(Action showHistory, Action<bool> toggleCapture, Func<Task> checkForUpdates, Action exit)
    {
        _toggleCapture = toggleCapture;
        _checkForUpdates = checkForUpdates;

        _menu = new RoundedContextMenuStrip
        {
            Renderer = new RoundedMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = SurfaceColor,
            ForeColor = TextColor,
            Padding = new Forms.Padding(5),
            DropShadowEnabled = true
        };

        var showMenuItem = CreateMenuItem("Open clipboard history");
        _captureMenuItem = CreateMenuItem("Capture clipboard");
        _captureMenuItem.Checked = true;
        _captureMenuItem.CheckOnClick = true;
        _captureMenuItem.Click += OnCaptureToggled;

        _autoStartMenuItem = CreateMenuItem("Auto start on Windows start");
        _autoStartMenuItem.CheckOnClick = true;
        _autoStartMenuItem.Click += OnAutoStartToggled;
        try
        {
            _autoStartMenuItem.Checked = IsAutoStartRegistered();
        }
        catch (Exception ex)
        {
            _autoStartMenuItem.Checked = false;
            AppLogger.Error("Could not read the Clipboard Manager auto-start setting.", ex);
        }

        _checkForUpdatesMenuItem = CreateMenuItem("Check for updates");
        _checkForUpdatesMenuItem.Click += OnCheckForUpdatesClicked;
        var quitMenuItem = CreateMenuItem("Quit ClipboardManager");
        showMenuItem.Click += (_, _) => showHistory();
        quitMenuItem.Click += (_, _) => exit();

        var separator = new Forms.ToolStripSeparator
        {
            AutoSize = false,
            Size = new Size(280, 1),
            Margin = new Forms.Padding(8, 5, 8, 5)
        };

        _menu.Items.Add(showMenuItem);
        _menu.Items.Add(_captureMenuItem);
        _menu.Items.Add(_autoStartMenuItem);
        _menu.Items.Add(separator);
        _menu.Items.Add(_checkForUpdatesMenuItem);
        _menu.Items.Add(new Forms.ToolStripSeparator
        {
            AutoSize = false,
            Size = new Size(280, 1),
            Margin = new Forms.Padding(8, 5, 8, 5)
        });
        _menu.Items.Add(quitMenuItem);

        var iconHandle = nint.Zero;
        Icon ownedIcon;
        try
        {
            iconHandle = CreateClipboardIconHandle();
            ownedIcon = Icon.FromHandle(iconHandle);
        }
        catch (Exception ex)
        {
            if (iconHandle != nint.Zero)
                DestroyIcon(iconHandle);

            AppLogger.Error("The custom tray icon could not be created; using the Windows fallback icon.", ex);
            iconHandle = nint.Zero;
            ownedIcon = (Icon)SystemIcons.Application.Clone();
        }

        _iconHandle = iconHandle;
        _ownedIcon = ownedIcon;
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "Clipboard Manager",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showHistory();
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_disposed)
            _notifyIcon.ShowBalloonTip(3_000, title, message, Forms.ToolTipIcon.Warning);
    }

    private static Forms.ToolStripMenuItem CreateMenuItem(string text) => new(text)
    {
        AutoSize = false,
        Size = new Size(280, 36),
        Margin = new Forms.Padding(3, 2, 3, 2),
        Padding = new Forms.Padding(9, 0, 12, 0),
        BackColor = SurfaceColor,
        ForeColor = TextColor,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point)
    };

    private void OnCaptureToggled(object? sender, EventArgs e) => _toggleCapture(_captureMenuItem.Checked);

    private void OnAutoStartToggled(object? sender, EventArgs e)
    {
        var requestedState = _autoStartMenuItem.Checked;
        try
        {
            SetAutoStart(requestedState);
        }
        catch (Exception ex)
        {
            _autoStartMenuItem.Checked = !requestedState;
            AppLogger.Error("Could not update the Clipboard Manager auto-start setting.", ex);
            ShowBalloon("Clipboard Manager", "The Windows startup setting could not be changed.");
        }
    }

    private async void OnCheckForUpdatesClicked(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        _checkForUpdatesMenuItem.Enabled = false;
        try
        {
            await _checkForUpdates();
        }
        catch (Exception ex)
        {
            AppLogger.Error("The tray update command failed.", ex);
            ShowBalloon("Clipboard Manager", "The update check could not be completed.");
        }
        finally
        {
            if (!_disposed)
                _checkForUpdatesMenuItem.Enabled = true;
        }
    }
    private static bool IsAutoStartRegistered()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = runKey?.GetValue(RunValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void SetAutoStart(bool enabled)
    {
        if (enabled)
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");
            runKey.SetValue(RunValueName, GetAutoStartCommand());
            return;
        }

        using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        existingKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static string GetAutoStartCommand()
    {
        // Prefer the apphost executable so Windows can launch the app directly.
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetFileNameWithoutExtension(processPath).Equals("ClipboardManager", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\"";
        }

        // Running through dotnet requires both the host and this assembly path.
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "ClipboardManager";
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\" \"{assemblyPath}\"";
        }

        return $"\"{assemblyPath}\"";
    }

    private static nint CreateClipboardIconHandle()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var tileBrush = new SolidBrush(Color.FromArgb(52, 112, 190));
        using var tilePath = CreateRoundedRectangle(new Rectangle(1, 1, 30, 30), 8);
        graphics.FillPath(tileBrush, tilePath);

        using var paperPen = new Pen(Color.White, 2.2f);
        using var clipBrush = new SolidBrush(Color.FromArgb(220, 238, 255));
        graphics.DrawRectangle(paperPen, 9, 8, 14, 17);
        graphics.FillRectangle(clipBrush, 12, 6, 8, 4);
        graphics.DrawLine(paperPen, 12, 13, 20, 13);
        graphics.DrawLine(paperPen, 12, 17, 20, 17);
        graphics.DrawLine(paperPen, 12, 21, 18, 21);

        return bitmap.GetHicon();
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _ownedIcon.Dispose();
        if (_iconHandle != nint.Zero)
            DestroyIcon(_iconHandle);
    }

    private sealed class RoundedContextMenuStrip : Forms.ContextMenuStrip
    {
        public RoundedContextMenuStrip()
        {
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0 || Height <= 0)
                return;

            using var path = CreateRoundedRectangle(new Rectangle(0, 0, Width, Height), 8);
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
        }
    }

    private sealed class RoundedMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public RoundedMenuRenderer() : base(new DarkMenuColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var path = CreateRoundedRectangle(bounds, 8);
            using var brush = new SolidBrush(SurfaceColor);
            using var pen = new Pen(BorderColor, 1);
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            // The rounded outline is drawn with the background to avoid square default borders.
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = e.Item.Bounds;
            bounds.Inflate(-4, -1);
            using var path = CreateRoundedRectangle(bounds, 5);
            using var brush = new SolidBrush(AccentColor);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(BorderColor, 1);
            var width = e.ToolStrip?.Width ?? e.Item.Width;
            e.Graphics.DrawLine(pen, 10, y, width - 10, y);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item is not Forms.ToolStripMenuItem { Checked: true })
            {
                base.OnRenderItemCheck(e);
                return;
            }

            var rect = e.ImageRectangle;
            using var pen = new Pen(Color.White, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(pen,
            [
                new Point(rect.Left + 3, rect.Top + rect.Height / 2),
                new Point(rect.Left + rect.Width / 2 - 1, rect.Bottom - 4),
                new Point(rect.Right - 2, rect.Top + 3)
            ]);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TextColor : Color.FromArgb(125, 135, 150);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkMenuColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => SurfaceColor;
        public override Color ImageMarginGradientBegin => SurfaceColor;
        public override Color ImageMarginGradientMiddle => SurfaceColor;
        public override Color ImageMarginGradientEnd => SurfaceColor;
        public override Color MenuBorder => BorderColor;
        public override Color MenuItemBorder => AccentColor;
        public override Color MenuItemSelected => AccentColor;
        public override Color MenuItemSelectedGradientBegin => AccentColor;
        public override Color MenuItemSelectedGradientEnd => AccentColor;
        public override Color SeparatorDark => BorderColor;
        public override Color SeparatorLight => BorderColor;
    }
}

