namespace ChatArchive.Core.Models;

public sealed record AttachmentInfo(
    long Id,
    int Ordinal,
    string Kind,
    string? Filename,
    bool IsAvailable,
    string? MimeType,
    int? Width,
    int? Height,
    double? Duration,
    string? DeclaredPath,
    string? ManagedPath,
    string? SourcePath,
    string? MediaSha256);
