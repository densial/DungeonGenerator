using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.AI;

internal static class ReadImageTool
{
    private static readonly ConcurrentDictionary<string, byte> DeliveredImages =
        new(StringComparer.Ordinal);

    public static async Task<AIContent[]> ReadImageAsync(
        [Description("The listed path to an image in the shared Workspace root or selected adventure directory. Supported formats are PNG, JPEG, GIF, and WebP. Images are cached in memory after their first read.")] string imagePath,
        [Description("A concise explanation of what needs to be inspected in the image and why it is needed.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Tools] ReadImageAsync called: imagePath='{imagePath}', reason='{ToolLog.OneLine(reason)}'");
        var (_, fullPath) = ToolWorkspace.ResolveReadPath(imagePath);
        if (!DeliveredImages.TryAdd(fullPath, 0))
        {
            Console.WriteLine($"[Tools] ReadImageAsync suppressed duplicate full-image delivery: '{imagePath}'");
            return
            [
                new TextContent(
                    $"The full image '{imagePath}' was already delivered earlier in this run. " +
                    "Reuse the primary session's map analysis or delegated source brief, or call ReadImageRegionAsync " +
                    "for one specific unresolved detail.")
            ];
        }

        try
        {
            var image = await ImageCache.GetAsync(imagePath, cancellationToken);
            Console.WriteLine($"[Tools] ReadImageAsync completed: '{image.RelativePath}'");
            return
            [
                new TextContent($"Loaded image: {image.RelativePath}"),
                new DataContent(image.Bytes, image.MediaType)
            ];
        }
        catch
        {
            DeliveredImages.TryRemove(fullPath, out _);
            throw;
        }
    }
}
