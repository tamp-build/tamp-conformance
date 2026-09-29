using System.Globalization;
using Tamp.Conformance.Bedrock;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class AwsSigV4Tests
{
    [Fact]
    public void SigningKey_Matches_Aws_Documented_Vector()
    {
        // AWS docs "deriving the signing key for SigV4": secret/date/region/service → known signing key.
        var key = AwsSigV4.SigningKey(
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20120215", "us-east-1", "iam");
        var hex = string.Concat(key.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        Assert.Equal("f4780e2d9f65fa895f9c67b32ce1baf0b0d8a43505a000a1a9e090d414db404d", hex);
    }

    [Fact]
    public void SigningKey_Is_Deterministic_And_Sensitive_To_Inputs()
    {
        byte[] K(string d, string r, string s) => AwsSigV4.SigningKey("secret", d, r, s);
        Assert.Equal(K("20260101", "us-east-1", "bedrock"), K("20260101", "us-east-1", "bedrock")); // deterministic
        Assert.NotEqual(K("20260101", "us-east-1", "bedrock"), K("20260102", "us-east-1", "bedrock")); // date
        Assert.NotEqual(K("20260101", "us-east-1", "bedrock"), K("20260101", "us-west-2", "bedrock")); // region
        Assert.Equal(32, K("20260101", "us-east-1", "bedrock").Length); // HMAC-SHA256
    }

    [Theory]
    [InlineData("anthropic.claude-3-5-sonnet-20240620-v1:0", "anthropic.claude-3-5-sonnet-20240620-v1%3A0")]
    [InlineData("a b", "a%20b")]
    [InlineData("keep-._~", "keep-._~")]   // RFC 3986 unreserved untouched
    public void EncodePathSegment_Percent_Encodes_Reserved_Chars(string input, string expected)
        => Assert.Equal(expected, AwsSigV4.EncodePathSegment(input));
}
