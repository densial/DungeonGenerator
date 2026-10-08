using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI;

internal static class RunPromptTool
{
    private const int MaxPromptLength = 100_000;
    private const int MaxPromptAttempts = 3;
    private const int MaxDelegationsPerRun = 4;

    private static AIAgent? ConfiguredAgent;
    private static readonly AsyncLocal<int> DelegationDepth = new();
    private static readonly ConcurrentDictionary<string, byte> DelegatedPrompts = new(StringComparer.Ordinal);
    private static int DelegationCount;

    public static void SetAgent(AIAgent agent)
    {
        ConfiguredAgent = agent ?? throw new ArgumentNullException(nameof(agent));
        Console.WriteLine("[Tools] RunPromptAsync configured with the current agent");
    }

    public static async Task<string> RunPromptAsync(
        [Description("The complete task prompt to run in a fresh independent agent session.")] string prompt,
        [Description("A concise explanation of what is being delegated and why a separate session is useful.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N")[..8];
        var enteredDelegation = false;
        Console.WriteLine(
            $"[Tools] RunPromptAsync called: operation={operationId}, prompt={prompt?.Length ?? 0} characters, reason='{ToolLog.OneLine(reason)}'");

        try
        {
            if (DelegationDepth.Value > 0)
            {
                throw new InvalidOperationException("Recursive RunPromptAsync delegation is not allowed.");
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("The prompt cannot be empty.", nameof(prompt));
            }

            if (prompt.Length > MaxPromptLength)
            {
                throw new ArgumentException("The prompt must be 100,000 characters or fewer.", nameof(prompt));
            }

            if (!RunStateManager.HasCurrentRunArtifactCheckpoint)
            {
                throw new InvalidOperationException(
                    $"RunPromptAsync is unavailable until the primary session writes or appends the first {ToolWorkspace.ArtifactRelativePath} checkpoint for this run.");
            }

            var promptFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.Trim())));
            if (!DelegatedPrompts.TryAdd(promptFingerprint, 0))
            {
                throw new InvalidOperationException("This delegated prompt has already run; duplicate delegation was blocked.");
            }

            var delegationNumber = Interlocked.Increment(ref DelegationCount);
            if (delegationNumber > MaxDelegationsPerRun)
            {
                throw new InvalidOperationException(
                    $"The run has reached its limit of {MaxDelegationsPerRun} delegated sessions.");
            }

            DelegationDepth.Value++;
            enteredDelegation = true;
            Console.WriteLine(
                $"[Tools] RunPromptAsync delegation accepted: operation={operationId}, number={delegationNumber}/{MaxDelegationsPerRun}");

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
                    var response = await AgentRunner.RunAsync(
                        agent,
                        attemptPrompt,
                        session,
                        cancellationToken,
                        runLabel: $"delegated-{operationId}-attempt-{attempt}");
                    Console.WriteLine($"[Tools] RunPromptAsync completed: operation={operationId}, attempt={attempt}, response={response.Length} characters");
                    return response;
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[Tools][CANCELLED] RunPromptAsync cancelled: operation={operationId}, attempt={attempt}");
                    Console.Error.WriteLine(ExceptionFormatter.FormatDetails(exception));
                    throw;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    Console.Error.WriteLine($"[Tools][ERROR] RunPromptAsync attempt failed: operation={operationId}, attempt={attempt}/{MaxPromptAttempts}");
                    Console.Error.WriteLine(ExceptionFormatter.FormatDetails(exception));

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
            Console.Error.WriteLine(ExceptionFormatter.FormatDetails(exception));
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Tools][ERROR] RunPromptAsync failed: operation={operationId}");
            Console.Error.WriteLine(ExceptionFormatter.FormatDetails(exception));
            throw new InvalidOperationException(
                $"RunPromptAsync failed for operation {operationId}. See the console diagnostics for the full exception chain.",
                exception);
        }
        finally
        {
            if (enteredDelegation)
            {
                DelegationDepth.Value--;
            }
        }
    }

    private static string BuildRecoveryPrompt(int attempt, Exception exception) => $"""
        The previous attempt in this same session failed during tool execution.

        Recovery attempt: {attempt}/{MaxPromptAttempts}
        Error type: {exception.GetType().FullName}
        Error message: {exception.Message}

        Diagnose the failure, correct the tool arguments or choose an alternative approach, and continue the original task. Preserve all useful work already completed in this session. Do not restart the task from scratch and do not delegate again unless explicitly required.
        """;
}
