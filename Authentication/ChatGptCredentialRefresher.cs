using System.ClientModel;

internal sealed class ChatGptCredentialRefresher(
    ChatGptAuthentication authentication,
    ApiKeyCredential credential) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _refreshLoop;

    public void Start()
    {
        _refreshLoop ??= RefreshLoopAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_refreshLoop is not null)
        {
            try
            {
                await _refreshLoop;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        _stopping.Dispose();
        authentication.Dispose();
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var accessToken = await authentication.GetAccessTokenAsync(cancellationToken);
                credential.Update(accessToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[ChatGPT Auth] Token refresh check failed: {exception.Message}");
            }
        }
    }
}
