using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;

internal static class AgentRunner
{
    public static async Task<string> RunAsync(
        AIAgent agent,
        string prompt,
        AgentSession session,
        CancellationToken cancellationToken = default,
        string? runLabel = null)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var label = string.IsNullOrWhiteSpace(runLabel) ? "agent-run" : runLabel;
        var stopwatch = Stopwatch.StartNew();
        var sawText = false;
        var endedWithNewLine = true;

        Console.WriteLine(
            $"[Agent][{runId}] Starting '{label}': prompt={prompt.Length:N0} characters, session={session.GetType().Name}");
        Console.WriteLine($"[Agent][{runId}] Live output:");

        try
        {
            var updates = agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken);
            var response = await ObserveAsync(updates, cancellationToken)
                .ToAgentResponseAsync(cancellationToken);

            EnsureOutputEndsWithNewLine();
            stopwatch.Stop();
            Console.WriteLine(
                $"[Agent][{runId}] Completed '{label}': elapsed={stopwatch.Elapsed}, response={response.Text?.Length ?? 0:N0} characters");
            return response.Text ?? string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EnsureOutputEndsWithNewLine();
            stopwatch.Stop();
            Console.Error.WriteLine($"[Agent][{runId}][CANCELLED] '{label}' after {stopwatch.Elapsed}");
            throw;
        }
        catch (Exception exception)
        {
            EnsureOutputEndsWithNewLine();
            stopwatch.Stop();
            Console.Error.WriteLine(
                $"[Agent][{runId}][ERROR] '{label}' failed after {stopwatch.Elapsed}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }

        void EnsureOutputEndsWithNewLine()
        {
            if (sawText && !endedWithNewLine)
            {
                Console.WriteLine();
            }
        }

        async IAsyncEnumerable<AgentResponseUpdate> ObserveAsync(
            IAsyncEnumerable<AgentResponseUpdate> source,
            [EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var update in source.WithCancellation(token))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    Console.Write(update.Text);
                    sawText = true;
                    endedWithNewLine = update.Text.EndsWith('\n');
                }

                yield return update;
            }
        }
    }
}
