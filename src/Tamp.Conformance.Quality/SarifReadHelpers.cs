using System.Text.Json;

namespace Tamp.Conformance.Quality;

/// <summary>
/// Shared SARIF-reading primitives for the SARIF-based quality sources (Roslyn, opengrep, …): string field
/// access, the result message text, and physical-location extraction + repo-root relativization. Extracted
/// so each source doesn't copy-paste them (keeps new-code duplication down and the file-URI handling — the
/// Unix-leading-slash subtlety — in one tested place).
/// </summary>
internal static class SarifReadHelpers
{
    /// <summary>A string property of a JSON object, or null when absent/not a string.</summary>
    public static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>The result's <c>message.text</c>, or "".</summary>
    public static string Message(JsonElement res) =>
        res.TryGetProperty("message", out var m) && m.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";

    /// <summary>First line of a (possibly multi-line) message, trimmed — used as a finding title.</summary>
    public static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }

    /// <summary>
    /// The first physical location's (file, line) from a result, with the file made relative to
    /// <paramref name="repoRoot"/> (forward-slashed) when it falls under it. Returns (null, null) if there's
    /// no usable location.
    /// </summary>
    public static (string? File, int? Line) Location(JsonElement res, string? repoRoot)
    {
        if (!res.TryGetProperty("locations", out var locs) || locs.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var loc in locs.EnumerateArray())
        {
            if (!loc.TryGetProperty("physicalLocation", out var pl)) continue;
            string? uri = pl.TryGetProperty("artifactLocation", out var al) ? Str(al, "uri") : null;
            if (uri is null) continue;
            int? line = null;
            if (pl.TryGetProperty("region", out var region) && region.TryGetProperty("startLine", out var sl) && sl.TryGetInt32(out var l)) line = l;
            return (Relativize(uri, repoRoot), line);
        }
        return (null, null);
    }

    /// <summary>
    /// Resolve a SARIF artifact URI to a repo-relative, forward-slashed path. <see cref="Uri.LocalPath"/>
    /// handles both <c>file:///C:/x</c> (Windows) and <c>file:///home/x</c> (Unix) — manual <c>file:///</c>
    /// stripping drops the leading slash on Unix absolute paths and breaks relativization on Linux/macOS CI.
    /// </summary>
    public static string Relativize(string uri, string? repoRoot)
    {
        string path;
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
            path = u.LocalPath;
        else
            path = Uri.UnescapeDataString(uri);
        path = path.Replace('\\', '/');
        if (repoRoot is not null)
        {
            var root = repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                path = path[root.Length..];
        }
        return path;
    }
}
