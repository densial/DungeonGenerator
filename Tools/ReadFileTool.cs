using System.Collections.Concurrent;
using System.ComponentModel;

internal static class ReadFileTool
{
    private const long MaxTextFileBytes = 2 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, byte> DeliveredTemplates =
        new(StringComparer.Ordinal);

    private static readonly HashSet<string> SupportedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".markdown",
        ".json",
        ".yaml",
        ".yml",
        ".xml",
        ".csv"
    };

    public static async Task<string> ReadFileAsync(
        [Description("The listed path to a shared root file or a file in the selected adventure directory. Use ListFilesAsync first to discover the exact path.")] string path,
        [Description("A concise explanation of what information is expected from this file and why it is needed.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] ReadFileAsync called: path='{path}', reason='{ToolLog.OneLine(reason)}'");
        var (workspaceRoot, fullPath) = ToolWorkspace.ResolveReadPath(path);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
        var isGenerationTemplate = Path.GetFileName(fullPath).Equals(
            "ADVENTURE_GENERATION_TEMPLATE.md",
            StringComparison.OrdinalIgnoreCase);

        if (isGenerationTemplate && !DeliveredTemplates.TryAdd(fullPath, 0))
        {
            Console.WriteLine($"[Tools] ReadFileAsync suppressed duplicate template delivery: '{relativePath}'");
            return
                $"The template '{relativePath}' was already delivered earlier in this run. " +
                "Reuse its section outline from the primary conversation or delegated source brief. " +
                "A delegated session without that brief must return a missing-context result instead of rereading the template.";
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("The file was not found.", fullPath);
            }

            var extension = Path.GetExtension(fullPath);
            if (!SupportedTextExtensions.Contains(extension))
            {
                throw new ArgumentException(
                    "Only text, Markdown, JSON, YAML, XML, and CSV files can be read by this tool.",
                    nameof(path));
            }

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxTextFileBytes)
            {
                throw new ArgumentException("The text file must be 2 MB or smaller.", nameof(path));
            }

            Console.WriteLine($"[Tools] ReadFileAsync reading: '{relativePath}' ({fileInfo.Length:N0} bytes)");
            var contents = await File.ReadAllTextAsync(fullPath, cancellationToken);
            Console.WriteLine($"[Tools] ReadFileAsync completed: '{relativePath}' ({contents.Length:N0} characters)");
            return $"File: {relativePath}\n\n{contents}";
        }
        catch
        {
            if (isGenerationTemplate)
            {
                DeliveredTemplates.TryRemove(fullPath, out _);
            }

            throw;
        }
    }
}
