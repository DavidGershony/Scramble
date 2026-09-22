using System.Runtime.CompilerServices;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Session;

/// <summary>
/// The durable record attached to one live <see cref="MlsGroup"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is attached to the group rather than passed to a method.</b>
/// The staging factories — <see cref="MarmotSelfUpdate.Stage"/>,
/// <see cref="MarmotGroupInvite.Add"/>,
/// <see cref="MarmotGroupLeave.CommitDepartures"/> — take an
/// <see cref="MlsGroup"/> and nothing else, deliberately: they are pure engine
/// operations, and giving them storage would mean staging a commit could fail
/// on I/O long before anybody decided to publish. But a commit staged on a
/// group the session owns has to be written down at the moment it is staged,
/// because that is the only moment the state it produces can be derived. The
/// natural place for the association is a field on <see cref="MlsGroup"/>, and
/// we cannot add one: the MLS library is pinned and out of bounds. So the field
/// lives outside the object, keyed by the object.
/// </para>
/// <para>
/// <b>A group nobody has bound behaves exactly as it did before.</b> Every
/// lookup here answers null for an unbound group, and every call site reads
/// null as "do nothing". That is what keeps the engine additive — the existing
/// callers that stage commits on groups they built themselves are untouched.
/// </para>
/// <para>
/// <b>The table is weak on the group.</b> A session that goes away without
/// closing must not keep a group — and the private key material inside it —
/// alive for the life of the process.
/// </para>
/// <para>
/// <b>The writes here block, and that is a real cost.</b> They are called from
/// <see cref="StagedCommit"/>'s constructor and from
/// <see cref="StagedCommit.Publishing"/>, both synchronous and not changeable
/// without changing every staging factory. The two alternatives are worse: a
/// fire-and-forget write races the send it is supposed to precede, which is
/// worse than no record at all because it looks like one; and no record at all
/// is the gap this subsystem exists to close. The providers the engine ships
/// are synchronous underneath — SQLite has no true async path — so this waits
/// on work that has already finished rather than on a scheduler. <b>A provider
/// whose async is genuinely asynchronous and which resumes on a captured
/// context would deadlock here</b>, and the day one exists is the day this has
/// to move.
/// </para>
/// </remarks>
public sealed class GroupJournal
{
    private static readonly ConditionalWeakTable<MlsGroup, GroupJournal> Bound = new();

    private GroupJournal(
        GroupId groupId, IMarmotStorageProvider storage, Func<DateTimeOffset> clock)
    {
        GroupId = groupId;
        Storage = storage;
        Clock = clock;
    }

    /// <summary>The Marmot id of the group this journal belongs to.</summary>
    public GroupId GroupId { get; }

    internal IMarmotStorageProvider Storage { get; }

    internal Func<DateTimeOffset> Clock { get; }

    /// <summary>
    /// Puts a live group under durable custody, or re-points an existing
    /// binding at it.
    /// </summary>
    /// <remarks>
    /// Idempotent on purpose. A group passes through here every time its state
    /// is archived, which happens repeatedly over its life, so a second bind is
    /// the ordinary case rather than a mistake.
    /// </remarks>
    public static void Bind(
        MlsGroup group,
        GroupId groupId,
        IMarmotStorageProvider storage,
        Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);

        Bound.AddOrUpdate(group, new GroupJournal(groupId, storage, clock));
    }

    /// <summary>Releases a group from durable custody.</summary>
    /// <remarks>
    /// Called when a session closes or replaces its live group. Skipping it
    /// would not corrupt anything — the table is weak — but a group still bound
    /// to a closed session's storage is one a stray staging call could still
    /// write through.
    /// </remarks>
    public static void Unbind(MlsGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Bound.Remove(group);
    }

    /// <summary>The journal for a group, or null when nobody owns it.</summary>
    public static GroupJournal? For(MlsGroup? group) =>
        group is not null && Bound.TryGetValue(group, out GroupJournal? journal) ? journal : null;

    /// <summary>
    /// Derives the state a staged commit produces, and writes it down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deriving it means merging, and there is no other way.</b> The
    /// post-commit state is computed inside
    /// <see cref="MlsGroup.MergePendingCommit"/> and exists nowhere else:
    /// <see cref="MlsGroup.Export"/> writes the current epoch and drops the
    /// pending commit, and the commit's own bytes cannot be replayed by their
    /// author, because MLS encrypts the new path secrets to everyone except the
    /// committer. So the group handed in here is merged and exported, and the
    /// session treats that instance as a throwaway: it keeps the bytes and
    /// points its live group back where it was, at a fresh import of the state
    /// it held before.
    /// </para>
    /// <para>
    /// <b>That is not an early apply.</b> The session's live group stands at
    /// the old epoch until the publish is confirmed, which is exactly the rule
    /// <see cref="StagedCommit"/> exists to enforce. What moves is which object
    /// the session points at, and the instance merged here is the one it stops
    /// pointing at until the relay has spoken.
    /// </para>
    /// <para>
    /// <b>The row is not evidence of a publish</b> and must never be read as
    /// one. It says a commit was prepared; <c>commit_publish_attempts</c> says
    /// what became of it. A recovery that adopted this state without asking
    /// would advance a member into an epoch that, if the crash came before the
    /// send, nobody else can reach.
    /// </para>
    /// </remarks>
    /// <returns>The state the commit produces, exported.</returns>
    internal byte[] Prepare(
        MlsGroup group,
        PublicMessage commit,
        EpochId newEpoch,
        CommitOrderingPriority priority)
    {
        // Read before the merge. The committer is a leaf index into a tree the
        // commit is about to move, and the ordering class resolves against a
        // proposal cache the merge clears -- the same pair of facts the inbound
        // path has to collect either side of an apply.
        byte[] committer = CommitterOf(group);
        var tip = new CommitTip(priority, CommitPublisher.CommitIdOf(commit), committer);

        group.MergePendingCommit();
        byte[] state = group.Export();

        Block(Storage.PutStagedCommitAsync(
            new StagedCommitRecord(GroupId, newEpoch, state, tip, Clock())));

        return state;
    }

    /// <summary>
    /// Records that the commit's bytes are about to reach a transport.
    /// </summary>
    /// <remarks>
    /// <b>The durable form of the <c>Publishing()</c> line.</b> Before it, the
    /// absence of a publish attempt is the positive statement "nobody else can
    /// have seen this commit", and recovery abandons. After it, recovery has to
    /// assume a relay may be serving the commit to every other member, and
    /// abandoning is the one move that cannot be undone.
    /// <see cref="CommitPublisher"/> writes the same row when it is driving;
    /// the write is a replace and the two agree on every field but the
    /// timestamp, so the overlap costs a row rewrite and buys a commit staged
    /// outside the publisher the same protection.
    /// </remarks>
    internal void HandedToTransport(MessageId commitId, EpochId newEpoch) =>
        Block(Storage.PutCommitPublishAttemptAsync(
            CommitPublishAttempt.HandedToTransport(GroupId, commitId, newEpoch, Clock())));

    /// <summary>
    /// Drops the prepared state for a commit that provably never left.
    /// </summary>
    /// <remarks>
    /// Only legal before <c>Publishing()</c>. Afterwards this row is what
    /// stands between a restart and a fork, and a publish that failed to answer
    /// is precisely the case it was written for.
    /// </remarks>
    internal void Abandon() => Block(Storage.ClearStagedCommitAsync(GroupId));

    /// <summary>Who we are, in the terms branch selection compares.</summary>
    /// <remarks>
    /// Ours by construction — we authored the commit — so unlike the inbound
    /// path there is no case where the committing leaf cannot be resolved. A
    /// group where it could not is one we are not a member of, and staging a
    /// commit on it was already impossible.
    /// </remarks>
    private static byte[] CommitterOf(MlsGroup group) =>
        group.GetMembers().First(m => m.leafIndex == group.MyLeafIndex).identity;

    private static void Block(Task work) => work.GetAwaiter().GetResult();
}
