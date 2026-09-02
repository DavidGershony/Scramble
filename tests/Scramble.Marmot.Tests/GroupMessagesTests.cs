using System.Text;
using DotnetMls.Crypto;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Sending and receiving application messages in a two-member group.
/// </summary>
/// <remarks>
/// The three layers each authenticate something different, and the tests are
/// organised around that: MLS proves which member sent the bytes, the kind-445
/// wrap hides which group they belong to, and the payload's own author field is
/// checked against the MLS sender so a member cannot write in someone else's
/// name. The last is the one with no MLS equivalent, so it gets the most
/// attention here.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class GroupMessagesTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private sealed class LocalSigner : IAccountIdentityProofSigner
    {
        public LocalSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            Secret = secret;
            AccountPublicKey = publicKey;
        }

        public byte[] Secret { get; }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public string Hex => Convert.ToHexString(AccountPublicKey.Span).ToLowerInvariant();

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    /// <summary>A creator and a joined member, both live at the same epoch.</summary>
    private async Task<(LocalSigner AliceSigner, CreatedGroup Alice, LocalSigner BobSigner,
        DotnetMls.Group.MlsGroup Bob)> PairAsync()
    {
        var aliceSigner = new LocalSigner();
        var bobSigner = new LocalSigner();

        var alice = await MarmotGroupBuilder.CreateAsync(
            _cs, aliceSigner, "Rakes", "", Now, Relays);

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, bobSigner, Now);
        StagedInvite staged = MarmotGroupInvite.Add(alice.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        var bob = DotnetMls.Group.MlsGroup.ProcessWelcome(
            _cs,
            staged.Welcome,
            bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey);

        return (aliceSigner, alice, bobSigner, bob);
    }

    private static NostrGroupPeeler Peeler() => new();

    // ---- The round trip ----

    [Fact]
    public async Task AMessageSentByOneMemberIsReadByTheOther()
    {
        var (aliceSigner, alice, _, bob) = await PairAsync();
        var peeler = Peeler();

        string envelope = GroupMessages.Send(
            alice.Group,
            peeler,
            MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now, "hello rakes"),
            aliceSigner.AccountPublicKey.Span);

        PeeledMessage peeled = peeler.Peel(
            envelope, _ => GroupMessages.ExporterSecret(bob));

        Assert.Equal(PeeledContentKind.GroupMessage, peeled.Kind);

        ReceivedGroupMessage received = GroupMessages.Receive(bob, peeled.MlsBytes);

        Assert.Equal("hello rakes", received.Event.Content);
        Assert.Equal(MarmotAppEvent.ChatKind, received.Event.Kind);
        Assert.Equal(aliceSigner.AccountPublicKey.ToArray(), received.SenderIdentity);
    }

    [Fact]
    public async Task MessagesFlowBothWays()
    {
        var (aliceSigner, alice, bobSigner, bob) = await PairAsync();
        var peeler = Peeler();

        string toBob = GroupMessages.Send(
            alice.Group, peeler, MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now, "ping"),
            aliceSigner.AccountPublicKey.Span);

        Assert.Equal("ping", GroupMessages.Receive(
            bob, peeler.Peel(toBob, _ => GroupMessages.ExporterSecret(bob)).MlsBytes).Event.Content);

        string toAlice = GroupMessages.Send(
            bob, peeler, MarmotAppEvent.Chat(bobSigner.Hex, (long)Now + 1, "pong"),
            bobSigner.AccountPublicKey.Span);

        ReceivedGroupMessage back = GroupMessages.Receive(
            alice.Group,
            peeler.Peel(toAlice, _ => GroupMessages.ExporterSecret(alice.Group)).MlsBytes);

        Assert.Equal("pong", back.Event.Content);
        Assert.Equal(bobSigner.AccountPublicKey.ToArray(), back.SenderIdentity);
    }

    [Fact]
    public async Task SeveralMessagesArriveInOrderAndKeepTheirIdentity()
    {
        var (aliceSigner, alice, _, bob) = await PairAsync();
        var peeler = Peeler();

        var sent = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            sent.Add(GroupMessages.Send(
                alice.Group, peeler,
                MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now + i, $"message {i}"),
                aliceSigner.AccountPublicKey.Span));
        }

        for (int i = 0; i < sent.Count; i++)
        {
            ReceivedGroupMessage received = GroupMessages.Receive(
                bob, peeler.Peel(sent[i], _ => GroupMessages.ExporterSecret(bob)).MlsBytes);

            Assert.Equal($"message {i}", received.Event.Content);
        }
    }

    [Fact]
    public async Task AMessageCarriesEmojiIntact()
    {
        var (aliceSigner, alice, _, bob) = await PairAsync();
        var peeler = Peeler();

        // Above the BMP, which is where every .NET JSON encoder wants to emit a
        // surrogate escape. The id is a hash over the canonical form, so a
        // mis-encoded one fails id validation at the far end rather than merely
        // looking wrong.
        const string text = "rakes 🍂🧹 done";

        string envelope = GroupMessages.Send(
            alice.Group, peeler, MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now, text),
            aliceSigner.AccountPublicKey.Span);

        Assert.Equal(text, GroupMessages.Receive(
            bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(bob)).MlsBytes)
            .Event.Content);
    }

    [Fact]
    public async Task TagsSurviveTheRoundTrip()
    {
        var (aliceSigner, alice, _, bob) = await PairAsync();
        var peeler = Peeler();

        string target = new('a', 64);
        var reaction = MarmotAppEvent.Create(
            aliceSigner.Hex, (long)Now, MarmotAppEvent.ReactionKind,
            [[MarmotAppEvent.EventRefTag, target]], "+");

        string envelope = GroupMessages.Send(
            alice.Group, peeler, reaction, aliceSigner.AccountPublicKey.Span);

        ReceivedGroupMessage received = GroupMessages.Receive(
            bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(bob)).MlsBytes);

        Assert.Equal(MarmotAppEvent.ReactionKind, received.Event.Kind);
        Assert.Equal(target, received.Event.FirstTagValue(MarmotAppEvent.EventRefTag));
    }

    // ---- Across membership changes ----

    [Fact]
    public async Task ConversationSurvivesAMemberBeingAdded()
    {
        var (aliceSigner, alice, _, bob) = await PairAsync();
        var peeler = Peeler();

        // Before.
        string first = GroupMessages.Send(
            alice.Group, peeler, MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now, "before"),
            aliceSigner.AccountPublicKey.Span);
        Assert.Equal("before", GroupMessages.Receive(
            bob, peeler.Peel(first, _ => GroupMessages.ExporterSecret(bob)).MlsBytes).Event.Content);

        // A third member joins, which advances the epoch and rotates every key.
        var carolSigner = new LocalSigner();
        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, carolSigner, Now);
        StagedInvite staged = MarmotGroupInvite.Add(alice.Group, _cs, [carolBundle.KeyPackage]);
        staged.Applied();
        bob.ProcessCommit(staged.Commit);

        var carol = DotnetMls.Group.MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, carolBundle.KeyPackage,
            carolBundle.PrivateMaterial.InitPrivateKey,
            carolBundle.PrivateMaterial.LeafPrivateKey,
            carolBundle.PrivateMaterial.SignaturePrivateKey);

        // After: everyone is at the same epoch and still reads each other. A
        // conversation that stops working when somebody joins is the failure
        // this rules out, and no single-epoch test can see it.
        string second = GroupMessages.Send(
            alice.Group, peeler, MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now + 1, "after"),
            aliceSigner.AccountPublicKey.Span);

        Assert.Equal("after", GroupMessages.Receive(
            bob, peeler.Peel(second, _ => GroupMessages.ExporterSecret(bob)).MlsBytes).Event.Content);
        Assert.Equal("after", GroupMessages.Receive(
            carol, peeler.Peel(second, _ => GroupMessages.ExporterSecret(carol)).MlsBytes).Event.Content);
    }

    [Fact]
    public async Task EveryMemberOfAThreeWayGroupReadsEveryOther()
    {
        var (aliceSigner, alice, bobSigner, bob) = await PairAsync();
        var peeler = Peeler();

        var carolSigner = new LocalSigner();
        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, carolSigner, Now);
        StagedInvite staged = MarmotGroupInvite.Add(alice.Group, _cs, [carolBundle.KeyPackage]);
        staged.Applied();
        bob.ProcessCommit(staged.Commit);

        var carol = DotnetMls.Group.MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, carolBundle.KeyPackage,
            carolBundle.PrivateMaterial.InitPrivateKey,
            carolBundle.PrivateMaterial.LeafPrivateKey,
            carolBundle.PrivateMaterial.SignaturePrivateKey);

        // Each sender in turn, read by both others, with the MLS-authenticated
        // sender checked every time — a group where one member's messages are
        // attributed to another is worse than one where they do not arrive.
        var senders = new[]
        {
            (Name: "alice", Group: alice.Group, Signer: aliceSigner),
            (Name: "bob", Group: bob, Signer: bobSigner),
            (Name: "carol", Group: carol, Signer: carolSigner),
        };

        long at = (long)Now;
        foreach (var sender in senders)
        {
            string envelope = GroupMessages.Send(
                sender.Group, peeler,
                MarmotAppEvent.Chat(sender.Signer.Hex, at++, $"hello from {sender.Name}"),
                sender.Signer.AccountPublicKey.Span);

            foreach (var reader in senders.Where(r => r.Name != sender.Name))
            {
                ReceivedGroupMessage received = GroupMessages.Receive(
                    reader.Group,
                    peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(reader.Group)).MlsBytes);

                Assert.Equal($"hello from {sender.Name}", received.Event.Content);
                Assert.Equal(
                    sender.Signer.AccountPublicKey.ToArray(), received.SenderIdentity);
            }
        }
    }

    [Fact]
    public async Task ARemovedMemberCannotReadWhatTheGroupSendsAfterwards()
    {
        var (aliceSigner, alice, bobSigner, bob) = await PairAsync();
        var peeler = Peeler();

        var carolSigner = new LocalSigner();
        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, carolSigner, Now);
        StagedInvite added = MarmotGroupInvite.Add(alice.Group, _cs, [carolBundle.KeyPackage]);
        added.Applied();
        bob.ProcessCommit(added.Commit);

        // Carol is removed; her copy of the group stays at the old epoch.
        StagedInvite removed = MarmotGroupInvite.Remove(
            alice.Group, [carolSigner.AccountPublicKey.ToArray()]);
        removed.Applied();
        bob.ProcessCommit(removed.Commit);

        string after = GroupMessages.Send(
            alice.Group, peeler, MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now + 2, "private now"),
            aliceSigner.AccountPublicKey.Span);

        // Bob, still a member, reads it.
        Assert.Equal("private now", GroupMessages.Receive(
            bob, peeler.Peel(after, _ => GroupMessages.ExporterSecret(bob)).MlsBytes).Event.Content);

        // Carol cannot even peel the outer wrap: the transport key moved with
        // the epoch she was removed at. Forward secrecy is not only an MLS
        // property here — the relay-visible layer rotates too.
        var carol = DotnetMls.Group.MlsGroup.ProcessWelcome(
            _cs, added.Welcome!, carolBundle.KeyPackage,
            carolBundle.PrivateMaterial.InitPrivateKey,
            carolBundle.PrivateMaterial.LeafPrivateKey,
            carolBundle.PrivateMaterial.SignaturePrivateKey);

        Assert.Throws<PeelFailedException>(
            () => peeler.Peel(after, _ => GroupMessages.ExporterSecret(carol)));

        _ = bobSigner;
    }

    // ---- What is refused ----

    [Fact]
    public async Task AMemberCannotWriteInSomebodyElsesName()
    {
        var (aliceSigner, alice, bobSigner, bob) = await PairAsync();
        var peeler = Peeler();

        // Alice authors a payload claiming Bob wrote it. MLS authenticates the
        // envelope perfectly — it really is from Alice's leaf — so only the
        // author check catches this. Without it, every member can forge every
        // other member.
        var forged = MarmotAppEvent.Chat(bobSigner.Hex, (long)Now, "bob said this");

        // The sender-side guard refuses it first, so a caller cannot even build
        // the message by accident.
        Assert.Throws<MarmotAppEventException>(() => GroupMessages.Send(
            alice.Group, peeler, forged, aliceSigner.AccountPublicKey.Span));

        // And if one were constructed anyway, the receiver refuses it. Built by
        // encrypting the forged payload directly, bypassing Send.
        var encrypted = alice.Group.EncryptApplicationMessage(forged.Encode());
        byte[] mlsBytes = DotnetMls.Codec.TlsCodec.Serialize(
            new DotnetMls.Types.MlsMessage(
                DotnetMls.Types.WireFormat.MlsPrivateMessage, encrypted).WriteTo);

        var ex = Assert.Throws<MarmotAppEventException>(
            () => GroupMessages.Receive(bob, mlsBytes));

        Assert.Contains("claims author", ex.Message);
    }

    [Fact]
    public void AnEventWhoseIdDoesNotMatchItsContentsIsRefused()
    {
        var real = MarmotAppEvent.Chat(new string('b', 64), (long)Now, "original");

        // Same id, different content: the shape an attacker wants, so that a
        // reply or reaction resolves to one message while the reader sees
        // another.
        var tampered = real with { Content = "tampered" };

        var ex = Assert.Throws<MarmotAppEventException>(
            () => MarmotAppEvent.Decode(tampered.Encode()));

        Assert.Contains("hash to", ex.Message);
    }

    [Fact]
    public void AnEventIdIsTheCanonicalNip01Hash()
    {
        // The id must be the NIP-01 hash of the canonical array, not of the
        // struct-order JSON the payload travels as. Confusing them yields an
        // event every peer rejects.
        string pubkey = new('c', 64);
        var appEvent = MarmotAppEvent.Chat(pubkey, 1_700_000_000, "hi");

        var template = new NostrEventTemplate(
            pubkey, 1_700_000_000, (int)MarmotAppEvent.ChatKind, [], "hi");

        Assert.Equal(
            Convert.ToHexString(template.ComputeId()).ToLowerInvariant(), appEvent.Id);
    }

    [Fact]
    public async Task AMessageFromAnotherGroupCannotBeRead()
    {
        var (aliceSigner, alice, _, bob) = await PairAsync();
        var (otherSigner, other, _, _) = await PairAsync();
        var peeler = Peeler();

        string envelope = GroupMessages.Send(
            other.Group, peeler, MarmotAppEvent.Chat(otherSigner.Hex, (long)Now, "not yours"),
            otherSigner.AccountPublicKey.Span);

        // The outer wrap is keyed by the sending group's exporter secret, so a
        // different group cannot even peel it — the relay-visible layer already
        // separates the two.
        Assert.Throws<PeelFailedException>(
            () => peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(bob)));

        _ = aliceSigner;
        _ = alice;
    }

    [Fact]
    public async Task TheTransportAddressComesFromTheRoutingComponent()
    {
        var (_, alice, _, _) = await PairAsync();

        byte[] transport = GroupMessages.TransportGroupId(alice.Group);

        Assert.Equal(alice.Routing.TransportGroupId.ToArray(), transport);

        // Deliberately unrelated to the MLS group id: the transport id is public
        // and the MLS id is not, so deriving one from the other would hand every
        // relay the group's real identity.
        Assert.NotEqual(alice.GroupId, transport);
    }

    [Fact]
    public async Task TheExporterSecretChangesWithTheEpoch()
    {
        var (_, alice, _, _) = await PairAsync();
        byte[] atEpoch1 = GroupMessages.ExporterSecret(alice.Group);

        var third = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        MarmotGroupInvite.Add(alice.Group, _cs, [third.KeyPackage]).Applied();

        // Forward secrecy at the transport layer depends on this: a key that
        // survived a membership change would let a removed member keep reading
        // the outer wrap.
        Assert.NotEqual(atEpoch1, GroupMessages.ExporterSecret(alice.Group));
    }

    [Fact]
    public async Task GarbageInPlaceOfAnMlsMessageIsRefusedCleanly()
    {
        var (_, _, _, bob) = await PairAsync();

        var ex = Assert.Throws<MarmotAppEventException>(
            () => GroupMessages.Receive(bob, Encoding.UTF8.GetBytes("not an MLS message")));

        Assert.Contains("MLSMessage", ex.Message);
    }
}
