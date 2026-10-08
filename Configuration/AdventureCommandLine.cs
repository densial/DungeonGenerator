internal static class AdventureCommandLine
{
    public const string Usage = "Usage: DungeonGenerator <adventure-directory>\n" +
                                "   or: DungeonGenerator --adventure <adventure-directory>";

    public static AdventureCommandLineResult Parse(string[] args)
    {
        if (args.Length == 1 && (args[0] is "--help" or "-h"))
        {
            return new AdventureCommandLineResult(null, ShowHelp: true, Error: null);
        }

        if (args.Length == 1)
        {
            return new AdventureCommandLineResult(args[0], ShowHelp: false, Error: null);
        }

        if (args.Length == 2 && (args[0] is "--adventure" or "-a"))
        {
            return new AdventureCommandLineResult(args[1], ShowHelp: false, Error: null);
        }

        return new AdventureCommandLineResult(
            null,
            ShowHelp: false,
            Error: "Exactly one adventure subdirectory must be provided.");
    }
}

internal sealed record AdventureCommandLineResult(
    string? AdventureDirectory,
    bool ShowHelp,
    string? Error);
