using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>A group joined through a Welcome.</summary>
/// <param name="Group">The live MLS group, at the epoch we were admitted at.</param>
/// <param name="GroupId">The MLS group id.</param>
/// <param name="Required">The group's required-component set.</param>
/// <param name="InviterIdentity">
/// Who invited us, taken from the gift wrap's <b>verified seal</b> rather than
/// from any field the rumor claims.
/// </param>
/// <param name="KeyPackageEventIdHex">The KeyPackage this Welcome consumed.</param>
public sealed record JoinedGroup(
    MlsGroup Group,
    byte[] GroupId,
    IReadOnlySet<ushort> Required,
    byte[] InviterIdentity,
    string KeyPackageEventIdHex)
{
    /// <summary>Builds the durable record for a group we joined.</summary>
    public GroupRecord ToRecord(DateTimeOffset joinedAt)
    {
        var epoch = new EpochId(Group.Epoch);

        return new GroupRecord(
            new GroupId(GroupId),
            epoch,
            ProtocolProfile.Current,
            joinedAt,
            joinedAt)
        {
            // Not zero, unlike a group we created. Everything before this epoch
            // happened without us and is not decryptable, so treating those
            // messages as delivery failures would be wrong.
            JoinEpoch = epoch,

            // Every leaf's proof still needs verifying; we have checked only our
            // own. Claiming otherwise would skip the check permanently.
            ValidatedTree = false,
        };
    }
}

/// <summary>
/// Joining a group from a gift-wrapped kind-444 Welcome.
/// </summary>
/// <remarks>
/// <para>
/// The inbound mirror of invite, and the asymmetry is the point: an invite is
/// something we chose, while a Welcome arrives from someone we may not know.
/// So everything here is checked before any state is created — the wrap, the
/// rumor's shape, that the KeyPackage it names is one we actually published and
/// still hold material for, and that the resulting GroupContext satisfies the
/// Current profile.
/// </para>
/// <para>
/// <b>A Welcome consumes a KeyPackage.</b> The private material for a
/// non-last-resort one must be erased once a Welcome has been processed against
/// it, and the caller does that through
/// <see cref="IKeyPackageStorage.MarkConsumedAsync"/> — only on success, so a
/// failed join leaves the KeyPackage usable for a retry.
/// </para>
/// </remarks>
public static class GroupJoin
{
    /// <summary>
    /// Reads a gift-wrapped Welcome without joining.
    /// </summary>
    /// <param name="envelope">A kind-1059 event addressed to us.</param>
    /// <param name="accountSecret">Our account secret, to open the wrap.</param>
    /// <exception cref="GiftWrapException">The wrap is not ours or is malformed.</exception>
    /// <exception cref="PeelFailedException">The rumor is not a conformant Welcome.</exception>
    public static WelcomeRumor Read(string envelope, ReadOnlySpan<byte> accountSecret)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // Unwrap first: until the seal verifies, nothing inside is attributable
        // and the sender field is just a claim.
        Rumor rumor = Nip59GiftWrap.Unwrap(envelope, accountSecret);
        return WelcomeEvent.Read(rumor);
    }

    /// <summary>
    /// Joins the group a Welcome admits us to.
    /// </summary>
    /// <param name="cs">The ciphersuite.</param>
    /// <param name="welcome">The rumor, from <see cref="Read"/>.</param>
    /// <param name="keyPackage">The KeyPackage the Welcome consumed.</param>
    /// <param name="material">Its private material.</param>
    /// <exception cref="AppComponentException">
    /// The resulting group does not satisfy the Current-profile invariants — we
    /// were invited to a group we cannot honour.
    /// </exception>
    public static JoinedGroup Join(
        ICipherSuite cs,
        WelcomeRumor welcome,
        KeyPackage keyPackage,
        KeyPackagePrivateMaterial material)
    {
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(welcome);
        ArgumentNullException.ThrowIfNull(keyPackage);
        ArgumentNullException.ThrowIfNull(material);

        MlsMessage message;
        try
        {
            message = MlsMessage.ReadFrom(new TlsReader(welcome.WelcomeBytes));
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException)
        {
            throw new PeelFailedException($"The Welcome is not a decodable MLSMessage: {ex.Message}");
        }

        if (message.WireFormat != WireFormat.MlsWelcome || message.Body is not Welcome body)
            throw new PeelFailedException($"The rumor carried {message.WireFormat}, not a Welcome.");

        MlsGroup group;
        try
        {
            group = MlsGroup.ProcessWelcome(
                cs,
                body,
                keyPackage,
                material.InitPrivateKey,
                material.LeafPrivateKey,
                material.SignaturePrivateKey,
                config: MarmotGroupSettings.Create());
        }
        catch (Exception ex) when (ex is InvalidOperationException or TlsDecodingException)
        {
            throw new PeelFailedException($"The Welcome could not be processed: {ex.Message}");
        }

        // Validated after the MLS join but before the caller is told it worked.
        // A group requiring something we cannot honour is one we must not stay
        // in: joining and silently ignoring mandatory state is the failure this
        // prevents, and it is worse than refusing the invite.
        IReadOnlySet<ushort> required = MarmotGroupBuilder.ValidateCreated(group, "joined group");

        return new JoinedGroup(
            group,
            group.GroupId.ToArray(),
            required,
            Convert.FromHexString(welcome.SenderPublicKeyHex),
            Convert.ToHexString(welcome.KeyPackageEventId).ToLowerInvariant());
    }

    /// <summary>
    /// Reads a Welcome, finds the KeyPackage it consumed, and joins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The KeyPackage is looked up by the <b>event id</b> the Welcome names,
    /// which is the only identifier the sender has for it. A Welcome naming
    /// something we never published, or whose material has already been erased,
    /// is refused rather than guessed at — accepting either would mean joining
    /// with a key we cannot prove is ours.
    /// </para>
    /// <para>
    /// Marks the KeyPackage consumed only on success, so a failed join leaves it
    /// usable for a retry.
    /// </para>
    /// </remarks>
    public static async Task<JoinedGroup> JoinFromEnvelopeAsync(
        ICipherSuite cs,
        string envelope,
        ReadOnlyMemory<byte> accountSecret,
        IKeyPackageStorage storage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(storage);

        WelcomeRumor welcome = Read(envelope, accountSecret.Span);
        string eventIdHex = Convert.ToHexString(welcome.KeyPackageEventId).ToLowerInvariant();

        KeyPackageRecord record = await storage.GetKeyPackageByEventAsync(eventIdHex, ct)
                .ConfigureAwait(false)
            ?? throw new PeelFailedException(
                $"The Welcome names KeyPackage event {eventIdHex}, which this device never published.");

        if (record.PrivateMaterial is null)
        {
            throw new PeelFailedException(
                $"The KeyPackage for event {eventIdHex} has been consumed and its material erased.");
        }

        var message = MlsMessage.ReadFrom(new TlsReader(record.PublicKeyPackage));
        var keyPackage = (KeyPackage)message.Body;

        JoinedGroup joined = Join(
            cs, welcome, keyPackage, KeyPackagePrivateMaterial.Decode(record.PrivateMaterial));

        await storage.MarkConsumedAsync(record.KeyPackageRefHex, ct).ConfigureAwait(false);

        return joined;
    }
}
