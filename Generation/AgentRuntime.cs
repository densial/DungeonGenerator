using Microsoft.Agents.AI;

internal sealed class AgentRuntime(AIAgent agent, IAsyncDisposable? resources = null) : IAsyncDisposable
{
    public AIAgent Agent { get; } = agent;

    public async ValueTask DisposeAsync()
    {
        if (resources is not null)
        {
            await resources.DisposeAsync();
        }
    }
}
