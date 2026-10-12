using System.Collections.Concurrent;

namespace Tamp.Conformance;

/// <summary>
/// Token usage for one model call — the fact every provider already returns and the seam used to discard
/// (#12). Counts only; never prompt/response content (redaction posture). Cache tokens are captured
/// distinctly because providers price them differently (Anthropic prompt caching).
/// </summary>
public sealed record ModelUsage(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0)
{
    /// <summary>No usage reported (e.g. a provider/path that doesn't surface counts). Zero, not unknown-cost.</summary>
    public static readonly ModelUsage None = new(0, 0);

    public long TotalTokens => InputTokens + OutputTokens;

    public static ModelUsage operator +(ModelUsage a, ModelUsage b) => new(
        a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens);
}

/// <summary>A completion's text plus the token usage the provider reported for it.</summary>
public sealed record ChatResult(string Text, ModelUsage Usage);

/// <summary>
/// Accumulates <see cref="ModelUsage"/> across a run, rolled up per <c>(capability, modelId)</c> — so one
/// rule <c>generate</c>, N <c>evaluate</c> calls, and a <c>reverse</c> pass are each separately countable
/// (#12). Thread-safe. Pure counting; cost (policy) is applied separately via <see cref="ModelPricing"/>.
/// </summary>
public sealed class UsageAccumulator
{
    private readonly ConcurrentDictionary<(string Capability, string ModelId), Entry> _entries = new();

    private sealed class Entry
    {
        public int Calls;
        public ModelUsage Usage = ModelUsage.None;
        public long LatencyMs;
        public readonly object Gate = new();
    }

    /// <summary>Record one call's usage under a capability (<c>generate</c> / <c>evaluate</c> / <c>reverse</c>) + model id.</summary>
    public void Record(string capability, string modelId, ModelUsage usage, long latencyMs = 0)
    {
        var e = _entries.GetOrAdd((capability, modelId), _ => new Entry());
        lock (e.Gate)
        {
            e.Calls++;
            e.Usage += usage;
            e.LatencyMs += latencyMs;
        }
    }

    /// <summary>Per-(capability, modelId) totals, ordered deterministically.</summary>
    public IReadOnlyList<UsageRecord> Totals => _entries
        .Select(kv => new UsageRecord(kv.Key.Capability, kv.Key.ModelId, kv.Value.Calls, kv.Value.Usage, kv.Value.LatencyMs))
        .OrderBy(r => r.Capability, StringComparer.Ordinal)
        .ThenBy(r => r.ModelId, StringComparer.Ordinal)
        .ToList();
}

/// <summary>One rolled-up usage line: a capability + model, its call count, summed tokens, and total latency.</summary>
public sealed record UsageRecord(string Capability, string ModelId, int Calls, ModelUsage Usage, long LatencyMs);

/// <summary>Per-million-token USD prices for a model. Cache prices optional (null → priced as input).</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, decimal? CacheReadPerMillion = null, decimal? CacheWritePerMillion = null);

/// <summary>
/// Config pricing table (#12): tokens are facts the producer emits; price is policy. This is a dated default
/// callers override — a price change re-values history without a re-scan (so the authoritative pricing lives
/// in findings; this is for local estimates/logs). Unknown model → cost <see langword="null"/> (never guessed);
/// a local/Ollama model priced at 0 still counts tokens + latency.
/// </summary>
public sealed class ModelPricing
{
    private readonly IReadOnlyDictionary<string, ModelPrice> _prices;

    /// <summary>The date these prices were set (prices change; stamp it).</summary>
    public string AsOf { get; }

    public ModelPricing(IReadOnlyDictionary<string, ModelPrice> prices, string asOf)
    {
        _prices = prices;
        AsOf = asOf;
    }

    /// <summary>
    /// Built-in default prices (USD per 1M tokens), dated 2026-10-12. Keyed by the frozen provenance model id
    /// (<c>provider/model</c>). Override with your own table; findings holds the authoritative one.
    /// </summary>
    public static ModelPricing Default { get; } = new(new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic/claude-opus-4-8"] = new(15m, 75m, CacheReadPerMillion: 1.5m, CacheWritePerMillion: 18.75m),
        ["anthropic/claude-sonnet-4-5"] = new(3m, 15m, CacheReadPerMillion: 0.3m, CacheWritePerMillion: 3.75m),
        ["anthropic/claude-haiku-4-5"] = new(0.8m, 4m, CacheReadPerMillion: 0.08m, CacheWritePerMillion: 1m),
    }, asOf: "2026-10-12");

    /// <summary>True when a price is known for <paramref name="modelId"/>.</summary>
    public bool Knows(string modelId) => _prices.ContainsKey(modelId);

    /// <summary>
    /// Estimated USD cost for <paramref name="usage"/> on <paramref name="modelId"/>, or <see langword="null"/>
    /// when the model isn't in the table (tokens are still known; cost is simply not asserted).
    /// </summary>
    public decimal? EstimateUsd(string modelId, ModelUsage usage)
    {
        if (!_prices.TryGetValue(modelId, out var p)) return null;
        decimal Per(long tokens, decimal perMillion) => tokens / 1_000_000m * perMillion;
        return Per(usage.InputTokens, p.InputPerMillion)
             + Per(usage.OutputTokens, p.OutputPerMillion)
             + Per(usage.CacheReadTokens, p.CacheReadPerMillion ?? p.InputPerMillion)
             + Per(usage.CacheWriteTokens, p.CacheWritePerMillion ?? p.InputPerMillion);
    }
}
