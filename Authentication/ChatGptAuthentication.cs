using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class ChatGptAuthentication : IDisposable
{
    private const string AuthorizationEndpoint = "https://auth.openai.com/api/accounts/authorize";
    private const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    private const string DiscoveryEndpoint = "https://auth.openai.com/.well-known/openid-configuration";
    private const string ExpectedIssuer = "https://auth.openai.com";
    private const string Resource = "https://api.openai.com/v1";
    private const string RequiredScope = "chatgpt.tokens.use.direct";
    private const string InitialClientId = "dynamic_agent_client";
    private const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private const string AgentName = "DungeonGenerator";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _credentialPath = GetCredentialPath();

    private ChatGptAuthRecord? _record;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _record ??= await LoadAsync(cancellationToken);
            if (_record is null)
            {
                Console.WriteLine("[ChatGPT Auth] No saved connection found. Continue with ChatGPT in your browser.");
                _record = await SignInAsync(existing: null, cancellationToken);
                await SaveAsync(_record, cancellationToken);
            }
            else if (_record.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                try
                {
                    _record = await RefreshAsync(_record, cancellationToken);
                    await SaveAsync(_record, cancellationToken);
                    Console.WriteLine("[ChatGPT Auth] Refreshed the saved ChatGPT connection.");
                }
                catch (ChatGptReauthenticationRequiredException)
                {
                    Console.WriteLine("[ChatGPT Auth] The saved connection needs approval again. Continue in your browser.");
                    _record = await SignInAsync(_record, cancellationToken);
                    await SaveAsync(_record, cancellationToken);
                }
            }

            EnsurePlanSharingEnabled(_record);
            return _record.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Could not list models for the connected ChatGPT account (HTTP {(int)response.StatusCode}): {Limit(body)}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The ChatGPT model catalog response did not contain a models array.");
        }

        return models
            .EnumerateArray()
            .Where(model => !model.TryGetProperty("visibility", out var visibility) || visibility.GetString() == "list")
            .Select(model => model.TryGetProperty("slug", out var slug) ? slug.GetString() : null)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => slug!)
            .ToArray();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _gate.Dispose();
    }

    private async Task<ChatGptAuthRecord> SignInAsync(
        ChatGptAuthRecord? existing,
        CancellationToken cancellationToken)
    {
        var hostId = existing?.HostId ?? $"urn:uuid:{Guid.NewGuid():D}";
        var requestedClientId = existing?.ClientId ?? InitialClientId;
        var state = CreateRandomValue(32);
        var nonce = CreateRandomValue(32);
        var verifier = CreateRandomValue(64);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var port = ReserveLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/auth/callback";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var query = new Dictionary<string, string>
        {
            ["client_id"] = requestedClientId,
            ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scopes,
            ["resource"] = Resource,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge
        };
        if (existing is null)
        {
            query["agent_name_hint"] = AgentName;
        }

        var authorizationUri = BuildUri(AuthorizationEndpoint, query);
        OpenBrowser(authorizationUri);

        OAuthCallback callback;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                callback = await HandleCallbackAsync(context, state, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for ChatGPT sign-in to complete.");
            }
        }

        var issuedClientId = existing is null
            ? callback.ClientId ?? throw new InvalidOperationException("ChatGPT registration did not return an issued client ID.")
            : existing.ClientId;
        if (existing is not null && callback.ClientId is not null && callback.ClientId != existing.ClientId)
        {
            throw new InvalidOperationException("ChatGPT returned a different client ID for the saved connection.");
        }

        var tokens = await ExchangeAuthorizationCodeAsync(
            issuedClientId,
            callback.Code,
            verifier,
            redirectUri,
            cancellationToken);
        var identity = await ValidateIdTokenAsync(tokens.IdToken, issuedClientId, nonce, cancellationToken);
        if (existing is not null && identity.Subject != existing.Subject)
        {
            throw new InvalidOperationException("The selected ChatGPT account does not match the saved connection.");
        }

        var record = CreateRecord(tokens, callback.Scope, issuedClientId, hostId, identity, existing);
        EnsurePlanSharingEnabled(record);
        Console.WriteLine($"[ChatGPT Auth] Connected{(record.Email is null ? string.Empty : $" as {record.Email}")}.");
        return record;
    }

    private async Task<ChatGptAuthRecord> RefreshAsync(
        ChatGptAuthRecord existing,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = existing.ClientId,
            ["refresh_token"] = existing.RefreshToken,
            ["resource"] = Resource
        });
        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                throw new ChatGptReauthenticationRequiredException();
            }

            throw new HttpRequestException(
                $"ChatGPT token refresh failed with HTTP {(int)response.StatusCode}: {Limit(body)}",
                null,
                response.StatusCode);
        }

        var tokens = DeserializeTokenResponse(body, requireIdToken: false);
        var now = DateTimeOffset.UtcNow;
        var scopes = ParseScopes(tokens.Scope).Length == 0 ? existing.Scopes : ParseScopes(tokens.Scope);
        return new ChatGptAuthRecord
        {
            Email = existing.Email,
            Issuer = existing.Issuer,
            Subject = existing.Subject,
            ClientId = existing.ClientId,
            HostId = existing.HostId,
            IdToken = string.IsNullOrWhiteSpace(tokens.IdToken) ? existing.IdToken : tokens.IdToken,
            AccessToken = tokens.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? existing.RefreshToken : tokens.RefreshToken,
            TokenType = tokens.TokenType ?? existing.TokenType,
            Scopes = scopes,
            ExpiresAt = now.AddSeconds(tokens.ExpiresIn),
            SavedAt = now
        };
    }

    private async Task<TokenResponse> ExchangeAuthorizationCodeAsync(
        string clientId,
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = Resource
        });
        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"ChatGPT authorization-code exchange failed with HTTP {(int)response.StatusCode}: {Limit(body)}",
                null,
                response.StatusCode);
        }

        return DeserializeTokenResponse(body, requireIdToken: true);
    }

    private async Task<ValidatedIdentity> ValidateIdTokenAsync(
        string idToken,
        string clientId,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("ChatGPT returned an invalid ID token.");
        }

        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        var algorithm = header.RootElement.GetProperty("alg").GetString();
        var keyId = header.RootElement.GetProperty("kid").GetString();
        if (algorithm != "RS256" || string.IsNullOrWhiteSpace(keyId))
        {
            throw new InvalidOperationException("ChatGPT returned an ID token with an unsupported signing algorithm.");
        }

        var key = await GetSigningKeyAsync(keyId, cancellationToken);
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64UrlDecode(key.Modulus),
            Exponent = Base64UrlDecode(key.Exponent)
        });
        var signedBytes = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!rsa.VerifyData(
                signedBytes,
                Base64UrlDecode(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
        {
            throw new InvalidOperationException("ChatGPT ID-token signature validation failed.");
        }

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var claims = payload.RootElement;
        var issuer = claims.GetProperty("iss").GetString();
        if (!string.Equals(issuer?.TrimEnd('/'), ExpectedIssuer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ChatGPT ID-token issuer validation failed.");
        }

        if (!AudienceContains(claims.GetProperty("aud"), clientId))
        {
            throw new InvalidOperationException("ChatGPT ID-token audience validation failed.");
        }

        var expiry = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64());
        if (expiry <= DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            throw new InvalidOperationException("ChatGPT returned an expired ID token.");
        }

        if (!claims.TryGetProperty("nonce", out var nonce) || nonce.GetString() != expectedNonce)
        {
            throw new InvalidOperationException("ChatGPT ID-token nonce validation failed.");
        }

        var subject = claims.GetProperty("sub").GetString();
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException("ChatGPT ID token did not contain a subject.");
        }

        var email = claims.TryGetProperty("email", out var emailClaim) ? emailClaim.GetString() : null;
        return new ValidatedIdentity(issuer!, subject, email);
    }

    private async Task<JsonWebKey> GetSigningKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        using var discoveryResponse = await _httpClient.GetAsync(DiscoveryEndpoint, cancellationToken);
        discoveryResponse.EnsureSuccessStatusCode();
        using var discovery = JsonDocument.Parse(
            await discoveryResponse.Content.ReadAsStringAsync(cancellationToken));
        var jwksUri = discovery.RootElement.GetProperty("jwks_uri").GetString()
            ?? throw new InvalidOperationException("OpenAI discovery metadata did not contain a JWKS URI.");

        using var keysResponse = await _httpClient.GetAsync(jwksUri, cancellationToken);
        keysResponse.EnsureSuccessStatusCode();
        using var keys = JsonDocument.Parse(await keysResponse.Content.ReadAsStringAsync(cancellationToken));
        foreach (var candidate in keys.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (candidate.GetProperty("kid").GetString() == keyId &&
                candidate.GetProperty("kty").GetString() == "RSA")
            {
                return new JsonWebKey(
                    candidate.GetProperty("n").GetString()!,
                    candidate.GetProperty("e").GetString()!);
            }
        }

        throw new InvalidOperationException("No matching OpenAI ID-token signing key was found.");
    }

    private static async Task<OAuthCallback> HandleCallbackAsync(
        HttpListenerContext context,
        string expectedState,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = context.Request.QueryString;
            if (query["state"] != expectedState)
            {
                throw new InvalidOperationException("ChatGPT sign-in returned an invalid state value.");
            }

            if (!string.IsNullOrWhiteSpace(query["error"]))
            {
                throw new InvalidOperationException(
                    $"ChatGPT sign-in was not completed: {query["error_description"] ?? query["error"]}");
            }

            var code = query["code"];
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("ChatGPT sign-in did not return an authorization code.");
            }

            await WriteBrowserResponseAsync(
                context.Response,
                "ChatGPT sign-in complete. You can close this window and return to DungeonGenerator.",
                cancellationToken);
            return new OAuthCallback(code, query["client_id"], query["scope"]);
        }
        catch
        {
            await WriteBrowserResponseAsync(
                context.Response,
                "ChatGPT sign-in could not be completed. Return to DungeonGenerator for details.",
                cancellationToken);
            throw;
        }
    }

    private static async Task WriteBrowserResponseAsync(
        HttpListenerResponse response,
        string message,
        CancellationToken cancellationToken)
    {
        if (response.OutputStream.CanWrite)
        {
            var html = $"<!doctype html><html><body><h1>{WebUtility.HtmlEncode(message)}</h1></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken);
        }

        response.Close();
    }

    private ChatGptAuthRecord CreateRecord(
        TokenResponse tokens,
        string? callbackScope,
        string clientId,
        string hostId,
        ValidatedIdentity identity,
        ChatGptAuthRecord? existing)
    {
        var now = DateTimeOffset.UtcNow;
        return new ChatGptAuthRecord
        {
            Email = identity.Email,
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            ClientId = clientId,
            HostId = hostId,
            IdToken = tokens.IdToken,
            AccessToken = tokens.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken)
                ? existing?.RefreshToken ?? throw new InvalidOperationException("ChatGPT did not return a refresh token.")
                : tokens.RefreshToken,
            TokenType = tokens.TokenType ?? "Bearer",
            Scopes = ParseScopes(tokens.Scope ?? callbackScope),
            ExpiresAt = now.AddSeconds(tokens.ExpiresIn),
            SavedAt = now
        };
    }

    private async Task<ChatGptAuthRecord?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_credentialPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_credentialPath);
        var record = await JsonSerializer.DeserializeAsync<ChatGptAuthRecord>(
            stream,
            JsonOptions,
            cancellationToken);
        return record ?? throw new InvalidOperationException("The saved ChatGPT credential file is empty or invalid.");
    }

    private async Task SaveAsync(ChatGptAuthRecord record, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_credentialPath)!;
        Directory.CreateDirectory(directory);
        SetOwnerOnlyDirectoryPermissions(directory);

        var temporaryPath = $"{_credentialPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            SetOwnerOnlyFilePermissions(temporaryPath);
            File.Move(temporaryPath, _credentialPath, overwrite: true);
            SetOwnerOnlyFilePermissions(_credentialPath);
            Console.WriteLine($"[ChatGPT Auth] Saved connection securely at '{_credentialPath}'.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static TokenResponse DeserializeTokenResponse(string body, bool requireIdToken)
    {
        var tokens = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("ChatGPT returned an empty token response.");
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) ||
            (requireIdToken && string.IsNullOrWhiteSpace(tokens.IdToken)) ||
            tokens.ExpiresIn <= 0)
        {
            throw new InvalidOperationException("ChatGPT returned an incomplete token response.");
        }

        return tokens;
    }

    private static void EnsurePlanSharingEnabled(ChatGptAuthRecord record)
    {
        if (!record.Scopes.Contains(RequiredScope, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "ChatGPT plan usage was not approved. Reconnect and allow DungeonGenerator to use your ChatGPT plan.");
        }
    }

    private static void OpenBrowser(Uri authorizationUri)
    {
        Console.WriteLine("[ChatGPT Auth] Opening Continue with ChatGPT in your browser...");
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUri.ToString(),
                UseShellExecute = true
            });
            if (process is not null)
            {
                return;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ChatGPT Auth] Could not open the browser automatically: {exception.Message}");
        }

        Console.WriteLine("Open this URL to continue:");
        Console.WriteLine(authorizationUri);
    }

    private static Uri BuildUri(string endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{endpoint}?{query}");
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateRandomValue(int bytes)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Convert.FromBase64String(padded);
    }

    private static bool AudienceContains(JsonElement audience, string clientId)
        => audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == clientId,
            JsonValueKind.Array => audience.EnumerateArray().Any(value => value.GetString() == clientId),
            _ => false
        };

    private static string[] ParseScopes(string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? []
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string GetCredentialPath()
    {
        var configurationRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(configurationRoot))
        {
            configurationRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configurationRoot, "DungeonGenerator", "chatgpt-auth.json");
    }

    private static void SetOwnerOnlyDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetOwnerOnlyFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string Limit(string value)
        => value.Length <= 1_000 ? value : value[..1_000];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record OAuthCallback(string Code, string? ClientId, string? Scope);

    private sealed record ValidatedIdentity(string Issuer, string Subject, string? Email);

    private sealed record JsonWebKey(string Modulus, string Exponent);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("id_token")]
        public string IdToken { get; init; } = string.Empty;

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }

    private sealed class ChatGptReauthenticationRequiredException : Exception;
}
