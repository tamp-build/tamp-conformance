using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Tamp.Conformance.Bedrock;

/// <summary>
/// AWS Signature Version 4 request signing — the minimum needed to call an AWS JSON endpoint over
/// <see cref="HttpClient"/> without the AWS SDK, so the Bedrock adapter stays a thin, dependency-free shim
/// like its Anthropic / OpenAI-compatible siblings. Implements the documented algorithm (canonical request →
/// string-to-sign → HMAC signing-key chain → <c>Authorization</c> header) for a POST with a JSON body.
/// The secret key is used only to derive the per-request signing key and is never logged or emitted.
/// </summary>
public static class AwsSigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>Credentials + region for signing. <see cref="SessionToken"/> is set only for temporary (STS/role) credentials.</summary>
    public sealed record Credentials
    {
        public required string AccessKeyId { get; init; }
        public required string SecretAccessKey { get; init; }
        public string? SessionToken { get; init; }
        public required string Region { get; init; }
    }

    /// <summary>
    /// Sign <paramref name="request"/> in place: computes the payload hash, adds the SigV4 headers
    /// (<c>x-amz-date</c>, <c>x-amz-content-sha256</c>, <c>x-amz-security-token</c> when a session token is
    /// present) and the <c>Authorization</c> header. <paramref name="service"/> is e.g. <c>bedrock</c>.
    /// <paramref name="nowUtc"/> is injectable for deterministic tests.
    /// </summary>
    public static void Sign(HttpRequestMessage request, byte[] payload, string service, Credentials creds, DateTimeOffset nowUtc)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required for signing.");
        var amzDate = nowUtc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Hex(Sha256(payload));

        // Content-type must be on the message content (set by the caller); host + amz headers we own here.
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrEmpty(creds.SessionToken))
            request.Headers.TryAddWithoutValidation("x-amz-security-token", creds.SessionToken);

        var host = uri.Host;
        var contentType = request.Content?.Headers.ContentType?.ToString() ?? "application/json";

        // Canonical headers MUST be sorted by lowercase name; we sign a fixed, sufficient set.
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = contentType.Trim(),
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (!string.IsNullOrEmpty(creds.SessionToken))
            signed["x-amz-security-token"] = creds.SessionToken!;

        var signedHeaderNames = string.Join(";", signed.Keys);
        var canonicalHeaders = new StringBuilder();
        foreach (var kv in signed)
            canonicalHeaders.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');

        var canonicalRequest = new StringBuilder()
            .Append("POST").Append('\n')
            .Append(uri.AbsolutePath).Append('\n')          // caller pre-encodes the path (segments URI-encoded)
            .Append(CanonicalQuery(uri)).Append('\n')
            .Append(canonicalHeaders).Append('\n')
            .Append(signedHeaderNames).Append('\n')
            .Append(payloadHash)
            .ToString();

        var scope = $"{dateStamp}/{creds.Region}/{service}/aws4_request";
        var stringToSign = new StringBuilder()
            .Append(Algorithm).Append('\n')
            .Append(amzDate).Append('\n')
            .Append(scope).Append('\n')
            .Append(Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest))))
            .ToString();

        var signingKey = SigningKey(creds.SecretAccessKey, dateStamp, creds.Region, service);
        var signature = Hex(HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        var authorization =
            $"{Algorithm} Credential={creds.AccessKeyId}/{scope}, SignedHeaders={signedHeaderNames}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    /// <summary>The SigV4 signing-key HMAC chain: kSecret → kDate → kRegion → kService → kSigning. Exposed for testing against AWS's documented vector.</summary>
    public static byte[] SigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes(service));
        return HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string CanonicalQuery(Uri uri)
    {
        var q = uri.Query.TrimStart('?');
        if (q.Length == 0) return string.Empty;
        // sort by key; Bedrock InvokeModel has no query params, so this is defensive/general.
        var pairs = q.Split('&').Select(p =>
        {
            var i = p.IndexOf('=');
            return i < 0 ? (Key: p, Val: "") : (Key: p[..i], Val: p[(i + 1)..]);
        }).OrderBy(p => p.Key, StringComparer.Ordinal);
        return string.Join("&", pairs.Select(p => $"{p.Key}={p.Val}"));
    }

    /// <summary>RFC 3986 URI-encode a single path segment (unreserved chars kept; everything else percent-encoded, uppercase hex). Used for the Bedrock model-id segment, whose <c>:</c> version suffix must become <c>%3A</c>.</summary>
    public static string EncodePathSegment(string segment)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(segment))
        {
            var c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '.' || c == '_' || c == '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(data);
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
