using DotnetMls.Crypto;
using Scramble.Marmot;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Engine.Session;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Marmot.Wire.Nostr;
using Xunit;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Receiving from a relay without being told what the bytes are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other test in this suite steps over the hard part.</b> They filter
/// the relay by our own <c>#h</c> tag, peel with our own exporter secret, and
/// receive into a group they already hold — three decisions a real client
/// cannot make, because it is handed an envelope and nothing else. That is not
/// a small gap: it is the layer at which a competing commit is sealed, and a
/// fork invisible there is a fork the convergence machinery never sees, however
/// green its own tests are.
/// </para>
/// <para>
/// So these tests hand <see cref="InboundFanIn"/> raw events off a live relay
/// and assert on what it works out for itself. The traffic is real — produced
/// by mdk's own client, which shares none of our code.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public class FanInInteropTests(MessageInteropFixture fixture) : IDisposable
{
    private readonly MessageInteropFixture _fixture = fixture;
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"fanin-interop-{Guid.NewGuid():N}.db");

    private SqliteMarmotStorageProvider? _storage;

    public void Dispose()
    {
        _storage?.Dispose();

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A stray temp file is not worth failing an interop run over.
        }
    }

    /// <summary>A fan-in over its own storage, holding the fixture's group.</summary>
    /// <remarks>
    /// The group is adopted rather than merely referenced: adoption is what
    /// registers its transport address in the routing index, and a group absent
    /// from that index receives nothing. Standing this up here rather than in
    /// the shared fixture keeps the other tests on the path they were written
    /// for.
    /// </remarks>
    private async Task<InboundFanIn> FanInAsync()
    {
        _storage = new SqliteMarmotStorageProvider($"Data Source={_dbPath}");

        var host = new MarmotSessionHost(
            _storage,
            new CipherSuite0x0001(),
            new UnusedCommitRelay(),
            new UnusedMessageRelay(),
            ConvergencePolicy.V1,
            () => DateTimeOffset.UtcNow);

        await host.AdoptAsync(
            _fixture.ScrambleGroup.ToRecord(DateTimeOffset.UtcNow),
            _fixture.ScrambleGroup.Group);

        return new InboundFanIn(host);
    }

    private sealed class UnusedCommitRelay : ICommitRelay
    {
        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default) =>
            throw new InvalidOperationException("These tests only receive.");
    }

    private sealed class UnusedMessageRelay : IMessageRelay
    {
        public Task<MessageSendOutcome> SendAsync(
            string envelope, CancellationToken ct = default) =>
            throw new InvalidOperationException("These tests only receive.");
    }

    [Fact]
    public async Task AnEnvelopeOffTheRelayFindsItsOwnGroup()
    {
        Assert.SkipUnless(_fixture.Ready, _fixture.SkipReason);

        InboundFanIn fanIn = await FanInAsync();
        string marker = $"fan-in {Guid.NewGuid():N}";

        await _fixture.PeerSaysAsync(marker);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        IngestResult? delivered = null;
        var refusals = new List<InputRejectionCategory>();

        while (clock.Elapsed < TimeSpan.FromSeconds(60) && delivered is null)
        {
            // Unfiltered by address. Whatever the relay holds, including other
            // groups' traffic and our own echoes.
            foreach (string envelope in await _fixture.FetchAllGroupEventsAsync())
            {
                InboundDelivery outcome = await fanIn.ReceiveAsync(envelope);

                switch (outcome)
                {
                    case InboundDelivery.Delivered d
                        when d.Result.Message?.Event.Content == marker:
                        delivered = d.Result;
                        break;

                    case InboundDelivery.Refused r
                        when r.Outcome is IngestOutcome.Ignored ignored:
                        refusals.Add(ignored.Category);
                        break;
                }

                if (delivered is not null)
                    break;
            }

            if (delivered is null)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.True(
            delivered is not null,
            $"The fan-in never routed the peer's message to its group.\n{_fixture.Log}");

        // Routed, decrypted, and attributed -- all from bytes that arrived with
        // no indication of which group they belonged to.
        Assert.Equal(marker, delivered!.Message!.Event.Content);

        Assert.Equal(
            _fixture.PeerPubkey,
            Convert.ToHexString(delivered.Message.SenderIdentity).ToLowerInvariant());
    }

    [Fact]
    public async Task TrafficThatIsNotOursIsRefusedRatherThanGuessedAt()
    {
        // The common case on a shared relay, and the one that must stay cheap.
        // A router that reached for "probably this group" would hand another
        // group's ciphertext to our keys.
        Assert.SkipUnless(_fixture.Ready, _fixture.SkipReason);

        InboundFanIn fanIn = await FanInAsync();

        InboundDelivery outcome = await fanIn.ReceiveAsync(await ForeignEnvelopeAsync());

        var refused = Assert.IsType<InboundDelivery.Refused>(outcome);
        var ignored = Assert.IsType<IngestOutcome.Ignored>(refused.Outcome);

        Assert.Equal(InputRejectionCategory.UnknownGroup, ignored.Category);
    }

    /// <summary>A genuine kind-445 belonging to a group that is not ours.</summary>
    /// <remarks>
    /// A real group rather than a hand-built envelope, because the refusal has
    /// to hold against traffic that is correct in every way except whose it is.
    /// Hand-assembled bytes would risk being refused for the wrong reason and
    /// the test would still pass.
    /// </remarks>
    private static async Task<string> ForeignEnvelopeAsync()
    {
        var stranger = new MessageInteropFixture.LocalSigner();

        CreatedGroup theirs = await MarmotGroupBuilder.CreateAsync(
            new CipherSuite0x0001(),
            stranger,
            "Strangers",
            "",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["wss://relay.example.com"]);

        return GroupMessages.Send(
            theirs.Group,
            new NostrGroupPeeler(),
            MarmotAppEvent.Chat(
                stranger.Hex, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "not for you"),
            stranger.AccountPublicKey.Span);
    }
}
