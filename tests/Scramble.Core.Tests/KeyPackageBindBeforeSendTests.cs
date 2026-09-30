using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// A published KeyPackage must be resolvable by its event id before anybody can
/// fetch it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> A Welcome names the KeyPackage it consumed by its
/// kind-30443 event id, and the joining device resolves that id to the private
/// material it kept. Publishing used to happen first and the binding afterwards,
/// which left a window in which the relay would serve a KeyPackage this device
/// could not resolve — the Welcome was refused, and
/// <c>AcceptInviteAsync</c> dismissed the invite permanently while telling the
/// peer the private key was no longer available, which is not what happened.
/// </para>
/// <para>
/// <b>Why the window was not theoretical.</b> It is a relay round-trip plus two
/// local writes, and <c>HeadlessRealRelayTests</c> was hitting it on most runs
/// through a variant of the same mistake. On a slow device or a first-run
/// database it is wider still.
/// </para>
/// <para>
/// <b>What is pinned here.</b> Not that the binding happens — an assertion that
/// it happened at all would have passed against the broken order too. What is
/// pinned is that it happens <i>before</i> the event can reach a relay, and that
/// a binding failure publishes nothing.
/// </para>
/// </remarks>
public class KeyPackageBindBeforeSendTests
{
    private static readonly string PrivKey = "1f".PadRight(64, '0');

    /// <summary>
    /// The sharp one: the callback runs before transmission, not after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Proven by ordering two failures against each other rather than by
    /// observing a relay. A <see cref="NostrService"/> with no connected relays
    /// always fails to transmit, with a message that says so — so if the
    /// callback's own exception is what comes out, the callback must have run
    /// first. Nothing here needs a relay, a container or a delay.
    /// </para>
    /// <para>
    /// The inverse is what makes it load-bearing: move the callback after the
    /// send and this test sees the no-relays failure instead.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheBindingRunsBeforeAnythingIsSentToARelay()
    {
        var nostr = new NostrService();
        var bindRan = false;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => nostr.PublishKeyPackageAsync(
                new byte[] { 0x01, 0x02, 0x03 },
                PrivKey,
                mdkTags: null,
                bindBeforeSend: _ =>
                {
                    bindRan = true;
                    throw new InvalidOperationException("binding refused on purpose");
                }));

        Assert.True(bindRan, "the binding callback was never invoked");
        Assert.Equal("binding refused on purpose", thrown.Message);

        // The transmission failure this service would otherwise produce. Seeing
        // it here would mean the send was attempted first.
        Assert.DoesNotContain("No connected relays", thrown.Message);
    }

    /// <summary>
    /// The callback is handed the id the event is actually published under.
    /// </summary>
    /// <remarks>
    /// An id invented for the callback and a different one on the wire would
    /// satisfy the ordering test above and still leave the KeyPackage
    /// unresolvable. Checked without a relay by letting the send fail after the
    /// binding: <see cref="PublishUnconfirmedException"/> and the no-relay
    /// refusal both carry nothing, so the assertion is that the id is a
    /// well-formed event id and is stable — the same value the signature commits
    /// to, since nothing after signing can change it.
    /// </remarks>
    [Fact]
    public async Task TheBindingIsGivenAWellFormedEventId()
    {
        var nostr = new NostrService();
        string? seen = null;

        await Assert.ThrowsAnyAsync<Exception>(
            () => nostr.PublishKeyPackageAsync(
                new byte[] { 0x04, 0x05 },
                PrivKey,
                mdkTags: null,
                bindBeforeSend: id =>
                {
                    seen = id;
                    return Task.CompletedTask;
                }));

        Assert.NotNull(seen);
        Assert.Equal(64, seen!.Length);
        Assert.True(
            seen.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
            $"not a lowercase hex event id: {seen}");
    }

    /// <summary>
    /// Omitting the callback is still allowed, for a publish with nothing to
    /// bind.
    /// </summary>
    /// <remarks>
    /// <c>MessageService.PublishDummyKeyPackagesAsync</c> publishes random bytes
    /// to mask how many devices an account has. Those are not KeyPackages and
    /// are never resolvable by design, so requiring a binding would either
    /// break that feature or invite a meaningless one. Pinned so the parameter
    /// is not later made mandatory on the assumption that every publish has
    /// something to bind.
    /// </remarks>
    [Fact]
    public async Task APublishWithNothingToBindIsStillAllowed()
    {
        var nostr = new NostrService();

        // Reaches transmission and fails there, which is the point: no binding
        // was required to get that far.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => nostr.PublishKeyPackageAsync(new byte[] { 0x06 }, PrivKey));

        Assert.Contains("No connected relays", thrown.Message);
    }

    /// <summary>
    /// <c>AutoPublishKeyPackageIfNeededAsync</c> actually uses the seam.
    /// </summary>
    /// <remarks>
    /// The service-level half. The mock stands in for the relay and asserts the
    /// state of the world at the two moments that matter: nothing is stored
    /// before the callback runs, and both the local record and the engine
    /// binding are in place by the time it returns. Asserting only the end state
    /// would have passed against the broken order.
    /// </remarks>
    [Fact]
    public async Task AutoPublishStoresAndBindsBeforeItLetsTheEventGo()
    {
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "ab".PadRight(64, '0'),
            PrivateKeyHex = PrivKey,
            IsCurrentUser = true,
        };

        var keyPackage = new KeyPackage
        {
            Id = Guid.NewGuid().ToString(),
            OwnerPublicKey = user.PublicKeyHex,
            Data = new byte[] { 0x07, 0x08, 0x09 },
            NostrTags = new List<List<string>> { new() { "d", "slot-0" } },
        };

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        storage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);
        storage.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        storage.Setup(s => s.GetUnusedKeyPackagesAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<KeyPackage>());

        var saved = new List<KeyPackage>();
        storage.Setup(s => s.SaveKeyPackageAsync(It.IsAny<KeyPackage>()))
            .Callback<KeyPackage>(saved.Add)
            .Returns(Task.CompletedTask);

        var mls = new Mock<IMlsService>();
        mls.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mls.Setup(m => m.GenerateKeyPackageAsync()).ReturnsAsync(keyPackage);

        var bound = new List<string>();
        mls.Setup(m => m.MarkKeyPackagePublishedAsync(It.IsAny<KeyPackage>(), It.IsAny<string>()))
            .Callback<KeyPackage, string>((_, id) => bound.Add(id))
            .Returns(Task.CompletedTask);

        string eventId = "cafe".PadRight(64, '0');

        var nostr = new Mock<INostrService>();
        nostr.Setup(n => n.Events)
            .Returns(new System.Reactive.Subjects.Subject<NostrEventReceived>());
        nostr.Setup(n => n.ConnectedRelayUrls).Returns(new[] { "wss://relay.example.com" });
        nostr.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(),
                It.IsAny<string>(),
                It.IsAny<List<List<string>>?>(),
                It.IsAny<Func<string, Task>?>()))
            .Returns(async (byte[] _, string _, List<List<string>>? _, Func<string, Task>? bind) =>
            {
                // The relay has been given nothing at this point, so neither
                // must have been done yet.
                Assert.Empty(saved);
                Assert.Empty(bound);

                if (bind is not null)
                    await bind(eventId);

                // And by the time the event may go out, both are done.
                Assert.Single(saved);
                Assert.Equal([eventId], bound);

                return eventId;
            });

        var messages = new MessageService(storage.Object, nostr.Object, mls.Object);
        await messages.InitializeAsync();

        await messages.AutoPublishKeyPackageIfNeededAsync();

        // Asserted again out here, because AutoPublish swallows its own failures
        // and an assertion tripped inside the callback would otherwise be
        // reported only as a warning nobody reads.
        Assert.Single(saved);
        Assert.Equal(eventId, saved[0].NostrEventId);
        Assert.Equal([eventId], bound);
    }
}
