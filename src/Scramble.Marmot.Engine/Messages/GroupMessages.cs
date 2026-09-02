using DotnetMls.Codec;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Wire.Nostr;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.Messages;

/// <summary>A decrypted application message and who actually sent it.</summary>
/// <param name="Event">The application payload.</param>
/// <param name="SenderIdentity">
/// The MLS-authenticated sender's account key. Not what the event claims — what
/// the ratchet tree says.
/// </param>
public sealed record ReceivedGroupMessage(MarmotAppEvent Event, byte[] SenderIdentity);

/// <summary>
/// Sending and receiving application messages over kind-445.
/// </summary>
/// <remarks>
/// <para>
/// Three layers, and each one authenticates something different. MLS encrypts
/// the payload and authenticates <i>which member</i> sent it. The kind-445 wrap
/// then encrypts the whole MLS message again under the group's exporter secret,
/// so a relay cannot tell one group's traffic from another's. And the payload
/// itself names an author, which is checked against the MLS sender — the step
/// that stops a member sending a message signed as somebody else.
/// </para>
/// <para>
/// The outer key is <c>MLS-Exporter("marmot", "group-event", 32)</c>, used
/// directly as a ChaCha20-Poly1305 key. It changes every epoch, which is what
/// makes the transport layer forward-secret along with the group.
/// </para>
/// </remarks>
public static class GroupMessages
{
    /// <summary>
    /// The group's current outer transport key.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored: it is epoch-scoped, so caching it across a
    /// commit would encrypt to a key the group has already moved past.
    /// </remarks>
    public static byte[] ExporterSecret(MlsGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.ExportSecret(
            NostrGroupPeeler.ExporterLabel,
            NostrGroupPeeler.ExporterContext,
            NostrGroupPeeler.ExporterLength);
    }

    /// <summary>
    /// The transport group id a group's messages are addressed to.
    /// </summary>
    /// <remarks>
    /// Read from the signed routing component, never from the MLS group id.
    /// They are deliberately unrelated: the transport id is public and the MLS
    /// id is not, so deriving one from the other would leak the group's
    /// identity to every relay that carries it.
    /// </remarks>
    public static byte[] TransportGroupId(MlsGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        foreach (var extension in group.GroupContext.Extensions)
        {
            if (extension.ExtensionType != MarmotDictionary.ExtensionType)
                continue;

            byte[]? routing = MarmotDictionary
                .Decode(extension.ExtensionData)
                .Get(AppComponent.NostrRouting);

            if (routing is not null)
                return NostrRouting.Decode(routing).TransportGroupId.ToArray();
        }

        throw new AppComponentException(
            "The group has no Nostr routing component, so it has no transport address.");
    }

    /// <summary>
    /// Encrypts an application event and wraps it as a kind-445 envelope.
    /// </summary>
    /// <param name="group">The group, at the epoch to send from.</param>
    /// <param name="peeler">The transport codec.</param>
    /// <param name="appEvent">
    /// The payload. Its author must be this member, or every receiver will
    /// reject it — which is checked here rather than at the far end, where the
    /// failure would be someone else's to diagnose.
    /// </param>
    /// <param name="senderIdentity">This member's account key.</param>
    /// <param name="expiresAt">Optional relay expiry, Unix seconds.</param>
    public static string Send(
        MlsGroup group,
        ITransportPeeler peeler,
        MarmotAppEvent appEvent,
        ReadOnlySpan<byte> senderIdentity,
        long? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(peeler);
        ArgumentNullException.ThrowIfNull(appEvent);

        appEvent.RequireSender(senderIdentity);

        PrivateMessage encrypted = group.EncryptApplicationMessage(appEvent.Encode());

        // Framed as an MLSMessage, like every other thing we put on the wire.
        // A bare PrivateMessage is refused by the receiver before it looks at
        // the group at all.
        byte[] mlsBytes = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPrivateMessage, encrypted).WriteTo);

        return peeler.WrapGroupMessage(
            mlsBytes, TransportGroupId(group), ExporterSecret(group), expiresAt);
    }

    /// <summary>
    /// Decrypts a peeled kind-445 message.
    /// </summary>
    /// <remarks>
    /// Takes already-peeled bytes rather than the envelope, because peeling
    /// needs the exporter secret of whichever group the message is addressed to
    /// — a lookup only the caller can do.
    /// </remarks>
    /// <exception cref="MarmotAppEventException">
    /// The payload is malformed, or its author is not the MLS sender.
    /// </exception>
    public static ReceivedGroupMessage Receive(MlsGroup group, ReadOnlySpan<byte> mlsBytes)
    {
        ArgumentNullException.ThrowIfNull(group);

        MlsMessage message;
        try
        {
            message = MlsMessage.ReadFrom(new TlsReader(mlsBytes.ToArray()));
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException)
        {
            throw new MarmotAppEventException($"Not a decodable MLSMessage: {ex.Message}");
        }

        if (message.WireFormat != WireFormat.MlsPrivateMessage
            || message.Body is not PrivateMessage privateMessage)
        {
            throw new MarmotAppEventException(
                $"Expected an application message, got {message.WireFormat}.");
        }

        var (plaintext, senderLeafIndex) = group.DecryptApplicationMessage(privateMessage);

        // The sender is taken from the ratchet tree, not from the payload. That
        // is the whole basis on which the next line can reject an impostor.
        byte[] senderIdentity = IdentityOfLeaf(group, senderLeafIndex);

        MarmotAppEvent appEvent = MarmotAppEvent.Decode(plaintext);
        appEvent.RequireSender(senderIdentity);

        return new ReceivedGroupMessage(appEvent, senderIdentity);
    }

    private static byte[] IdentityOfLeaf(MlsGroup group, uint leafIndex)
    {
        foreach (var (index, identity) in group.GetMembers())
        {
            if (index == leafIndex)
                return identity;
        }

        throw new MarmotAppEventException(
            $"The message names leaf {leafIndex}, which is not a member of this group.");
    }
}
