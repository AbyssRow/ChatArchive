using ChatArchive.Core.Data;

namespace ChatArchive.Core.Importing;

public enum ImportPhase
{
    Discover,
    Importing,
    Finalizing,
    Done,
    Failed,
}

public sealed record ImportProgress(
    ImportPhase Phase,
    int FilesDone,
    int FilesTotal,
    string CurrentFile,
    long MessagesSeen,
    long Added,
    long Duplicates,
    long Revised,
    long Variants,
    long Attachments,
    long MissingMedia);

public sealed record FileImportResult(
    string Path,
    string Platform,
    string Status,
    long MessagesSeen,
    long Added,
    long Duplicates,
    long Revised,
    long Variants,
    long Attachments,
    long MissingMedia,
    string? Error);

public sealed record ImportRunResult(
    IReadOnlyList<FileImportResult> Files,
    long FilesFound,
    long FilesImported,
    long FilesSkipped,
    long FilesFailed,
    long MessagesSeen,
    long Added,
    long Duplicates,
    long Revised,
    long Variants,
    long Attachments,
    long MissingMedia)
{
    public static ImportRunResult Empty { get; } = new(
        Array.Empty<FileImportResult>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
