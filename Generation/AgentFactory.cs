using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

internal static class AgentFactory
{
    public static async Task<AgentRuntime> CreateAsync(
        GeneratorSettings settings,
        string name,
        string instructions,
        IList<AITool> tools,
        CancellationToken cancellationToken = default)
    {
        if (settings.Provider.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return await CreateChatGptAsync(settings.ChatGPT, name, instructions, tools, cancellationToken);
        }

        return CreateLocal(settings.Local, name, instructions, tools);
    }

    private static AgentRuntime CreateLocal(
        LocalGeneratorSettings settings,
        string name,
        string instructions,
        IList<AITool> tools)
    {
        var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.Endpoint),
            Transport = new HttpClientPipelineTransport(httpClient),
            NetworkTimeout = Timeout.InfiniteTimeSpan
        };
        var chatClient = new OpenAIClient(
                // The SDK requires a credential object, but local servers commonly ignore it.
                new ApiKeyCredential("not-required"),
                clientOptions)
            .GetChatClient(settings.Model);

        Console.WriteLine($"[Generator] Using local model '{settings.Model}' at {settings.Endpoint}");
        var agent = chatClient.AsAIAgent(name: name, tools: tools, instructions: instructions);
        return new AgentRuntime(agent, new AsyncDisposableAction(() =>
        {
            httpClient.Dispose();
            return ValueTask.CompletedTask;
        }));
    }

    private static async Task<AgentRuntime> CreateChatGptAsync(
        ChatGptGeneratorSettings settings,
        string name,
        string instructions,
        IList<AITool> tools,
        CancellationToken cancellationToken)
    {
        var authentication = new ChatGptAuthentication();
        try
        {
            var accessToken = await authentication.GetAccessTokenAsync(cancellationToken);
            var availableModels = await authentication.GetAvailableModelsAsync(accessToken, cancellationToken);
            if (!availableModels.Contains(settings.Model, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"ChatGPT model '{settings.Model}' is not available to the connected account. " +
                    $"Available models: {string.Join(", ", availableModels)}");
            }

            var credential = new ApiKeyCredential(accessToken);
            var credentialRefresher = new ChatGptCredentialRefresher(authentication, credential);

            var httpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var clientOptions = new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(httpClient),
                NetworkTimeout = Timeout.InfiniteTimeSpan
            };
            var responseClient = new OpenAIClient(credential, clientOptions).GetResponsesClient();

            var agentOptions = new ChatClientAgentOptions
            {
                Name = name,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Tools = tools,
                    RawRepresentationFactory = _ => new CreateResponseOptions
                    {
                        ReasoningOptions = new ResponseReasoningOptions
                        {
                            ReasoningEffortLevel = ParseReasoningEffort(settings.ThinkingLevel)
                        }
                    }
                }
            };

            Console.WriteLine(
                $"[Generator] Using ChatGPT plan model '{settings.Model}' with '{settings.ThinkingLevel}' thinking");
            var agent = responseClient.AsAIAgent(
                options: agentOptions,
                model: settings.Model,
                clientFactory: _ => responseClient.AsIChatClientWithStoredOutputDisabled(settings.Model));

            credentialRefresher.Start();
            return new AgentRuntime(agent, new CompositeAsyncDisposable(credentialRefresher, httpClient));
        }
        catch
        {
            authentication.Dispose();
            throw;
        }
    }

    private static ResponseReasoningEffortLevel ParseReasoningEffort(string thinkingLevel) =>
        thinkingLevel.ToLowerInvariant() switch
        {
            "none" => ResponseReasoningEffortLevel.None,
            "low" => ResponseReasoningEffortLevel.Low,
            "medium" => ResponseReasoningEffortLevel.Medium,
            "high" => ResponseReasoningEffortLevel.High,
            // OpenAI 2.13 uses a string-backed extensible enum. These values are
            // accepted by the Responses API even though 2.13 has no named fields for them.
            "xhigh" => new ResponseReasoningEffortLevel("xhigh"),
            "max" => new ResponseReasoningEffortLevel("max"),
            _ => throw new InvalidOperationException($"Unsupported thinking level '{thinkingLevel}'.")
        };

    private sealed class AsyncDisposableAction(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }

    private sealed class CompositeAsyncDisposable(
        IAsyncDisposable asyncDisposable,
        IDisposable disposable) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await asyncDisposable.DisposeAsync();
            disposable.Dispose();
        }
    }
}
