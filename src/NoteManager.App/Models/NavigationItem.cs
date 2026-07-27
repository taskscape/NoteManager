namespace NoteManager.App.Models;

public sealed class NavigationItem
{
    public required string Label { get; init; }
    public string Count { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE8A5";
    public int Level { get; init; }
    public bool IsHeader { get; init; }
    public bool IsSelectable { get; init; } = true;
    public bool IsAccent { get; init; }
    public string FilterKey { get; init; } = string.Empty;
}
