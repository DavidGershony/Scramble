using DotnetMls.Codec;
using DotnetMls.Types;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// Turning a Welcome into the gift-wrapped kind-444 event a new member receives.
/// </summary>
/// <remarks>
/// <para>
/// The Welcome goes on the wire as an <c>MLSMessage</c>, not as a bare
/// <c>Welcome</c> struct — the receiver deserializes an <c>MLSMessage</c> and
/// extracts the Welcome body from it, so a bare struct is refused before
/// anything about the group is looked at. Same rule as the KeyPackage, and easy
/// to get wrong in the same way.
/// </para>
/// <para>
/// It is then wrapped twice: sealed to the recipient under the sender's account
/// key, and wrapped again under a throwaway key. The outer layer is what keeps a
/// relay from learning who is inviting whom, so <b>the ephemeral key must be
/// fresh per wrap</b> — reusing one links every invite a sender makes and
/// defeats the layer entirely.
/// </para>
/// <para>
/// The rumor names the <b>KeyPackage event id</b>, not the KeyPackageRef. That
/// is the id the recipient looks their own published KeyPackage up by, and it is
/// the only way they can find the private material this Welcome needs — a
/// Welcome naming the ref instead is one the recipient cannot open.
/// </para>
/// </remarks>
public static class WelcomePublication
{
    /// <summary>Serializes a Welcome as the <c>MLSMessage</c> that carries it.</summary>
    public static byte[] Serialize(Welcome welcome)
    {
        ArgumentNullException.ThrowIfNull(welcome);
        return TlsCodec.Serialize(new MlsMessage(WireFormat.MlsWelcome, welcome).WriteTo);
    }

    /// <summary>
    /// Builds the unsigned kind-444 rumor.
    /// </summary>
    /// <param name="senderPublicKeyHex">
    /// The inviter's account key. The recipient reads the sender from the
    /// verified seal rather than from here, but the two must agree or the wrap
    /// is refused.
    /// </param>
    /// <param name="keyPackageEventId">
    /// The 32-byte id of the kind-30443 event whose KeyPackage this Welcome
    /// consumed.
    /// </param>
    /// <param name="relays">Group relays the new member should use.</param>
    /// <param name="welcome">The MLS Welcome.</param>
    /// <param name="createdAt">Unix seconds.</param>
    public static Rumor BuildRumor(
        string senderPublicKeyHex,
        ReadOnlySpan<byte> keyPackageEventId,
        IReadOnlyList<string> relays,
        Welcome welcome,
        long createdAt)
    {
        ArgumentNullException.ThrowIfNull(senderPublicKeyHex);
        ArgumentNullException.ThrowIfNull(relays);

        return new Rumor(
            senderPublicKeyHex,
            createdAt,
            WelcomeEvent.Kind,
            WelcomeEvent.BuildTags(keyPackageEventId, relays),
            Convert.ToBase64String(Serialize(welcome)));
    }

    /// <summary>
    /// Builds and gift-wraps a Welcome, ready to publish as a kind-1059 event.
    /// </summary>
    /// <param name="senderPrivateKey">The inviter's account secret.</param>
    /// <param name="recipientPublicKey">The invitee's account key, x-only.</param>
    /// <remarks>
    /// The ephemeral key for the outer wrap is generated here rather than taken
    /// as a parameter, so a caller cannot accidentally reuse one across invites.
    /// </remarks>
    public static string Wrap(
        ReadOnlySpan<byte> senderPrivateKey,
        ReadOnlySpan<byte> senderPublicKey,
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> keyPackageEventId,
        IReadOnlyList<string> relays,
        Welcome welcome,
        long createdAt)
    {
        Rumor rumor = BuildRumor(
            Convert.ToHexString(senderPublicKey).ToLowerInvariant(),
            keyPackageEventId,
            relays,
            welcome,
            createdAt);

        var (ephemeralPrivateKey, ephemeralPublicKey) = Bip340.GenerateKeyPair();

        return Nip59GiftWrap.Wrap(
            rumor,
            senderPrivateKey,
            senderPublicKey,
            recipientPublicKey,
            ephemeralPrivateKey,
            ephemeralPublicKey,
            static (secret, id) => Bip340.Sign(secret, id));
    }
}
