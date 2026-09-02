namespace Scramble.Marmot.AppComponents;

/// <summary>
/// The Marmot app-component ids and their schema names.
/// </summary>
/// <remarks>
/// <para>
/// App components are where Dark Matter put the group state that the old
/// protocol carried in MLS extensions. The <c>0xf2ee</c> <c>NostrGroupData</c>
/// extension is gone; routing now lives in <see cref="NostrRouting"/> and
/// disappearing messages in <see cref="MessageRetention"/>. Component state
/// itself sits in the MLS <c>app_data_dictionary</c>.
/// </para>
/// <para>
/// Only the v1 set is defined here. Media, QUIC, avatar and lifecycle
/// components are deliberately absent: they are deferred past cutover, and an
/// id constant with no codec behind it is an invitation to advertise support
/// this implementation does not have. The single exception is
/// <see cref="EncryptedMediaV1Frozen"/>, which is named in order to be
/// refused.
/// </para>
/// </remarks>
public static class AppComponent
{
    /// <summary>
    /// First id in the private-use range.
    /// </summary>
    /// <remarks>
    /// Kind-30443 KeyPackage events publish only private-use components;
    /// standardized ids below this boundary stay discoverable from the decoded
    /// MLS capabilities instead.
    /// </remarks>
    public const ushort PrivateUseStart = 0x8000;

    /// <summary>
    /// The upstream MLS-extensions-draft component carrying the supported and
    /// required component id lists inside an app-data dictionary.
    /// </summary>
    public const ushort AppComponents = 0x0001;

    /// <summary>The upstream draft component advertising SafeAAD usage.</summary>
    public const ushort SafeAad = 0x0002;

    /// <summary><c>marmot.group.profile.v1</c> — group name and description.</summary>
    public const ushort GroupProfile = 0x8001;

    /// <summary><c>marmot.group.admin-policy.v1</c> — who may commit what.</summary>
    public const ushort GroupAdminPolicy = 0x8003;

    /// <summary>
    /// <c>marmot.transport.nostr.routing.v1</c> — the routing id and relay list.
    /// </summary>
    /// <remarks>
    /// Required for any Nostr-routed group: the transport reads
    /// <c>nostr_group_id</c> and the relay list from here and derives them from
    /// nothing else.
    /// </remarks>
    public const ushort NostrRouting = 0x8004;

    /// <summary><c>marmot.group.message-retention.v1</c> — disappearing messages.</summary>
    public const ushort MessageRetention = 0x8005;

    /// <summary>
    /// The frozen v1 encrypted-media component, which a Current-profile group
    /// may neither require nor carry state for.
    /// </summary>
    /// <remarks>
    /// The one id defined here without a codec behind it, and deliberately so:
    /// it exists to be <i>refused</i>, not supported. Upstream freezes it —
    /// <c>validate_current_profile_group_context</c> rejects a group that
    /// requires it or holds its state — so a group carrying it is one every
    /// current peer already refuses, and silently accepting it would leave us
    /// alone in a group nobody else will join. The v2 media component
    /// (<c>0x800b</c>) is merely deferred, which is a different thing.
    /// </remarks>
    public const ushort EncryptedMediaV1Frozen = 0x8008;

    /// <summary>
    /// <c>marmot.member.account-identity-proof.v2</c> — binds a Marmot account
    /// key to a LeafNode's MLS signature key.
    /// </summary>
    /// <remarks>
    /// LeafNode-only. The proof bytes live in the LeafNode's app-data
    /// dictionary; a GroupContext dictionary only requires the id through its
    /// component lists.
    /// </remarks>
    public const ushort AccountIdentityProof = 0x8009;

    /// <summary>
    /// <c>marmot.group.lifecycle.v1</c> — whether the group is active or
    /// disbanded, in one byte.
    /// </summary>
    /// <remarks>
    /// Deferred at P4 and un-deferred once interop showed why: every group the
    /// reference implementation creates requires it, and it refuses any invitee
    /// whose leaf does not advertise it. See <see cref="GroupLifecycle"/>.
    /// </remarks>
    public const ushort GroupLifecycle = 0x800c;

    /// <summary>
    /// <c>marmot.group.agent-text-stream.quic.v1</c> — the group's policy for
    /// QUIC-backed agent text streams.
    /// </summary>
    /// <remarks>
    /// Supporting this component means being able to read and honour the
    /// policy. It is <b>not</b> a claim that this client performs the streams:
    /// the per-role capabilities (<c>0xf2d1</c>/<c>0xf2d2</c>/<c>0xf2d4</c>) are
    /// separate MLS extension types and we advertise none of them, so a group
    /// requiring a role correctly refuses us.
    /// </remarks>
    public const ushort AgentTextStreamQuic = 0x8006;

    /// <summary>
    /// <c>marmot.group.encrypted-media.v2</c> — where the group's encrypted
    /// media may live.
    /// </summary>
    /// <remarks>
    /// Live, unlike the frozen v1 at <see cref="EncryptedMediaV1Frozen"/>. The
    /// two are not versions of one supported thing: v1 may neither be required
    /// nor carried, while v2 is ordinary group state.
    /// </remarks>
    public const ushort EncryptedMediaV2 = 0x800b;

    public const string GroupProfileSchema = "marmot.group.profile.v1";
    public const string GroupAdminPolicySchema = "marmot.group.admin-policy.v1";
    public const string NostrRoutingSchema = "marmot.transport.nostr.routing.v1";
    public const string MessageRetentionSchema = "marmot.group.message-retention.v1";
    public const string AccountIdentityProofSchema = "marmot.member.account-identity-proof.v2";
    public const string GroupLifecycleSchema = "marmot.group.lifecycle.v1";
    public const string AgentTextStreamQuicSchema = "marmot.group.agent-text-stream.quic.v1";
    public const string EncryptedMediaV2Schema = "marmot.group.encrypted-media.v2";

    /// <summary>Whether an id falls in the private-use range.</summary>
    public static bool IsPrivateUse(ushort id) => id >= PrivateUseStart;

    /// <summary>
    /// The schema name for a known id, or null.
    /// </summary>
    /// <remarks>
    /// Null for an id this implementation does not implement, including the
    /// deferred components. That is the honest answer: knowing a name would not
    /// make the component supported.
    /// </remarks>
    public static string? SchemaOf(ushort id) => id switch
    {
        GroupProfile => GroupProfileSchema,
        GroupAdminPolicy => GroupAdminPolicySchema,
        NostrRouting => NostrRoutingSchema,
        MessageRetention => MessageRetentionSchema,
        AccountIdentityProof => AccountIdentityProofSchema,
        GroupLifecycle => GroupLifecycleSchema,
        AgentTextStreamQuic => AgentTextStreamQuicSchema,
        EncryptedMediaV2 => EncryptedMediaV2Schema,
        _ => null,
    };
}

/// <summary>
/// Raised when component bytes do not decode, or decode to something the
/// schema forbids.
/// </summary>
/// <remarks>
/// Component state is signed group state: every member must reach the same
/// decision about the same bytes. So a violation is an error rather than
/// something to repair locally — a receiver that silently normalises what it
/// was given has forked its view of the group from everyone else's.
/// </remarks>
public sealed class AppComponentException(string message) : Exception(message);
