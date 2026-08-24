using Scramble.Marmot.Storage;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// KeyPackage records and the private material a Welcome consumes.
/// </summary>
/// <remarks>
/// The lifecycle here is the join path's foundation and a key-hygiene
/// obligation at once, so the tests split accordingly: the first half proves a
/// Welcome can still find its material, the second that the material actually
/// goes away and cannot come back.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class KeyPackageStorageTests : IDisposable
{
    private readonly StorageFixture _fixture = new();

    private IKeyPackageStorage Storage => _fixture.Provider;

    private const string SlotId = "1f2e3d4c5b6a79880011223344556677889900aabbccddeeff00112233445566";

    private static readonly byte[] PublicBytes = "the published MLSMessage"u8.ToArray();
    private static readonly byte[] PrivateBytes = "the init key bundle"u8.ToArray();

    private static KeyPackageRecord Record(
        string refHex = "aa",
        bool lastResort = false,
        string? slotId = null,
        byte[]? privateMaterial = null) =>
        new(
            refHex.PadRight(64, 'b'),
            slotId ?? SlotId,
            PublicBytes,
            privateMaterial ?? PrivateBytes,
            lastResort,
            NotBefore: 1700000000,
            NotAfter: 1700000000 + 7_261_200,
            KeyPackageRecordState.Created,
            DateTimeOffset.UtcNow);

    public void Dispose() => _fixture.Dispose();

    // -- Round trip --

    [Fact]
    public async Task APersistedKeyPackageComesBackWithItsPrivateMaterial()
    {
        // The whole point. The previous implementation discarded init_key and
        // hpke private material, so no join could ever complete.
        var record = Record();
        await Storage.PutKeyPackageAsync(record);

        var loaded = await Storage.GetKeyPackageAsync(record.KeyPackageRefHex);

        Assert.NotNull(loaded);
        Assert.Equal(PrivateBytes, loaded.PrivateMaterial);
        Assert.Equal(PublicBytes, loaded.PublicKeyPackage);
        Assert.Equal(SlotId, loaded.SlotId);
        Assert.Equal(1700000000, loaded.NotBefore);
        Assert.Equal(KeyPackageRecordState.Created, loaded.State);
        Assert.True(loaded.CanConsume);
    }

    [Fact]
    public async Task AnUnknownKeyPackageIsNull()
    {
        Assert.Null(await Storage.GetKeyPackageAsync("ff".PadRight(64, 'f')));
    }

    [Fact]
    public async Task AWelcomeFindsItsKeyPackageByEventId()
    {
        // The join path's real entry point: a Welcome names the kind-30443
        // event, and the private material has to be reachable from that alone.
        var record = Record();
        await Storage.PutKeyPackageAsync(record);
        await Storage.MarkPublishedAsync(record.KeyPackageRefHex, "cc".PadRight(64, 'd'));

        var found = await Storage.GetKeyPackageByEventAsync("cc".PadRight(64, 'd'));

        Assert.NotNull(found);
        Assert.Equal(record.KeyPackageRefHex, found.KeyPackageRefHex);
        Assert.Equal(PrivateBytes, found.PrivateMaterial);
    }

    [Fact]
    public async Task ListingFiltersBySlotAndState()
    {
        await Storage.PutKeyPackageAsync(Record("a1"));
        await Storage.PutKeyPackageAsync(Record("a2"));
        await Storage.PutKeyPackageAsync(Record("a3", slotId: new string('c', 64)));
        await Storage.MarkPublishedAsync("a2".PadRight(64, 'b'), "ee".PadRight(64, 'f'));

        Assert.Equal(3, (await Storage.ListKeyPackagesAsync()).Count);
        Assert.Equal(2, (await Storage.ListKeyPackagesAsync(slotId: SlotId)).Count);
        Assert.Single(await Storage.ListKeyPackagesAsync(state: KeyPackageRecordState.Published));
        Assert.Single(await Storage.ListKeyPackagesAsync(SlotId, KeyPackageRecordState.Created));
    }

    [Fact]
    public async Task SeveralKeyPackagesShareOneSlotOverTime()
    {
        // Publishing a replacement supersedes the previous occupant on the
        // relay; locally both records stay, because a Welcome encrypted to the
        // older one may still arrive.
        await Storage.PutKeyPackageAsync(Record("a1"));
        await Storage.PutKeyPackageAsync(Record("a2"));

        Assert.Equal(2, (await Storage.ListKeyPackagesAsync(slotId: SlotId)).Count);
    }

    // -- Insert-only --

    [Fact]
    public async Task InsertingTheSameKeyPackageTwiceIsRejected()
    {
        // Not a replace. A caller holding a record from before an erase must
        // not be able to write the private material back.
        var record = Record();
        await Storage.PutKeyPackageAsync(record);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Storage.PutKeyPackageAsync(record));
    }

    [Fact]
    public async Task AnErasedKeyPackageCannotBeReinsertedWithItsMaterial()
    {
        var record = Record();
        await Storage.PutKeyPackageAsync(record);
        await Storage.ErasePrivateMaterialAsync(record.KeyPackageRefHex);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Storage.PutKeyPackageAsync(record));

        var loaded = await Storage.GetKeyPackageAsync(record.KeyPackageRefHex);
        Assert.Null(loaded!.PrivateMaterial);
    }

    [Fact]
    public async Task TwoKeyPackagesCannotClaimOneEventId()
    {
        // Which private material to use would otherwise be a coin flip.
        await Storage.PutKeyPackageAsync(Record("a1"));
        await Storage.PutKeyPackageAsync(Record("a2"));
        await Storage.MarkPublishedAsync("a1".PadRight(64, 'b'), "cc".PadRight(64, 'd'));

        await Assert.ThrowsAnyAsync<Exception>(
            () => Storage.MarkPublishedAsync("a2".PadRight(64, 'b'), "cc".PadRight(64, 'd')));
    }

    // -- Transitions --

    [Fact]
    public async Task PublishingBindsTheEventIdAndAdvancesTheState()
    {
        var record = Record();
        await Storage.PutKeyPackageAsync(record);

        Assert.True(await Storage.MarkPublishedAsync(record.KeyPackageRefHex, "cc".PadRight(64, 'd')));

        var loaded = await Storage.GetKeyPackageAsync(record.KeyPackageRefHex);
        Assert.Equal(KeyPackageRecordState.Published, loaded!.State);
        Assert.Equal("cc".PadRight(64, 'd'), loaded.EventIdHex);
    }

    [Fact]
    public async Task ALatePublishConfirmationDoesNotReopenAConsumedKeyPackage()
    {
        var record = Record();
        await Storage.PutKeyPackageAsync(record);
        await Storage.MarkPublishedAsync(record.KeyPackageRefHex, "cc".PadRight(64, 'd'));
        await Storage.MarkConsumedAsync(record.KeyPackageRefHex);

        Assert.False(await Storage.MarkPublishedAsync(record.KeyPackageRefHex, "ee".PadRight(64, 'f')));

        var loaded = await Storage.GetKeyPackageAsync(record.KeyPackageRefHex);
        Assert.Equal(KeyPackageRecordState.Consumed, loaded!.State);
    }

    [Fact]
    public async Task ConsumingAnUnknownKeyPackageReportsFailureRatherThanSucceedingSilently()
    {
        Assert.False(await Storage.MarkConsumedAsync("ff".PadRight(64, 'f')));
    }

    [Fact]
    public async Task ARetiredKeyPackageCannotBeMarkedConsumedAgain()
    {
        // Its material is gone, so reading as consumable again would send the
        // join path after a bundle that no longer exists.
        var record = Record();
        await Storage.PutKeyPackageAsync(record);
        await Storage.ErasePrivateMaterialAsync(record.KeyPackageRefHex);

        Assert.False(await Storage.MarkConsumedAsync(record.KeyPackageRefHex));
    }

    // -- Erasure --

    [Fact]
    public async Task ErasingRemovesThePrivateMaterialButKeepsTheRecord()
    {
        // The record outlives the material on purpose: a Welcome naming a spent
        // KeyPackage must be told it is spent, which a missing row cannot say
        // as distinct from 'never mine'.
        var record = Record();
        await Storage.PutKeyPackageAsync(record);

        Assert.True(await Storage.ErasePrivateMaterialAsync(record.KeyPackageRefHex));

        var loaded = await Storage.GetKeyPackageAsync(record.KeyPackageRefHex);
        Assert.NotNull(loaded);
        Assert.Null(loaded.PrivateMaterial);
        Assert.False(loaded.CanConsume);
        Assert.Equal(KeyPackageRecordState.Retired, loaded.State);
        Assert.Equal(PublicBytes, loaded.PublicKeyPackage);
    }

    [Fact]
    public async Task ErasingIsIdempotent()
    {
        // The deadlines that force erasure can fire more than once — a
        // replacement publish and the not_after sweep can both name the same
        // KeyPackage — and neither may fail because the other got there first.
        var record = Record(lastResort: true);
        await Storage.PutKeyPackageAsync(record);

        Assert.True(await Storage.ErasePrivateMaterialAsync(record.KeyPackageRefHex));
        Assert.True(await Storage.ErasePrivateMaterialAsync(record.KeyPackageRefHex));
    }

    [Fact]
    public async Task ErasingWorksFromEveryState()
    {
        // Erasure must never be blocked by which state a record happens to be
        // in: the obligation is a deadline, not a step in a sequence.
        foreach (var (refHex, advance) in new (string, Func<string, Task>)[]
        {
            ("b1", _ => Task.CompletedTask),
            ("b2", r => Storage.MarkPublishedAsync(r, r).ContinueWith(_ => { })),
            ("b3", r => Storage.MarkConsumedAsync(r).ContinueWith(_ => { })),
        })
        {
            string key = refHex.PadRight(64, 'b');
            await Storage.PutKeyPackageAsync(Record(refHex));
            await advance(key);

            Assert.True(await Storage.ErasePrivateMaterialAsync(key));
            Assert.Null((await Storage.GetKeyPackageAsync(key))!.PrivateMaterial);
        }
    }

    [Fact]
    public async Task ErasingAnUnknownKeyPackageReportsFailure()
    {
        Assert.False(await Storage.ErasePrivateMaterialAsync("ff".PadRight(64, 'f')));
    }

    // -- Orphan pruning --

    [Fact]
    public async Task AnUnpublishedKeyPackageCanBeDeletedOutright()
    {
        // The orphan case: built, persisted, never published. Nothing on any
        // relay refers to it, so there is no Welcome to answer. Without this,
        // retries against a failing relay accumulate private key material
        // indefinitely.
        var record = Record();
        await Storage.PutKeyPackageAsync(record);

        Assert.True(await Storage.DeleteKeyPackageAsync(record.KeyPackageRefHex));
        Assert.Null(await Storage.GetKeyPackageAsync(record.KeyPackageRefHex));
    }

    [Fact]
    public async Task DeletingAnUnknownKeyPackageReportsFailure()
    {
        Assert.False(await Storage.DeleteKeyPackageAsync("ff".PadRight(64, 'f')));
    }

    // -- Durability --

    [Fact]
    public async Task ARolledBackTransactionLeavesNoPrivateMaterialBehind()
    {
        var record = Record();

        await using (var tx = await _fixture.Provider.BeginTransactionAsync())
        {
            await Storage.PutKeyPackageAsync(record);
            await tx.RollbackAsync();
        }

        Assert.Null(await Storage.GetKeyPackageAsync(record.KeyPackageRefHex));
    }

    [Fact]
    public async Task AnErasureInsideACommittedTransactionSticks()
    {
        var record = Record();
        await Storage.PutKeyPackageAsync(record);

        await using (var tx = await _fixture.Provider.BeginTransactionAsync())
        {
            await Storage.ErasePrivateMaterialAsync(record.KeyPackageRefHex);
            await tx.CommitAsync();
        }

        Assert.Null((await Storage.GetKeyPackageAsync(record.KeyPackageRefHex))!.PrivateMaterial);
    }
}
