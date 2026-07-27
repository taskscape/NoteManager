using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed record TagAssignmentContext(
    string NoteFileName,
    string[] SelectedTags,
    string[] RecentTags,
    string[] AllTags);

public static class TagAssignmentService
{
    public static TagAssignmentContext CreateContext(
        IEnumerable<NoteItem> notes,
        NoteItem selectedNote,
        int recentTagLimit = 50)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(selectedNote);
        ArgumentOutOfRangeException.ThrowIfNegative(recentTagLimit);

        var noteList = notes.ToArray();
        var selectedTags = LowercaseDistinct(selectedNote.Tags);
        var recentTags = LowercaseDistinct(
                noteList.SelectMany(note => note.Tags))
            .Take(recentTagLimit)
            .ToArray();
        var allTags = LowercaseDistinct(
                noteList.SelectMany(note => note.Tags))
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();

        return new TagAssignmentContext(
            selectedNote.FileName,
            selectedTags,
            recentTags,
            allTags);
    }

    private static string[] LowercaseDistinct(IEnumerable<string> tags)
        => tags
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
