using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tamp.Conformance;

/// <summary>Fetches a project's Zero Trust profile from tamp-findings. Kept an interface for testability.</summary>
public interface IZtProfileClient
{
    /// <summary>Returns the project's ZT profile, or <see langword="null"/> when the project is not ZT-scored (findings returns 404).</summary>
    ZtProfile? Get(string endpoint, string ingestToken);
}

/// <summary>
/// HTTP <see cref="IZtProfileClient"/> against tamp-findings' <c>GET {endpoint}/projects/self/zt-profile</c>
/// (un-prefixed surface, version in body), authenticated with the project ingest token (<c>prj_…</c>) as a
/// bearer credential. A 404 (not ZT-scored / no model loaded) returns <see langword="null"/>, not an error;
/// other non-success statuses throw. Read-only; the token never leaves the request header.
/// </summary>
public sealed class FindingsZtProfileClient : IZtProfileClient, IDisposable
{
    private const string Path = "/projects/self/zt-profile";
    private readonly HttpClient _http;

    public FindingsZtProfileClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    public ZtProfile? Get(string endpoint, string ingestToken)
    {
        var url = endpoint.TrimEnd('/') + Path;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingestToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = _http.Send(req);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;   // project not ZT-scored — not an error

        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"tamp-findings zt-profile {(int)resp.StatusCode}: {payload}");

        return JsonSerializer.Deserialize<ZtProfile>(payload, JsonOpts)
               ?? throw new FormatException("zt-profile returned no parseable body.");
    }

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
