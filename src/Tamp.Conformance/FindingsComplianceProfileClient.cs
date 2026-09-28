using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tamp.Conformance;

/// <summary>
/// HTTP <see cref="IComplianceProfileClient"/> against tamp-findings'
/// <c>GET {endpoint}/api/v1/projects/self/compliance-profile</c>, authenticated with the ingest token as a
/// bearer credential. Read-only; the token never leaves the request header.
/// </summary>
public sealed class FindingsComplianceProfileClient : IComplianceProfileClient, IDisposable
{
    private const string Path = "/api/v1/projects/self/compliance-profile";
    private readonly HttpClient _http;

    public FindingsComplianceProfileClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    public ComplianceProfile Get(string endpoint, string ingestToken)
    {
        var url = endpoint.TrimEnd('/') + Path;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingestToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = _http.Send(req);
        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"tamp-findings compliance-profile {(int)resp.StatusCode}: {payload}");

        return JsonSerializer.Deserialize<ComplianceProfile>(payload, JsonOpts)
               ?? throw new FormatException("compliance-profile returned no parseable body.");
    }

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
