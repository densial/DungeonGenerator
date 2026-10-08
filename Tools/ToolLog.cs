internal static class ToolLog
{
    private const int MaxLoggedValueLength = 500;

    public static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "not provided";
        }

        var oneLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return oneLine.Length <= MaxLoggedValueLength
            ? oneLine
            : $"{oneLine[..MaxLoggedValueLength]}…";
    }
}
