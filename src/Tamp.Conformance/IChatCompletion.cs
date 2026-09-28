namespace Tamp.Conformance;

/// <summary>
/// The thin provider seam: send a system + user prompt, get text back. Each BYOK adapter
/// (<c>Tamp.Conformance.Anthropic</c>, <c>.OpenAiCompatible</c>, …) implements this as a small HTTP
/// shim; the valuable, provider-agnostic logic (extraction prompt, rule schema, JSON parsing, calibrated
/// abstention) lives in core and is unit-testable without a network call. Keeping providers behind this
/// interface is what makes bring-your-own-key work — including in-boundary / air-gapped endpoints, which
/// only differ by <see cref="ModelConfig.Endpoint"/>.
/// </summary>
public interface IChatCompletion
{
    /// <summary>Complete a single-turn prompt. Implementations return the assistant's text content only.</summary>
    string Complete(string system, string user);

    /// <summary>Identifier frozen into the attestation <c>Provenance.ModelId</c> (e.g. <c>anthropic/claude-opus-4-8</c>).</summary>
    string ModelId { get; }
}

/// <summary>
/// Provider-neutral model knobs. The API key is the adapter's concern (it registers it with the build's
/// redaction table via <c>Secret</c>), not carried here, so core stays independent of any provider's auth.
/// <see cref="Endpoint"/> is the base-URL override that points an adapter at a gov region, Azure, Poolside,
/// or a self-hosted air-gapped endpoint.
/// </summary>
public sealed record ModelConfig
{
    /// <summary>Base URL override; null uses the adapter's default (e.g. the public provider endpoint).</summary>
    public string? Endpoint { get; init; }

    /// <summary>Model identifier as the provider expects it, e.g. <c>claude-opus-4-8</c> or <c>poolside/laguna-s-2.1</c>.</summary>
    public required string ModelId { get; init; }

    public int MaxTokens { get; init; } = 4096;
}
