using System.ComponentModel;
using System.Text;

internal static class AppendFileTool
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const long MaxAppendBytes = 1 * 1024 * 1024;

    public static async Task<string> AppendFileAsync(
        [Description("The existing Markdown file to append to inside the selected adventure directory. A bare filename is automatically placed there.")] string path,
        [Description("One or more complete Markdown sections to append in final reading order. Do not include drafts, placeholders, or status messages.")] string contents,
        [Description("A concise explanation of which completed sections are being checkpointed and why they are ready to append.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"[Tools] AppendFileAsync called: path='{path}', contents={contents.Length:N0} characters, reason='{ToolLog.OneLine(reason)}'");

        if (string.IsNullOrWhiteSpace(contents))
        {
            throw new ArgumentException("The Markdown content to append cannot be empty.", nameof(contents));
        }

        var (workspaceRoot, fullPath) = ToolWorkspace.ResolveAdventurePath(path);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only Markdown files can be appended by this tool.", nameof(path));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The Markdown file must be created with WriteFileAsync before content can be appended.",
                fullPath);
        }

        var section = contents.Trim();
        var sectionBytes = Encoding.UTF8.GetBytes(section);
        if (sectionBytes.LongLength > MaxAppendBytes)
        {
            throw new ArgumentException("A single append must be 1 MB or smaller.", nameof(contents));
        }

        var existingLength = new FileInfo(fullPath).Length;
        string separator;
        await using (var stream = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 16 * 1024,
                         FileOptions.Asynchronous))
        {
            separator = await SelectSeparatorAsync(stream, cancellationToken);
        }

        var separatorBytes = Encoding.UTF8.GetBytes(separator);
        var finalNewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);
        var resultingLength = existingLength + separatorBytes.Length + sectionBytes.Length + finalNewLineBytes.Length;
        if (resultingLength > MaxFileBytes)
        {
            throw new ArgumentException("The resulting Markdown file must be 5 MB or smaller.", nameof(contents));
        }

        await AtomicFile.AppendAllTextAsync(fullPath, separator, section, cancellationToken);
        await RunStateManager.RecordArtifactCheckpointAsync(relativePath, resultingLength, reason);

        Console.WriteLine(
            $"[Tools] AppendFileAsync completed: '{relativePath}', appended={sectionBytes.Length:N0} bytes, total={resultingLength:N0} bytes");
        return $"Appended {sectionBytes.Length:N0} bytes to {relativePath}; file is now {resultingLength:N0} bytes.";
    }

    private static async Task<string> SelectSeparatorAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length == 0)
        {
            return string.Empty;
        }

        stream.Seek(-1, SeekOrigin.End);
        var finalByte = new byte[1];
        await stream.ReadExactlyAsync(finalByte, cancellationToken);
        return finalByte[0] == (byte)'\n'
            ? Environment.NewLine
            : $"{Environment.NewLine}{Environment.NewLine}";
    }
}
