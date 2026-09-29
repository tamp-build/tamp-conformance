using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// Push a generation to, and fetch the active rule-set from, tamp-findings — the authoritative adr-rules
/// store (findings ADR 0012, per tamp-conformance ADR 0003). Push replaces the whole set (upsert by
/// (adrRef,ruleId); absent rules soft-retire); fetch returns the active set the analyzer runs. The git
/// <c>adr-rules.json</c> stays the generation source + human-review diff; findings is the served copy.
/// </summary>
public interface IAdrRulesStore
{
    /// <summary>POST a whole generation. Returns findings' {upserted, retired, active} counts.</summary>
    AdrRulesPushResult PushGeneration(string endpoint, string ingestToken, string extractionModelId, IReadOnlyList<AdrRuleWithRef> rules);

    /// <summary>GET the active (non-retired) rule-set to run.</summary>
    IReadOnlyList<AdrRuleWithRef> FetchActive(string endpoint, string ingestToken);
}

/// <summary>An <see cref="AdrRule"/> paired with its ADR ref (findings serves a flat list keyed by (adrRef, ruleId)).</summary>
public sealed record AdrRuleWithRef
{
    public required string AdrRef { get; init; }
    public required AdrRule Rule { get; init; }
}

public sealed record AdrRulesPushResult
{
    public int Upserted { get; init; }
    public int Retired { get; init; }
    public int Active { get; init; }

    /// <summary>
    /// POA&amp;Ms auto-closed because a rule they depended on was invalidated by this push (findings TFND-196:
    /// a reviewed rule whose content changed is forced to Draft, so a mandate it backed is no longer backed by
    /// an active + reviewed rule → its POA&amp;M is superseded/cancelled with an audit reason). Empty/absent when
    /// nothing was superseded.
    /// </summary>
    public IReadOnlyList<SupersededPoam>? Superseded { get; init; }
}

/// <summary>One POA&amp;M closed by a supersession (see <see cref="AdrRulesPushResult.Superseded"/>). Fields are best-effort; <see cref="Extra"/> captures anything else findings returns so the contract can evolve without breaking this client.</summary>
public sealed record SupersededPoam
{
    public string? PoamId { get; init; }
    public string? MandateId { get; init; }
    public string? Reason { get; init; }
    public string? PriorStatus { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>HTTP <see cref="IAdrRulesStore"/> — <c>POST /projects/self/adr-rules</c> + <c>GET /projects/self/adr-ruleset</c>, Bearer <c>prj_</c>.</summary>
public sealed class FindingsAdrRulesClient : IAdrRulesStore, IDisposable
{
    private const string PushPath = "/projects/self/adr-rules";
    private const string FetchPath = "/projects/self/adr-ruleset";
    private readonly HttpClient _http;

    public FindingsAdrRulesClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    public AdrRulesPushResult PushGeneration(string endpoint, string ingestToken, string extractionModelId, IReadOnlyList<AdrRuleWithRef> rules)
    {
        var wire = rules.Select(RuleWire.ToWire).ToList();
        var body = JsonSerializer.Serialize(new Generation
        {
            GenerationSha = "sha256:" + AbsolutePath.Sha256Of(string.Join("\n", wire.Select(w => w.RulesSha))),
            ExtractionModelId = extractionModelId,
            Rules = wire,
        }, JsonOpts);

        using var resp = Send(HttpMethod.Post, endpoint.TrimEnd('/') + PushPath, ingestToken, body);
        var payload = ReadBody(resp);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"tamp-findings adr-rules push {(int)resp.StatusCode}: {payload}");
        return JsonSerializer.Deserialize<AdrRulesPushResult>(payload, JsonOpts) ?? new AdrRulesPushResult();
    }

    public IReadOnlyList<AdrRuleWithRef> FetchActive(string endpoint, string ingestToken)
    {
        using var resp = Send(HttpMethod.Get, endpoint.TrimEnd('/') + FetchPath, ingestToken, body: null);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return Array.Empty<AdrRuleWithRef>();
        var payload = ReadBody(resp);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"tamp-findings adr-ruleset fetch {(int)resp.StatusCode}: {payload}");

        var parsed = JsonSerializer.Deserialize<RulesetResponse>(payload, JsonOpts)
                     ?? throw new FormatException("adr-ruleset returned no parseable body.");
        return (parsed.Rules ?? Array.Empty<WireRule>()).Select(RuleWire.FromWire).ToList();
    }

    private HttpResponseMessage Send(HttpMethod method, string url, string token, string? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.Send(req);
    }

    private static string ReadBody(HttpResponseMessage resp) => new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();

    public void Dispose() => _http.Dispose();

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- wire DTOs (findings' shape) ----
    private sealed record Generation
    {
        public required string GenerationSha { get; init; }
        public required string ExtractionModelId { get; init; }
        public required IReadOnlyList<WireRule> Rules { get; init; }
    }

    private sealed record RulesetResponse
    {
        public string? SchemaVersion { get; init; }
        public string? ProjectId { get; init; }
        public string? AsOf { get; init; }
        public IReadOnlyList<WireRule>? Rules { get; init; }
    }
}

/// <summary>findings' wire rule shape (POST /projects/self/adr-rules item / GET adr-ruleset item).</summary>
internal sealed record WireRule
{
    public required string AdrRef { get; init; }
    public required string RuleId { get; init; }
    public string? Intent { get; init; }
    public string? Method { get; init; }
    public string? CheckSpec { get; init; }
    public IReadOnlyList<string>? ControlRefs { get; init; }
    public string? ZtPillar { get; init; }
    public string? ZtFunction { get; init; }
    public int? ZtStage { get; init; }
    public string? MandateId { get; init; }
    public string? RulesSha { get; init; }
    public string? ReviewStatus { get; init; }
}

/// <summary>Maps between the local <see cref="AdrRule"/> and findings' <see cref="WireRule"/>. <c>checkSpec</c> is our check definition, opaque to findings.</summary>
internal static class RuleWire
{
    public static WireRule ToWire(AdrRuleWithRef r)
    {
        var rule = r.Rule;
        var checkSpec = JsonSerializer.Serialize(new CheckSpec
        {
            ForbiddenPattern = rule.ForbiddenPattern,
            RequiredPattern = rule.RequiredPattern,
            Scope = rule.Scope,
        }, FindingsAdrRulesClient.JsonOpts);

        return new WireRule
        {
            AdrRef = r.AdrRef,
            RuleId = rule.Id,
            Intent = rule.Claim,
            Method = rule.Kind == RuleKind.Semantic ? ConformanceMethod.Semantic : ConformanceMethod.Deterministic,
            CheckSpec = checkSpec,
            ControlRefs = rule.ControlRefs,
            ZtPillar = rule.ZtPillar,
            ZtFunction = rule.ZtFunction,
            ZtStage = rule.ZtStage,
            MandateId = rule.MandateId,
            RulesSha = "sha256:" + AbsolutePath.Sha256Of(checkSpec + "|" + rule.Id + "|" + (rule.Claim ?? "")),
            ReviewStatus = rule.ReviewStatus ?? "Draft",
        };
    }

    public static AdrRuleWithRef FromWire(WireRule w)
    {
        CheckSpec spec = new();
        if (!string.IsNullOrEmpty(w.CheckSpec))
        {
            try { spec = JsonSerializer.Deserialize<CheckSpec>(w.CheckSpec, FindingsAdrRulesClient.JsonOpts) ?? new CheckSpec(); }
            catch (JsonException) { spec = new CheckSpec(); }
        }
        return new AdrRuleWithRef
        {
            AdrRef = w.AdrRef,
            Rule = new AdrRule
            {
                Id = w.RuleId,
                Claim = w.Intent ?? w.RuleId,
                Kind = string.Equals(w.Method, ConformanceMethod.Semantic, StringComparison.OrdinalIgnoreCase) ? RuleKind.Semantic : RuleKind.Deterministic,
                ForbiddenPattern = spec.ForbiddenPattern,
                RequiredPattern = spec.RequiredPattern,
                Scope = spec.Scope,
                ControlRefs = w.ControlRefs,
                ZtPillar = w.ZtPillar,
                ZtFunction = w.ZtFunction,
                ZtStage = w.ZtStage,
                MandateId = w.MandateId,
                ReviewStatus = w.ReviewStatus,
            },
        };
    }

    private sealed record CheckSpec
    {
        public string? ForbiddenPattern { get; init; }
        public string? RequiredPattern { get; init; }
        public IReadOnlyList<string>? Scope { get; init; }
    }
}
