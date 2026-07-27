using Avalonia.Controls;
using Avalonia.Interactivity;
using NoteManager.App.Infrastructure;
using NoteManager.App.Services;

namespace NoteManager.Desktop.Dialogs;

public partial class AssignTagsDialog : Window
{
    private readonly List<TagOption> _options;

    public AssignTagsDialog()
        : this(new TagAssignmentContext(string.Empty, [], [], []))
    {
    }

    public AssignTagsDialog(TagAssignmentContext context)
    {
        InitializeComponent();
        HeadingText.Text = $"Assign tags to “{context.NoteFileName}”";
        var selected = context.SelectedTags.ToHashSet(StringComparer.Ordinal);
        _options = context.AllTags
            .Concat(context.SelectedTags)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .Select(tag => new TagOption(tag, selected.Contains(tag)))
            .ToList();
        RefreshOptions();
    }

    public IReadOnlyList<string> SelectedTags { get; private set; } = [];

    private void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
        => RefreshOptions();

    private void RefreshOptions()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        TagItems.ItemsSource = _options.Where(option =>
            query.Length == 0
            || option.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        var parsed = MarkdownTagEditorService.ParseTagInput(NewTagsBox.Text ?? string.Empty);
        if (parsed.InvalidTags.Length > 0)
        {
            ValidationText.Text =
                "Unsupported tag names: " + string.Join(", ", parsed.InvalidTags);
            return;
        }

        SelectedTags = _options
            .Where(option => option.IsSelected)
            .Select(option => option.Name)
            .Concat(parsed.Tags)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);

    private sealed class TagOption(string name, bool isSelected) : ObservableObject
    {
        private bool _isSelected = isSelected;

        public string Name { get; } = name;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
