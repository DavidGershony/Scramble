using System.Buffers.Binary;

namespace Scramble.Marmot.AppComponents;

/// <summary>
/// Roles a member may hold for QUIC-backed agent text streams.
/// </summary>
/// <remarks>
/// A bit mask, and each role is backed by its own private-use MLS extension
/// capability. That separation is the point: holding a <i>role</i> is a claim
/// about what a member can do, while supporting the <i>component</i> is only a
/// claim that they can read and honour the policy. A client can legitimately do
/// the second without the first.
/// </remarks>
[Flags]
public enum AgentTextStreamRoles : byte
{
    None = 0,

    /// <summary>May receive stream previews.</summary>
    Receive = 0x01,

    /// <summary>May send stream frames.</summary>
    Send = 0x02,

    /// <summary>May fan frames out to other members.</summary>
    Fanout = 0x04,
}

/// <summary>
/// <c>marmot.group.agent-text-stream.quic.v1</c> (<c>0x8006</c>) — the group's
/// policy for QUIC-backed agent text streams.
/// </summary>
/// <remarks>
/// <para>
/// <b>Supporting this component is not the same as performing the streams.</b>
/// The component is a policy: which roles the group requires and allows, and
/// the frame, replay and padding limits. Being able to decode and honour it is
/// what the <c>0x8006</c> advertisement means. Actually receiving or sending
/// frames requires the per-role extension capabilities (<c>0xf2d1</c>,
/// <c>0xf2d2</c>, <c>0xf2d4</c>), which are separate and which this
/// implementation does not advertise.
/// </para>
/// <para>
/// That distinction is upstream's, not ours: <c>capabilities_of_leaf</c> reads
/// app components from the leaf's dictionary and role capabilities from the MLS
/// extension list, and a group requiring a role rejects a member who lacks it at
/// invite time. So carrying the policy without the roles is coherent and safe —
/// we can be in a group that requires no roles of us, and are correctly refused
/// by one that does.
/// </para>
/// <para>
/// Exactly 12 bytes, big-endian, no framing.
/// </para>
/// </remarks>
/// <param name="RequiredRoles">Roles every member must hold. Never empty.</param>
/// <param name="AllowedRoles">Roles a member may hold. Must include the required ones.</param>
/// <param name="MaxPlaintextFrameLength">Largest plaintext frame, in bytes.</param>
/// <param name="ReplayTtlSeconds">How long a frame stays replay-protected.</param>
/// <param name="PaddingBucketBytes">Padding granularity, or zero for none.</param>
public sealed record AgentTextStreamPolicy(
    AgentTextStreamRoles RequiredRoles,
    AgentTextStreamRoles AllowedRoles,
    uint MaxPlaintextFrameLength,
    uint ReplayTtlSeconds,
    ushort PaddingBucketBytes)
{
    /// <summary>The component id.</summary>
    public const ushort ComponentId = AppComponent.AgentTextStreamQuic;

    /// <summary>The schema name.</summary>
    public const string Schema = AppComponent.AgentTextStreamQuicSchema;

    /// <summary>Exact encoded size. Anything else is invalid.</summary>
    public const int EncodedLength = 12;

    /// <summary>Every defined role bit; anything outside this is unknown.</summary>
    public const AgentTextStreamRoles RoleMask =
        AgentTextStreamRoles.Receive | AgentTextStreamRoles.Send | AgentTextStreamRoles.Fanout;

    /// <summary>Largest permitted plaintext frame, in bytes.</summary>
    public const uint MaxFrameLength = 65519;

    /// <summary>Largest permitted replay window, in seconds.</summary>
    public const uint MaxReplayTtlSeconds = 5 * 60;

    /// <summary>Largest permitted padding bucket, in bytes.</summary>
    public const ushort MaxPaddingBucketBytes = 4096;

    /// <summary>Encodes the policy, validating it first.</summary>
    /// <exception cref="AppComponentException">The policy is not valid.</exception>
    public byte[] Encode()
    {
        Validate();

        var buffer = new byte[EncodedLength];
        buffer[0] = (byte)RequiredRoles;
        buffer[1] = (byte)AllowedRoles;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(2, 4), MaxPlaintextFrameLength);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(6, 4), ReplayTtlSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10, 2), PaddingBucketBytes);
        return buffer;
    }

    /// <summary>Decodes and validates the policy.</summary>
    /// <exception cref="AppComponentException">Wrong length, or an invalid policy.</exception>
    public static AgentTextStreamPolicy Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != EncodedLength)
        {
            throw new AppComponentException(
                $"The agent-text-stream policy must be exactly {EncodedLength} bytes, got {data.Length}.");
        }

        var policy = new AgentTextStreamPolicy(
            (AgentTextStreamRoles)data[0],
            (AgentTextStreamRoles)data[1],
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(2, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(6, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(data.Slice(10, 2)));

        policy.Validate();
        return policy;
    }

    /// <summary>
    /// Checks the policy's own consistency rules.
    /// </summary>
    /// <remarks>
    /// Every rule here refuses something a lenient decoder would carry, and each
    /// would put us at odds with the group over signed state. An unknown role
    /// bit is the one worth naming: it means a newer peer required a role we
    /// cannot even name, so treating it as "no role" would have us believe we
    /// satisfy a requirement we do not understand.
    /// </remarks>
    /// <exception cref="AppComponentException">A rule does not hold.</exception>
    public void Validate()
    {
        if (RequiredRoles == AgentTextStreamRoles.None)
            throw new AppComponentException("The required agent-text-stream roles cannot be empty.");

        if ((RequiredRoles & ~RoleMask) != 0)
        {
            throw new AppComponentException(
                $"The required agent-text-stream roles contain unknown bits: 0x{(byte)RequiredRoles:x2}.");
        }

        if ((AllowedRoles & ~RoleMask) != 0)
        {
            throw new AppComponentException(
                $"The allowed agent-text-stream roles contain unknown bits: 0x{(byte)AllowedRoles:x2}.");
        }

        if ((RequiredRoles & ~AllowedRoles) != 0)
        {
            throw new AppComponentException(
                "The required agent-text-stream roles must be a subset of the allowed roles.");
        }

        if (MaxPlaintextFrameLength == 0)
            throw new AppComponentException("The agent-text-stream frame limit cannot be zero.");

        if (MaxPlaintextFrameLength > MaxFrameLength)
        {
            throw new AppComponentException(
                $"The agent-text-stream frame limit {MaxPlaintextFrameLength} exceeds {MaxFrameLength}.");
        }

        if (ReplayTtlSeconds > MaxReplayTtlSeconds)
        {
            throw new AppComponentException(
                $"The agent-text-stream replay TTL {ReplayTtlSeconds}s exceeds {MaxReplayTtlSeconds}s.");
        }

        if (PaddingBucketBytes > MaxPaddingBucketBytes)
        {
            throw new AppComponentException(
                $"The agent-text-stream padding bucket {PaddingBucketBytes} exceeds {MaxPaddingBucketBytes}.");
        }
    }
}
