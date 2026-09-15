namespace ChatArchive.Core.Migration;

public sealed record MigrationCliParseResult(
    bool Success,
    string? From,
    string? To,
    string? UnknownArgument = null);

public static class MigrationCli
{
    public static MigrationCliParseResult Parse(string[] args)
    {
        string? from = null;
        string? to = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length:
                    from = args[++i];
                    break;
                case "--to" when i + 1 < args.Length:
                    to = args[++i];
                    break;
                default:
                    return new MigrationCliParseResult(false, from, to, args[i]);
            }
        }

        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        {
            return new MigrationCliParseResult(false, from, to);
        }

        return new MigrationCliParseResult(true, from, to);
    }
}
