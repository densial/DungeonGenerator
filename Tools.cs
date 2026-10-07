using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

internal static class Tools
{
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const long MaxTextFileBytes = 2 * 1024 * 1024;
    private const long MaxWritableFileBytes = 5 * 1024 * 1024;
    private const int MaxListedFiles = 500;
    private const int MaxSearchResults = 8;
    private const int MaxSearchQueryLength = 500;
    private const int MaxPromptLength = 100_000;
    private const int MaxPromptAttempts = 3;

    private static AIAgent? ConfiguredAgent;

    private static readonly HttpClient WebClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

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

    public static Task<string[]> ListFilesAsync(
        [Description("A directory inside the current workspace to search recursively. Use '.' for the workspace root.")] string path,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] ListFilesAsync called: path='{path}'");
        var (workspaceRoot, directoryPath) = ResolveWorkspacePath(path);
        Console.WriteLine($"[Tools] ListFilesAsync scanning: '{Path.GetRelativePath(workspaceRoot, directoryPath)}'");
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"The directory was not found: {Path.GetRelativePath(workspaceRoot, directoryPath)}");
        }

        var files = new List<string>();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(workspaceRoot, filePath);
            if (IsGeneratedPath(relativePath))
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

        files.Sort(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"[Tools] ListFilesAsync completed: {files.Count} entries returned");
        return Task.FromResult(files.ToArray());
    }

    public static async Task<string> ReadFileAsync(
        [Description("The path to a text or Markdown file inside the current workspace. Use ListFilesAsync first to discover the exact path.")] string path,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] ReadFileAsync called: path='{path}'");
        var (workspaceRoot, fullPath) = ResolveWorkspacePath(path);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

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

    public static async Task<string> WriteFileAsync(
        [Description("The destination path for the completed adventure inside the current workspace. Prefer a .md or .markdown extension.")] string path,
        [Description("The complete file contents to write.")] string contents,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] WriteFileAsync called: path='{path}', contents={contents.Length:N0} characters");
        var (workspaceRoot, fullPath) = ResolveWorkspacePath(path);
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
        await File.WriteAllTextAsync(fullPath, contents, cancellationToken);
        Console.WriteLine($"[Tools] WriteFileAsync completed: '{relativePath}'");
        return $"Wrote {relativePath} ({byteCount:N0} bytes).";
    }

    public static async Task<AIContent[]> ReadImageAsync(
        [Description("The path to an image inside the current workspace. Supported formats are PNG, JPEG, GIF, and WebP.")] string imagePath,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] ReadImageAsync called: imagePath='{imagePath}'");
        var (workspaceRoot, fullPath) = ResolveWorkspacePath(imagePath);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The image file was not found.", fullPath);
        }

        var mediaType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => throw new ArgumentException("Only PNG, JPEG, GIF, and WebP images are supported.", nameof(imagePath))
        };

        if (new FileInfo(fullPath).Length > MaxImageBytes)
        {
            throw new ArgumentException("The image must be 10 MB or smaller.", nameof(imagePath));
        }

        Console.WriteLine($"[Tools] ReadImageAsync loading: '{relativePath}' as {mediaType}");
        var image = await DataContent.LoadFromAsync(fullPath, mediaType, cancellationToken);
        Console.WriteLine($"[Tools] ReadImageAsync completed: '{relativePath}'");
        return
        [
            new TextContent($"Loaded image: {relativePath}"),
            image
        ];
    }

    public static async Task<AIContent[]> ReadImageRegionAsync(
        [Description("The path to an image inside the current workspace.")] string imagePath,
        [Description("The horizontal pixel coordinate of the region's top-left corner.")] int x,
        [Description("The vertical pixel coordinate of the region's top-left corner.")] int y,
        [Description("The width of the region in pixels.")] int width,
        [Description("The height of the region in pixels.")] int height,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] ReadImageRegionAsync called: imagePath='{imagePath}', region=({x}, {y}, {width}, {height})");
        var (workspaceRoot, fullPath) = ResolveWorkspacePath(imagePath);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The image file was not found.", fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (!SupportedImageExtensions.Contains(extension))
        {
            throw new ArgumentException("Only PNG, JPEG, GIF, and WebP images are supported.", nameof(imagePath));
        }

        if (new FileInfo(fullPath).Length > MaxImageBytes)
        {
            throw new ArgumentException("The image must be 10 MB or smaller.", nameof(imagePath));
        }

        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentException("x and y must be non-negative, and width and height must be positive.");
        }

        using var sourceImage = await Image.LoadAsync(fullPath, cancellationToken);
        Console.WriteLine($"[Tools] ReadImageRegionAsync source loaded: '{relativePath}' dimensions={sourceImage.Width}x{sourceImage.Height}");
        if (x > sourceImage.Width - width || y > sourceImage.Height - height)
        {
            throw new ArgumentException(
                $"The requested region must fit inside the image ({sourceImage.Width}x{sourceImage.Height}).");
        }

        using var region = sourceImage.Clone(context => context.Crop(new Rectangle(x, y, width, height)));
        Console.WriteLine($"[Tools] ReadImageRegionAsync cropping: region=({x}, {y}, {width}, {height})");
        await using var output = new MemoryStream();
        await region.SaveAsPngAsync(output, cancellationToken);
        Console.WriteLine($"[Tools] ReadImageRegionAsync completed: PNG output={output.Length:N0} bytes");

        return
        [
            new TextContent($"Loaded region ({x}, {y}, {width}, {height}) from {relativePath} as PNG."),
            new DataContent(output.ToArray(), "image/png")
        ];
    }

    public static async Task<string> SearchWebAsync(
        [Description("The web search query. Keep it focused and no longer than 500 characters.")] string query,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] SearchWebAsync called: query='{query.Replace("\r", " ").Replace("\n", " ")}'");
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("The search query cannot be empty.", nameof(query));
        }

        query = query.Trim();
        if (query.Length > MaxSearchQueryLength)
        {
            throw new ArgumentException("The search query must be 500 characters or fewer.", nameof(query));
        }

        var searchUri = new Uri($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
        Console.WriteLine($"[Tools] SearchWebAsync requesting: '{searchUri}'");
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DungeonGenerator", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await WebClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"[Tools] SearchWebAsync response: {(int)response.StatusCode} {response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var results = new StringBuilder($"Search results for: {query}\n\n");
        var resultCount = 0;
        foreach (Match titleMatch in SearchTitleRegex.Matches(html))
        {
            if (resultCount == MaxSearchResults)
            {
                break;
            }

            var title = CleanHtml(titleMatch.Groups["title"].Value);
            var url = NormalizeSearchUrl(titleMatch.Groups["url"].Value);
            var remainingHtml = html[titleMatch.Index..];
            var snippetMatch = SearchSnippetRegex.Match(remainingHtml);
            var snippet = snippetMatch.Success ? CleanHtml(snippetMatch.Groups["snippet"].Value) : "";

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            resultCount++;
            results.AppendLine($"{resultCount}. {title}");
            results.AppendLine($"   URL: {url}");
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                results.AppendLine($"   {snippet}");
            }

            results.AppendLine();
        }

        if (resultCount == 0)
        {
            Console.WriteLine("[Tools] SearchWebAsync completed: no results");
            return $"No web results found for: {query}";
        }

        Console.WriteLine($"[Tools] SearchWebAsync completed: {resultCount} results");
        return results.ToString().TrimEnd();
    }

    public static void SetAgent(AIAgent agent)
    {
        ConfiguredAgent = agent ?? throw new ArgumentNullException(nameof(agent));
        Console.WriteLine("[Tools] RunPromptAsync configured with the current agent");
    }

    public static async Task<string> RunPromptAsync(
        [Description("The complete task prompt to run in a fresh independent agent session.")] string prompt,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N")[..8];
        Console.WriteLine($"[Tools] RunPromptAsync called: operation={operationId}, prompt={prompt?.Length ?? 0} characters");

        try
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("The prompt cannot be empty.", nameof(prompt));
            }

            if (prompt.Length > MaxPromptLength)
            {
                throw new ArgumentException("The prompt must be 100,000 characters or fewer.", nameof(prompt));
            }

            var agent = ConfiguredAgent ?? throw new InvalidOperationException("RunPromptAsync is not configured with an agent.");
            cancellationToken.ThrowIfCancellationRequested();

            Console.WriteLine($"[Tools] RunPromptAsync creating a fresh session: operation={operationId}");
            var session = await agent.CreateSessionAsync(cancellationToken);
            Console.WriteLine($"[Tools] RunPromptAsync session created: operation={operationId}, sessionType={session.GetType().FullName}");

            Exception? lastException = null;
            for (var attempt = 1; attempt <= MaxPromptAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attemptPrompt = attempt == 1
                    ? prompt
                    : BuildRecoveryPrompt(attempt, lastException!);

                Console.WriteLine($"[Tools] RunPromptAsync running attempt {attempt}/{MaxPromptAttempts}: operation={operationId}");
                try
                {
                    var response = await agent.RunAsync(attemptPrompt, session, cancellationToken: cancellationToken);
                    Console.WriteLine($"[Tools] RunPromptAsync completed: operation={operationId}, attempt={attempt}, response={response.Text?.Length ?? 0} characters");
                    return response.Text ?? string.Empty;
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[Tools][CANCELLED] RunPromptAsync cancelled: operation={operationId}, attempt={attempt}");
                    Console.Error.WriteLine(FormatExceptionDetails(exception));
                    throw;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    Console.Error.WriteLine($"[Tools][ERROR] RunPromptAsync attempt failed: operation={operationId}, attempt={attempt}/{MaxPromptAttempts}");
                    Console.Error.WriteLine(FormatExceptionDetails(exception));

                    if (attempt < MaxPromptAttempts)
                    {
                        Console.WriteLine($"[Tools] RunPromptAsync preserving session and preparing recovery attempt: operation={operationId}");
                    }
                }
            }

            throw new InvalidOperationException(
                $"RunPromptAsync failed after {MaxPromptAttempts} attempts for operation {operationId}. See the console diagnostics for the full exception chains.",
                lastException);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[Tools][CANCELLED] RunPromptAsync cancelled: operation={operationId}");
            Console.Error.WriteLine(FormatExceptionDetails(exception));
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Tools][ERROR] RunPromptAsync failed: operation={operationId}");
            Console.Error.WriteLine(FormatExceptionDetails(exception));
            throw new InvalidOperationException(
                $"RunPromptAsync failed for operation {operationId}. See the console diagnostics for the full exception chain.",
                exception);
        }
    }

    public static string FormatExceptionDetails(Exception exception)
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

    private static string BuildRecoveryPrompt(int attempt, Exception exception) => $"""
        The previous attempt in this same session failed during tool execution.

        Recovery attempt: {attempt}/{MaxPromptAttempts}
        Error type: {exception.GetType().FullName}
        Error message: {exception.Message}

        Diagnose the failure, correct the tool arguments or choose an alternative approach, and continue the original task. Preserve all useful work already completed in this session. Do not restart the task from scratch and do not delegate again unless explicitly required.
        """;

    private static (string WorkspaceRoot, string FullPath) ResolveWorkspacePath(string path)
    {
        var workspaceRoot = GetWorkspaceRoot();
        var fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path, workspaceRoot);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

        if (relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}") || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("The path must be inside the Workspace directory.", nameof(path));
        }

        RejectSymbolicLinkPath(workspaceRoot, fullPath, path);
        return (workspaceRoot, fullPath);
    }

    private static string GetWorkspaceRoot() => WorkspaceRoot.Value;

    private static readonly Lazy<string> WorkspaceRoot = new(FindWorkspaceRoot);

    private static string FindWorkspaceRoot()
    {
        var solutionDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (solutionDirectory is not null && !solutionDirectory.EnumerateFiles("*.sln").Any())
        {
            solutionDirectory = solutionDirectory.Parent;
        }

        if (solutionDirectory is null)
        {
            throw new InvalidOperationException("Could not locate the solution directory from the current working directory.");
        }

        var workspaceDirectories = Directory
            .EnumerateDirectories(solutionDirectory.FullName, "Workspace", SearchOption.AllDirectories)
            .Where(directory => !IsGeneratedPath(Path.GetRelativePath(solutionDirectory.FullName, directory)))
            .Where(directory => (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) == 0)
            .ToArray();

        if (workspaceDirectories.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one Workspace directory inside the solution directory, found {workspaceDirectories.Length}.");
        }

        var workspaceRoot = Path.GetFullPath(workspaceDirectories[0])
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Console.WriteLine($"[Tools] Filesystem workspace restricted to: '{workspaceRoot}'");
        return workspaceRoot;
    }

    private static void RejectSymbolicLinkPath(string workspaceRoot, string fullPath, string originalPath)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
        var currentPath = workspaceRoot;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
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

    private static bool IsGeneratedPath(string relativePath) =>
        relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is ".git" or ".idea" or "bin" or "obj");

    private static readonly Regex SearchTitleRegex = new(
        "<a\\b(?=[^>]*\\bclass=\"[^\"]*\\bresult__a\\b[^\"]*\")(?=[^>]*\\bhref=\"(?<url>[^\"]+)\")[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SearchSnippetRegex = new(
        "<a\\b[^>]*\\bclass=\"[^\"]*\\bresult__snippet\\b[^\"]*\"[^>]*>(?<snippet>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static string CleanHtml(string value)
    {
        var withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static string NormalizeSearchUrl(string value)
    {
        var decodedValue = WebUtility.HtmlDecode(value);
        if (decodedValue.StartsWith("//", StringComparison.Ordinal))
        {
            decodedValue = $"https:{decodedValue}";
        }

        if (!Uri.TryCreate(decodedValue, UriKind.Absolute, out var uri))
        {
            return decodedValue;
        }

        var queryParameter = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .FirstOrDefault(parameter => parameter.Length == 2 && parameter[0] == "uddg");

        return queryParameter is null ? uri.ToString() : WebUtility.UrlDecode(queryParameter[1]);
    }

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp"
    };
}
