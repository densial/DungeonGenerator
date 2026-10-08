internal static class ToolWorkspace
{
    private const string ArtifactFileName = "ADVENTURE.md";

    private static readonly Lazy<string> WorkspaceRootValue = new(FindWorkspaceRoot);
    private static string? _adventureRoot;
    private static string? _adventureRelativePath;

    public static string WorkspaceRoot => WorkspaceRootValue.Value;

    public static string AdventureRoot =>
        _adventureRoot ?? throw new InvalidOperationException("The adventure workspace has not been configured.");

    public static string AdventureRelativePath =>
        _adventureRelativePath ?? throw new InvalidOperationException("The adventure workspace has not been configured.");

    public static string ArtifactRelativePath => Path.Combine(AdventureRelativePath, ArtifactFileName);

    public static void ConfigureAdventure(string directoryName)
    {
        if (_adventureRoot is not null)
        {
            throw new InvalidOperationException("The adventure workspace has already been configured.");
        }

        var trimmedName = directoryName.Trim();
        if (trimmedName.Length == 0 || trimmedName is "." or "..")
        {
            throw new ArgumentException("The adventure directory name cannot be empty, '.' or '..'.", nameof(directoryName));
        }

        if (Path.IsPathRooted(trimmedName) ||
            trimmedName.Contains(Path.DirectorySeparatorChar) ||
            trimmedName.Contains(Path.AltDirectorySeparatorChar) ||
            trimmedName.Contains('/') ||
            trimmedName.Contains('\\'))
        {
            throw new ArgumentException(
                "The adventure directory must be a single directory name directly beneath Workspace.",
                nameof(directoryName));
        }

        if (IsGeneratedPath(trimmedName))
        {
            throw new ArgumentException("The adventure directory name is reserved.", nameof(directoryName));
        }

        var workspaceRoot = WorkspaceRoot;
        var adventureRoot = Path.GetFullPath(trimmedName, workspaceRoot);
        EnsureInsideRoot(workspaceRoot, adventureRoot, directoryName);
        RejectSymbolicLinkPath(workspaceRoot, adventureRoot, directoryName);

        Directory.CreateDirectory(adventureRoot);
        RejectSymbolicLinkPath(workspaceRoot, adventureRoot, directoryName);

        _adventureRoot = adventureRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _adventureRelativePath = Path.GetRelativePath(workspaceRoot, _adventureRoot);
        Console.WriteLine(
            $"[Workspace] Selected adventure directory: '{_adventureRelativePath}' (shared root: '{workspaceRoot}')");
    }

    public static (string WorkspaceRoot, string FullPath) ResolveReadPath(string path)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file or directory path is required.", nameof(path));
        }

        // Prefer an existing adventure file for bare names such as ADVENTURE.md.
        if (IsSinglePathSegment(path))
        {
            var adventureCandidate = Path.GetFullPath(path, AdventureRoot);
            if (Path.GetFileName(path).Equals(ArtifactFileName, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(adventureCandidate) ||
                Directory.Exists(adventureCandidate))
            {
                RejectSymbolicLinkPath(WorkspaceRoot, adventureCandidate, path);
                return (WorkspaceRoot, adventureCandidate);
            }
        }

        var fullPath = ResolveInsideWorkspace(path);
        if (!IsWorkspaceRoot(fullPath) && !IsDirectChildOfWorkspace(fullPath) && !IsInsideAdventure(fullPath))
        {
            throw new ArgumentException(
                $"The path must identify a shared root file or a file inside the selected adventure directory '{AdventureRelativePath}'.",
                nameof(path));
        }

        RejectSymbolicLinkPath(WorkspaceRoot, fullPath, path);
        return (WorkspaceRoot, fullPath);
    }

    public static (string WorkspaceRoot, string FullPath) ResolveAdventurePath(string path)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An adventure-relative destination path is required.", nameof(path));
        }

        string fullPath;
        if (!Path.IsPathRooted(path))
        {
            var workspaceCandidate = Path.GetFullPath(path, WorkspaceRoot);
            fullPath = IsInsideAdventure(workspaceCandidate)
                ? workspaceCandidate
                : Path.GetFullPath(path, AdventureRoot);
        }
        else
        {
            fullPath = Path.GetFullPath(path);
        }

        if (!IsInsideAdventure(fullPath))
        {
            throw new ArgumentException(
                $"All generated files must be inside the selected adventure directory '{AdventureRelativePath}'.",
                nameof(path));
        }

        RejectSymbolicLinkPath(WorkspaceRoot, fullPath, path);
        return (WorkspaceRoot, fullPath);
    }

    public static IReadOnlyList<WorkspaceListingScope> ResolveListingScopes(string path)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return
            [
                new WorkspaceListingScope(WorkspaceRoot, Recursive: false),
                new WorkspaceListingScope(AdventureRoot, Recursive: true)
            ];
        }

        var (_, directoryPath) = ResolveReadPath(path);
        if (IsWorkspaceRoot(directoryPath))
        {
            return
            [
                new WorkspaceListingScope(WorkspaceRoot, Recursive: false),
                new WorkspaceListingScope(AdventureRoot, Recursive: true)
            ];
        }

        if (!IsInsideAdventure(directoryPath))
        {
            throw new ArgumentException(
                "Only the Workspace root or the selected adventure directory can be listed.",
                nameof(path));
        }

        return [new WorkspaceListingScope(directoryPath, Recursive: true)];
    }

    public static bool IsAdventureArtifact(string workspaceRelativePath) =>
        PathsEqual(workspaceRelativePath, ArtifactRelativePath);

    public static bool IsGeneratedPath(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.Equals(".dungeon-generator-state.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith(".dungeon-generator-", StringComparison.OrdinalIgnoreCase) ||
               relativePath
                   .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(segment => segment is ".git" or ".idea" or "bin" or "obj");
    }

    private static string ResolveInsideWorkspace(string path)
    {
        var fullPath = Path.GetFullPath(path, WorkspaceRoot);
        EnsureInsideRoot(WorkspaceRoot, fullPath, path);
        return fullPath;
    }

    private static void EnsureConfigured()
    {
        _ = AdventureRoot;
    }

    private static bool IsSinglePathSegment(string path) =>
        !Path.IsPathRooted(path) &&
        !path.Contains(Path.DirectorySeparatorChar) &&
        !path.Contains(Path.AltDirectorySeparatorChar) &&
        !path.Contains('/') &&
        !path.Contains('\\');

    private static bool IsWorkspaceRoot(string fullPath) => PathsEqual(fullPath, WorkspaceRoot);

    private static bool IsDirectChildOfWorkspace(string fullPath) =>
        PathsEqual(Path.GetDirectoryName(fullPath), WorkspaceRoot);

    private static bool IsInsideAdventure(string fullPath)
    {
        var relativePath = Path.GetRelativePath(AdventureRoot, fullPath);
        return relativePath == "." || IsContainedRelativePath(relativePath);
    }

    private static void EnsureInsideRoot(string root, string fullPath, string originalPath)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath != "." && !IsContainedRelativePath(relativePath))
        {
            throw new ArgumentException("The path must be inside the Workspace directory.", nameof(originalPath));
        }
    }

    private static bool IsContainedRelativePath(string relativePath) =>
        relativePath != ".." &&
        !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) &&
        !Path.IsPathRooted(relativePath);

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(
            left?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FindWorkspaceRoot()
    {
        var solutionDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (solutionDirectory is not null &&
               !solutionDirectory.EnumerateFiles("*.sln").Any() &&
               !solutionDirectory.EnumerateFiles("*.csproj").Any())
        {
            solutionDirectory = solutionDirectory.Parent;
        }

        if (solutionDirectory is null)
        {
            throw new InvalidOperationException("Could not locate the solution or project directory from the current working directory.");
        }

        var workspaceRoot = Path.Combine(solutionDirectory.FullName, "Workspace");
        if (!Directory.Exists(workspaceRoot))
        {
            throw new InvalidOperationException($"The Workspace directory was not found at '{workspaceRoot}'.");
        }

        if ((new DirectoryInfo(workspaceRoot).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The Workspace directory cannot be a symbolic link or reparse point.");
        }

        workspaceRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Console.WriteLine($"[Tools] Filesystem workspace restricted to: '{workspaceRoot}'");
        return workspaceRoot;
    }

    private static void RejectSymbolicLinkPath(string workspaceRoot, string fullPath, string originalPath)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
        var currentPath = workspaceRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            FileSystemInfo fileSystemInfo = File.Exists(currentPath)
                ? new FileInfo(currentPath)
                : new DirectoryInfo(currentPath);

            if (fileSystemInfo.Exists && (fileSystemInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    $"The path cannot pass through a symbolic link or reparse point: '{originalPath}'.",
                    nameof(originalPath));
            }

            if (!fileSystemInfo.Exists)
            {
                break;
            }
        }
    }
}

internal sealed record WorkspaceListingScope(string DirectoryPath, bool Recursive);
