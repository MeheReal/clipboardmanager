using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Interop;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;
using Forms = System.Windows.Forms;

namespace ClipboardManager;

public partial class MainWindow : Window
{
    private const int VisibleResultLimit = 5;


    private readonly ClipboardPasteService _pasteService = new();
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<ClipboardItem> _visibleResults = [];
    private nint _returnTarget;
    private bool _isPasting;
    private bool _refreshPending;
    private bool _refreshingVisibleResults;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        HistoryList.ItemsSource = _visibleResults;
        _viewModel.FilteredItems.CollectionChanged += FilteredItems_CollectionChanged;
        RefreshVisibleResults();
    }


    public void EnsureNativeWindowHandle()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
    }

    public void ShowOverlay(nint previouslyActiveWindow)
    {
        _returnTarget = previouslyActiveWindow;

        // Clear the query and reset position every time the overlay is opened.
        _viewModel.SearchText = string.Empty;
        RefreshVisibleResults();

        var activeScreen = previouslyActiveWindow != nint.Zero
            ? Forms.Screen.FromHandle(previouslyActiveWindow)
            : Forms.Screen.FromPoint(Forms.Cursor.Position);
        var workArea = activeScreen.WorkingArea;
        Left = workArea.Left + (workArea.Width - Width) / 2d;
        Top = workArea.Top + 64;

        if (!IsVisible)
            Show();

        Activate();
        FocusSearchBox();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsVisible)
                return;

            Activate();
            FocusSearchBox();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusSearchBox()
    {
        Focus();
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
        SearchTextBox.CaretIndex = SearchTextBox.Text?.Length ?? 0;
    }

    private void FilteredItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshPending)
            return;

        _refreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshPending = false;
            RefreshVisibleResults();
        }), System.Windows.Threading.DispatcherPriority.DataBind);
    }

    private void RefreshVisibleResults()
    {
        var previousSelection = _viewModel.SelectedItem;
        _refreshingVisibleResults = true;

        _visibleResults.Clear();
        foreach (var item in _viewModel.FilteredItems.Take(VisibleResultLimit))
            _visibleResults.Add(item);

        var selection = previousSelection is not null && _visibleResults.Contains(previousSelection)
            ? previousSelection
            : _visibleResults.FirstOrDefault();

        HistoryList.SelectedItem = selection;
        _viewModel.SelectedItem = selection;
        _refreshingVisibleResults = false;

        if (selection is not null)
            HistoryList.ScrollIntoView(selection);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingVisibleResults)
            _viewModel.SelectedItem = HistoryList.SelectedItem as ClipboardItem;
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SearchTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = System.Windows.Media.Brushes.DodgerBlue;
    }

    private void SearchTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(59, 130, 160));
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible && !_isPasting)
            Hide();
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Up or Key.Down)
        {
            MoveSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var selected = _viewModel.SelectedItem ?? _visibleResults.FirstOrDefault();
            if (selected is not null)
                await PasteSelectedAsync(selected);
        }
    }

    private void MoveSelection(int direction)
    {
        if (_visibleResults.Count == 0)
            return;

        var currentIndex = _viewModel.SelectedItem is null
            ? -1
            : _visibleResults.IndexOf(_viewModel.SelectedItem);

        var nextIndex = direction > 0
            ? Math.Min(currentIndex + 1, _visibleResults.Count - 1)
            : Math.Max(currentIndex < 0 ? 0 : currentIndex - 1, 0);

        var selection = _visibleResults[nextIndex];
        HistoryList.SelectedItem = selection;
        _viewModel.SelectedItem = selection;
        HistoryList.ScrollIntoView(selection);
    }

    private async void HistoryList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (IsInsideButton(source))
            return;

        var clickedItem = FindListBoxItem(source)?.DataContext as ClipboardItem;
        if (clickedItem is not null)
            await PasteSelectedAsync(clickedItem);
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            if (element is ListBoxItem)
                return false;

            element = GetParent(element);
        }

        return false;
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button { DataContext: ClipboardItem item })
            return;

        try
        {
            await _viewModel.DeleteItemAsync(item);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not delete the clipboard history item.", ex);
            System.Windows.MessageBox.Show(this, "This clipboard item could not be deleted.", "Clipboard Manager",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
    private async Task PasteSelectedAsync(ClipboardItem item)
    {
        if (_isPasting)
            return;

        _isPasting = true;
        try
        {
            if (!await _pasteService.SetClipboardTextAsync(item))
                return;

            _viewModel.RecordLastPastedItem(item.TextContent);
            Hide();
            await Task.Delay(80);
            _ = ClipboardPasteService.TryPasteInto(_returnTarget);
        }
        finally
        {
            _isPasting = false;
        }
    }


    private static ListBoxItem? FindListBoxItem(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ListBoxItem item)
                return item;

            element = GetParent(element);
        }

        return null;
    }

    private void WindowSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveControl(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // The drag can be interrupted if the overlay is dismissed mid-gesture.
        }
    }

    private static bool IsInteractiveControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.TextBoxBase or
                ListBoxItem or
                System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            element = GetParent(element);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual or Visual3D)
            return VisualTreeHelper.GetParent(element);
        if (element is FrameworkContentElement frameworkContentElement)
            return frameworkContentElement.Parent;
        if (element is ContentElement contentElement)
            return ContentOperations.GetParent(contentElement);

        return LogicalTreeHelper.GetParent(element);
    }
}


