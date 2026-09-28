using System.Net.WebSockets;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

public class NostrServiceTests
{
    private const string LocalRelayUrl = "wss://relay2.angor.io";
    private readonly NostrService _nostrService;

    public NostrServiceTests()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _nostrService = new NostrService();
    }

    private static bool IsLocalRelayAvailable()
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            ws.ConnectAsync(new Uri(LocalRelayUrl), cts.Token).GetAwaiter().GetResult();
            ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------- refusing to wrap an already-finished event

    /// <summary>A complete, signed Nostr event, as the engine hands one over.</summary>
    private static byte[] FinishedEvent() => System.Text.Encoding.UTF8.GetBytes(
        """
        {"id":"aa11","pubkey":"bb22","created_at":1,"kind":445,
         "tags":[["h","cc33"]],"content":"dGVzdA==","sig":"dd44"}
        """);

    [Fact]
    public async Task PublishCommit_RefusesBytesThatAreAlreadyASignedEvent()
    {
        // Without this the bytes are base64'd into the content of a *second*
        // kind-445, carrying an ["encoding","base64"] tag that current peers
        // reject before any MLS processing -- and nothing throws, so the
        // sender sees a successful publish. No relay is needed: the refusal
        // must come before anything is put on a wire.
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _nostrService.PublishCommitAsync(FinishedEvent(), "cc33", null));

        Assert.Equal("commitData", ex.ParamName);
        Assert.Contains("PublishRawEventJsonAsync", ex.Message);
    }

    [Fact]
    public async Task PublishGroupMessage_RefusesBytesThatAreAlreadyASignedEvent()
    {
        // The same trap by the other door: StageRemoveMemberAsync's bytes reach
        // this method rather than PublishCommitAsync.
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _nostrService.PublishGroupMessageAsync(FinishedEvent(), "cc33", null));

        Assert.Equal("encryptedData", ex.ParamName);
    }

    [Fact]
    public async Task PublishCommit_DoesNotRefuseCiphertextThatHappensToOpenWithABrace()
    {
        // The guard must not start refusing the payload it exists to protect.
        // MIP-03 ciphertext is opaque bytes: one opening with '{' is a
        // coincidence, not an event, and refusing it would break the path that
        // works today. It gets past the guard and fails on having no relay --
        // a different exception, which is the whole point.
        byte[] ciphertext = [(byte)'{', 0x00, 0xFF, 0x01, 0x02];

        Exception ex = await Record.ExceptionAsync(
            () => _nostrService.PublishCommitAsync(ciphertext, "cc33", null));

        Assert.IsNotType<ArgumentException>(ex);
    }

    [Fact]
    public async Task PublishCommit_DoesNotRefuseJsonThatIsNotAnEvent()
    {
        // Three fields are required together -- id, sig and kind -- so an
        // application payload that happens to be a JSON object still publishes.
        byte[] json = System.Text.Encoding.UTF8.GetBytes("""{"id":"aa11","kind":445}""");

        Exception ex = await Record.ExceptionAsync(
            () => _nostrService.PublishCommitAsync(json, "cc33", null));

        Assert.IsNotType<ArgumentException>(ex);
    }

    // ------------------------------------------- the outbound Welcome rumor

    [Fact]
    public void WelcomeRumorCarriesNoEncodingTag()
    {
        // marmot-cs's WelcomeEventBuilder adds ["encoding","base64"], and current
        // peers reject a Welcome carrying it before any MLS processing -- so every
        // invite this app sent was droppable by the reference client, on the
        // Welcome rather than on the commit, with no error either side could
        // explain.
        var tags = NostrService.BuildWelcomeRumorTags(
            new string('a', 64), new[] { "wss://relay.example.com" });

        Assert.DoesNotContain(tags, t => t.Count > 0 && t[0] == "encoding");
    }

    [Fact]
    public void WelcomeRumorCarriesExactlyTheTwoRoutingTags()
    {
        // Both are routing-significant and each must appear exactly once: a peer
        // that took the first of a repeated tag could be steered by a prepended
        // one. An extra tag of our own is also a peer's judgement call to reject,
        // and the p tag it used to carry was redundant -- the kind-1059 wrap
        // carries the p that routes.
        var tags = NostrService.BuildWelcomeRumorTags(
            new string('a', 64), new[] { "wss://relay.example.com", "wss://other.example.com" });

        Assert.Equal(2, tags.Count);
        Assert.Equal(new[] { "e", new string('a', 64) }, tags[0]);
        Assert.Equal(new[] { "relays", "wss://relay.example.com", "wss://other.example.com" }, tags[1]);
    }

    [Fact]
    public void GenerateKeyPair_ShouldReturnValidKeys()
    {
        // Act
        var (privateKeyHex, publicKeyHex, nsec, npub) = _nostrService.GenerateKeyPair();

        // Assert
        Assert.NotEmpty(privateKeyHex);
        Assert.NotEmpty(publicKeyHex);
        Assert.StartsWith("nsec", nsec);
        Assert.StartsWith("npub", npub);
        Assert.Equal(64, privateKeyHex.Length); // 32 bytes = 64 hex chars
        Assert.Equal(64, publicKeyHex.Length);
    }

    [Fact]
    public void GenerateKeyPair_ShouldGenerateDifferentKeysEachTime()
    {
        // Act
        var (privateKey1, _, _, _) = _nostrService.GenerateKeyPair();
        var (privateKey2, _, _, _) = _nostrService.GenerateKeyPair();

        // Assert
        Assert.NotEqual(privateKey1, privateKey2);
    }

    [Fact]
    public void ImportPrivateKey_WithHex_ShouldWork()
    {
        // Arrange
        var (originalPrivateKeyHex, _, _, _) = _nostrService.GenerateKeyPair();

        // Act
        var (importedPrivateKeyHex, _, nsec, npub) = _nostrService.ImportPrivateKey(originalPrivateKeyHex);

        // Assert
        Assert.Equal(originalPrivateKeyHex, importedPrivateKeyHex);
        Assert.StartsWith("nsec", nsec);
        Assert.StartsWith("npub", npub);
    }

    [Fact]
    public void ImportPrivateKey_WithNsec_ShouldWork()
    {
        // Arrange - Generate a key and get its nsec
        var (originalPrivateKeyHex, originalPublicKeyHex, originalNsec, originalNpub) = _nostrService.GenerateKeyPair();

        // Act - Import using the nsec
        var (importedPrivateKeyHex, importedPublicKeyHex, importedNsec, importedNpub) = _nostrService.ImportPrivateKey(originalNsec);

        // Assert - All values should match
        Assert.Equal(originalPrivateKeyHex, importedPrivateKeyHex);
        Assert.Equal(originalPublicKeyHex, importedPublicKeyHex);
        Assert.Equal(originalNsec, importedNsec);
        Assert.Equal(originalNpub, importedNpub);
    }

    [Fact]
    public void GenerateKeyPair_NsecRoundTrip_ShouldProduceSameKeys()
    {
        // This test verifies the complete flow: generate -> use nsec -> import
        // which is the exact flow that was broken before

        // Generate new identity
        var (privateKeyHex, publicKeyHex, nsec, npub) = _nostrService.GenerateKeyPair();

        // Import using the generated nsec (simulates "Continue with this identity")
        var (importedPrivateKeyHex, importedPublicKeyHex, importedNsec, importedNpub) = _nostrService.ImportPrivateKey(nsec);

        // Everything should match
        Assert.Equal(privateKeyHex, importedPrivateKeyHex);
        Assert.Equal(publicKeyHex, importedPublicKeyHex);
        Assert.Equal(nsec, importedNsec);
        Assert.Equal(npub, importedNpub);
    }

    [Fact]
    public async Task ConnectAsync_ShouldUpdateConnectionStatus()
    {
        Assert.SkipUnless(IsLocalRelayAvailable(), "Requires local relay on wss://relay2.angor.io");

        // Arrange
        var statusUpdates = new List<NostrConnectionStatus>();
        using var subscription = _nostrService.ConnectionStatus.Subscribe(status => statusUpdates.Add(status));

        // Act
        await _nostrService.ConnectAsync(LocalRelayUrl);

        // Assert
        Assert.Single(statusUpdates);
        Assert.Equal(LocalRelayUrl, statusUpdates[0].RelayUrl);
        Assert.True(statusUpdates[0].IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldUpdateConnectionStatus()
    {
        Assert.SkipUnless(IsLocalRelayAvailable(), "Requires local relay on wss://relay2.angor.io");

        // Arrange
        await _nostrService.ConnectAsync(LocalRelayUrl);

        var statusUpdates = new List<NostrConnectionStatus>();
        using var subscription = _nostrService.ConnectionStatus.Subscribe(status => statusUpdates.Add(status));

        // Act
        await _nostrService.DisconnectAsync();

        // Assert
        Assert.Single(statusUpdates);
        Assert.False(statusUpdates[0].IsConnected);
    }


    // --------------------------------------- per-group `since` on kind-445 filters
    //
    // A newly joined group is quiet and its messages predate a busy chat's last
    // activity. MainViewModel hands one `since` -- max(LastActivityAt) - 5min -- for
    // every group at once, and the service applies it to all of them, so the new
    // group's messages are never requested from the relay. They are not lost; they
    // were never asked for. Reported as "accepted the invite, see no messages".

    private static Dictionary<string, object> FilterFor(
        List<Dictionary<string, object>> filters, string groupId) =>
        filters.Single(f => ((string[])f["#h"]).Contains(groupId));

    [Fact]
    public void GroupMessageFilters_GiveEachGroupItsOwnSince()
    {
        var older = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var newer = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var filters = NostrService.BuildGroupMessageFilters(new[]
        {
            ("aaa", (DateTimeOffset?)older),
            ("bbb", (DateTimeOffset?)newer),
        });

        Assert.Equal(older.ToUnixTimeSeconds(), FilterFor(filters, "aaa")["since"]);
        Assert.Equal(newer.ToUnixTimeSeconds(), FilterFor(filters, "bbb")["since"]);
    }

    [Fact]
    public void GroupMessageFilters_DoNotBoundAGroupThatAskedForNoHorizon()
    {
        // A null Since means "I have no history, send everything". Inheriting another
        // group's window silently turns that into a bounded request, which is the
        // form of this bug that loses the most.
        var filters = NostrService.BuildGroupMessageFilters(new[]
        {
            ("fresh", (DateTimeOffset?)null),
            ("busy", (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
        });

        Assert.False(FilterFor(filters, "fresh").ContainsKey("since"));
    }

    [Fact]
    public void GroupMessageFilters_ShareOneFilterWhenTheHorizonMatches()
    {
        // Correctness must not cost a subscription per group: equal horizons coalesce.
        var same = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

        var filters = NostrService.BuildGroupMessageFilters(new[]
        {
            ("one", (DateTimeOffset?)same),
            ("two", (DateTimeOffset?)same),
        });

        var only = Assert.Single(filters);
        Assert.Equal(new[] { "one", "two" }, (string[])only["#h"]);
        Assert.Equal(same.ToUnixTimeSeconds(), only["since"]);
    }

    [Fact]
    public void GroupMessageFilters_AlwaysRequestKind445()
    {
        var filters = NostrService.BuildGroupMessageFilters(new[]
        {
            ("x", (DateTimeOffset?)null),
            ("y", (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
        });

        Assert.All(filters, f => Assert.Equal(new[] { 445 }, (int[])f["kinds"]));
    }

    [Fact]
    public void GroupMessageFilters_TakeTheWidestHorizonForARepeatedGroup()
    {
        // Asking for too much history is recoverable; silently asking for too little is
        // the bug this whole change is about.
        var older = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var newer = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var filters = NostrService.BuildGroupMessageFilters(new[]
        {
            ("dup", (DateTimeOffset?)newer),
            ("dup", (DateTimeOffset?)older),
        });

        var only = Assert.Single(filters);
        Assert.Equal(new[] { "dup" }, (string[])only["#h"]);
        Assert.Equal(older.ToUnixTimeSeconds(), only["since"]);
    }

    [Fact]
    public void GroupMessageFilters_LetNullBeatAnyHorizonForARepeatedGroup()
    {
        var filters = NostrService.BuildGroupMessageFilters(new[]
        {
            ("dup", (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
            ("dup", (DateTimeOffset?)null),
        });

        Assert.False(Assert.Single(filters).ContainsKey("since"));
    }
}
