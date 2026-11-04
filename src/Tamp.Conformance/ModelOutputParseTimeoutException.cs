using System.Text.RegularExpressions;

namespace Tamp.Conformance;

/// <summary>
/// A model response could not be parsed because locating its JSON payload exceeded the regex match
/// timeout. The response is <b>unusable</b>: this is an extraction failure, never "the model found no rules /
/// no decisions". Derives from <see cref="FormatException"/> so existing "unparseable model output" handling
/// (an <c>error</c> verdict, not a pass) still applies, while callers that care can tell it apart.
/// </summary>
public sealed class ModelOutputParseTimeoutException : FormatException
{
    public ModelOutputParseTimeoutException(int responseLength, TimeSpan matchTimeout, RegexMatchTimeoutException inner)
        : base($"Model response ({responseLength:N0} chars) was unusable: extracting its JSON exceeded the " +
               $"{matchTimeout.TotalMilliseconds:N0} ms regex match timeout. This is an extraction failure, not an empty result.", inner)
    {
        ResponseLength = responseLength;
        MatchTimeout = matchTimeout;
    }

    /// <summary>Length, in characters, of the raw model response that could not be parsed.</summary>
    public int ResponseLength { get; }

    /// <summary>The match timeout that was exceeded.</summary>
    public TimeSpan MatchTimeout { get; }
}
