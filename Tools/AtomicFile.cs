using System.Text;

internal static class AtomicFile
{
    public static async Task WriteAllTextAsync(
        string destinationPath,
        string contents,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = CreateTemporaryPath(destinationPath);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task AppendAllTextAsync(
        string destinationPath,
        string separator,
        string contents,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = CreateTemporaryPath(destinationPath);
        try
        {
            File.Copy(destinationPath, temporaryPath, overwrite: false);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(separator.AsMemory(), cancellationToken);
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
                await writer.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string CreateTemporaryPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
        var fileName = Path.GetFileName(destinationPath);
        return Path.Combine(directory, $".dungeon-generator-{fileName}-{Guid.NewGuid():N}.tmp");
    }
}
