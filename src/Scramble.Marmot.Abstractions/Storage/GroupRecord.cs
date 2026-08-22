namespace Scramble.Marmot.Storage;

/// <summary>
/// The engine's durable view of a group. MLS state itself lives in MLS
/// storage; this is the Marmot-layer record beside it.
/// </summary>
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
}
