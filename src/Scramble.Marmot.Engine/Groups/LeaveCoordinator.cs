using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// Raised when a group is used after the local member has asked to leave it, or
/// been removed from it.
/// </summary>
public sealed class GroupDepartedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public GroupDepartedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The durable half of leaving: an intent that outlives the proposal carrying
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A <c>self_remove</c> proposal is bound to one epoch and the intent behind
/// it is not.</b> Every member drops a cached proposal when the epoch advances,
/// so a departure request that is overtaken by anyone else's commit — someone
/// joining, someone else leaving, a routine key rotation — simply disappears.
/// Nobody rejects it and nothing reports a failure: the member is just still in
/// the group, and their client believes otherwise.
/// </para>
/// <para>
/// So the proposal cannot be the record of intent. <see cref="LeaveRequest"/> is
/// written before the proposal is published and cleared only when a commit
/// actually removes the member, and <see cref="ReproposeIfStaleAsync"/> re-sends
/// against each new epoch until then. That is also what makes the intent survive
/// a crash between recording it and publishing, and a cold start afterwards.
/// </para>
/// <para>
/// This owns the intent, not the schedule. When to call
/// <see cref="ReproposeIfStaleAsync"/> — on every epoch change, on session open,
/// on a timer — is the caller's, because only the caller sees the clock.
/// </para>
/// </remarks>
/// <param name="storage">Where the intent and the group record live.</param>
/// <param name="clock">Reads the current time.</param>
public sealed class LeaveCoordinator(IMarmotStorageProvider storage, Func<DateTimeOffset> clock)
{
    private readonly IMarmotStorageProvider _storage =
        storage ?? throw new ArgumentNullException(nameof(storage));

    private readonly Func<DateTimeOffset> _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Records the intent to leave and builds the proposal to publish.
    /// </summary>
    /// <remarks>
    /// <b>Recorded before the proposal is returned, deliberately.</b> The
    /// opposite order loses the intent to a crash between publishing and
    /// writing, and the member is then left in a group they believe they have
    /// left — the failure this whole type exists to prevent. Recording an intent
    /// we never publish is recoverable by contrast: the next repropose sends it.
    /// </remarks>
    /// <exception cref="GroupDepartedException">We are already leaving, or gone.</exception>
    public async Task<PublicMessage> RequestAsync(
        MlsGroup group, GroupId groupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        await RequireStillPresentAsync(groupId, ct);

        if (await _storage.GetLeaveRequestAsync(groupId, ct) is not null)
        {
            throw new GroupDepartedException(
                "This group already has an outstanding leave request; repropose it instead.");
        }

        // Built first: it validates that leaving is possible at all, and
        // recording an intent that Request would refuse leaves a request no
        // repropose can ever satisfy.
        PublicMessage proposal = MarmotGroupLeave.Request(group);
        var epoch = new EpochId(group.Epoch);

        await _storage.PutLeaveRequestAsync(
            new LeaveRequest(groupId, epoch, _clock())
            {
                ProposedInEpoch = epoch,
            },
            ct);

        return proposal;
    }

    /// <summary>
    /// Re-sends an outstanding departure request if the epoch has moved past the
    /// one it was last proposed in.
    /// </summary>
    /// <remarks>
    /// Returns null in the ordinary case — no request, or one already proposed
    /// at this epoch — so this is cheap to call on every epoch change.
    /// </remarks>
    /// <returns>A proposal to publish, or null when nothing is due.</returns>
    public async Task<PublicMessage?> ReproposeIfStaleAsync(
        MlsGroup group, GroupId groupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        LeaveRequest? request = await _storage.GetLeaveRequestAsync(groupId, ct);
        if (request is null)
            return null;

        var epoch = new EpochId(group.Epoch);
        if (request.ProposedInEpoch == epoch)
            return null;

        PublicMessage proposal = MarmotGroupLeave.Request(group);

        await _storage.PutLeaveRequestAsync(request with { ProposedInEpoch = epoch }, ct);

        return proposal;
    }

    /// <summary>
    /// Records the outcome of a handshake message on a group we may be leaving.
    /// </summary>
    /// <remarks>
    /// The two arms are not symmetric. A commit that removed us <i>resolves</i>
    /// the intent whether or not we asked to leave — being evicted reaches the
    /// same end — so it clears the request and marks the group removed. Any
    /// other commit only advances the epoch, which is what makes the outstanding
    /// request stale and due for reproposal.
    /// </remarks>
    public async Task ObserveAsync(
        GroupId groupId, HandshakeOutcome outcome, CancellationToken ct = default)
    {
        if (outcome != HandshakeOutcome.RemovedByCommit)
            return;

        await _storage.ClearLeaveRequestAsync(groupId, ct);

        if (await _storage.GetGroupAsync(groupId, ct) is { } record)
        {
            // Kept rather than deleted: the history stays readable. What changes
            // is that it stops being a group we can act in.
            await _storage.PutGroupAsync(
                record with { UpdatedAt = _clock(), Removed = true }, ct);
        }
    }

    /// <summary>Whether a departure request is outstanding for this group.</summary>
    public async Task<bool> IsLeavingAsync(GroupId groupId, CancellationToken ct = default) =>
        await _storage.GetLeaveRequestAsync(groupId, ct) is not null;

    /// <summary>
    /// Refuses to let a group be used for new outbound work once we have asked
    /// to leave it or been removed from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different states, one gate. A removed member cannot send anything the
    /// group could read — its keys belong to an epoch the members have left — so
    /// that half is a correctness rule. Gating a <i>pending</i> departure is a
    /// product rule instead: the send would work, and the member has said they
    /// are done, so continuing to talk while waiting to be let out is not what
    /// they asked for.
    /// </para>
    /// <para>
    /// Sending is gated; receiving is not. A leaver stays a member until someone
    /// commits the request, and refusing to read during that window would hide
    /// messages that were legitimately addressed to them — including the commit
    /// that finally lets them go.
    /// </para>
    /// </remarks>
    /// <exception cref="GroupDepartedException">The group is leaving or gone.</exception>
    public async Task RequireCanSendAsync(GroupId groupId, CancellationToken ct = default)
    {
        await RequireStillPresentAsync(groupId, ct);

        if (await _storage.GetLeaveRequestAsync(groupId, ct) is not null)
        {
            throw new GroupDepartedException(
                "This group has an outstanding leave request, so it cannot send.");
        }
    }

    private async Task RequireStillPresentAsync(GroupId groupId, CancellationToken ct)
    {
        if (await _storage.GetGroupAsync(groupId, ct) is { Removed: true })
        {
            throw new GroupDepartedException(
                "The local member has been removed from this group.");
        }
    }
}
