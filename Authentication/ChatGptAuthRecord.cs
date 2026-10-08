using System.Text.Json.Serialization;

internal sealed class ChatGptAuthRecord
{
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("issuer")]
    public required string Issuer { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("ext_agent_host_id")]
    public required string HostId { get; init; }

    [JsonPropertyName("id_token")]
    public required string IdToken { get; init; }

    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("scopes")]
    public string[] Scopes { get; init; } = [];

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("saved_at")]
    public DateTimeOffset SavedAt { get; init; }
}
