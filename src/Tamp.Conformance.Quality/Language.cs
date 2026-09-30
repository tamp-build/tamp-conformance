namespace Tamp.Conformance.Quality;

/// <summary>
/// Maps a source file to a normalized language name. Used by the coverage accountant to build the
/// per-language denominator (what's in the tree) against which analyzed files are attributed.
/// </summary>
public static class Language
{
    // Extension (lower, with dot) → language. Kept small + explicit; unknown extensions are ignored
    // (not code we account for). Add languages as sources for them come online.
    private static readonly IReadOnlyDictionary<string, string> ByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".vb"] = "vbnet",
        [".fs"] = "fsharp",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".py"] = "python",
        [".go"] = "go",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".rb"] = "ruby",
        [".php"] = "php",
        [".rs"] = "rust",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".hpp"] = "cpp",
        [".swift"] = "swift",
        [".scala"] = "scala",
        [".sql"] = "sql",
    };

    /// <summary>The language for a path, or null if the extension isn't a source language we account for.</summary>
    public static string? ForPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var lang) ? lang : null;
    }
}
