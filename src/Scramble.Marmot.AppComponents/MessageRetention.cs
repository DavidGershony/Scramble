namespace Scramble.Marmot.AppComponents;

/// <summary>
/// <c>marmot.group.message-retention.v1</c> (<c>0x8005</c>) — disappearing messages.
/// </summary>
/// <remarks>
/// <para>
/// Optional, and where the old <c>disappearing_message_secs</c> field from the
/// <c>0xf2ee</c> extension went. Exactly eight bytes: a big-endian
/// <c>uint64</c> of seconds, where zero means disabled. Every value in the
/// <c>uint64</c> range is valid state — v1 defines no protocol maximum, and an
/// application that considers a duration unreasonable may refuse to *enable* it
/// locally but MUST NOT treat otherwise-valid state received from the group as
/// invalid.
/// </para>
/// <para>
/// Each application message pins the retention of its own source epoch. Later
/// updates or removal do not shorten, extend or restore an already-sent
/// message's expiry, and a retry or republication of the same MLS message
/// reuses the same pinned value.
/// </para>
/// <para>
/// The expiry is <b>advisory</b>. The duration is signed group state, but the
/// base timestamp is the sender's own <c>created_at</c>, so a sender that
/// back- or forward-dates a message shifts when its own message expires. This
/// inherits the trust already placed in the authenticated sender; it is not a
/// deletion guarantee against a hostile one.
/// </para>
/// </remarks>
/// <param name="Seconds">Retention duration in seconds; zero disables.</param>
public sealed record MessageRetention(ulong Seconds)
{
    /// <summary>Encoded length of the component, in bytes.</summary>
    public const int EncodedLength = 8;

    /// <summary>Retention disabled.</summary>
    public static MessageRetention Disabled { get; } = new(0);

    /// <summary>Whether disappearing messages are on.</summary>
    public bool IsEnabled => Seconds != 0;

    /// <summary>Encodes the component as a big-endian <c>uint64</c>.</summary>
    public byte[] Encode()
    {
        var output = new byte[EncodedLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(output, Seconds);
        return output;
    }

    /// <summary>Decodes the component.</summary>
    /// <remarks>
    /// A fixed width, so anything other than exactly eight bytes is malformed.
    /// Note this is <i>not</i> a varint — unlike every other length in these
    /// components — because the schema declares a bare <c>uint64</c>.
    /// </remarks>
    /// <exception cref="AppComponentException">The length is wrong.</exception>
    public static MessageRetention Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedLength)
        {
            throw new AppComponentException(
                $"A message-retention component is {EncodedLength} bytes, got {bytes.Length}.");
        }

        return new MessageRetention(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }

    /// <summary>
    /// The NIP-40 expiry for a message sent at <paramref name="createdAt"/>, or
    /// null when there is none to attach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exact checked arithmetic, deliberately. The spec forbids wrapping,
    /// saturating, or computing through an inexact number: each of those
    /// produces a timestamp that looks plausible and is wrong, and the sender
    /// would attach it as fact. When the sum does not fit, the answer is to
    /// omit the tag — the component state and the message stay valid.
    /// </para>
    /// <para>
    /// Returns null when retention is disabled, when
    /// <paramref name="createdAt"/> is negative, or when the sum overflows.
    /// </para>
    /// </remarks>
    public ulong? ExpiryFor(long createdAt)
    {
        if (!IsEnabled || createdAt < 0)
            return null;

        ulong basis = (ulong)createdAt;
        if (Seconds > ulong.MaxValue - basis)
            return null;

        return basis + Seconds;
    }
}
