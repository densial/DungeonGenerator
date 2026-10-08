using System.ComponentModel;

internal static class ListFilesTool
{
    private const int MaxListedFiles = 500;

    public static Task<string[]> ListFilesAsync(
        [Description("A visible directory to list. Use '.' to list shared Workspace root files plus files in the selected adventure directory; other adventure directories are hidden.")] string path,
        [Description("A concise explanation of what this listing is intended to find and why it is needed.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] ListFilesAsync called: path='{path}', reason='{ToolLog.OneLine(reason)}'");
        var workspaceRoot = ToolWorkspace.WorkspaceRoot;
        var scopes = ToolWorkspace.ResolveListingScopes(path);

        var files = new List<string>();
        foreach (var scope in scopes)
        {
            Console.WriteLine(
                $"[Tools] ListFilesAsync scanning: '{Path.GetRelativePath(workspaceRoot, scope.DirectoryPath)}', recursive={scope.Recursive}");
            if (!Directory.Exists(scope.DirectoryPath))
            {
                throw new DirectoryNotFoundException(
                    $"The directory was not found: {Path.GetRelativePath(workspaceRoot, scope.DirectoryPath)}");
            }

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = scope.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var filePath in Directory.EnumerateFiles(scope.DirectoryPath, "*", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(workspaceRoot, filePath);
                if (ToolWorkspace.IsGeneratedPath(relativePath) ||
                    (!scope.Recursive && Path.GetFileName(filePath).Equals("ADVENTURE.md", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var fileInfo = new FileInfo(filePath);
                files.Add($"{relativePath} ({fileInfo.Length:N0} bytes)");

                if (files.Count == MaxListedFiles)
                {
                    files.Add($"[Listing truncated after {MaxListedFiles} files]");
                    break;
                }
            }

            if (files.Count > MaxListedFiles)
            {
                break;
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"[Tools] ListFilesAsync completed: {files.Count} entries returned");
        return Task.FromResult(files.ToArray());
    }
}
