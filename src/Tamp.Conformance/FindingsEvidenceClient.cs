using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tamp.Conformance;

/// <summary>Result of an evidence ingest POST — findings reports how many events it accepted vs skipped.</summary>
public sealed record IngestResult
{
    public int Accepted { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string>? Builds { get; init; }
}

/// <summary>Forwards conformance evidence to tamp-findings' ingest surface.</summary>
public interface IEvidenceIngestClient
{
    /// <summary>POST canonical BuildEvents (NDJSON, one per line) to findings; it filters <c>conformance.evaluated</c> and skips the rest.</summary>
    IngestResult PostNdjson(string endpoint, string ingestToken, string ndjson);
}

/// <summary>
/// HTTP <see cref="IEvidenceIngestClient"/> against tamp-findings' <c>POST {endpoint}/ingest/conformance</c>.
/// The producer emits <c>conformance.evaluated</c> onto the canonical NDJSON stream (TAMP_EVENTS); this
/// forwards that stream to findings, which filters by <c>payload."$type"</c> and skips non-conformance
/// events — so the whole stream can be fire-hosed at it. Auth is the project/client ingest token
/// (<c>prj_…</c>/<c>cli_…</c>), same scheme as <c>/ingest/findings</c>. Verdicts are frozen at ingest.
/// </summary>
public sealed class FindingsEvidenceClient : IEvidenceIngestClient, IDisposable
{
    private const string Path = "/ingest/conformance";
    private readonly HttpClient _http;

    public FindingsEvidenceClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    public IngestResult PostNdjson(string endpoint, string ingestToken, string ndjson)
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
            throw new HttpRequestException($"tamp-findings /ingest/conformance {(int)resp.StatusCode}: {payload}");

        return JsonSerializer.Deserialize<IngestResult>(payload, JsonOpts) ?? new IngestResult();
    }

    /// <summary>Convenience: read an NDJSON events file (e.g. the TAMP_EVENTS sink) and forward it.</summary>
    public IngestResult PostEventsFile(string endpoint, string ingestToken, string ndjsonFilePath)
        => PostNdjson(endpoint, ingestToken, File.ReadAllText(ndjsonFilePath));

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
