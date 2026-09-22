using DotnetMls.Group;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Messages;

/// <summary>
/// The address one epoch published to, and the key its envelopes were sealed
/// under.
/// </summary>
/// <param name="Epoch">The epoch these belong to.</param>
/// <param name="TransportGroupId">
/// The routing handle that epoch's <c>h</c> tag carried. Kept beside the secret
/// because the two rotate together: the routing id lives in signed group state,
/// so a commit can move it, and an envelope from before that commit names the
/// old address and is sealed under the old key.
/// </param>
/// <param name="ExporterSecret">
/// <c>MLS-Exporter("marmot", "group-event", 32)</c> as it was at that epoch.
/// </param>
public sealed record RetainedTransportKey(
    EpochId Epoch,
    byte[] TransportGroupId,
    byte[] ExporterSecret);

/// <summary>
/// The transport keys of the epochs a group has recently left.
/// </summary>
/// <remarks>
/// <para>
/// <b>The MLS layer cannot supply these, by design.</b> A group keeps enough of
/// each past epoch to read an application message sent in it — that is what
/// <c>MaxPastEpochs</c> buys — but <c>RetainCurrentEpoch</c> deliberately drops
/// the exporter secret along with the init, membership and confirmation
/// secrets, because retaining them would let old key material act in the
/// present. So the inner MLS message from an epoch we have left is readable and
/// the kind-445 wrap around it is not, and the message fails at the transport
/// layer before ingest ever sees MLS bytes.
/// </para>
/// <para>
/// <b>The epoch archive is where the key comes back from, and it is the only
/// place it can.</b> A checkpoint is <c>MlsGroup.Export()</c>, which writes all
/// fourteen key-schedule secrets, so importing one yields a group whose
/// <c>ExportSecret</c> is that epoch's. Nothing else retains it:
/// <c>ISnapshotStorage</c> captures Marmot-layer rows plus the group's
/// <i>current</i> live state, which is the state that already failed.
/// </para>
/// <para>
/// <b>The bound is the archive's own window and not a new constant.</b> It
/// retains <c>MaxRewindCommits</c> epochs back, which the pinned v1 policy sets
/// equal to <c>AppMessagePastEpochLimit</c> and to the MLS past-epoch window —
/// three names for one number, and <c>ConvergencePolicy.RequireWindowMatches</c>
/// refuses a policy where they disagree. So a key this holds is exactly a key
/// the group could still use the inner half of, and a key it does not hold is
/// one no amount of trying would help with.
/// </para>
/// <para>
/// <b>Cached because a restore is not free.</b> Rebuilding costs one
/// <c>MlsGroup.Import</c> per retained epoch — a full group deserialisation —
/// and paying that per undecryptable envelope would make a flood of junk
/// addressed to our routing id expensive. Paid once per epoch the group stands
/// at instead. What is held is at most five 32-byte secrets, strictly less key
/// material than the archive rows they were derived from, which are on disk for
/// the same window.
/// </para>
/// </remarks>
public sealed class RetainedTransportKeys(EpochArchive archive)
{
    private readonly EpochArchive _archive =
        archive ?? throw new ArgumentNullException(nameof(archive));

    private ulong? _builtForEpoch;
    private IReadOnlyList<RetainedTransportKey> _retained = [];

    /// <summary>
    /// The live epoch's key first, then every retained epoch below it, newest
    /// first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newest first because that is the order the answers arrive in: an
    /// envelope that did not open under the live key was almost always sealed
    /// one epoch ago, by a member who had not yet seen the commit we just
    /// applied. <b>That ordering is a cost property and not a correctness
    /// one</b>, and it survives mutation — reversing it delivers exactly the
    /// same messages, one AEAD failure later. It is stated here rather than
    /// left to look load-bearing.
    /// </para>
    /// <para>
    /// The live key is read off <paramref name="live"/> rather than off its
    /// checkpoint. They agree whenever both exist, and the group is the one that
    /// cannot be stale — a group created and not yet committed has no checkpoint
    /// for the epoch it is standing on until something captures one.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<RetainedTransportKey>> NewestFirstAsync(
        GroupId groupId, MlsGroup live, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(live);

        var liveKey = new RetainedTransportKey(
            new EpochId(live.Epoch),
            GroupMessages.TransportGroupId(live),
            GroupMessages.ExporterSecret(live));

        if (_builtForEpoch != live.Epoch)
        {
            _retained = await BuildAsync(groupId, live.Epoch, ct);
            _builtForEpoch = live.Epoch;
        }

        return [liveKey, .. _retained];
    }

    /// <summary>
    /// Forgets what was built, for a session that has changed which group it
    /// holds.
    /// </summary>
    /// <remarks>
    /// <b>Not covered by the epoch check, which is why it exists.</b> A reorg
    /// can land on an epoch numbered the same as the one it left — a competing
    /// branch of equal length — and the keys of the branch we abandoned are
    /// wrong at every epoch above the fork. Invalidating on epoch alone would
    /// keep them.
    /// </remarks>
    public void Invalidate()
    {
        _builtForEpoch = null;
        _retained = [];
    }

    private async Task<IReadOnlyList<RetainedTransportKey>> BuildAsync(
        GroupId groupId, ulong liveEpoch, CancellationToken ct)
    {
        EpochWindow window = await _archive.LoadWindowAsync(groupId, new EpochId(liveEpoch), ct);

        var keys = new List<RetainedTransportKey>();

        foreach (EpochId epoch in window.Epochs.Reverse())
        {
            // The live epoch's own checkpoint is skipped rather than preferred:
            // the caller already has that key off the group itself, and a
            // checkpoint for an epoch reached twice describes whichever visit
            // wrote last.
            if (epoch.Value >= liveEpoch)
                continue;

            // Restore answers null only for an epoch the window does not hold,
            // and these came from the window. A checkpoint that will not import
            // throws, which is the archive's stated contract and not something
            // to soften here: swallowing it would narrow this member's horizon
            // by however many epochs are unreadable, silently.
            MlsGroup at = window.Restore(epoch)!;

            keys.Add(new RetainedTransportKey(
                epoch,
                GroupMessages.TransportGroupId(at),
                GroupMessages.ExporterSecret(at)));
        }

        return keys;
    }
}
