using System.Runtime.CompilerServices;

// The app test suite needs the deterministic Markdown reader seam for transient-access recovery coverage.
[assembly: InternalsVisibleTo("NoteManager.App.Tests")]
