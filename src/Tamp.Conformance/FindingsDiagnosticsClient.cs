using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tamp.Conformance;

/// <summary>Result of a diagnostics ingest POST — findings reports how many advisory notes it accepted vs skipped.</summary>
public sealed record DiagnosticsIngestResult
{
    public int Accepted { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string>? Builds { get; init; }
}

/// <summary>Forwards reverse-examination advisories (undocumented-decision notes) to tamp-findings.</summary>
public interface IDiagnosticsIngestClient
{
    /// <summary>POST canonical BuildEvents (NDJSON) to findings' diagnostics surface; it keeps only <c>diagnostic.emitted</c> whose ruleId starts <c>undocumented-decision:</c> and skips the rest.</summary>
    DiagnosticsIngestResult PostNdjson(string endpoint, string ingestToken, string ndjson);
}

/// <summary>
/// HTTP <see cref="IDiagnosticsIngestClient"/> against tamp-findings' <c>POST {endpoint}/ingest/diagnostics</c>
/// (findings ADR 0013). The reverse-examination capability emits <c>diagnostic.emitted</c> notes
/// (<c>undocumented-decision:&lt;kind&gt;</c>) onto the canonical NDJSON stream; this forwards them to the
/// <b>advisory</b> evidence surface — a <em>separate</em> endpoint from <c>/ingest/conformance</c> so advisory
/// counts never muddy the conformance verdict counts. findings binds each note to a ComponentVersion by
/// <c>provenance.commitSha</c>, defaults its control to <c>CM-3</c>, and NEVER gates or raises a POA&amp;M on it.
/// Auth is the project/client ingest token (<c>prj_…</c>/<c>cli_…</c>), same scheme as the other ingests.
/// </summary>
public sealed class FindingsDiagnosticsClient : IDiagnosticsIngestClient, IDisposable
{
    private const string Path = "/ingest/diagnostics";
    private readonly HttpClient _http;

    public FindingsDiagnosticsClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    public DiagnosticsIngestResult PostNdjson(string endpoint, string ingestToken, string ndjson)
    {
        var url = endpoint.TrimEnd('/') + Path;
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingestToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = _http.Send(req);
        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"tamp-findings /ingest/diagnostics {(int)resp.StatusCode}: {payload}");

        return JsonSerializer.Deserialize<DiagnosticsIngestResult>(payload, JsonOpts) ?? new DiagnosticsIngestResult();
    }

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
