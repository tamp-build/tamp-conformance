using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tamp.Conformance.Quality;

/// <summary>The three raw SonarQube/SonarCloud API responses for a project, ready to feed the parsers.</summary>
public sealed record SonarCloudSnapshot(string MeasuresJson, string IssuesJson, string HotspotsJson)
{
    /// <summary>Build a <see cref="SonarQubeApiSource"/> (findings) from this snapshot.</summary>
    public SonarQubeApiSource ToSource(string? analysisId = null) => new(IssuesJson, HotspotsJson, analysisId);

    /// <summary>Parse the quality-gate verdict from this snapshot.</summary>
    public QualityGateVerdict ToGateVerdict(string? analysisId = null) => SonarQualityGate.Parse(MeasuresJson, analysisId);
}

/// <summary>
/// Live read-only client for the SonarQube/SonarCloud web API. Fetches the measures/component,
/// issues/search (paginated + merged) and hotspots/search responses for a project, which the
/// <see cref="SonarQubeApiSource"/> and <see cref="SonarQualityGate"/> parse. Read-only projection of
/// an already-completed analysis — no scanning happens here.
/// </summary>
public sealed class SonarCloudFetcher
{
    private const int PageSize = 500;                       // SonarQube max page size
    private const int MaxPages = 40;                        // safety cap (20k issues)

    private static readonly string[] MeasureMetrics =
        { "alert_status", "quality_gate_details", "bugs", "vulnerabilities", "code_smells", "security_hotspots", "ncloc", "sqale_index" };

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _token;

    /// <param name="http">Client to use (its lifetime is the caller's; auth is set per-request, not on the client).</param>
    /// <param name="token">SonarCloud token (sent as Bearer). For SonarCloud OSS this is typically a user token.</param>
    /// <param name="baseUrl">API base, default the public SonarCloud.</param>
    public SonarCloudFetcher(HttpClient http, string token, string baseUrl = "https://sonarcloud.io")
    {
        _http = http;
        _token = token;
        _base = baseUrl.TrimEnd('/');
    }

    /// <summary>Fetch measures + all issues (merged) + hotspots for a project (optionally a branch).</summary>
    public async Task<SonarCloudSnapshot> FetchAsync(string projectKey, string? branch = null, CancellationToken ct = default)
    {
        var key = Uri.EscapeDataString(projectKey);
        var b = string.IsNullOrEmpty(branch) ? "" : $"&branch={Uri.EscapeDataString(branch!)}";

        var measures = await GetStringAsync(
            $"{_base}/api/measures/component?component={key}&metricKeys={string.Join(",", MeasureMetrics)}{b}", ct);
        var issues = await FetchAllIssuesAsync(key, b, ct);
        var hotspots = await GetStringAsync(
            $"{_base}/api/hotspots/search?projectKey={key}&ps={PageSize}{b}", ct);

        return new SonarCloudSnapshot(measures, issues, hotspots);
    }

    // issues/search is paged; fetch every page and merge the `issues` arrays into one `{ "issues": [...] }`
    // document so SonarQubeApiSource (which reads a single response) sees the whole set.
    private async Task<string> FetchAllIssuesAsync(string key, string branchQuery, CancellationToken ct)
    {
        var merged = new StringBuilder("{\"issues\":[");
        var first = true;
        for (var page = 1; page <= MaxPages; page++)
        {
            var body = await GetStringAsync(
                $"{_base}/api/issues/search?componentKeys={key}&resolved=false&ps={PageSize}&p={page}{branchQuery}", ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var count = 0;
            if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in arr.EnumerateArray())
                {
                    if (!first) merged.Append(',');
                    merged.Append(issue.GetRawText());
                    first = false;
                    count++;
                }
            }
            var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv
                : root.TryGetProperty("paging", out var pg) && pg.TryGetProperty("total", out var pt) && pt.TryGetInt32(out var ptv) ? ptv
                : 0;
            if (count == 0 || page * PageSize >= total) break;
        }
        merged.Append("]}");
        return merged.ToString();
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        var payload = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"SonarCloud {(int)resp.StatusCode} for {url}: {(payload.Length > 300 ? payload[..300] : payload)}");
        return payload;
    }
}
