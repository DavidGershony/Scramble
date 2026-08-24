namespace Scramble.Marmot.AppComponents;

/// <summary>
/// <c>marmot.group.admin-policy.v1</c> (<c>0x8003</c>) — who may commit what.
/// </summary>
/// <remarks>
/// <para>
/// The sole membership and settings authority for a v1 group, and required for
/// its whole lifetime — an <c>AppDataUpdate remove</c> targeting this component
/// is invalid, so nobody, admin included, can commit one. If it were absent, no
/// member would be authorized to add anyone, so a Welcome from a group without
/// it is rejected rather than treated as unrestricted.
/// </para>
/// <para>
/// An admin key is a Marmot account identity — the same raw 32-byte x-only
/// Nostr key a member carries in its MLS <c>BasicCredential</c>, not a separate
/// authorization key. So a multi-device account is one entry across all of its
/// leaves, and "is this member an admin" is answered by matching its
/// MLS-authenticated account identity, never its leaf.
/// </para>
/// <para>
/// Being listed is not sufficient: an <b>active</b> admin is one that is listed
/// <i>and</i> has at least one member leaf in the group. That distinction is
/// what <see cref="IsActiveAdmin"/> exists to keep visible, and it is a
/// cross-component check — this component's bytes alone cannot answer it.
/// </para>
/// <para>
/// If every active admin loses its keys, v1 has no succession, override or
/// automatic promotion. Messages keep flowing and non-admin SelfRemove keeps
/// working, but membership and settings are frozen permanently. Local policy
/// MUST NOT elevate anyone inside a frozen group.
/// </para>
/// </remarks>
public sealed record AdminPolicy
{
    /// <summary>Length of an admin key, in bytes.</summary>
    public const int KeyLength = 32;

    private AdminPolicy(IReadOnlyList<byte[]> admins) => Admins = admins;

    /// <summary>The admin account keys: sorted lexicographically, unique, non-empty.</summary>
    /// <remarks>
    /// v1 sets no independent ceiling on the count. Every distinct account in
    /// the group may legitimately be an admin, and uniqueness plus the
    /// membership check already bound the list by the group's account count.
    /// </remarks>
    public IReadOnlyList<byte[]> Admins { get; }

    /// <summary>
    /// Builds a policy, canonicalising the key list.
    /// </summary>
    /// <remarks>
    /// Producer-side, so sorting and de-duplicating is safe here — the caller
    /// has not committed to bytes yet. <see cref="Decode"/> rejects the same
    /// input instead.
    /// </remarks>
    public static AdminPolicy Create(IEnumerable<byte[]> admins)
    {
        ArgumentNullException.ThrowIfNull(admins);

        var canonical = new List<byte[]>();
        foreach (byte[] admin in admins)
        {
            ArgumentNullException.ThrowIfNull(admin);
            if (admin.Length != KeyLength)
                throw new AppComponentException($"An admin key must be {KeyLength} bytes.");

            canonical.Add(admin.ToArray());
        }

        canonical.Sort(Compare);

        var deduplicated = new List<byte[]>(canonical.Count);
        foreach (byte[] admin in canonical)
        {
            if (deduplicated.Count == 0 || Compare(deduplicated[^1], admin) != 0)
                deduplicated.Add(admin);
        }

        if (deduplicated.Count == 0)
            throw new AppComponentException("An admin policy must list at least one admin.");

        return new AdminPolicy(deduplicated);
    }

    /// <summary>Whether <paramref name="accountKey"/> appears in the list.</summary>
    /// <remarks>
    /// Listing alone is <b>not</b> admin authority — see
    /// <see cref="IsActiveAdmin"/>. This is the cheap half of the test and is
    /// separated so a caller cannot use it by accident believing it is the
    /// whole one.
    /// </remarks>
    public bool IsListed(ReadOnlySpan<byte> accountKey)
    {
        foreach (byte[] admin in Admins)
        {
            if (admin.AsSpan().SequenceEqual(accountKey))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="accountKey"/> is an <i>active</i> admin: listed,
    /// and holding at least one member leaf.
    /// </summary>
    /// <param name="accountsWithMemberLeaves">
    /// The account identities that hold a leaf in the epoch being judged.
    /// Supplied by the caller because this component cannot see the ratchet
    /// tree, and the check is against the <b>resulting</b> epoch.
    /// </param>
    public bool IsActiveAdmin(
        ReadOnlySpan<byte> accountKey, IEnumerable<byte[]> accountsWithMemberLeaves)
    {
        ArgumentNullException.ThrowIfNull(accountsWithMemberLeaves);

        if (!IsListed(accountKey))
            return false;

        foreach (byte[] account in accountsWithMemberLeaves)
        {
            if (account.AsSpan().SequenceEqual(accountKey))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether every listed admin holds a member leaf, as a valid epoch requires.
    /// </summary>
    /// <remarks>
    /// The coupling rule: a commit removing an account's last leaf must remove
    /// its admin key in the same commit, so a state listing an account with no
    /// leaf is invalid in the resulting epoch. This runs on <b>every</b> commit,
    /// not only ones carrying admin bytes — a commit that removes a listed
    /// account's last leaf without updating this component is invalid even
    /// though it never re-serialises the component.
    /// </remarks>
    public bool EveryAdminHasAMemberLeaf(IEnumerable<byte[]> accountsWithMemberLeaves)
    {
        ArgumentNullException.ThrowIfNull(accountsWithMemberLeaves);

        var accounts = accountsWithMemberLeaves.ToList();
        foreach (byte[] admin in Admins)
        {
            if (!accounts.Any(account => account.AsSpan().SequenceEqual(admin)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Encodes the component: a varint byte length, then the 32-byte keys.
    /// </summary>
    public byte[] Encode()
    {
        var keyBytes = new List<byte>(Admins.Count * KeyLength);
        foreach (byte[] admin in Admins)
            keyBytes.AddRange(admin);

        var output = new List<byte>();
        ComponentCodec.WriteVarint((ulong)keyBytes.Count, output);
        output.AddRange(keyBytes);

        return output.ToArray();
    }

    /// <summary>
    /// Decodes and validates the component.
    /// </summary>
    /// <remarks>
    /// An unsorted or duplicated list is rejected rather than canonicalised.
    /// Silently sorting an admin list would be the worst possible place to
    /// normalise: two members could then disagree about who governs the group
    /// while both believing their state is valid.
    /// </remarks>
    /// <exception cref="AppComponentException">The bytes are not a valid policy.</exception>
    public static AdminPolicy Decode(ReadOnlySpan<byte> bytes)
    {
        (ulong length, int prefixLength) = ComponentCodec.ReadVarint(bytes);

        if (length > int.MaxValue)
            throw new AppComponentException("The admin policy length is too large.");

        int end = prefixLength + (int)length;
        if (end > bytes.Length)
            throw new AppComponentException("The admin policy is truncated.");
        if (end != bytes.Length)
            throw new AppComponentException("The admin policy has trailing bytes.");
        if (length == 0 || length % KeyLength != 0)
        {
            throw new AppComponentException(
                $"An admin policy must contain one or more {KeyLength}-byte keys.");
        }

        var admins = new List<byte[]>((int)length / KeyLength);
        for (int i = prefixLength; i < end; i += KeyLength)
            admins.Add(bytes.Slice(i, KeyLength).ToArray());

        for (int i = 1; i < admins.Count; i++)
        {
            int order = Compare(admins[i - 1], admins[i]);
            if (order > 0)
                throw new AppComponentException("Admin keys must be sorted.");
            if (order == 0)
                throw new AppComponentException("Admin keys must be unique.");
        }

        return new AdminPolicy(admins);
    }

    private static int Compare(byte[] left, byte[] right) =>
        left.AsSpan().SequenceCompareTo(right);
}
