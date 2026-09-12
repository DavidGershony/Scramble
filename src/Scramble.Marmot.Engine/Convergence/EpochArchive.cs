using DotnetMls.Crypto;
using DotnetMls.Group;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Convergence;

/// <summary>
/// The retained epochs of one group, read into memory for a single pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loaded in one go rather than asked for an epoch at a time.</b> Which
/// epoch needs restoring is not known until the candidates have been built, and
/// building them is what needs the states — so a lazy per-epoch lookup would
/// have to be synchronous over storage inside the materializer's inner loop.
/// The window is bounded by the rewind horizon, so reading all of it costs a
/// handful of rows.
/// </para>
/// <para>
/// <b>Every <see cref="Restore"/> returns a fresh group, never a shared one.</b>
/// Candidate evaluation works by applying commits to the group it is handed, so
/// two branches forking from one epoch would otherwise replay onto the same
/// instance and each would see the other's commits. The cost of an import is
/// the price of that isolation.
/// </para>
/// </remarks>
public sealed class EpochWindow
{
    private readonly Dictionary<ulong, EpochCheckpoint> _byEpoch;
    private readonly ICipherSuite _cs;

    internal EpochWindow(IReadOnlyList<EpochCheckpoint> checkpoints, ICipherSuite cs)
    {
        _byEpoch = checkpoints.ToDictionary(c => c.Epoch.Value);
        _cs = cs;
    }

    /// <summary>The epochs held, oldest first.</summary>
    public IReadOnlyList<EpochId> Epochs =>
        _byEpoch.Keys.Order().Select(e => new EpochId(e)).ToList();

    /// <summary>
    /// A throwaway copy of the group as it was at an epoch, or null when that
    /// epoch is not retained.
    /// </summary>
    /// <remarks>
    /// Null means out of horizon, and nothing else. A checkpoint that will not
    /// import is a broken archive rather than a missing one, and it throws:
    /// answering null there would file the failure as "too far back" and
    /// silently narrow this member's horizon by however many epochs are
    /// unreadable.
    /// </remarks>
    public MlsGroup? Restore(EpochId epoch) =>
        _byEpoch.TryGetValue(epoch.Value, out EpochCheckpoint? checkpoint)
            ? MlsGroup.Import(checkpoint.GroupState, _cs)
            : null;

    /// <summary>
    /// The commit that produced an epoch, or null when that epoch is not
    /// retained or was not produced by a commit we held.
    /// </summary>
    /// <remarks>
    /// What <see cref="CandidateMaterializer.Materialize"/> needs for the branch
    /// we are already on, which is the one candidate that cannot be described on
    /// the way in. The two nulls are not worth distinguishing to a caller: both
    /// mean this member cannot state its own branch's terms, and in both cases
    /// the only sound move is to decline to decide.
    /// </remarks>
    public CommitTip? TipAt(EpochId epoch) =>
        _byEpoch.TryGetValue(epoch.Value, out EpochCheckpoint? checkpoint)
            ? checkpoint.Tip
            : null;
}

/// <summary>
/// Keeping enough past MLS state that a fork can be evaluated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capture happens after the commit is applied; its class is read
/// before.</b> The state belonging to epoch N is the state the commit produced,
/// so it can only be captured afterwards — while the commit's ordering class
/// resolves against a proposal cache that applying it clears. The two facts
/// about one epoch therefore have to be collected either side of the same step
/// and written together, which is why they share a row.
/// </para>
/// <para>
/// <b>Pruning is part of capturing, not a separate duty.</b> An archive trimmed
/// on its own schedule is one that is either holding key material for branches
/// the policy already refuses, or — the failure that matters — quietly short of
/// the horizon at the moment a fork arrives.
/// </para>
/// </remarks>
public sealed class EpochArchive(
    IEpochArchiveStorage storage,
    ICipherSuite cipherSuite,
    ConvergencePolicy policy,
    Func<DateTimeOffset> clock)
{
    private readonly IEpochArchiveStorage _storage =
        storage ?? throw new ArgumentNullException(nameof(storage));

    private readonly ICipherSuite _cs =
        cipherSuite ?? throw new ArgumentNullException(nameof(cipherSuite));

    private readonly ConvergencePolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    private readonly Func<DateTimeOffset> _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The oldest epoch worth retaining against a current tip.
    /// </summary>
    /// <remarks>
    /// Exactly the horizon <see cref="BranchSelection.IsEligible"/> applies, and
    /// deliberately not one epoch tighter: a branch forking at
    /// <c>tip - MaxRewindCommits</c> is eligible, so its fork epoch must still
    /// be restorable or the policy and the archive disagree about what this
    /// member may adopt.
    /// </remarks>
    public EpochId OldestRetainedFor(EpochId currentEpoch) => new(
        currentEpoch.Value > _policy.MaxRewindCommits
            ? currentEpoch.Value - _policy.MaxRewindCommits
            : 0);

    /// <summary>
    /// Archives the group at the epoch it currently stands on, and drops
    /// whatever has fallen out of horizon.
    /// </summary>
    /// <param name="tip">
    /// The commit that produced this epoch, read before it was applied, or null
    /// when no commit we held produced it — a group's first epoch, or one joined
    /// through a Welcome.
    /// </param>
    public async Task CaptureAsync(
        GroupId groupId,
        MlsGroup group,
        CommitTip? tip,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        var epoch = new EpochId(group.Epoch);

        await _storage.PutEpochCheckpointAsync(
            new EpochCheckpoint(groupId, epoch, group.Export(), tip, _clock()), ct);

        await _storage.PruneEpochCheckpointsBeforeAsync(groupId, OldestRetainedFor(epoch), ct);
    }

    /// <summary>
    /// Archives the epoch the group stands on, if nothing has yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called before leaving an epoch, because every epoch left behind is a
    /// place a fork can start.</b> Arriving somewhere archives it, so the only
    /// epoch that can go unrecorded is the first one a group is held at — epoch
    /// zero of a group we created, or the epoch of a Welcome we joined from.
    /// Neither arrives through an apply, and a competing commit framed at either
    /// would find nothing to rebuild from.
    /// </para>
    /// <para>
    /// <b>The tip is null, and that is the truth rather than a shortfall.</b> No
    /// commit of ours produced this epoch: the group began here, or somebody
    /// else's commit admitted us and we never held it. Recording the state
    /// without a tip is what lets a later branch fork from this epoch while
    /// still refusing to let our own branch compete on invented terms.
    /// </para>
    /// <para>
    /// <b>Only when absent.</b> An epoch already archived was archived by
    /// whoever applied the commit that produced it, together with what that
    /// commit was — and overwriting that with a null tip would throw away the
    /// one description of it that exists.
    /// </para>
    /// </remarks>
    public async Task CaptureIfAbsentAsync(
        GroupId groupId, MlsGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (await _storage.GetEpochCheckpointAsync(groupId, new EpochId(group.Epoch), ct) is null)
            await CaptureAsync(groupId, group, tip: null, ct);
    }

    /// <summary>
    /// Reads the retained window for a convergence pass.
    /// </summary>
    public async Task<EpochWindow> LoadWindowAsync(
        GroupId groupId, EpochId currentEpoch, CancellationToken ct = default) =>
        new(
            await _storage.ListEpochCheckpointsAsync(
                groupId, OldestRetainedFor(currentEpoch), ct),
            _cs);
}
