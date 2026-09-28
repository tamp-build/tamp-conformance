using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamp.Conformance;

namespace Tamp.Conformance.Anthropic;

/// <summary>
/// Anthropic (Claude) <see cref="IChatCompletion"/> — a thin HTTP shim over the Messages API. The
/// recommended default provider; endpoint is overridable via <see cref="ModelConfig.Endpoint"/> for an
/// Anthropic-compatible gateway. The API key is supplied by the caller (read from the environment or
/// <c>~/.claude/credentials.json</c> via <see cref="FromEnvironment"/>) and only ever travels in the
/// request header — never emitted on the build stream.
/// </summary>
public sealed class AnthropicChat : IChatCompletion, IDisposable
{
    private const string DefaultEndpoint = "https://api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ModelConfig _config;

    public AnthropicChat(string apiKey, ModelConfig config, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _config = config;
        _http = http ?? new HttpClient();
    }

    public string ModelId => $"anthropic/{_config.ModelId}";

    public string Complete(string system, string user)
    {
        var body = JsonSerializer.Serialize(new MessagesRequest
        {
            Model = _config.ModelId,
            MaxTokens = _config.MaxTokens,
            System = system,
            Messages = new[] { new Message { Role = "user", Content = user } },
        }, JsonOpts);

        var endpoint = (_config.Endpoint ?? DefaultEndpoint).TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = _http.Send(req);
        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)resp.StatusCode}: {payload}");

        var parsed = JsonSerializer.Deserialize<MessagesResponse>(payload, JsonOpts)
                     ?? throw new FormatException("Anthropic API returned no parseable body.");
        var sb = new StringBuilder();
        foreach (var block in parsed.Content ?? Array.Empty<ContentBlock>())
        {
            if (block.Type == "text" && block.Text is not null)
                sb.Append(block.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a client, resolving the API key from <c>ANTHROPIC_API_KEY</c> and falling back to
    /// <c>~/.claude/credentials.json</c> (<c>anthropic_api_key</c>). Throws if no key is found.
    /// </summary>
    public static AnthropicChat FromEnvironment(ModelConfig config)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(key))
            key = ReadKeyFromCredentialsFile();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("No Anthropic API key: set ANTHROPIC_API_KEY or add anthropic_api_key to ~/.claude/credentials.json.");
        return new AnthropicChat(key, config);
    }

    private static string? ReadKeyFromCredentialsFile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".claude", "credentials.json");
        if (!File.Exists(path))
            return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("anthropic_api_key", out var v) ? v.GetString() : null;
    }

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record MessagesRequest
    {
        public required string Model { get; init; }
        public required int MaxTokens { get; init; }
        public string? System { get; init; }
        public required IReadOnlyList<Message> Messages { get; init; }
    }

    private sealed record Message
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    private sealed record MessagesResponse
    {
        public IReadOnlyList<ContentBlock>? Content { get; init; }
    }

    private sealed record ContentBlock
    {
        public string? Type { get; init; }
        public string? Text { get; init; }
    }
}
