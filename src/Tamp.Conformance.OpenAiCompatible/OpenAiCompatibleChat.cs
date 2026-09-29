using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamp.Conformance;

namespace Tamp.Conformance.OpenAiCompatible;

/// <summary>
/// One OpenAI-compatible <see cref="IChatCompletion"/> that covers many providers by base-URL override:
/// OpenAI, Azure OpenAI, Poolside, and any self-hosted / air-gapped OpenAI-compatible server (vLLM,
/// Ollama). It posts a clean, standard <c>/chat/completions</c> body (no provider-specific extras such
/// as <c>cache_control</c>, which some endpoints reject) and reads <c>choices[0].message.content</c>,
/// tolerating an extra <c>reasoning_content</c> sibling (Poolside enables thinking by default). Auth is
/// configurable: <c>Authorization: Bearer …</c> by default, or a raw <c>api-key</c> header for Azure.
/// </summary>
public sealed class OpenAiCompatibleChat : IChatCompletion, IDisposable
{
    private const string DefaultEndpoint = "https://api.openai.com/v1";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ModelConfig _config;
    private readonly string _authHeader;
    private readonly string _authPrefix;
    private readonly string _providerLabel;

    /// <summary>
    /// <paramref name="authHeader"/>/<paramref name="authPrefix"/> select auth: <c>Authorization</c> + <c>"Bearer "</c>
    /// (default) or <c>api-key</c> + <c>""</c> (Azure). <paramref name="providerLabel"/> prefixes <see cref="ModelId"/>
    /// for provenance (e.g. <c>openai</c>, <c>poolside</c>, <c>azure</c>).
    /// </summary>
    public OpenAiCompatibleChat(
        string apiKey,
        ModelConfig config,
        HttpClient? http = null,
        string authHeader = "Authorization",
        string authPrefix = "Bearer ",
        string providerLabel = "openai")
    {
        _apiKey = apiKey;
        _config = config;
        _http = http ?? new HttpClient();
        _authHeader = authHeader;
        _authPrefix = authPrefix;
        _providerLabel = providerLabel;
    }

    public string ModelId => $"{_providerLabel}/{_config.ModelId}";

    public string Complete(string system, string user)
    {
        var body = JsonSerializer.Serialize(new ChatRequest
        {
            Model = _config.ModelId,
            MaxTokens = _config.MaxTokens,
            Temperature = _config.Temperature,
            Messages = new[]
            {
                new Message { Role = "system", Content = system },
                new Message { Role = "user", Content = user },
            },
        }, JsonOpts);

        var endpoint = (_config.Endpoint ?? DefaultEndpoint).TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation(_authHeader, $"{_authPrefix}{_apiKey}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = _http.Send(req);
        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI-compatible API {(int)resp.StatusCode}: {payload}");

        var parsed = JsonSerializer.Deserialize<ChatResponse>(payload, JsonOpts)
                     ?? throw new FormatException("OpenAI-compatible API returned no parseable body.");
        return parsed.Choices is { Count: > 0 } ? parsed.Choices[0].Message?.Content ?? "" : "";
    }

    /// <summary>Build a client, resolving the key from <c>CONFORMANCE_MODEL_API_KEY</c> then <c>OPENAI_API_KEY</c>.</summary>
    public static OpenAiCompatibleChat FromEnvironment(
        ModelConfig config, string authHeader = "Authorization", string authPrefix = "Bearer ", string providerLabel = "openai")
    {
        var key = Environment.GetEnvironmentVariable("CONFORMANCE_MODEL_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("No API key: set CONFORMANCE_MODEL_API_KEY or OPENAI_API_KEY.");
        return new OpenAiCompatibleChat(key, config, authHeader: authHeader, authPrefix: authPrefix, providerLabel: providerLabel);
    }

    /// <summary>Convenience for Poolside: sets the hosted endpoint default and <c>poolside</c> label. Pass an in-VPC endpoint via <paramref name="config"/> for air-gapped use.</summary>
    public static OpenAiCompatibleChat ForPoolside(string apiKey, ModelConfig config, HttpClient? http = null)
        => new(apiKey, config with { Endpoint = config.Endpoint ?? "https://inference.poolside.ai/v1" }, http, providerLabel: "poolside");

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ChatRequest
    {
        public required string Model { get; init; }
        public required int MaxTokens { get; init; }
        public double? Temperature { get; init; }
        public required IReadOnlyList<Message> Messages { get; init; }
    }

    private sealed record Message
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    private sealed record ChatResponse
    {
        public IReadOnlyList<Choice>? Choices { get; init; }
    }

    private sealed record Choice
    {
        public ResponseMessage? Message { get; init; }
    }

    private sealed record ResponseMessage
    {
        public string? Content { get; init; }
        // reasoning_content (Poolside thinking) is intentionally not bound — we read content only.
    }
}
