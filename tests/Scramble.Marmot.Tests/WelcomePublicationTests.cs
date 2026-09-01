using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Wrapping a Welcome for the member it admits.
/// </summary>
/// <remarks>
/// The round trip here is the whole test: wrap a real Welcome, unwrap it as the
/// recipient, and process it into a joined group. Anything less proves only
/// that the bytes survived, not that they mean what the recipient needs.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class WelcomePublicationTests
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

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    private async Task<(LocalSigner Inviter, LocalSigner Invitee, MarmotKeyPackageBundle Bundle, StagedInvite Staged)>
        InviteAsync()
    {
        var inviter = new LocalSigner();
        var invitee = new LocalSigner();

        var group = await MarmotGroupBuilder.CreateAsync(_cs, inviter, "Rakes", "", Now);
        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, invitee, Now);

        var staged = MarmotGroupInvite.Add(group.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        return (inviter, invitee, bundle, staged);
    }

    [Fact]
    public void TheWelcomeGoesOnTheWireAsAnMlsMessage()
    {
        // A bare Welcome struct is refused by the receiver before anything about
        // the group is looked at, because it deserializes an MLSMessage and
        // extracts the body. Same rule as the KeyPackage, same easy mistake.
        var welcome = new Welcome { CipherSuite = _cs.Id };

        var message = MlsMessage.ReadFrom(new TlsReader(WelcomePublication.Serialize(welcome)));

        Assert.Equal(WireFormat.MlsWelcome, message.WireFormat);
        Assert.IsType<Welcome>(message.Body);
    }

    [Fact]
    public async Task TheRumorNamesTheKeyPackageEventNotTheRef()
    {
        var (inviter, _, bundle, staged) = await InviteAsync();
        byte[] eventId = RandomEventId();

        Rumor rumor = WelcomePublication.BuildRumor(
            Hex(inviter.AccountPublicKey.Span), eventId, Relays, staged.Welcome, (long)Now);

        WelcomeRumor read = WelcomeEvent.Read(rumor);

        // The event id is how the recipient finds their own published KeyPackage
        // and therefore its private material. A Welcome naming the ref instead
        // is one they cannot open.
        Assert.Equal(eventId, read.KeyPackageEventId);
        Assert.NotEqual(bundle.KeyPackageRefHex, Hex(read.KeyPackageEventId));
        Assert.Equal(Relays, read.Relays);
    }

    [Fact]
    public async Task TheRecipientUnwrapsItAndJoinsTheGroup()
    {
        var (inviter, invitee, bundle, staged) = await InviteAsync();
        byte[] eventId = RandomEventId();

        string envelope = WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            invitee.AccountPublicKey.Span,
            eventId,
            Relays,
            staged.Welcome,
            (long)Now);

        // As the recipient: open the wrap, read the rumor, process the Welcome.
        Rumor rumor = Nip59GiftWrap.Unwrap(envelope, invitee.Secret);
        WelcomeRumor read = WelcomeEvent.Read(rumor);

        var message = MlsMessage.ReadFrom(new TlsReader(read.WelcomeBytes));
        var joined = MlsGroup.ProcessWelcome(
            _cs,
            (Welcome)message.Body,
            bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey);

        Assert.Equal(1UL, joined.Epoch);

        // The sender is read off the verified seal, not off the rumor's own
        // author field, so this is the inviter's identity being authenticated
        // rather than merely asserted.
        Assert.Equal(Hex(inviter.AccountPublicKey.Span), read.SenderPublicKeyHex);
    }

    [Fact]
    public async Task SomebodyElseCannotOpenTheWrap()
    {
        var (inviter, invitee, _, staged) = await InviteAsync();
        var stranger = new LocalSigner();

        string envelope = WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            invitee.AccountPublicKey.Span,
            RandomEventId(),
            Relays,
            staged.Welcome,
            (long)Now);

        Assert.Throws<GiftWrapException>(() => Nip59GiftWrap.Unwrap(envelope, stranger.Secret));
    }

    [Fact]
    public async Task EachWrapUsesAFreshEphemeralKey()
    {
        var (inviter, invitee, _, staged) = await InviteAsync();

        string first = Wrap(inviter, invitee, staged);
        string second = Wrap(inviter, invitee, staged);

        // The outer layer exists so a relay cannot see who is inviting whom.
        // A reused ephemeral key links every invite one sender makes and
        // defeats it entirely, which is why the key is generated inside Wrap
        // rather than accepted as a parameter.
        Assert.NotEqual(AuthorOf(first), AuthorOf(second));
    }

    private string Wrap(LocalSigner inviter, LocalSigner invitee, StagedInvite staged) =>
        WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            invitee.AccountPublicKey.Span,
            RandomEventId(),
            Relays,
            staged.Welcome,
            (long)Now);

    private static string AuthorOf(string envelope) =>
        System.Text.Json.JsonDocument.Parse(envelope).RootElement
            .GetProperty("pubkey").GetString()!;

    private static byte[] RandomEventId() =>
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();
}
