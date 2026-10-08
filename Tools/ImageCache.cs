using System.Collections.Concurrent;

internal static class ImageCache
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Lazy<Task<CachedImageData>>> Images =
        new(StringComparer.Ordinal);

    public static async Task<CachedImageData> GetAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        var (workspaceRoot, fullPath) = ToolWorkspace.ResolveReadPath(imagePath);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

        var newEntry = new Lazy<Task<CachedImageData>>(
            () => LoadAsync(fullPath, relativePath),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = Images.GetOrAdd(fullPath, newEntry);

        Console.WriteLine(ReferenceEquals(entry, newEntry)
            ? $"[Tools] Image cache miss: '{relativePath}'"
            : $"[Tools] Image cache hit: '{relativePath}'");

        try
        {
            return await entry.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            if (entry.IsValueCreated && entry.Value.IsFaulted)
            {
                Images.TryRemove(fullPath, out _);
            }

            throw;
        }
    }

    private static async Task<CachedImageData> LoadAsync(string fullPath, string relativePath)
    {
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
            _ => throw new ArgumentException("Only PNG, JPEG, GIF, and WebP images are supported.", nameof(fullPath))
        };

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxImageBytes)
        {
            throw new ArgumentException("The image must be 10 MB or smaller.", nameof(fullPath));
        }

        Console.WriteLine($"[Tools] Image cache loading from disk: '{relativePath}' as {mediaType}");
        var bytes = await File.ReadAllBytesAsync(fullPath);
        if (bytes.LongLength > MaxImageBytes)
        {
            throw new ArgumentException("The image must be 10 MB or smaller.", nameof(fullPath));
        }

        Console.WriteLine($"[Tools] Image cached: '{relativePath}' ({bytes.Length:N0} bytes)");
        return new CachedImageData(relativePath, mediaType, bytes);
    }
}

internal sealed record CachedImageData(string RelativePath, string MediaType, byte[] Bytes);
