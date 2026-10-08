using System.ComponentModel;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

internal static class ReadImageRegionTool
{
    public static async Task<AIContent[]> ReadImageRegionAsync(
        [Description("The listed path to an image in the shared Workspace root or selected adventure directory. The image is read from the shared in-memory cache after its first use.")] string imagePath,
        [Description("The horizontal pixel coordinate of the region's top-left corner.")] int x,
        [Description("The vertical pixel coordinate of the region's top-left corner.")] int y,
        [Description("The width of the region in pixels.")] int width,
        [Description("The height of the region in pixels.")] int height,
        [Description("A concise explanation of what detail this crop should clarify and why it is needed.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"[Tools] ReadImageRegionAsync called: imagePath='{imagePath}', region=({x}, {y}, {width}, {height}), reason='{ToolLog.OneLine(reason)}'");
        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentException("x and y must be non-negative, and width and height must be positive.");
        }

        var cachedImage = await ImageCache.GetAsync(imagePath, cancellationToken);
        await using var input = new MemoryStream(cachedImage.Bytes, writable: false);
        using var sourceImage = await Image.LoadAsync(input, cancellationToken);
        Console.WriteLine($"[Tools] ReadImageRegionAsync source loaded from memory: '{cachedImage.RelativePath}' dimensions={sourceImage.Width}x{sourceImage.Height}");
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
            new TextContent($"Loaded region ({x}, {y}, {width}, {height}) from {cachedImage.RelativePath} as PNG."),
            new DataContent(output.ToArray(), "image/png")
        ];
    }
}
