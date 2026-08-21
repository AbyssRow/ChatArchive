namespace ChatArchive.App.ViewModels;

public static class UiInputParser
{
    public static (string? Platform, string? Kind) ParseConversationFilter(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return (null, null);
        }

        var parts = tag.Split('|', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && parts[0].Length > 0
            && parts[1].Length > 0
            ? (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant())
            : (null, null);
    }

    public static string PickerExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length is > 1 and <= 12
            && extension.AsSpan(1).ToArray().All(char.IsAsciiLetterOrDigit))
        {
            return extension.ToLowerInvariant();
        }

        return ".png";
    }
}

public sealed class LatestRequestGate
{
    private long _version;

    public long Next() => Interlocked.Increment(ref _version);

    public bool IsCurrent(long version) => version == Interlocked.Read(ref _version);
}
