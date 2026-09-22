namespace Scramble.Marmot.Storage;

/// <summary>
/// The engine's durable view of a group, including the MLS state it stands on.
/// </summary>
/// <remarks>
/// <b>The MLS state used to be described here as living somewhere else.</b> It
/// did not: nothing persisted it, so a group whose own member did all the
/// committing had no durable state at all and a restart found a Marmot-layer
/// record describing a group it could not reconstruct. <see cref="LiveState"/>
/// is that gap closed. The epoch archive is not a substitute — it keeps past
/// epochs so a fork can be evaluated, and is fed from the inbound commit path,
/// which a group nobody else commits in never reaches.
/// </remarks>
/// <param name="Removed">
/// True once the local member has been removed. The group is kept rather than
/// deleted so history stays readable, but it must reject new sends.
/// </param>
/// <param name="JoinEpoch">
/// The epoch the local member joined at. Messages from before it are not
/// decryptable by us and must not be treated as delivery failures.
/// </param>
/// <param name="ValidatedTree">
/// True once every leaf's account-identity proof has been verified for the
/// current epoch, so session-open hydration can skip re-verifying each leaf.
/// </param>
public sealed record GroupRecord(
    GroupId Id,
    EpochId Epoch,
    ProtocolProfile Profile,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool Removed { get; init; }

    public EpochId? JoinEpoch { get; init; }

    public bool ValidatedTree { get; init; }

    /// <summary>
    /// The exported MLS group as the engine last confirmed it, or null for a
    /// record written before there was anywhere to put it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Confirmed, not current.</b> It advances only when a commit's fate is
    /// settled — a commit of somebody else's applied, or one of ours the relay
    /// took. A commit of ours that is staged and unpublished lives in
    /// <see cref="StagedCommitRecord"/> instead, so a restart that has to
    /// abandon it has an untouched state to come back to. Writing the staged
    /// state here would make abandonment the unrecoverable direction, which is
    /// the wrong way round.
    /// </para>
    /// <para>
    /// <b>It carries private key material</b> — a group export includes the
    /// member's signature and HPKE private keys, because a restored group has
    /// to be able to keep participating. Exactly as sensitive as the live group
    /// state, and it belongs under the same protection.
    /// </para>
    /// </remarks>
    public byte[]? LiveState { get; init; }
}
