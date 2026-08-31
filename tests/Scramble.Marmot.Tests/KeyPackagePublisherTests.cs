using DotnetMls.Crypto;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Publishing a KeyPackage, and what happens to its private material when the
/// publish does not go cleanly.
/// </summary>
/// <remarks>
/// The ordering under test is persist-then-publish, and the interesting cases
/// are all failures. Erasing the private key for a KeyPackage that did reach a
/// relay is unrecoverable — people can encrypt Welcomes to it that we can never
/// open — so "rejected" and "we do not know" have to behave differently, and
/// these tests are what hold that apart.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class KeyPackagePublisherTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly LocalSigner _signer = new();

    private IKeyPackageStorage Storage => _fixture.Provider;

    private const ulong Now = 1_760_000_000;

    public void Dispose() => _fixture.Dispose();

    /// <summary>A signer holding a real Nostr key, so signatures verify.</summary>
    private sealed class LocalSigner : IAccountIdentityProofSigner
    {
        private readonly byte[] _secret;

        public LocalSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            _secret = secret;
            AccountPublicKey = publicKey;
        }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        /// <summary>
        /// When set, returns garbage for templates of this kind.
        /// </summary>
        /// <remarks>
        /// Per-kind rather than global because two different templates are
        /// signed here: the kind-450 proof first, then the kind-30443 event. A
        /// signer that fails both never reaches the second guard.
        /// </remarks>
        public int? FailOnKind { get; set; }

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(
                template.Kind == FailOnKind ? new byte[64] : Bip340.Sign(_secret, template.ComputeId()));
    }

    /// <summary>A relay that reports whatever it is told to, and keeps the envelope.</summary>
    private sealed class StubRelay(KeyPackagePublishOutcome outcome) : IKeyPackageRelay
    {
        public string? LastEnvelope { get; private set; }

        public int Attempts { get; private set; }

        public Exception? Throw { get; set; }

        public Task<KeyPackagePublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default)
        {
            Attempts++;
            LastEnvelope = envelope;

            if (Throw is { } ex)
                throw ex;

            return Task.FromResult(outcome);
        }
    }

    private KeyPackagePublisher PublisherFor(IKeyPackageRelay relay) =>
        new(_cs, _signer, Storage, relay);

    // ---- The happy path ----

    [Fact]
    public async Task AnAcceptedPublishLeavesARecordBoundToItsEventId()
    {
        var relay = new StubRelay(KeyPackagePublishOutcome.Accepted);

        var published = await PublisherFor(relay).PublishAsync(Now);

        var record = await Storage.GetKeyPackageAsync(published.Bundle.KeyPackageRefHex);
        Assert.NotNull(record);
        Assert.Equal(KeyPackageRecordState.Published, record!.State);
        Assert.Equal(published.EventIdHex, record.EventIdHex);
        Assert.True(record.CanConsume);

        // The join path finds material by the event id a Welcome names, not by
        // the ref. A record that cannot be reached that way is not stored.
        var byEvent = await Storage.GetKeyPackageByEventAsync(published.EventIdHex);
        Assert.Equal(record.KeyPackageRefHex, byEvent!.KeyPackageRefHex);
    }

    [Fact]
    public async Task ThePublishedEnvelopeParsesAsAConformantPublication()
    {
        var relay = new StubRelay(KeyPackagePublishOutcome.Accepted);

        var published = await PublisherFor(relay).PublishAsync(Now);

        // Round-tripped through the codec's own verifier: the id and signature
        // are checked before any field is read, so this proves the envelope is
        // one a relay and a peer would both accept.
        KeyPackagePublication publication = KeyPackageEvent.Parse(relay.LastEnvelope!);
        var validated = KeyPackagePublicationValidator.Validate(publication, _cs, Now);

        Assert.Equal(published.Bundle.KeyPackageRefHex, validated.Publication.KeyPackageRefHex);
        Assert.Equal(published.SlotId, publication.SlotId);
        Assert.Equal(published.EventIdHex, publication.EventIdHex);
    }

    // ---- The slot ----

    [Fact]
    public async Task ARepublishReusesTheSlotSoItSupersedesRatherThanAccumulates()
    {
        var relay = new StubRelay(KeyPackagePublishOutcome.Accepted);
        var publisher = PublisherFor(relay);

        var first = await publisher.PublishAsync(Now);
        var second = await publisher.PublishAsync(Now + 1);

        // Kind 30443 is addressable on (author, kind, d). A fresh slot each
        // time would leave every old KeyPackage discoverable forever, and each
        // one is an invitation we can only honour once.
        Assert.Equal(first.SlotId, second.SlotId);
        Assert.NotEqual(first.Bundle.KeyPackageRefHex, second.Bundle.KeyPackageRefHex);

        var inSlot = await Storage.ListKeyPackagesAsync(slotId: first.SlotId);
        Assert.Equal(2, inSlot.Count);
    }

    [Fact]
    public async Task AnExplicitSlotIsHonouredOverTheOneInUse()
    {
        var relay = new StubRelay(KeyPackagePublishOutcome.Accepted);
        var publisher = PublisherFor(relay);

        await publisher.PublishAsync(Now);
        string other = KeyPackageEvent.NewSlotId();

        var published = await publisher.PublishAsync(Now + 1, slotId: other);

        Assert.Equal(other, published.SlotId);
    }

    [Fact]
    public async Task TheFirstSlotIsMintedAndIsNotDerivedFromAnyKey()
    {
        string slot = await PublisherFor(new StubRelay(KeyPackagePublishOutcome.Accepted))
            .CurrentSlotIdAsync();

        Assert.Equal(64, slot.Length);
        Assert.DoesNotContain(
            Convert.ToHexString(_signer.AccountPublicKey.ToArray()).ToLowerInvariant(), slot);

        // Random, so two calls with nothing stored disagree. A slot id is
        // public: deriving it from the account or leaf key would leak or link
        // identity material.
        Assert.NotEqual(
            slot,
            await PublisherFor(new StubRelay(KeyPackagePublishOutcome.Accepted)).CurrentSlotIdAsync());
    }

    // ---- Failure, and what it does to the private material ----

    [Fact]
    public async Task ARejectedPublishDeletesTheOrphanedRecord()
    {
        var relay = new StubRelay(KeyPackagePublishOutcome.Rejected);

        var ex = await Assert.ThrowsAsync<KeyPackagePublishException>(
            () => PublisherFor(relay).PublishAsync(Now));

        Assert.Equal(KeyPackagePublishOutcome.Rejected, ex.Outcome);

        // Nothing anywhere refers to it, so retaining private key material
        // would be pure risk — and retries against a failing relay would
        // accumulate it indefinitely.
        Assert.Empty(await Storage.ListKeyPackagesAsync());
    }

    [Fact]
    public async Task AnIndeterminatePublishKeepsTheRecordAndItsMaterial()
    {
        var relay = new StubRelay(KeyPackagePublishOutcome.Indeterminate);

        var ex = await Assert.ThrowsAsync<KeyPackagePublishException>(
            () => PublisherFor(relay).PublishAsync(Now));

        Assert.Equal(KeyPackagePublishOutcome.Indeterminate, ex.Outcome);

        // The KeyPackage may be live. Deleting it here is the unrecoverable
        // mistake: someone could already be encrypting a Welcome to it.
        var record = Assert.Single(await Storage.ListKeyPackagesAsync());
        Assert.Equal(KeyPackageRecordState.Created, record.State);
        Assert.True(record.CanConsume);
        Assert.Null(record.EventIdHex);
    }

    [Fact]
    public async Task ARelayThatThrowsIsReadAsIndeterminateNotAsRejected()
    {
        var relay = new StubRelay(KeyPackagePublishOutcome.Accepted)
        {
            Throw = new HttpRequestException("the socket went away"),
        };

        var ex = await Assert.ThrowsAsync<KeyPackagePublishException>(
            () => PublisherFor(relay).PublishAsync(Now));

        // A transport that throws has told us nothing about what the relay saw,
        // and the safe reading of nothing is that it might have seen it.
        Assert.Equal(KeyPackagePublishOutcome.Indeterminate, ex.Outcome);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Single(await Storage.ListKeyPackagesAsync());
    }

    [Fact]
    public async Task ASignerThatReturnsSomethingElseIsCaughtBeforeAnythingIsSent()
    {
        // The proof signs fine and the event signature does not: a remote
        // signer answering the second request with a stale or wrong response.
        // The proof path has its own guard, so this is the only way to reach
        // the publisher's.
        var relay = new StubRelay(KeyPackagePublishOutcome.Accepted);
        _signer.FailOnKind = KeyPackageEvent.Kind;

        var ex = await Assert.ThrowsAsync<KeyPackagePublishException>(
            () => PublisherFor(relay).PublishAsync(Now));

        Assert.Equal(KeyPackagePublishOutcome.Rejected, ex.Outcome);

        // Nothing sent and nothing stored: publishing an event every relay
        // drops would burn the slot for no gain.
        Assert.Equal(0, relay.Attempts);
        Assert.Empty(await Storage.ListKeyPackagesAsync());
    }

    // ---- The ordering itself ----

    [Fact]
    public async Task TheMaterialIsDurableBeforeAnythingReachesTheRelay()
    {
        KeyPackageRecord? seenDuringPublish = null;

        var relay = new InspectingRelay(async () =>
            seenDuringPublish = (await Storage.ListKeyPackagesAsync()).SingleOrDefault());

        await PublisherFor(relay).PublishAsync(Now);

        // The inverse ordering — publish, then persist — loses the private key
        // for a KeyPackage other people can already fetch. That is the failure
        // the previous implementation shipped, and it is unrecoverable.
        Assert.NotNull(seenDuringPublish);
        Assert.Equal(KeyPackageRecordState.Created, seenDuringPublish!.State);
        Assert.True(seenDuringPublish.CanConsume);
    }

    private sealed class InspectingRelay(Func<Task> onPublish) : IKeyPackageRelay
    {
        public async Task<KeyPackagePublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default)
        {
            await onPublish();
            return KeyPackagePublishOutcome.Accepted;
        }
    }
}
