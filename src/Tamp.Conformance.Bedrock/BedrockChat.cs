using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamp.Conformance;

namespace Tamp.Conformance.Bedrock;

/// <summary>
/// AWS Bedrock <see cref="IChatCompletion"/> for Anthropic (Claude) models — a thin shim over the Bedrock
/// Runtime <c>InvokeModel</c> API, signed with hand-rolled <see cref="AwsSigV4"/> (no AWS SDK dependency,
/// matching the other adapters). The request body is the Bedrock-flavored Anthropic Messages format
/// (<c>anthropic_version: "bedrock-2023-05-31"</c>); the model id lives in the URL path, not the body.
/// Credentials come from the caller (or the standard AWS env vars via <see cref="FromEnvironment"/>) and
/// only ever go into the SigV4 signature — never onto the build stream. The default endpoint is
/// <c>bedrock-runtime.{region}.amazonaws.com</c>; override via <see cref="ModelConfig.Endpoint"/> for
/// GovCloud, FIPS, or a VPC endpoint.
/// </summary>
public sealed class BedrockChat : IChatCompletion, IDisposable
{
    private const string Service = "bedrock";
    private const string BedrockAnthropicVersion = "bedrock-2023-05-31";

    private readonly HttpClient _http;
    private readonly AwsSigV4.Credentials _creds;
    private readonly ModelConfig _config;
    private readonly Func<DateTimeOffset> _clock;

    public BedrockChat(AwsSigV4.Credentials credentials, ModelConfig config, HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _creds = credentials;
        _config = config;
        _http = http ?? new HttpClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Frozen into <c>Provenance.ModelId</c>, e.g. <c>bedrock/anthropic.claude-3-5-sonnet-20240620-v1:0</c>.</summary>
    public string ModelId => $"bedrock/{_config.ModelId}";

    public string Complete(string system, string user)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new InvokeRequest
        {
            AnthropicVersion = BedrockAnthropicVersion,
            MaxTokens = _config.MaxTokens,
            Temperature = _config.Temperature,
            System = system,
            Messages = new[] { new Message { Role = "user", Content = user } },
        }, JsonOpts);

        var baseUrl = (_config.Endpoint ?? $"https://bedrock-runtime.{_creds.Region}.amazonaws.com").TrimEnd('/');
        // Model id is a path segment; its version suffix ":0" must be percent-encoded, and the signed
        // canonical URI must use the SAME encoding — so encode once and reuse.
        var url = $"{baseUrl}/model/{AwsSigV4.EncodePathSegment(_config.ModelId)}/invoke";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        AwsSigV4.Sign(req, body, Service, _creds, _clock());

        using var resp = _http.Send(req);
        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Bedrock InvokeModel {(int)resp.StatusCode}: {payload}");

        var parsed = JsonSerializer.Deserialize<InvokeResponse>(payload, JsonOpts)
                     ?? throw new FormatException("Bedrock returned no parseable body.");
        var sb = new StringBuilder();
        foreach (var block in parsed.Content ?? Array.Empty<ContentBlock>())
        {
            if (block.Type == "text" && block.Text is not null)
                sb.Append(block.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a client from the standard AWS environment: <c>AWS_ACCESS_KEY_ID</c>, <c>AWS_SECRET_ACCESS_KEY</c>,
    /// optional <c>AWS_SESSION_TOKEN</c> (temporary/role credentials), and the region from
    /// <c>AWS_REGION</c> / <c>AWS_DEFAULT_REGION</c> (or <paramref name="regionOverride"/>). Throws if the key,
    /// secret, or region is missing.
    /// </summary>
    public static BedrockChat FromEnvironment(ModelConfig config, string? regionOverride = null)
    {
        var access = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        var session = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");
        var region = regionOverride
                     ?? Environment.GetEnvironmentVariable("AWS_REGION")
                     ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");

        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(secret))
            throw new InvalidOperationException("No AWS credentials: set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY.");
        if (string.IsNullOrEmpty(region))
            throw new InvalidOperationException("No AWS region: set AWS_REGION (or pass regionOverride).");

        return new BedrockChat(new AwsSigV4.Credentials
        {
            AccessKeyId = access,
            SecretAccessKey = secret,
            SessionToken = session,
            Region = region,
        }, config);
    }

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record InvokeRequest
    {
        public required string AnthropicVersion { get; init; }
        public required int MaxTokens { get; init; }
        public double? Temperature { get; init; }
        public string? System { get; init; }
        public required IReadOnlyList<Message> Messages { get; init; }
    }

    private sealed record Message
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    private sealed record InvokeResponse
    {
        public IReadOnlyList<ContentBlock>? Content { get; init; }
    }

    private sealed record ContentBlock
    {
        public string? Type { get; init; }
        public string? Text { get; init; }
    }
}
