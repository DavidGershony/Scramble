using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The signer seam, including what happens when an external signer misbehaves.
/// </summary>
[Trait("Category", "MarmotEngine")]
public class AccountIdentityProofSignerTests
{
    private const ushort CipherSuite = 0x0001;
    private const ushort SignatureScheme = 0x0807;

    private static readonly byte[] AccountKey = Convert.FromHexString(
        "f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9");

    private static readonly byte[] LeafKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private const ulong KnownCreatedAt = 1700000000;

    private static readonly byte[] KnownGoodSignature = Convert.FromHexString(
        "c5315d3c85b9d4907cb03395a2a97b3ba2eab393f8e45b13a5d5233acedac60a" +
        "51d2a295e1b1b5ee372d18a49bdb8041a7dba9dedce722c7c6f712f78bbdfb5d");

    /// <summary>A signer that returns whatever it is told to.</summary>
    private sealed class StubSigner : IAccountIdentityProofSigner
    {
        private readonly Func<NostrEventTemplate, byte[]> _sign;

        public StubSigner(Func<NostrEventTemplate, byte[]> sign) => _sign = sign;

        public ReadOnlyMemory<byte> AccountPublicKey => AccountKey;

        public NostrEventTemplate? LastRequested { get; private set; }

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default)
        {
            LastRequested = template;
            return Task.FromResult(_sign(template));
        }
    }

    [Fact]
    public async Task CreatingAProofSignsTheTemplateAndReturnsIt()
    {
        var signer = new StubSigner(_ => KnownGoodSignature);

        var proof = await AccountIdentityProofSigning.CreateAsync(
            signer, CipherSuite, SignatureScheme, LeafKey, KnownCreatedAt);

        Assert.Equal(AccountKey, proof.SignerPublicKey);
        Assert.Equal(KnownCreatedAt, proof.CreatedAt);
        Assert.Equal(KnownGoodSignature, proof.Signature);
        Assert.Equal(AccountIdentityProofResult.Valid,
            proof.Validate(AccountKey, CipherSuite, SignatureScheme, LeafKey));
    }

    [Fact]
    public async Task TheSignerIsShownTheWholeTemplateNotJustAHash()
    {
        var signer = new StubSigner(_ => KnownGoodSignature);

        await AccountIdentityProofSigning.CreateAsync(
            signer, CipherSuite, SignatureScheme, LeafKey, KnownCreatedAt);

        // A signer UI has to be able to show the user what they are approving.
        Assert.NotNull(signer.LastRequested);
        Assert.Equal(450, signer.LastRequested!.Kind);
        Assert.Equal("Authorize this MLS leaf key for my Marmot account", signer.LastRequested.Content);
    }

    [Fact]
    public async Task ASignatureOverADifferentTemplateIsRejected()
    {
        // A remote signer returning a stale response from an earlier request is
        // the realistic failure, and it must be caught here rather than by
        // every peer rejecting our KeyPackage later.
        var signer = new StubSigner(_ => KnownGoodSignature);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountIdentityProofSigning.CreateAsync(
                signer, CipherSuite, SignatureScheme, LeafKey, KnownCreatedAt + 1));

        Assert.Contains("does not verify", ex.Message);
    }

    [Fact]
    public async Task AGarbageSignatureIsRejected()
    {
        var signer = new StubSigner(_ => new byte[64]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountIdentityProofSigning.CreateAsync(
                signer, CipherSuite, SignatureScheme, LeafKey, KnownCreatedAt));
    }

    [Fact]
    public async Task AWrongLengthSignatureIsRejected()
    {
        var signer = new StubSigner(_ => new byte[10]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountIdentityProofSigning.CreateAsync(
                signer, CipherSuite, SignatureScheme, LeafKey, KnownCreatedAt));
    }

    [Fact]
    public async Task CancellationIsPassedToTheSigner()
    {
        // Signing can mean a round trip to Amber and a human tapping approve,
        // so it must be abandonable.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var signer = new StubSigner(_ =>
        {
            cts.Token.ThrowIfCancellationRequested();
            return KnownGoodSignature;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AccountIdentityProofSigning.CreateAsync(
                signer, CipherSuite, SignatureScheme, LeafKey, KnownCreatedAt, cts.Token));
    }

    [Fact]
    public void VerifySignedTemplateAcceptsAGenuineSignature()
    {
        var template = AccountIdentityProof.BuildSigningEvent(
            AccountKey, KnownCreatedAt, CipherSuite, SignatureScheme, LeafKey);

        Assert.True(AccountIdentityProofSigning.VerifySignedTemplate(
            AccountKey, template, KnownGoodSignature));
    }

    [Fact]
    public void VerifySignedTemplateRejectsAnotherAccountsSignature()
    {
        var template = AccountIdentityProof.BuildSigningEvent(
            AccountKey, KnownCreatedAt, CipherSuite, SignatureScheme, LeafKey);
        var otherAccount = Convert.FromHexString(
            "e8f9e0e0f6e6b0f3f2f1f0e9e8e7e6e5e4e3e2e1e0dfdedddcdbdad9d8d7d6d5");

        Assert.False(AccountIdentityProofSigning.VerifySignedTemplate(
            otherAccount, template, KnownGoodSignature));
    }

    [Fact]
    public async Task CreatedAtDefaultsToNow()
    {
        ulong before = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        NostrEventTemplate? seen = null;
        var signer = new StubSigner(t =>
        {
            seen = t;
            return KnownGoodSignature;
        });

        // The signature will not verify for a fresh timestamp, which is fine:
        // what matters is the timestamp actually handed to the signer.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountIdentityProofSigning.CreateAsync(
                signer, CipherSuite, SignatureScheme, LeafKey));

        Assert.NotNull(seen);
        Assert.InRange(
            (ulong)seen!.CreatedAt, before, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
    }
}
