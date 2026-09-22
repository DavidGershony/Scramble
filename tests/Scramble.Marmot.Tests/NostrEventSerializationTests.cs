using System.Text;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// NIP-01 canonical serialisation.
/// </summary>
/// <remarks>
/// The event id is a hash of these exact bytes, so any escaping difference
/// yields an id no other implementation agrees with and a signature every peer
/// rejects. These cases exist because the .NET JSON encoders all get the
/// astral-plane case wrong. Characters that are line separators in C# source
/// (U+2028, U+2029) are written as escapes here on purpose.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class NostrEventSerializationTests
{
    private const string Pubkey = "f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9";

    private static NostrEventTemplate WithContent(string content) =>
        new(Pubkey, 1700000000, 1, Array.Empty<IReadOnlyList<string>>(), content);

    [Fact]
    public void AsciiContentSerializesAsExpected()
    {
        Assert.Equal(
            $"[0,\"{Pubkey}\",1700000000,1,[],\"hello\"]",
            WithContent("hello").Serialize());
    }

    [Fact]
    public void EmojiAreEmittedVerbatimNotAsSurrogateEscapes()
    {
        // The defect this file exists for: every .NET JSON encoder writes
        // 😀 here, producing an id nobody else computes.
        string serialized = WithContent("gm \U0001F600").Serialize();

        Assert.Contains("gm \U0001F600", serialized);
        Assert.DoesNotContain("\\u", serialized);
    }

    [Theory]
    [InlineData("café")]
    [InlineData("中文")]
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466")]
    [InlineData("\U0001D54F")]
    public void NonAsciiAndU007fAreEmittedVerbatim(string content)
    {
        string serialized = WithContent(content).Serialize();

        Assert.Contains(content, serialized);
        Assert.DoesNotContain("\\u", serialized);
    }

    [Fact]
    public void TheSevenNamedEscapesAreUsed()
    {
        string serialized = WithContent("\"\\\n\r\t\b\f").Serialize();

        Assert.Contains("\\\"", serialized);
        Assert.Contains("\\\\", serialized);
        Assert.Contains("\\n", serialized);
        Assert.Contains("\\r", serialized);
        Assert.Contains("\\t", serialized);
        Assert.Contains("\\b", serialized);
        Assert.Contains("\\f", serialized);
    }

    [Fact]
    public void OtherControlCharactersUseFourDigitLowercaseHex()
    {
        string serialized = WithContent(
            new string(new[] { (char)0x00, (char)0x1F, (char)0x01 })).Serialize();

        Assert.Contains("\\u0000", serialized);
        Assert.Contains("\\u001f", serialized);
        Assert.Contains("\\u0001", serialized);
    }

    [Theory]
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    [InlineData(0x007F)] // delete
    public void CharactersTheJsonEncodersEscapeAreEmittedVerbatim(int codePoint)
    {
        // Built at runtime: U+2028 and U+2029 are line terminators in C# source,
        // so they cannot appear literally here.
        string content = "a" + (char)codePoint + "b";

        string serialized = WithContent(content).Serialize();

        Assert.Contains(content, serialized);
        Assert.DoesNotContain("\\u", serialized);
    }

    [Fact]
    public void AnUnpairedSurrogateIsRejectedRatherThanCorrupted()
    {
        // Replacing it with U+FFFD, as the JSON encoders do, silently changes
        // the bytes being signed.
        var template = WithContent("bad \ud800 surrogate");

        Assert.Throws<ArgumentException>(() => template.Serialize());
    }

    [Fact]
    public void TagValuesAreEscapedTheSameWayAsContent()
    {
        var template = new NostrEventTemplate(
            Pubkey, 1700000000, 1,
            new[] { new[] { "d", "emoji \U0001F600 and \"quotes\"" } },
            "body");

        string serialized = template.Serialize();

        Assert.Contains("emoji \U0001F600 and \\\"quotes\\\"", serialized);
    }

    [Fact]
    public void StructureMatchesTheCanonicalArrayForm()
    {
        var template = new NostrEventTemplate(
            Pubkey, 42, 7,
            new[] { new[] { "a", "b" }, new[] { "c" } },
            "x");

        Assert.Equal(
            $"[0,\"{Pubkey}\",42,7,[[\"a\",\"b\"],[\"c\"]],\"x\"]",
            template.Serialize());
    }

    [Fact]
    public void EmptyTagsAndContentSerializeCleanly()
    {
        Assert.Equal(
            $"[0,\"{Pubkey}\",1700000000,1,[],\"\"]",
            WithContent("").Serialize());
    }

    [Fact]
    public void TheIdIsSha256OverTheUtf8Bytes()
    {
        var template = WithContent("hello");

        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(template.Serialize())),
            template.ComputeId());
    }

    [Fact]
    public void TheEscapedFormWouldHashDifferently()
    {
        // Guards against a future "simplification" back to a JSON encoder: the
        // surrogate-escaped form hashes differently, which is the whole defect.
        string canonical = WithContent("gm \U0001F600").Serialize();
        string escaped = canonical.Replace("\U0001F600", "\\uD83D\\uDE00");

        Assert.NotEqual(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(escaped))),
            Convert.ToHexString(WithContent("gm \U0001F600").ComputeId()));
    }
}
