using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Storage;

/// <summary>
/// A group as it stood at one epoch, kept so a competing branch can be built
/// from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is MLS state, not Marmot-layer rows.</b> A snapshot
/// (<see cref="ISnapshotStorage"/>) captures the engine's own tables; this
/// captures the exported MLS group. Convergence needs both and for different
/// reasons: the MLS state is what a candidate commit is replayed against, and
/// the rows are what a reorg has to invalidate afterwards.
/// </para>
/// <para>
/// <b>The blob carries private key material</b> — a group export includes the
/// member's signature and HPKE private keys, because a restored group has to be
/// able to keep participating. It is exactly as sensitive as the live group
/// state and belongs under the same protection.
/// </para>
/// </remarks>
/// <param name="GroupState">The exported MLS group.</param>
/// <param name="TipPriority">
/// The ordering class of the commit that produced this epoch.
/// </param>
public sealed record EpochCheckpoint(
    GroupId GroupId,
    EpochId Epoch,
    byte[] GroupState,
    CommitOrderingPriority TipPriority,
    DateTimeOffset CreatedAt);
