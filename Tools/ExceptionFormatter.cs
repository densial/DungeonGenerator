using System.Text;

internal static class ExceptionFormatter
{
    public static string FormatDetails(Exception exception)
    {
        var details = new StringBuilder();
        var depth = 0;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            details.AppendLine($"Exception[{depth}]: {current.GetType().FullName}");
            details.AppendLine($"Message[{depth}]: {current.Message}");
            details.AppendLine($"HResult[{depth}]: 0x{current.HResult:X8}");

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                details.AppendLine($"StackTrace[{depth}]:");
                details.AppendLine(current.StackTrace);
            }

            if (current.InnerException is not null)
            {
                details.AppendLine($"InnerException[{depth + 1}]:");
            }

            depth++;
        }

        return details.ToString().TrimEnd();
    }
}
