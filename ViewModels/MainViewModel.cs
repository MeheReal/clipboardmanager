using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ClipboardManager.Models;
using ClipboardManager.Services;

namespace ClipboardManager.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int LastPastedPreviewLength = 35;
    private static readonly string LastPastedStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardManager",
        "last-pasted.txt");

    private string _searchText = string.Empty;
    private string _lastPastedItemText = LoadLastPastedItemText();
    private ClipboardItem? _selectedItem;

    public MainViewModel(ClipboardDatabase database) => Database = database;

    private ClipboardDatabase Database { get; }
    public ObservableCollection<ClipboardItem> Items { get; } = [];
    public ObservableCollection<ClipboardItem> FilteredItems { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;
            _searchText = value;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    public string LastPastedItemText
    {
        get => _lastPastedItemText;
        set
        {
            var preview = CreateLastPastedPreview(value);
            if (_lastPastedItemText == preview)
                return;

            _lastPastedItemText = preview;
            OnPropertyChanged();
            SaveLastPastedItemText(preview);
        }
    }

    public ClipboardItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Equals(_selectedItem, value))
                return;
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public bool HasNoResults => FilteredItems.Count == 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RecordLastPastedItem(string text) => LastPastedItemText = text;

    public async Task LoadHistoryAsync()
    {
        var recent = await Database.GetRecentAsync();
        Items.Clear();
        foreach (var item in recent)
            Items.Add(item);
        RefreshFilter();
    }

    public async Task<bool> DeleteItemAsync(ClipboardItem item)
    {
        if (!await Database.DeleteAsync(item.Id))
            return false;

        Items.Remove(item);
        FilteredItems.Remove(item);
        if (Equals(SelectedItem, item))
            SelectedItem = FilteredItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
        return true;
    }
    public void AddItem(ClipboardItem item)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => AddItem(item)));
            return;
        }

        Items.Insert(0, item);
        while (Items.Count > ClipboardDatabase.HistoryLimit)
            Items.RemoveAt(Items.Count - 1);
        RefreshFilter();
    }

    private void RefreshFilter()
    {
        var query = SearchText.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? Items.ToArray()
            : Items.Where(item => item.TextContent.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        FilteredItems.Clear();
        foreach (var match in matches)
            FilteredItems.Add(match);
        SelectedItem = FilteredItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }

    private static string LoadLastPastedItemText()
    {
        try
        {
            if (!File.Exists(LastPastedStatePath))
                return "Nothing yet";

            var savedText = File.ReadAllText(LastPastedStatePath);
            return string.IsNullOrWhiteSpace(savedText)
                ? "Nothing yet"
                : CreateLastPastedPreview(savedText);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not load the last pasted clipboard item.", ex);
            return "Nothing yet";
        }
    }

    private static string CreateLastPastedPreview(string? text)
    {
        var singleLineText = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(singleLineText))
            return "Empty text";

        return singleLineText.Length > LastPastedPreviewLength
            ? $"{singleLineText[..LastPastedPreviewLength]}..."
            : singleLineText;
    }

    private static void SaveLastPastedItemText(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastPastedStatePath)!);
            File.WriteAllText(LastPastedStatePath, text);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not save the last pasted clipboard item.", ex);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

