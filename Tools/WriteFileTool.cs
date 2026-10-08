using System.ComponentModel;
using System.Text;

internal static class WriteFileTool
{
    private const long MaxWritableFileBytes = 5 * 1024 * 1024;

    public static async Task<string> WriteFileAsync(
        [Description("The destination inside the selected adventure directory. A bare filename is automatically placed there. Use this to create the initial Markdown artifact or deliberately replace the whole file.")] string path,
        [Description("The complete contents for the initial file or full replacement. Use AppendFileAsync for later completed sections.")] string contents,
        [Description("A concise explanation of what is being written and why this file is needed.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"[Tools] WriteFileAsync called: path='{path}', contents={contents.Length:N0} characters, reason='{ToolLog.OneLine(reason)}'");
        var (workspaceRoot, fullPath) = ToolWorkspace.ResolveAdventurePath(path);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
        var extension = Path.GetExtension(fullPath);

        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only Markdown and text files can be written by this tool.", nameof(path));
        }

        var byteCount = Encoding.UTF8.GetByteCount(contents);
        if (byteCount > MaxWritableFileBytes)
        {
            throw new ArgumentException("The output file must be 5 MB or smaller.", nameof(contents));
        }

        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (parentDirectory is not null)
        {
            Console.WriteLine($"[Tools] WriteFileAsync ensuring directory: '{Path.GetRelativePath(workspaceRoot, parentDirectory)}'");
            Directory.CreateDirectory(parentDirectory);
        }

        Console.WriteLine($"[Tools] WriteFileAsync writing: '{relativePath}' ({byteCount:N0} bytes)");
        await AtomicFile.WriteAllTextAsync(fullPath, contents, cancellationToken);
        var writtenBytes = new FileInfo(fullPath).Length;
        await RunStateManager.RecordArtifactCheckpointAsync(relativePath, writtenBytes, reason);
        Console.WriteLine($"[Tools] WriteFileAsync completed: '{relativePath}'");
        return $"Wrote {relativePath} ({writtenBytes:N0} bytes).";
    }
}
