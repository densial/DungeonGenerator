using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class RunStateManager
{
    private const string StateFileName = ".dungeon-generator-state.json";
    private const string ArtifactFileName = "ADVENTURE.md";

    private static RunStateManager? Current;

    private readonly string _statePath;
    private readonly string _artifactPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RunState _state;

    private RunStateManager(string statePath, string artifactPath, RunState state, string recoveryInstructions)
    {
        _statePath = statePath;
        _artifactPath = artifactPath;
        _state = state;
        RecoveryInstructions = recoveryInstructions;
    }

    public string RecoveryInstructions { get; }

    public static bool HasCurrentRunArtifactCheckpoint => Current?._state.CheckpointCount > 0;

    public static async Task<RunStateManager> BeginAsync(CancellationToken cancellationToken = default)
    {
        var (_, statePath) = ToolWorkspace.ResolveAdventurePath(StateFileName);
        var (_, artifactPath) = ToolWorkspace.ResolveAdventurePath(ArtifactFileName);
        var previous = await LoadPreviousAsync(statePath, cancellationToken);
        var artifact = GetArtifactSnapshot(artifactPath);
        var recoveryInstructions = BuildRecoveryInstructions(previous, artifact);
        var runId = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;
        var state = new RunState
        {
            RunId = runId,
            AdventureDirectory = ToolWorkspace.AdventureRelativePath,
            PreviousRunId = previous?.RunId,
            Status = RunStatus.Running,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            RecoveryMode = DetermineRecoveryMode(previous, artifact),
            PreviousStatus = previous?.Status,
            ArtifactExists = artifact.Exists,
            ArtifactBytes = artifact.Bytes,
            CheckpointCount = 0
        };

        var manager = new RunStateManager(statePath, artifactPath, state, recoveryInstructions);
        await manager.SaveAsync(cancellationToken);
        Current = manager;

        Console.WriteLine(
            $"[Recovery] Run {runId} started: mode={state.RecoveryMode}, previousStatus={state.PreviousStatus ?? "none"}, artifact={artifact.Description}");
        return manager;
    }

    public static async Task RecordArtifactCheckpointAsync(
        string relativePath,
        long artifactBytes,
        string? reason)
    {
        var manager = Current;
        if (manager is null || !ToolWorkspace.IsAdventureArtifact(relativePath))
        {
            return;
        }

        await manager._gate.WaitAsync();
        try
        {
            manager._state.ArtifactExists = true;
            manager._state.ArtifactBytes = artifactBytes;
            manager._state.CheckpointCount++;
            manager._state.LastCheckpointAtUtc = DateTimeOffset.UtcNow;
            manager._state.LastCheckpointReason = ToolLog.OneLine(reason);
            manager._state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await manager.SaveAsync(CancellationToken.None);
            Console.WriteLine(
                $"[Recovery] Checkpoint {manager._state.CheckpointCount} saved: artifact={artifactBytes:N0} bytes");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Recovery][WARNING] Could not persist artifact checkpoint: {exception.Message}");
        }
        finally
        {
            manager._gate.Release();
        }
    }

    public async Task MarkCompletedAsync(string response, CancellationToken cancellationToken = default)
    {
        var artifact = GetArtifactSnapshot(_artifactPath);
        if (!artifact.Exists || artifact.Bytes == 0)
        {
            throw new InvalidOperationException(
                $"The agent completed without creating a non-empty {ToolWorkspace.ArtifactRelativePath} artifact.");
        }

        var contents = await File.ReadAllTextAsync(_artifactPath, cancellationToken);
        if (UnresolvedPlaceholderRegex.IsMatch(contents))
        {
            throw new InvalidOperationException(
                $"{ToolWorkspace.ArtifactRelativePath} still contains unresolved template placeholders.");
        }

        await UpdateTerminalStateAsync(
            RunStatus.Completed,
            artifact,
            error: null,
            responseLength: response.Length,
            cancellationToken);
    }

    public Task MarkCancelledAsync(Exception exception) =>
        TryUpdateTerminalStateAsync(RunStatus.Cancelled, exception, responseLength: null);

    public Task MarkFailedAsync(Exception exception) =>
        TryUpdateTerminalStateAsync(RunStatus.Failed, exception, responseLength: null);

    private async Task TryUpdateTerminalStateAsync(string status, Exception exception, int? responseLength)
    {
        try
        {
            await UpdateTerminalStateAsync(
                status,
                GetArtifactSnapshot(_artifactPath),
                $"{exception.GetType().FullName}: {exception.Message}",
                responseLength,
                CancellationToken.None);
        }
        catch (Exception stateException)
        {
            Console.Error.WriteLine($"[Recovery][WARNING] Could not mark run as {status}: {stateException.Message}");
        }
    }

    private async Task UpdateTerminalStateAsync(
        string status,
        ArtifactSnapshot artifact,
        string? error,
        int? responseLength,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state.Status = status;
            _state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _state.FinishedAtUtc = DateTimeOffset.UtcNow;
            _state.ArtifactExists = artifact.Exists;
            _state.ArtifactBytes = artifact.Bytes;
            _state.LastError = error;
            _state.ResponseCharacters = responseLength;
            await SaveAsync(cancellationToken);
            Console.WriteLine(
                $"[Recovery] Run {_state.RunId} marked {status}: artifact={artifact.Description}, checkpoints={_state.CheckpointCount}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(_state, SerializerOptions);
        await AtomicFile.WriteAllTextAsync(_statePath, json, cancellationToken);
    }

    private static async Task<RunState?> LoadPreviousAsync(
        string statePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(statePath);
            return await JsonSerializer.DeserializeAsync<RunState>(stream, SerializerOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            Console.Error.WriteLine(
                $"[Recovery][WARNING] Previous run state could not be read; the artifact will be inspected directly: {exception.Message}");
            return null;
        }
    }

    private static string DetermineRecoveryMode(RunState? previous, ArtifactSnapshot artifact)
    {
        if (previous is null)
        {
            return artifact.Exists ? "RecoverUntrackedArtifact" : "Fresh";
        }

        if (previous.Status == RunStatus.Completed)
        {
            return artifact.Exists ? "ValidateCompletedArtifact" : "RestartMissingArtifact";
        }

        return artifact.Exists && artifact.Bytes > 0
            ? "ResumeInterruptedArtifact"
            : "RestartWithoutArtifact";
    }

    private static string BuildRecoveryInstructions(RunState? previous, ArtifactSnapshot artifact)
    {
        var mode = DetermineRecoveryMode(previous, artifact);
        var artifactPath = ToolWorkspace.ArtifactRelativePath;
        var priorDescription = previous is null
            ? "No readable prior run manifest was found."
            : $"Prior run {previous.RunId} ended with status '{previous.Status}' and recorded {previous.CheckpointCount} artifact checkpoints.";

        return $"""
            Recovery mode: {mode}
            {priorDescription}
            Existing {artifactPath}: {artifact.Description}

            Recovery requirements:
            - If {artifactPath} exists, read it before any write or append operation.
            - Compare its headings and content with ADVENTURE_GENERATION_TEMPLATE.md and identify the last complete, internally consistent section.
            - Preserve complete existing sections. Never append a duplicate heading or repeat content already present.
            - If the file ends cleanly at a completed section, continue with the next missing section using AppendFileAsync.
            - If the final section is truncated, contradictory, contains placeholders, or is out of template order, reconstruct a clean corrected document and replace it atomically with WriteFileAsync before continuing.
            - If no usable artifact exists, restart from the template and map, then create {artifactPath} early.
            - If the earlier run was already complete, validate the existing artifact. Do not regenerate or append unless a concrete completeness, consistency, or rules issue requires correction.
            - Report the recovery decision in a `[Status]` message before modifying the artifact.
            """;
    }

    private static ArtifactSnapshot GetArtifactSnapshot(string artifactPath)
    {
        var file = new FileInfo(artifactPath);
        return file.Exists
            ? new ArtifactSnapshot(true, file.Length)
            : new ArtifactSnapshot(false, 0);
    }

    private static readonly Regex UnresolvedPlaceholderRegex = new(
        "\\{\\{[^{}]+\\}\\}",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record ArtifactSnapshot(bool Exists, long Bytes)
    {
        public string Description => Exists ? $"present ({Bytes:N0} bytes)" : "missing";
    }

    private sealed class RunState
    {
        public string RunId { get; set; } = "";
        public string AdventureDirectory { get; set; } = "";
        public string? PreviousRunId { get; set; }
        public string Status { get; set; } = RunStatus.Running;
        public string? PreviousStatus { get; set; }
        public string RecoveryMode { get; set; } = "Fresh";
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public bool ArtifactExists { get; set; }
        public long ArtifactBytes { get; set; }
        public int CheckpointCount { get; set; }
        public DateTimeOffset? LastCheckpointAtUtc { get; set; }
        public string? LastCheckpointReason { get; set; }
        public string? LastError { get; set; }
        public int? ResponseCharacters { get; set; }
    }

    private static class RunStatus
    {
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";
        public const string Failed = "Failed";
    }
}
