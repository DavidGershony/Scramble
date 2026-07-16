using System.Security.Cryptography;
using MarmotCs.Protocol.Mip00;
using MarmotCs.Protocol.Mip02;
using MarmotCs.Protocol.Mip03;
using MarmotCs.Protocol.Crypto;
using Xunit;
namespace Scramble.Diagnostics;

/// <summary>
/// Protocol compliance tests that verify Scramble's Nostr events conform to
/// the Marmot protocol spec as implemented in marmot-cs.
///
/// For each MIP, tests verify:
/// 1. Library round-trip: Builder output → Parser input → identical data
/// 2. Tag completeness: all required tags present with correct values
/// </summary>
[Trait("Category", "ProtocolCompliance")]
public class MarmotProtocolComplianceTests
{
    private readonly ITestOutputHelper _output;

    public MarmotProtocolComplianceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MIP-02: Welcome Event (kind 444)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Mip02_WelcomeBuilder_RoundTrips_WithParser()
    {
        // Arrange: realistic Welcome data
        var welcomeBytes = RandomNumberGenerator.GetBytes(512);
        var keyPackageEventId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var relays = new[] { "wss://relay1.example.com", "wss://relay2.example.com" };

        // Act: build with library
        var (content, tags) = WelcomeEventBuilder.BuildWelcomeEvent(welcomeBytes, keyPackageEventId, relays);
        _output.WriteLine($"Builder content: {content.Length} chars");
        _output.WriteLine($"Builder tags: {tags.Length} tags");
        foreach (var tag in tags)
            _output.WriteLine($"  [{string.Join(", ", tag)}]");

        // Act: parse with library
        var (parsedBytes, parsedKpId, parsedRelays) = WelcomeEventParser.ParseWelcomeEvent(content, tags);

        // Assert: round-trip fidelity
        Assert.Equal(welcomeBytes, parsedBytes);
        Assert.Equal(keyPackageEventId, parsedKpId);
        Assert.Equal(relays, parsedRelays);
    }

    [Fact]
    public void Mip02_WelcomeBuilder_RequiresKeyPackageEventId()
    {
        var welcomeBytes = RandomNumberGenerator.GetBytes(100);

        // null should throw
        Assert.Throws<ArgumentNullException>(() =>
            WelcomeEventBuilder.BuildWelcomeEvent(welcomeBytes, null!, new[] { "wss://relay.test" }));

        // empty should throw
        Assert.Throws<ArgumentException>(() =>
            WelcomeEventBuilder.BuildWelcomeEvent(welcomeBytes, "", new[] { "wss://relay.test" }));
    }

    [Fact]
    public void Mip02_WelcomeParser_RejectsWithoutETag()
    {
        var content = Convert.ToBase64String(RandomNumberGenerator.GetBytes(100));
        var tags = new[]
        {
            new[] { "encoding", "base64" },
            new[] { "relays", "wss://relay.test" }
            // Missing "e" tag
        };

        var ex = Assert.Throws<FormatException>(() =>
            WelcomeEventParser.ParseWelcomeEvent(content, tags));
        _output.WriteLine($"Parser correctly rejected: {ex.Message}");
        Assert.Contains("e", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mip02_WelcomeParser_RejectsWithoutEncodingTag()
    {
        var content = Convert.ToBase64String(RandomNumberGenerator.GetBytes(100));
        var kpId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tags = new[]
        {
            new[] { "e", kpId },
            new[] { "relays", "wss://relay.test" }
            // Missing "encoding" tag
        };

        var ex = Assert.Throws<FormatException>(() =>
            WelcomeEventParser.ParseWelcomeEvent(content, tags));
        _output.WriteLine($"Parser correctly rejected: {ex.Message}");
        Assert.Contains("encoding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mip02_WelcomeBuilder_ProducesAllRequiredTags()
    {
        var welcomeBytes = RandomNumberGenerator.GetBytes(256);
        var kpId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var relays = new[] { "wss://relay2.angor.io" };

        var (content, tags) = WelcomeEventBuilder.BuildWelcomeEvent(welcomeBytes, kpId, relays);

        // Verify all MIP-02 required tags
        Assert.Contains(tags, t => t[0] == "e" && t[1] == kpId);
        Assert.Contains(tags, t => t[0] == "encoding" && t[1] == "base64");
        Assert.Contains(tags, t => t[0] == "relays" && t.Length > 1);
        _output.WriteLine("All MIP-02 required tags present");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MIP-03: Group Event (kind 445)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Mip03_GroupEventBuilder_RoundTrips_WithParser()
    {
        var mlsBytes = RandomNumberGenerator.GetBytes(256);
        var groupId = RandomNumberGenerator.GetBytes(16);
        var encryptionKey = RandomNumberGenerator.GetBytes(32);

        // Build
        var (content, tags) = GroupEventBuilder.BuildGroupEvent(mlsBytes, groupId, encryptionKey);
        _output.WriteLine($"Builder content: {content.Length} chars");
        foreach (var tag in tags)
            _output.WriteLine($"  [{string.Join(", ", tag)}]");

        // Parse
        var (parsedBytes, parsedGroupId) = GroupEventParser.ParseGroupEvent(content, tags, encryptionKey);

        // Verify
        Assert.Equal(mlsBytes, parsedBytes);
        Assert.Equal(groupId, parsedGroupId);
    }

    [Fact]
    public void Mip03_GroupEventBuilder_ProducesEncodingTag()
    {
        var mlsBytes = RandomNumberGenerator.GetBytes(100);
        var groupId = RandomNumberGenerator.GetBytes(16);
        var key = RandomNumberGenerator.GetBytes(32);

        var (_, tags) = GroupEventBuilder.BuildGroupEvent(mlsBytes, groupId, key);

        Assert.Contains(tags, t => t[0] == "h");
        Assert.Contains(tags, t => t[0] == "encoding" && t[1] == "base64");
        _output.WriteLine("MIP-03 h and encoding tags present");
    }

    [Fact]
    public void Mip03_GroupEventParser_RejectsWithoutHTag()
    {
        var content = Convert.ToBase64String(RandomNumberGenerator.GetBytes(100));
        var key = RandomNumberGenerator.GetBytes(32);
        var tags = new[]
        {
            new[] { "encoding", "base64" }
            // Missing "h" tag
        };

        Assert.Throws<FormatException>(() =>
            GroupEventParser.ParseGroupEvent(content, tags, key));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MIP-00: KeyPackage Event (kind 30443)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Mip00_KeyPackageBuilder_RoundTrips_WithParser()
    {
        var kpBytes = RandomNumberGenerator.GetBytes(304);
        var identityHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var relays = new[] { "wss://relay2.angor.io" };

        // Build
        var (content, tags) = KeyPackageEventBuilder.BuildKeyPackageEvent(kpBytes, identityHex, relays);
        _output.WriteLine($"Builder tags: {tags.Length}");
        foreach (var tag in tags)
            _output.WriteLine($"  [{string.Join(", ", tag)}]");

        // Parse
        var (parsedBytes, kpRef, parsedRelays) = KeyPackageEventParser.ParseKeyPackageEvent(content, tags);

        // Verify
        Assert.Equal(kpBytes, parsedBytes);
        Assert.NotEmpty(kpRef); // KeyPackageRef is a hash
        Assert.Equal(relays, parsedRelays);
    }

    [Fact]
    public void Mip00_KeyPackageBuilder_ProducesAllRequiredTags()
    {
        var kpBytes = RandomNumberGenerator.GetBytes(200);
        var identityHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var (_, tags) = KeyPackageEventBuilder.BuildKeyPackageEvent(kpBytes, identityHex, Array.Empty<string>());

        Assert.Contains(tags, t => t[0] == "encoding" && t[1] == "base64");
        Assert.Contains(tags, t => t[0] == "mls_protocol_version");
        Assert.Contains(tags, t => t[0] == "mls_ciphersuite");
        Assert.Contains(tags, t => t[0] == "i"); // KeyPackageRef
        _output.WriteLine("All MIP-00 required tags present");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MIP-03: Commit Race Resolution
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Mip03_CommitRaceResolver_EarliestTimestampWins()
    {
        var commits = new[]
        {
            (eventId: "bbbb", createdAt: DateTimeOffset.UtcNow),
            (eventId: "aaaa", createdAt: DateTimeOffset.UtcNow.AddSeconds(-5)),
        };

        var winner = CommitRaceResolver.ResolveWinner(commits);
        Assert.Equal("aaaa", winner);
        _output.WriteLine($"Winner: {winner} (earliest timestamp)");
    }

    [Fact]
    public void Mip03_CommitRaceResolver_SameTimestamp_SmallestIdWins()
    {
        var now = DateTimeOffset.UtcNow;
        var commits = new[]
        {
            (eventId: "cccc", createdAt: now),
            (eventId: "aaaa", createdAt: now),
            (eventId: "bbbb", createdAt: now),
        };

        var winner = CommitRaceResolver.ResolveWinner(commits);
        Assert.Equal("aaaa", winner);
        _output.WriteLine($"Winner: {winner} (smallest ID at same timestamp)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Crypto: Exporter Secret Key Derivation
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Crypto_ExporterKeyDerivation_ProducesDeterministicKeys()
    {
        var exporterSecret = RandomNumberGenerator.GetBytes(32);

        var (privKey1, pubKey1) = ExporterSecretKeyDerivation.DeriveKeyPair(exporterSecret);
        var (privKey2, pubKey2) = ExporterSecretKeyDerivation.DeriveKeyPair(exporterSecret);

        Assert.Equal(privKey1, privKey2);
        Assert.Equal(pubKey1, pubKey2);
        Assert.Equal(32, privKey1.Length);
        Assert.Equal(32, pubKey1.Length);
        _output.WriteLine($"Deterministic key pair: priv={Convert.ToHexString(privKey1)[..16]}..., pub={Convert.ToHexString(pubKey1)[..16]}...");
    }
}
