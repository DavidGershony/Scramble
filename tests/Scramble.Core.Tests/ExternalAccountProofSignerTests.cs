using System.Text.Json;
using Scramble.Core.Services;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Signing an account-identity proof through a remote signer.
/// </summary>
/// <remarks>
/// <b>What these cannot prove.</b> The signer here is a double, so it agrees
/// with whatever this code assumes about NIP-46. What is in question at the
/// cutover is what a real signer app does with an ungranted kind and with a
/// <c>created_at</c> it did not choose — and only Amber can answer that. These
/// pin our side: that we send the template's own timestamp, and that a signer
/// which returns something else is refused rather than trusted.
/// </remarks>
public class ExternalAccountProofSignerTests
{
    private const string AccountHex =
        "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private static NostrEventTemplate Template(long createdAt = 1_700_000_000) =>
        new(AccountHex, createdAt, 450, [["h", "abc"]], "proof-content");

    /// <summary>A signer that echoes the template back, with edits.</summary>
    private sealed class FakeSigner : IExternalSigner
    {
        public Func<UnsignedNostrEvent, string>? Respond { get; set; }
        public UnsignedNostrEvent? Received { get; private set; }

        public bool IsConnected { get; set; } = true;
        public string? PublicKeyHex { get; set; } = AccountHex;

        public Task<string> SignEventAsync(UnsignedNostrEvent unsignedEvent)
        {
            Received = unsignedEvent;
            return Task.FromResult(Respond!(unsignedEvent));
        }

        public string? Npub => null;
        public IReadOnlyList<string> RelayUrls => [];
        public string? RelayUrl => null;
        public string? RemotePubKey => null;
        public string? Secret => null;
        public string? LocalPrivateKeyHex => null;
        public string? LocalPublicKeyHex => null;
        public int ProofOfWorkDifficulty { get; set; }
        public IObservable<ExternalSignerStatus> Status => throw new NotSupportedException();

        public Task<string> GetPublicKeyAsync() => Task.FromResult(PublicKeyHex!);
        public Task<string?> ResolveSigningPubKeyAsync() => Task.FromResult(PublicKeyHex);
        public Task<bool> ConnectAsync(string uri) => throw new NotSupportedException();
        public Task<bool> ConnectWithStringAsync(string s) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task ReconnectAsync() => throw new NotSupportedException();
        public string GenerateConnectionUri(IEnumerable<string> relayUrls) =>
            throw new NotSupportedException();
        public Task<string> GenerateAndListenForConnectionAsync(IEnumerable<string> relayUrls) =>
            throw new NotSupportedException();
        public Task<bool> RestoreSessionAsync(IEnumerable<string> relayUrls, string remotePubKey,
            string localPrivateKeyHex, string localPublicKeyHex, string? secret = null) =>
            throw new NotSupportedException();
        public Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey) =>
            throw new NotSupportedException();
        public Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey) =>
            throw new NotSupportedException();
    }

    private static string Echo(UnsignedNostrEvent e, string sig, long? createdAt = null,
        int? kind = null, string? pubkey = null, string? content = null) =>
        JsonSerializer.Serialize(new
        {
            id = "aa",
            pubkey = pubkey ?? AccountHex,
            created_at = createdAt ?? new DateTimeOffset(e.CreatedAt).ToUnixTimeSeconds(),
            kind = kind ?? e.Kind,
            tags = e.Tags,
            content = content ?? e.Content,
            sig,
        });

    private static string Sig(byte fill) => new string(
        Convert.ToHexString([.. Enumerable.Repeat(fill, 64)]).ToLowerInvariant());

    [Fact]
    public async Task TheTemplatesOwnTimestampIsWhatGetsSigned()
    {
        // The whole reason this class exists. INostrEventSigner has no
        // created_at parameter, so ExternalNostrEventSigner stamps UtcNow and
        // the signature verifies over a different id than the proof commits to.
        var signer = new FakeSigner { Respond = e => Echo(e, Sig(0xAB)) };

        await new ExternalAccountProofSigner(signer).SignAsync(Template(1_700_000_000));

        Assert.NotNull(signer.Received);
        Assert.Equal(
            1_700_000_000,
            new DateTimeOffset(signer.Received!.CreatedAt).ToUnixTimeSeconds());

        Assert.Equal(450, signer.Received.Kind);
        Assert.Equal("proof-content", signer.Received.Content);
    }

    [Fact]
    public async Task TheSignatureComesBackAsSixtyFourBytes()
    {
        var signer = new FakeSigner { Respond = e => Echo(e, Sig(0xAB)) };

        byte[] signature =
            await new ExternalAccountProofSigner(signer).SignAsync(Template());

        Assert.Equal(64, signature.Length);
        Assert.All(signature, b => Assert.Equal(0xAB, b));
    }

    [Theory]
    [InlineData("created_at")]
    [InlineData("kind")]
    [InlineData("pubkey")]
    [InlineData("content")]
    public async Task ASignerThatRewritesTheTemplateIsRefused(string field)
    {
        // The failure mode that could not be ruled out without a real signer.
        // A proof commits to an exact template, so a signature over anything
        // else is unusable -- and the refusal names the field, because
        // "signature did not verify" sends a reader to our own codec instead.
        var signer = new FakeSigner
        {
            Respond = e => field switch
            {
                "created_at" => Echo(e, Sig(0xAB), createdAt: 1_700_000_001),
                "kind" => Echo(e, Sig(0xAB), kind: 1),
                "pubkey" => Echo(e, Sig(0xAB), pubkey: new string('b', 64)),
                _ => Echo(e, Sig(0xAB), content: "something else"),
            },
        };

        InvalidOperationException ex =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ExternalAccountProofSigner(signer).SignAsync(Template()));

        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public async Task ASignerThatIsNotConnectedIsRefusedBeforeAnythingIsSent()
    {
        var signer = new FakeSigner
        {
            IsConnected = false,
            Respond = _ => throw new Xunit.Sdk.XunitException(
                "Nothing should have been sent to a disconnected signer."),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ExternalAccountProofSigner(signer).SignAsync(Template()));
    }

    [Fact]
    public async Task AShortSignatureIsRefusedRatherThanPassedOn()
    {
        var signer = new FakeSigner { Respond = e => Echo(e, "abcd") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ExternalAccountProofSigner(signer).SignAsync(Template()));
    }

    [Fact]
    public void TheAccountKeyIsReadWhenAskedForRatherThanAtConstruction()
    {
        // The signer is wired before it finishes connecting. A key captured at
        // construction would be null for the life of the object.
        var signer = new FakeSigner { PublicKeyHex = null };
        var proofSigner = new ExternalAccountProofSigner(signer);

        Assert.Throws<InvalidOperationException>(() => proofSigner.AccountPublicKey);

        signer.PublicKeyHex = AccountHex;

        Assert.Equal(32, proofSigner.AccountPublicKey.Length);
    }
}
