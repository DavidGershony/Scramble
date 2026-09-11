using DotnetMls.Codec;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Engine.Convergence;

/// <summary>A commit the engine has kept, as convergence sees it.</summary>
/// <param name="Id">Its content-derived id.</param>
/// <param name="SourceEpoch">The epoch it was built from — where it forks.</param>
/// <param name="Wire">The MLS message bytes.</param>
/// <param name="IsOurs">
/// Whether this device authored it. Ours cannot be replayed, which is the whole
/// reason the flag exists — see <see cref="CandidateMaterializer"/>.
/// </param>
public sealed record StoredCommit(
    MessageId Id, EpochId SourceEpoch, byte[] Wire, bool IsOurs);

/// <summary>Why a stored commit did not become a candidate.</summary>
public enum MaterializationRefusal
{
    /// <summary>Its fork epoch is no longer reachable — pruned, or never held.</summary>
    NoSnapshot,

    /// <summary>It does not apply to the state it claims to fork from.</summary>
    DoesNotApply,

    /// <summary>The replay budget ran out before reaching it.</summary>
    BudgetExhausted,

    /// <summary>Ours, and not on the branch we already hold.</summary>
    UnreplayableOwnCommit,
}

/// <summary>One stored commit that was not turned into a candidate.</summary>
public sealed record RefusedCommit(MessageId Id, MaterializationRefusal Reason);

/// <summary>What a materialization pass produced.</summary>
/// <param name="Candidates">Branches that can be scored.</param>
/// <param name="Refused">Commits that could not be, and why.</param>
/// <param name="CommitsApplied">Replays performed, against the budget.</param>
public sealed record MaterializationResult(
    IReadOnlyList<BranchCandidate> Candidates,
    IReadOnlyList<RefusedCommit> Refused,
    int CommitsApplied);

/// <summary>
/// Turning stored commits into branches that can be compared.
/// </summary>
/// <remarks>
/// <para>
/// A commit's tip epoch and committer are not in its header — they are what
/// <i>applying</i> it produces. So a branch cannot be scored without being
/// built, and building one means returning to the epoch it forks from and
/// replaying forward. An MLS group cannot rewind, so that means a fresh copy
/// restored from an archived epoch, never the live group.
/// </para>
/// <para>
/// <b>Everything here runs on throwaway copies.</b> The live group is not
/// touched until <see cref="Reorg"/>, and only once a winner is known. A
/// materializer that mutated the live group to evaluate a branch would leave
/// the member on whichever candidate it happened to try last.
/// </para>
/// <para>
/// <b>The budget is a security bound, not a performance one.</b> Commits arrive
/// from the network, and each one costs a full MLS commit application. Without
/// a cap, a peer can hand us a chain of plausible-looking commits and make us
/// spend the rest of the epoch verifying them — and the cost is paid before we
/// know they are worthless.
/// </para>
/// <para>
/// <b>Our own commits cannot be replayed, and do not need to be.</b> MLS
/// refuses to process a commit it authored: the sender already merged it and
/// holds no path secret encrypted to itself. Upstream solves this by stamping
/// own commits with what replay would have derived. We do not need the stamp,
/// because of where own commits can appear: a commit we authored was built from
/// our own state, so it is on the branch we are already holding, and for that
/// branch the live epoch <i>is</i> the materialization. One appearing anywhere
/// else is refused rather than guessed at.
/// </para>
/// </remarks>
/// <param name="policy">The pinned convergence policy.</param>
/// <param name="restore">
/// Produces a throwaway copy of the group as it was at an epoch, or null when
/// that epoch is no longer retained.
/// </param>
public sealed class CandidateMaterializer(
    ConvergencePolicy policy,
    Func<EpochId, MlsGroup?> restore)
{
    /// <summary>
    /// Commit applications one pass may perform.
    /// </summary>
    /// <remarks>
    /// Derived rather than chosen: the rewind horizon bounds how far back a
    /// branch may fork, so a pass never legitimately needs to replay more than
    /// that many commits per branch. The multiplier allows for several
    /// competing branches at once without letting the total grow with however
    /// many commits a peer chose to send.
    /// </remarks>
    public const int MaxBranchesPerPass = 8;

    private readonly ConvergencePolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    private readonly Func<EpochId, MlsGroup?> _restore =
        restore ?? throw new ArgumentNullException(nameof(restore));

    /// <summary>The replay budget for one pass.</summary>
    public int ReplayBudget => (int)_policy.MaxRewindCommits * MaxBranchesPerPass;

    /// <summary>
    /// Builds every branch the stored commits describe.
    /// </summary>
    /// <param name="live">The group as it stands, for the branch we hold.</param>
    /// <param name="currentBranchId">The id of the branch we are on.</param>
    /// <param name="currentTipPriority">
    /// The ordering class of the commit that put us here.
    /// </param>
    /// <param name="stored">Commits kept for convergence.</param>
    /// <param name="witnessesOn">
    /// The application messages that decrypt against a materialized branch.
    /// Taken as a function of the built state rather than of the branch id,
    /// because a witness is evidence only if the branch can actually read it.
    /// </param>
    /// <remarks>
    /// <b>Why the current tip's class is passed in rather than read.</b> Every
    /// other candidate is classified on the way in, from the proposal cache the
    /// commit's references resolve against. The branch we are already on has no
    /// such moment left: applying its tip is what cleared that cache. So the
    /// class has to be remembered when the commit is applied and handed back
    /// here. There is deliberately no default — a default would be a guess, and
    /// guessing "ordinary" understates our own branch against a peer that knows
    /// better, which is a disagreement rather than a conservative choice.
    /// </remarks>
    public MaterializationResult Materialize(
        MlsGroup live,
        string currentBranchId,
        CommitOrderingPriority currentTipPriority,
        IReadOnlyList<StoredCommit> stored,
        Func<MlsGroup, IReadOnlyList<AppWitness>> witnessesOn)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(witnessesOn);

        var candidates = new List<BranchCandidate>();
        var refused = new List<RefusedCommit>();
        int applied = 0;

        // Chains are found by following source epochs: a commit built from
        // epoch N extends any branch whose tip is N. Ordering by epoch means a
        // chain is always discovered from its root, so no commit is applied
        // before the one it depends on.
        var byForkEpoch = stored
            .GroupBy(c => c.SourceEpoch)
            .OrderBy(g => g.Key.Value)
            .ToList();

        var consumed = new HashSet<MessageId>();

        foreach (var group in byForkEpoch)
        {
            foreach (StoredCommit head in group)
            {
                if (consumed.Contains(head.Id))
                    continue;

                if (head.IsOurs)
                {
                    // On the branch we hold, the live group already is the
                    // materialization; anywhere else it cannot be replayed.
                    refused.Add(new RefusedCommit(
                        head.Id, MaterializationRefusal.UnreplayableOwnCommit));
                    continue;
                }

                if (applied >= ReplayBudget)
                {
                    refused.Add(new RefusedCommit(
                        head.Id, MaterializationRefusal.BudgetExhausted));
                    continue;
                }

                MlsGroup? probe = _restore(head.SourceEpoch);
                if (probe is null)
                {
                    refused.Add(new RefusedCommit(head.Id, MaterializationRefusal.NoSnapshot));
                    continue;
                }

                BranchCandidate? candidate = Extend(
                    probe, head, stored, consumed, refused, witnessesOn, ref applied);

                if (candidate is not null)
                    candidates.Add(candidate);
            }
        }

        // The branch we already hold competes on the same terms. Leaving it out
        // would let a late-arriving commit win by being the only candidate,
        // which is how a group gets talked off a history it has delivered.
        candidates.Add(new BranchCandidate(
            currentBranchId,
            ForkEpochOf(candidates, live),
            live.Epoch,
            currentTipPriority,
            IdentityOfSelf(live),
            DigestOfBranchId(currentBranchId),
            witnessesOn(live)));

        return new MaterializationResult(candidates, refused, applied);
    }

    /// <summary>
    /// Applies a chain of commits to a probe, following source epochs forward.
    /// </summary>
    private BranchCandidate? Extend(
        MlsGroup probe,
        StoredCommit head,
        IReadOnlyList<StoredCommit> stored,
        HashSet<MessageId> consumed,
        List<RefusedCommit> refused,
        Func<MlsGroup, IReadOnlyList<AppWitness>> witnessesOn,
        ref int applied)
    {
        ulong forkEpoch = head.SourceEpoch.Value;
        StoredCommit? next = head;
        byte[]? committer = null;
        byte[] tipWire = head.Wire;

        // The tip's class, not the branch's. A branch is ranked by the commit
        // that ends it, so this is overwritten at every step rather than
        // accumulated -- a privileged commit three steps back does not make an
        // ordinary tip privileged.
        CommitOrderingPriority priority = CommitOrderingPriority.Ordinary;

        while (next is not null)
        {
            if (applied >= ReplayBudget)
            {
                refused.Add(new RefusedCommit(
                    next.Id, MaterializationRefusal.BudgetExhausted));
                break;
            }

            PublicMessage commit;
            try
            {
                var message = MlsMessage.ReadFrom(new TlsReader(next.Wire));
                commit = (PublicMessage)message.Body;

                // Both read before applying, and for the same reason twice over:
                // afterwards the tree has moved and the sender's leaf may belong
                // to somebody else entirely, and the proposal cache that says
                // what this commit's references were has been cleared.
                committer = IdentityOfLeaf(probe, commit.Content.Sender.LeafIndex);
                priority = CommitOrdering.PriorityOf(probe, commit) ?? priority;
                probe.ProcessCommit(commit);
            }
            catch (Exception)
            {
                // A commit that does not apply is not a branch. Refusing it here
                // keeps an unprocessable message out of a comparison it could
                // otherwise win on depth alone.
                refused.Add(new RefusedCommit(next.Id, MaterializationRefusal.DoesNotApply));
                return null;
            }

            applied++;
            consumed.Add(next.Id);
            tipWire = next.Wire;

            var reached = new EpochId(probe.Epoch);
            next = stored.FirstOrDefault(
                c => !c.IsOurs && !consumed.Contains(c.Id) && c.SourceEpoch == reached);
        }

        if (committer is null)
            return null;

        return new BranchCandidate(
            Convert.ToHexString(Digest(tipWire)).ToLowerInvariant(),
            forkEpoch,
            probe.Epoch,
            priority,
            committer,
            Digest(tipWire),
            witnessesOn(probe));
    }

    /// <summary>
    /// Moves the live group onto a different branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two levels, and both must move or neither.</b> The MLS group has to
    /// leave the branch it is on and take the other path, and the engine's
    /// record of what it has delivered has to be invalidated to match. A member
    /// whose MLS state adopted a branch while its message records still
    /// describe the old one shows history that no longer exists and cannot
    /// decrypt the history that does.
    /// </para>
    /// <para>
    /// So the new state is built completely, on a throwaway copy, <i>before</i>
    /// anything is committed to. A failure part-way leaves the caller holding
    /// the group it started with, which is the only outcome that is always
    /// recoverable: staying on a losing branch is a disagreement that later
    /// convergence can resolve, while a half-applied reorg is a group whose
    /// state matches nobody's.
    /// </para>
    /// </remarks>
    /// <returns>The group on the winning branch.</returns>
    /// <exception cref="InvalidOperationException">The branch could not be built.</exception>
    public MlsGroup Reorg(BranchCandidate winner, IReadOnlyList<StoredCommit> stored)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(stored);

        MlsGroup rebuilt = _restore(new EpochId(winner.ForkEpoch))
            ?? throw new InvalidOperationException(
                $"Epoch {winner.ForkEpoch} is no longer retained, so the winning branch "
                + "cannot be rebuilt. The rewind horizon should have refused it earlier.");

        var applied = new HashSet<MessageId>();
        ulong reached = rebuilt.Epoch;

        while (reached < winner.TipEpoch)
        {
            StoredCommit? step = stored.FirstOrDefault(
                c => !c.IsOurs
                     && !applied.Contains(c.Id)
                     && c.SourceEpoch.Value == reached);

            if (step is null)
            {
                throw new InvalidOperationException(
                    $"No stored commit continues the winning branch from epoch {reached} "
                    + $"towards {winner.TipEpoch}.");
            }

            var message = MlsMessage.ReadFrom(new TlsReader(step.Wire));
            rebuilt.ProcessCommit((PublicMessage)message.Body);

            applied.Add(step.Id);
            reached = rebuilt.Epoch;
        }

        if (rebuilt.Epoch != winner.TipEpoch)
        {
            throw new InvalidOperationException(
                $"Rebuilt branch reached epoch {rebuilt.Epoch}, not the {winner.TipEpoch} "
                + "it was scored at.");
        }

        return rebuilt;
    }

    private static ulong ForkEpochOf(IReadOnlyList<BranchCandidate> candidates, MlsGroup live) =>
        candidates.Count == 0 ? live.Epoch : candidates.Min(c => c.ForkEpoch);

    private static byte[] Digest(byte[] wire) =>
        System.Security.Cryptography.SHA256.HashData(wire);

    private static byte[] DigestOfBranchId(string branchId)
    {
        // A branch id is the hex digest of the commit that produced it, so it
        // round-trips. The exception is the genesis pseudo-branch, which has no
        // commit behind it and is hashed instead -- the tie-break needs 32
        // bytes and does not care where they came from, only that every member
        // derives the same ones.
        if (branchId.Length == 64)
        {
            try
            {
                return Convert.FromHexString(branchId);
            }
            catch (FormatException)
            {
                // Not a digest after all; fall through and hash it.
            }
        }

        return Digest(System.Text.Encoding.UTF8.GetBytes(branchId));
    }

    private static byte[] IdentityOfSelf(MlsGroup group) =>
        IdentityOfLeaf(group, group.MyLeafIndex);

    private static byte[] IdentityOfLeaf(MlsGroup group, uint leafIndex) =>
        group.GetMembers().Single(m => m.leafIndex == leafIndex).identity;
}
