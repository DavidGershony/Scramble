using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Marmot;
using Scramble.Marmot.Storage;

namespace Scramble.Core.Services;

/// <summary>
/// Puts the engine's key material and message content through
/// <see cref="ISecureStorage"/> on the way to storage, and takes it back out on
/// the way up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The legacy engine's
/// <c>EncryptedSqliteStorageProvider</c> did exactly this job on the old schema;
/// the Dark Matter engine was built standalone and nothing in
/// <c>Scramble.Marmot.*</c> knows the concept exists, so the cutover moved
/// ratchet state and leaf private keys from DPAPI-protected fields to plaintext
/// BLOBs. See <c>ai-tasks/remaining-work-2026-09.md</c> §14.
/// </para>
/// <para>
/// <b>Why a decorator and not an encrypted database file.</b> SQLCipher would be
/// one seam instead of the eleven below, but it is a native dependency that has
/// to load on .NET Android ARM — and this project already abandoned an engine
/// for precisely that failure (the Rust uniffi backend). It also gives up
/// sharing one file per profile, which
/// <see cref="DarkMatterMlsServiceFactory.TablePrefix"/> documents as
/// deliberate. A managed decorator makes no platform bet.
/// </para>
/// <para>
/// <b>Where it sits, and what that buys.</b> Above the provider, so every write
/// reaching SQLite is already protected and every read is revealed before the
/// engine sees it. One consequence is worth stating because it looks like a gap:
/// <c>snapshots.data</c> is a JSON document the provider builds <i>below</i> this
/// decorator, out of rows it reads for itself — so what it serialises is the
/// already-protected bytes, and what it restores is written back still
/// protected. The snapshot is protected transitively, and symmetrically, without
/// this class touching it. Moving snapshot capture above this decorator would
/// silently undo that.
/// </para>
/// <para>
/// <b>The disposition of every stored byte column</b> is recorded in
/// <c>SecureMarmotStorageProviderTests</c>, which fails if a migration adds a
/// BLOB column or a record gains a <c>byte[]</c>-shaped member that nobody has
/// classified. That census is the mitigation for this shape's real weakness: a
/// field somebody forgets is otherwise a silent leak.
/// </para>
/// <para>
/// <b>What is deliberately left in the clear, and why it has to be.</b>
/// Identifiers and routing addresses — <c>groups.group_id</c>,
/// <c>messages.id</c>, <c>welcomes.id</c>, <c>routing_index.transport_group_id</c>,
/// every <c>group_id</c> foreign key, <c>epoch_states.staged_commit</c>,
/// <c>commit_publish_attempts.commit_id</c> and both <c>tip_commit</c> columns.
/// <see cref="ISecureStorage.Protect"/> is not deterministic (DPAPI is not), so
/// an encrypted identifier would be a different value on every write: lookups by
/// it would miss, <c>INSERT OR REPLACE</c> would insert instead of replace, and
/// <see cref="StagedCommitHandle"/>'s and <see cref="CommitTip.BranchId"/>'s
/// equality — which the engine uses to recognise its own commit — would never
/// match. All of these are content-derived digests of bytes a relay already
/// carries, so there is nothing secret in them to lose.
/// <c>key_packages.public_bytes</c> is in the clear for the plainer reason that
/// it is published to relays as a kind-30443 event.
/// </para>
/// <para>
/// <b>Opening a database whose rows predate this class.</b>
/// <see cref="ISecureStorage.Unprotect"/> returns a value with no magic prefix
/// unchanged, so an unprotected legacy row reads back correctly and nothing
/// corrupts — but silently, which for security code is the wrong kind of
/// success. Every such read is therefore counted, and logged once per field, into
/// <see cref="FieldsFoundUnprotected"/>. It is not an exception: existing groups
/// are abandoned by decision (<c>p11-cutover-plan-2026-09.md</c> §2) and there
/// are no users, so refusing to open would only brick a developer's profile over
/// state that is already written off — while the store heals forward, since every
/// rewrite of a row protects it.
/// </para>
/// </remarks>
public sealed class SecureMarmotStorageProvider : IMarmotStorageProvider, IDisposable
{
    private readonly IMarmotStorageProvider _inner;
    private readonly ISecureStorage _secure;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, int> _unprotected = new();

    /// <param name="inner">The provider whose rows are being protected.</param>
    /// <param name="secureStorage">The platform's at-rest protection.</param>
    public SecureMarmotStorageProvider(IMarmotStorageProvider inner, ISecureStorage secureStorage)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _secure = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _logger = LoggingConfiguration.CreateLogger<SecureMarmotStorageProvider>();
    }

    /// <summary>
    /// Fields that have been read back unprotected, and how many times.
    /// </summary>
    /// <remarks>
    /// Non-empty means the database holds rows written before at-rest protection
    /// existed, or by a host whose <see cref="ISecureStorage"/> protects nothing.
    /// Either way it is the one observable difference between "protected" and
    /// "appears to work", which is why it is surfaced rather than only logged.
    /// </remarks>
    public IReadOnlyDictionary<string, int> FieldsFoundUnprotected => _unprotected;

    // -- Protection helpers --

    private byte[] Protect(byte[] plain) => _secure.Protect(plain);

    private byte[]? ProtectOpt(byte[]? plain) => plain is null ? null : _secure.Protect(plain);

    /// <summary>
    /// Reveals a stored value, noticing when it was never protected.
    /// </summary>
    /// <remarks>
    /// The detection is "unprotecting changed nothing", not a magic-prefix test,
    /// because the prefix's value is not part of the <see cref="ISecureStorage"/>
    /// contract and is spelled out separately in five platform heads — two of
    /// which this change is not allowed to touch. Every one of them returns the
    /// input unchanged when the prefix is absent, which is the behaviour the
    /// interface documents, so comparing against the input tests the contract
    /// rather than a constant.
    /// </remarks>
    private byte[] Reveal(byte[] stored, string field)
    {
        byte[] plain = _secure.Unprotect(stored);

        if (plain.Length == stored.Length && plain.AsSpan().SequenceEqual(stored))
        {
            bool first = !_unprotected.ContainsKey(field);
            _unprotected.AddOrUpdate(field, 1, static (_, n) => n + 1);

            if (first)
            {
                _logger.LogWarning(
                    "{Field} was stored without at-rest protection and has been read back in " +
                    "the clear. The row predates SecureMarmotStorageProvider (or this host's " +
                    "ISecureStorage protects nothing). It is not corrupt and the value is " +
                    "correct; it is simply unprotected on disk, and stays so until the row is " +
                    "rewritten. MLS state from before the Dark Matter cutover is abandoned by " +
                    "decision — delete the profile database to start protected.",
                    field);
            }
        }

        return plain;
    }

    private byte[]? RevealOpt(byte[]? stored, string field) =>
        stored is null ? null : Reveal(stored, field);

    // Committer is a member's account key: not key material, but it says which
    // member drove a group to an epoch, and the legacy decorator protected the
    // equivalent (Message.SenderIdentity). It is read into memory and compared
    // there, never used in a WHERE, so protecting it costs nothing. Its sibling
    // Commit is the branch digest and stays in the clear — see the class remarks.
    private CommitTip ProtectTip(CommitTip tip) =>
        tip with { Committer = Protect(tip.Committer) };

    private CommitTip? ProtectTipOpt(CommitTip? tip) =>
        tip is null ? null : ProtectTip(tip);

    private CommitTip RevealTip(CommitTip tip) =>
        tip with { Committer = Reveal(tip.Committer, "CommitTip.Committer") };

    private CommitTip? RevealTipOpt(CommitTip? tip) =>
        tip is null ? null : RevealTip(tip);

    private GroupRecord ProtectGroup(GroupRecord g) =>
        g with { LiveState = ProtectOpt(g.LiveState) };

    private GroupRecord RevealGroup(GroupRecord g) =>
        g with { LiveState = RevealOpt(g.LiveState, "GroupRecord.LiveState") };

    private MessageRecord ProtectMessage(MessageRecord m) =>
        m with { Wire = Protect(m.Wire) };

    private MessageRecord RevealMessage(MessageRecord m) =>
        m with { Wire = Reveal(m.Wire, "MessageRecord.Wire") };

    private WelcomeRecord ProtectWelcome(WelcomeRecord w) =>
        w with { Wire = Protect(w.Wire) };

    private WelcomeRecord RevealWelcome(WelcomeRecord w) =>
        w with { Wire = Reveal(w.Wire, "WelcomeRecord.Wire") };

    private QueuedOutboundIntent ProtectIntent(QueuedOutboundIntent i) =>
        i with { Payload = Protect(i.Payload) };

    private QueuedOutboundIntent RevealIntent(QueuedOutboundIntent i) =>
        i with { Payload = Reveal(i.Payload, "QueuedOutboundIntent.Payload") };

    private KeyPackageRecord ProtectKeyPackage(KeyPackageRecord k) =>
        k with { PrivateMaterial = ProtectOpt(k.PrivateMaterial) };

    private KeyPackageRecord RevealKeyPackage(KeyPackageRecord k) =>
        k with { PrivateMaterial = RevealOpt(k.PrivateMaterial, "KeyPackageRecord.PrivateMaterial") };

    private EpochCheckpoint ProtectCheckpoint(EpochCheckpoint c) =>
        c with { GroupState = Protect(c.GroupState), Tip = ProtectTipOpt(c.Tip) };

    private EpochCheckpoint RevealCheckpoint(EpochCheckpoint c) =>
        c with
        {
            GroupState = Reveal(c.GroupState, "EpochCheckpoint.GroupState"),
            Tip = RevealTipOpt(c.Tip),
        };

    private StagedCommitRecord ProtectStagedCommit(StagedCommitRecord s) =>
        s with { GroupState = Protect(s.GroupState), Tip = ProtectTip(s.Tip) };

    private StagedCommitRecord RevealStagedCommit(StagedCommitRecord s) =>
        s with
        {
            GroupState = Reveal(s.GroupState, "StagedCommitRecord.GroupState"),
            Tip = RevealTip(s.Tip),
        };

    // -- IGroupStorage --

    public Task PutGroupAsync(GroupRecord group, CancellationToken ct = default) =>
        _inner.PutGroupAsync(ProtectGroup(group), ct);

    public async Task<GroupRecord?> GetGroupAsync(GroupId id, CancellationToken ct = default)
    {
        var g = await _inner.GetGroupAsync(id, ct).ConfigureAwait(false);
        return g is null ? null : RevealGroup(g);
    }

    public async Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken ct = default) =>
        (await _inner.ListGroupsAsync(ct).ConfigureAwait(false)).Select(RevealGroup).ToList();

    public async Task<IReadOnlyList<GroupRecord>> ListLiveGroupsAsync(CancellationToken ct = default) =>
        (await _inner.ListLiveGroupsAsync(ct).ConfigureAwait(false)).Select(RevealGroup).ToList();

    public Task DeleteGroupAsync(GroupId id, CancellationToken ct = default) =>
        _inner.DeleteGroupAsync(id, ct);

    // -- IMessageStorage --

    public Task PutMessageAsync(MessageRecord message, CancellationToken ct = default) =>
        _inner.PutMessageAsync(ProtectMessage(message), ct);

    public async Task<MessageRecord?> GetMessageAsync(MessageId id, CancellationToken ct = default)
    {
        var m = await _inner.GetMessageAsync(id, ct).ConfigureAwait(false);
        return m is null ? null : RevealMessage(m);
    }

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        GroupId groupId, CancellationToken ct = default) =>
        (await _inner.ListMessagesAsync(groupId, ct).ConfigureAwait(false))
            .Select(RevealMessage).ToList();

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesByStateAsync(
        GroupId groupId, MessageRecordState state, CancellationToken ct = default) =>
        (await _inner.ListMessagesByStateAsync(groupId, state, ct).ConfigureAwait(false))
            .Select(RevealMessage).ToList();

    public Task PutTransportSeenAsync(string transportId, CancellationToken ct = default) =>
        _inner.PutTransportSeenAsync(transportId, ct);

    public Task<bool> HasTransportSeenAsync(string transportId, CancellationToken ct = default) =>
        _inner.HasTransportSeenAsync(transportId, ct);

    public Task InvalidateAfterEpochAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default) =>
        _inner.InvalidateAfterEpochAsync(groupId, epoch, ct);

    // -- IOutboundIntentStorage --

    public Task PutIntentAsync(QueuedOutboundIntent intent, CancellationToken ct = default) =>
        _inner.PutIntentAsync(ProtectIntent(intent), ct);

    public async Task<IReadOnlyList<QueuedOutboundIntent>> ListIntentsAsync(
        GroupId groupId, CancellationToken ct = default) =>
        (await _inner.ListIntentsAsync(groupId, ct).ConfigureAwait(false))
            .Select(RevealIntent).ToList();

    public Task DeleteIntentAsync(MessageId id, CancellationToken ct = default) =>
        _inner.DeleteIntentAsync(id, ct);

    public Task ClearIntentsAsync(GroupId groupId, CancellationToken ct = default) =>
        _inner.ClearIntentsAsync(groupId, ct);

    // -- ILeaveRequestStorage (no byte payload) --

    public Task PutLeaveRequestAsync(LeaveRequest request, CancellationToken ct = default) =>
        _inner.PutLeaveRequestAsync(request, ct);

    public Task<LeaveRequest?> GetLeaveRequestAsync(
        GroupId groupId, CancellationToken ct = default) =>
        _inner.GetLeaveRequestAsync(groupId, ct);

    public Task<IReadOnlyList<LeaveRequest>> ListLeaveRequestsAsync(CancellationToken ct = default) =>
        _inner.ListLeaveRequestsAsync(ct);

    public Task ClearLeaveRequestAsync(GroupId groupId, CancellationToken ct = default) =>
        _inner.ClearLeaveRequestAsync(groupId, ct);

    // -- IWelcomeStorage --

    public Task PutWelcomeAsync(WelcomeRecord welcome, CancellationToken ct = default) =>
        _inner.PutWelcomeAsync(ProtectWelcome(welcome), ct);

    public async Task<WelcomeRecord?> GetWelcomeAsync(MessageId id, CancellationToken ct = default)
    {
        var w = await _inner.GetWelcomeAsync(id, ct).ConfigureAwait(false);
        return w is null ? null : RevealWelcome(w);
    }

    public async Task<IReadOnlyList<WelcomeRecord>> ListWelcomesAsync(
        WelcomeRecordState? state = null, CancellationToken ct = default) =>
        (await _inner.ListWelcomesAsync(state, ct).ConfigureAwait(false))
            .Select(RevealWelcome).ToList();

    // -- IKeyPackageStorage --

    public Task PutKeyPackageAsync(KeyPackageRecord record, CancellationToken ct = default) =>
        _inner.PutKeyPackageAsync(ProtectKeyPackage(record), ct);

    public async Task<KeyPackageRecord?> GetKeyPackageAsync(
        string keyPackageRefHex, CancellationToken ct = default)
    {
        var k = await _inner.GetKeyPackageAsync(keyPackageRefHex, ct).ConfigureAwait(false);
        return k is null ? null : RevealKeyPackage(k);
    }

    public async Task<KeyPackageRecord?> GetKeyPackageByEventAsync(
        string eventIdHex, CancellationToken ct = default)
    {
        var k = await _inner.GetKeyPackageByEventAsync(eventIdHex, ct).ConfigureAwait(false);
        return k is null ? null : RevealKeyPackage(k);
    }

    public async Task<IReadOnlyList<KeyPackageRecord>> ListKeyPackagesAsync(
        string? slotId = null,
        KeyPackageRecordState? state = null,
        CancellationToken ct = default) =>
        (await _inner.ListKeyPackagesAsync(slotId, state, ct).ConfigureAwait(false))
            .Select(RevealKeyPackage).ToList();

    public Task<bool> MarkPublishedAsync(
        string keyPackageRefHex, string eventIdHex, CancellationToken ct = default) =>
        _inner.MarkPublishedAsync(keyPackageRefHex, eventIdHex, ct);

    public Task<bool> MarkConsumedAsync(string keyPackageRefHex, CancellationToken ct = default) =>
        _inner.MarkConsumedAsync(keyPackageRefHex, ct);

    public Task<bool> ErasePrivateMaterialAsync(
        string keyPackageRefHex, CancellationToken ct = default) =>
        _inner.ErasePrivateMaterialAsync(keyPackageRefHex, ct);

    public Task<bool> DeleteKeyPackageAsync(
        string keyPackageRefHex, CancellationToken ct = default) =>
        _inner.DeleteKeyPackageAsync(keyPackageRefHex, ct);

    // -- IRoutingIndexStorage (addresses only; see class remarks) --

    public Task PutRoutingAsync(
        byte[] transportGroupId,
        GroupId groupId,
        EpochId firstEpoch,
        CancellationToken ct = default) =>
        _inner.PutRoutingAsync(transportGroupId, groupId, firstEpoch, ct);

    public Task<RoutingIndexRecord?> ResolveAsync(
        byte[] transportGroupId, CancellationToken ct = default) =>
        _inner.ResolveAsync(transportGroupId, ct);

    public Task<IReadOnlyList<RoutingIndexRecord>> ListRoutingAsync(
        GroupId groupId, CancellationToken ct = default) =>
        _inner.ListRoutingAsync(groupId, ct);

    public Task<RoutingIndexRecord?> CurrentRoutingAsync(
        GroupId groupId, CancellationToken ct = default) =>
        _inner.CurrentRoutingAsync(groupId, ct);

    public Task<int> PruneRoutingAsync(
        GroupId groupId, EpochId horizon, CancellationToken ct = default) =>
        _inner.PruneRoutingAsync(groupId, horizon, ct);

    // -- ISnapshotStorage (protected transitively; see class remarks) --

    public Task<string> CreateSnapshotAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default) =>
        _inner.CreateSnapshotAsync(groupId, epoch, ct);

    public Task RollbackToSnapshotAsync(string snapshotName, CancellationToken ct = default) =>
        _inner.RollbackToSnapshotAsync(snapshotName, ct);

    public Task ReleaseSnapshotAsync(string snapshotName, CancellationToken ct = default) =>
        _inner.ReleaseSnapshotAsync(snapshotName, ct);

    public Task<string?> GetSnapshotAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default) =>
        _inner.GetSnapshotAsync(groupId, epoch, ct);

    public Task<IReadOnlyList<string>> ListSnapshotsAsync(
        GroupId groupId, CancellationToken ct = default) =>
        _inner.ListSnapshotsAsync(groupId, ct);

    public Task PruneSnapshotsBeforeAsync(
        GroupId groupId, EpochId oldestRetainedEpoch, CancellationToken ct = default) =>
        _inner.PruneSnapshotsBeforeAsync(groupId, oldestRetainedEpoch, ct);

    // -- IEpochArchiveStorage --

    public Task PutEpochCheckpointAsync(
        EpochCheckpoint checkpoint, CancellationToken ct = default) =>
        _inner.PutEpochCheckpointAsync(ProtectCheckpoint(checkpoint), ct);

    public async Task<EpochCheckpoint?> GetEpochCheckpointAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        var c = await _inner.GetEpochCheckpointAsync(groupId, epoch, ct).ConfigureAwait(false);
        return c is null ? null : RevealCheckpoint(c);
    }

    public async Task<IReadOnlyList<EpochCheckpoint>> ListEpochCheckpointsAsync(
        GroupId groupId, EpochId oldestEpoch, CancellationToken ct = default) =>
        (await _inner.ListEpochCheckpointsAsync(groupId, oldestEpoch, ct).ConfigureAwait(false))
            .Select(RevealCheckpoint).ToList();

    public Task<int> PruneEpochCheckpointsBeforeAsync(
        GroupId groupId, EpochId oldestRetainedEpoch, CancellationToken ct = default) =>
        _inner.PruneEpochCheckpointsBeforeAsync(groupId, oldestRetainedEpoch, ct);

    // -- IEpochStateStorage (staged_commit is a commit digest; see class remarks) --

    public Task PutEpochStateAsync(EpochStateRecord record, CancellationToken ct = default) =>
        _inner.PutEpochStateAsync(record, ct);

    public Task<EpochStateRecord?> GetEpochStateAsync(
        GroupId groupId, CancellationToken ct = default) =>
        _inner.GetEpochStateAsync(groupId, ct);

    public Task<IReadOnlyList<EpochStateRecord>> ListEpochStatesAsync(
        CancellationToken ct = default) =>
        _inner.ListEpochStatesAsync(ct);

    public Task<bool> ClearEpochStateAsync(GroupId groupId, CancellationToken ct = default) =>
        _inner.ClearEpochStateAsync(groupId, ct);

    // -- ICommitPublishAttemptStorage (commit_id is a digest; see class remarks) --

    public Task PutCommitPublishAttemptAsync(
        CommitPublishAttempt attempt, CancellationToken ct = default) =>
        _inner.PutCommitPublishAttemptAsync(attempt, ct);

    public Task<CommitPublishAttempt?> GetCommitPublishAttemptAsync(
        GroupId groupId, CancellationToken ct = default) =>
        _inner.GetCommitPublishAttemptAsync(groupId, ct);

    public Task<IReadOnlyList<CommitPublishAttempt>> ListCommitPublishAttemptsAsync(
        CancellationToken ct = default) =>
        _inner.ListCommitPublishAttemptsAsync(ct);

    public Task<bool> ClearCommitPublishAttemptAsync(
        GroupId groupId, CancellationToken ct = default) =>
        _inner.ClearCommitPublishAttemptAsync(groupId, ct);

    // -- IStagedCommitStorage --

    public Task PutStagedCommitAsync(
        StagedCommitRecord record, CancellationToken ct = default) =>
        _inner.PutStagedCommitAsync(ProtectStagedCommit(record), ct);

    public async Task<StagedCommitRecord?> GetStagedCommitAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        var s = await _inner.GetStagedCommitAsync(groupId, ct).ConfigureAwait(false);
        return s is null ? null : RevealStagedCommit(s);
    }

    public async Task<IReadOnlyList<StagedCommitRecord>> ListStagedCommitsAsync(
        CancellationToken ct = default) =>
        (await _inner.ListStagedCommitsAsync(ct).ConfigureAwait(false))
            .Select(RevealStagedCommit).ToList();

    public Task ClearStagedCommitAsync(GroupId groupId, CancellationToken ct = default) =>
        _inner.ClearStagedCommitAsync(groupId, ct);

    // -- Transactions --

    public Task<IStorageTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        _inner.BeginTransactionAsync(ct);

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
