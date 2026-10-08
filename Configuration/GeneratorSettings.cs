using System.Text.Json;

internal sealed class GeneratorSettings
{
    public string Provider { get; init; } = "Local";

    public LocalGeneratorSettings Local { get; init; } = new();

    public ChatGptGeneratorSettings ChatGPT { get; init; } = new();

    public static async Task<GeneratorSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveConfigurationPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The generator configuration file was not found.", fullPath);
        }

        await using var stream = File.OpenRead(fullPath);
        var root = await JsonSerializer.DeserializeAsync<ConfigurationRoot>(
            stream,
            SerializerOptions,
            cancellationToken);
        var settings = root?.Generator
            ?? throw new InvalidOperationException("The configuration file must contain a 'Generator' object.");

        settings.Validate();
        Console.WriteLine($"[Configuration] Generator provider: {settings.Provider}");
        return settings;
    }

    private void Validate()
    {
        if (!Provider.Equals("Local", StringComparison.OrdinalIgnoreCase) &&
            !Provider.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Generator.Provider must be either 'Local' or 'ChatGPT'.");
        }

        if (Provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(Local.Endpoint, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("Generator.Local.Endpoint must be an absolute URI.");
            }

            if (string.IsNullOrWhiteSpace(Local.Model))
            {
                throw new InvalidOperationException("Generator.Local.Model is required for the local provider.");
            }
        }
        else if (string.IsNullOrWhiteSpace(ChatGPT.Model))
        {
            throw new InvalidOperationException("Generator.ChatGPT.Model is required for the ChatGPT provider.");
        }
        else
        {
            ChatGPT.Validate();
        }
    }

    private static string ResolveConfigurationPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var workingDirectoryPath = Path.GetFullPath(path, Directory.GetCurrentDirectory());
        if (File.Exists(workingDirectoryPath))
        {
            return workingDirectoryPath;
        }

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class ConfigurationRoot
    {
        public GeneratorSettings? Generator { get; init; }
    }
}

internal sealed class LocalGeneratorSettings
{
    public string Endpoint { get; init; } = "http://127.0.0.1:1234/v1/";

    public string Model { get; init; } = "unsloth/muse-glimmer-30b";
}

internal sealed class ChatGptGeneratorSettings
{
    public string Model { get; init; } = "gpt-5.6-luna";

    public string ThinkingLevel { get; init; } = "high";

    public void Validate()
    {
        if (!SupportedThinkingLevels.Contains(ThinkingLevel, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Generator.ChatGPT.ThinkingLevel must be one of: none, low, medium, high, xhigh, max.");
        }

        if (Model.Equals("gpt-6-astra", StringComparison.OrdinalIgnoreCase) &&
            ThinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("gpt-6-astra does not support a 'none' thinking level.");
        }

        if (Model.Equals("gpt-6.1-sol", StringComparison.OrdinalIgnoreCase) &&
            ThinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("gpt-6.1-sol does not support a 'none' thinking level.");
        }
    }

    private static readonly string[] SupportedThinkingLevels =
        ["none", "low", "medium", "high", "xhigh", "max"];
}
