using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NoteManager.App.Infrastructure;
using NoteManager.App.Services;

namespace NoteManager.App.Views;

public partial class AssignTagsWindow : Window
{
    private static readonly Brush ActiveModeBackground =
        new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly Brush ActiveModeBorder =
        new SolidColorBrush(Color.FromRgb(0x00, 0x6C, 0xBE));
    private static readonly Brush InactiveModeBackground =
        new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush InactiveModeBorder =
        new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC2));
    private static readonly Brush ActiveModeForeground = Brushes.White;
    private static readonly Brush InactiveModeForeground =
        new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));

    private readonly Dictionary<string, AssignableTagItem> _items =
        new(StringComparer.Ordinal);
    private readonly List<string> _recentTags;
    private readonly List<string> _allTags;
    private bool _showRecentTags = true;
    private bool _isBulkUpdating;

    public AssignTagsWindow(TagAssignmentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        InitializeComponent();

        AssignmentHeading.Text =
            $"Assign tags to note: \"{context.NoteFileName}\"";
        _recentTags = context.SelectedTags
            .Concat(context.RecentTags)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _allTags = context.AllTags
            .Concat(context.SelectedTags)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

        foreach (var tag in _allTags.Concat(_recentTags).Distinct(StringComparer.Ordinal))
        {
            AddItem(tag, context.SelectedTags.Contains(tag, StringComparer.Ordinal));
        }

        RefreshVisibleTags();
        Loaded += (_, _) => TagSearchBox.Focus();
    }

    public ObservableCollection<AssignableTagItem> VisibleTags { get; } = [];

    public IReadOnlyList<string> SelectedTags { get; private set; } = [];

    private AssignableTagItem AddItem(string tag, bool isSelected)
    {
        var normalizedTag = tag.Trim().ToLowerInvariant();
        if (_items.TryGetValue(normalizedTag, out var existing))
        {
            if (isSelected)
            {
                existing.IsSelected = true;
            }

            return existing;
        }

        var item = new AssignableTagItem(normalizedTag, isSelected);
        item.PropertyChanged += TagItem_OnPropertyChanged;
        _items.Add(normalizedTag, item);
        return item;
    }

    private void TagItem_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_isBulkUpdating || e.PropertyName != nameof(AssignableTagItem.IsSelected))
        {
            return;
        }

        UpdateSelectedCount();
        if (HideUnassignedTagsCheckBox.IsChecked == true
            && sender is AssignableTagItem { IsSelected: false })
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                RefreshVisibleTags);
        }
    }

    private void RecentTagsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _showRecentTags = true;
        UpdateModeAppearance();
        RefreshVisibleTags();
    }

    private void AllTagsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _showRecentTags = false;
        UpdateModeAppearance();
        RefreshVisibleTags();
    }

    private void UpdateModeAppearance()
    {
        RecentTagsButton.Background = _showRecentTags
            ? ActiveModeBackground
            : InactiveModeBackground;
        RecentTagsButton.Foreground = _showRecentTags
            ? ActiveModeForeground
            : InactiveModeForeground;
        RecentTagsButton.BorderBrush = _showRecentTags
            ? ActiveModeBorder
            : InactiveModeBorder;

        AllTagsButton.Background = _showRecentTags
            ? InactiveModeBackground
            : ActiveModeBackground;
        AllTagsButton.Foreground = _showRecentTags
            ? InactiveModeForeground
            : ActiveModeForeground;
        AllTagsButton.BorderBrush = _showRecentTags
            ? InactiveModeBorder
            : ActiveModeBorder;
    }

    private void TagSearchBox_OnTextChanged(object sender, RoutedEventArgs e)
        => RefreshVisibleTags();

    private void HideUnassignedTagsCheckBox_OnClick(
        object sender,
        RoutedEventArgs e)
        => RefreshVisibleTags();

    private void RefreshVisibleTags()
    {
        if (!IsInitialized)
        {
            return;
        }

        var searchText = TagSearchBox.Text.Trim();
        var hideUnassigned = HideUnassignedTagsCheckBox.IsChecked == true;
        var source = _showRecentTags ? _recentTags : _allTags;
        var matchingItems = source
            .Distinct(StringComparer.Ordinal)
            .Select(tag => _items[tag])
            .Where(item =>
                (!hideUnassigned || item.IsSelected)
                && (searchText.Length == 0
                    || item.Name.Contains(
                        searchText,
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        VisibleTags.Clear();
        foreach (var item in matchingItems)
        {
            VisibleTags.Add(item);
        }

        TagListDescription.Text = _showRecentTags
            ? $"50 most recently used tags in this folder · {matchingItems.Length:N0} shown"
            : $"All tags in this folder · {matchingItems.Length:N0} shown";
        UpdateSelectedCount();
    }

    private void SelectAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isBulkUpdating = true;
        try
        {
            foreach (var item in VisibleTags)
            {
                item.IsSelected = true;
            }
        }
        finally
        {
            _isBulkUpdating = false;
        }

        UpdateSelectedCount();
    }

    private void ClearAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isBulkUpdating = true;
        try
        {
            foreach (var item in _items.Values)
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _isBulkUpdating = false;
        }

        RefreshVisibleTags();
    }

    private void AddTagsButton_OnClick(object sender, RoutedEventArgs e)
        => AddEnteredTags();

    private void NewTagsTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        AddEnteredTags();
    }

    private bool AddEnteredTags()
    {
        var result = MarkdownTagEditorService.ParseTagInput(NewTagsTextBox.Text);
        if (result.InvalidTags.Length > 0)
        {
            ShowValidation(
                "These tag names contain unsupported characters: "
                + string.Join(", ", result.InvalidTags));
            return false;
        }

        if (result.Tags.Length == 0)
        {
            ShowValidation("Enter at least one tag name.");
            return false;
        }

        _isBulkUpdating = true;
        try
        {
            foreach (var tag in result.Tags)
            {
                AddItem(tag, isSelected: true);
                _recentTags.Remove(tag);
                _recentTags.Insert(0, tag);
                if (!_allTags.Contains(tag, StringComparer.Ordinal))
                {
                    _allTags.Add(tag);
                }
            }

            _allTags.Sort(StringComparer.Ordinal);
        }
        finally
        {
            _isBulkUpdating = false;
        }

        NewTagsTextBox.Clear();
        HideValidation();
        RefreshVisibleTags();
        return true;
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NewTagsTextBox.Text)
            && !AddEnteredTags())
        {
            return;
        }

        var invalidSelections = _items.Values
            .Where(item =>
                item.IsSelected
                && !MarkdownTagEditorService.IsValidTag(item.Name))
            .Select(item => item.Name)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        if (invalidSelections.Length > 0)
        {
            ShowValidation(
                "Remove or replace these invalid existing tags: "
                + string.Join(", ", invalidSelections));
            return;
        }

        SelectedTags = _items.Values
            .Where(item => item.IsSelected)
            .Select(item => item.Name)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        DialogResult = true;
    }

    private void UpdateSelectedCount()
    {
        var count = _items.Values.Count(item => item.IsSelected);
        SelectedCountText.Text = count == 1
            ? "1 tag selected"
            : $"{count:N0} tags selected";
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
        NewTagsTextBox.Focus();
        NewTagsTextBox.SelectAll();
    }

    private void HideValidation()
    {
        ValidationText.Text = string.Empty;
        ValidationText.Visibility = Visibility.Collapsed;
    }
}

public sealed class AssignableTagItem : ObservableObject
{
    private bool _isSelected;

    public AssignableTagItem(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
