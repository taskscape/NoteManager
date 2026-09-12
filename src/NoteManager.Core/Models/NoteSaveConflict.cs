namespace NoteManager.App.Models;

/// <summary>
/// Describes a save that was intentionally stopped to preserve both the local
/// draft and a revision changed or removed outside NoteManager.
/// </summary>
public sealed record NoteSaveConflict(
    NoteSaveConflictKind Kind,
    string SourceFilePath,
    string FileName,
    string LocalDraftContent,
    string? BaselineContent,
    string? ExternalContent);

public enum NoteSaveConflictKind
{
    ExternalModification,
    ExternalDeletion,
    BaselineUnavailable
}
