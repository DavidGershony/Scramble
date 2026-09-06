using System.Text;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;

namespace Scramble.Marmot.Tests.Convergence;

/// <summary>
/// Runs one of upstream's conformance scenarios against our engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point of this is to be told we are wrong by something we did not
/// write.</b> Branch selection is a consensus rule: agreeing with ourselves
/// proves nothing, because the failure mode is a rule reproduced backwards from
/// a correct reading of upstream's source — which looks right, passes every test
/// its author writes, and diverges only on the cases the rule exists for.
/// </para>
/// <para>
/// What is real here: the MLS state, the commits, the Welcomes, the branch
/// selection. What is simulated: the network, and when a client gets to process
/// what it has been handed.
/// </para>
/// <para>
/// <b>Two fidelity gaps, both deliberate and both bounded by the vectors that
/// currently exist.</b>
/// </para>
/// <list type="number">
/// <item><c>initial_admins</c> is not modelled: our group builder makes the
/// creator the sole admin. The vectors are unaffected, and they say so
/// themselves — <c>convergence-committer-selected</c> expects the decisive rule
/// to be <c>tip_committer</c>, which can only happen if <c>tip_priority</c>
/// tied, so an invite by an admin and an invite by a non-admin must both be
/// Ordinary. A scenario whose outcome turned on admin status would need this
/// built.</item>
/// <item>Messages carry MLS bytes rather than kind-445 envelopes. A commit
/// released after the group has moved on would need its original epoch's
/// exporter secret to peel, which is the unbuilt across-epoch retention — the
/// scenario would fail for a reason unrelated to convergence. The transport is
/// covered against a live peer by the interop suite instead.</item>
/// </list>
/// </remarks>
public sealed class ScenarioRunner
{
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly ScenarioRelay _relay = new();
    private readonly Dictionary<string, ScenarioClient> _clients = [];
    private readonly List<string> _log = [];

    /// <summary>The convergence policy every client applies.</summary>
    public ConvergencePolicy Policy { get; } = ConvergencePolicy.V1;

    /// <summary>What happened, for a failure message.</summary>
    public string Log => string.Join('\n', _log);

    /// <summary>A client by scenario name.</summary>
    public ScenarioClient Client(string name) => _clients[name];

    /// <summary>Runs every step of a scenario in order.</summary>
    public async Task RunAsync(ScenarioVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        foreach (string name in vector.Clients)
        {
            var client = new ScenarioClient(name, _cs);
            client.Bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, client.Signer(), Now);
            _clients[name] = client;
        }

        for (int i = 0; i < vector.Steps.Count; i++)
        {
            ScenarioStep step = vector.Steps[i];
            _log.Add($"{i}: {step.Type}");
            await ApplyAsync(step);
        }
    }

    /// <summary>
    /// Ticks every client until nobody moves, then reports where they landed.
    /// </summary>
    /// <remarks>
    /// Beyond what the vectors script. A scenario stops as soon as it has shown
    /// what it came to show, which usually leaves some members still holding a
    /// commit they have not looked at — so the vectors never state the property
    /// convergence exists for: that everyone ends up in the same place.
    /// </remarks>
    /// <returns>Each client with a group, and the state it settled in.</returns>
    public IReadOnlyList<(string Name, ulong Epoch, string Members)> SettleAll()
    {
        // Bounded rather than "until quiet": a selector that oscillates between
        // two branches would otherwise hang the suite instead of failing it.
        for (int round = 0; round < 8; round++)
        {
            foreach (ScenarioClient client in _clients.Values)
                Tick(client);
        }

        return _clients.Values
            .Where(c => c.Group is not null)
            .Select(c => (
                c.Name,
                c.Group!.Epoch,
                string.Join(
                    ",",
                    c.Group.GetMembers()
                        .Select(m => Convert.ToHexString(m.identity).ToLowerInvariant())
                        .OrderBy(id => id, StringComparer.Ordinal))))
            .ToList();
    }

    private async Task ApplyAsync(ScenarioStep step)
    {
        switch (step.Type)
        {
            case "create_group":
                await CreateGroupAsync(step);
                break;

            case "invite_members":
                InviteMembers(step);
                break;

            case "acknowledge_outbound":
                AcknowledgeOutbound(step);
                break;

            case "withhold_message":
                Withhold(step);
                break;

            case "release_withheld":
                _relay.Release(step.String("label"));
                break;

            case "send_app_message":
                SendAppMessage(step);
                break;

            case "deliver_all":
                // Delivery is pull-based: TakeFor hands each client its batch
                // when it ticks, so this step only marks the point at which
                // everything published so far becomes available.
                break;

            case "tick":
                foreach (string name in step.Strings("clients"))
                    Tick(_clients[name]);
                break;

            case "observe":
                foreach (string name in step.Strings("clients"))
                    Tick(_clients[name]);
                break;

            case "clear_events":
                foreach (string name in step.Strings("clients"))
                    _clients[name].ReceivedPayloads.Clear();
                break;

            default:
                throw new NotSupportedException(
                    $"The scenario uses step '{step.Type}', which this harness does not "
                    + "model. Adding it is the work; pretending it is a no-op would make "
                    + "the vector pass without running what it describes.");
        }
    }

    private async Task CreateGroupAsync(ScenarioStep step)
    {
        ScenarioClient creator = _clients[step.String("creator")];

        CreatedGroup created = await MarmotGroupBuilder.CreateAsync(
            _cs, creator.Signer(), step.String("name"), "", Now, Relays);

        creator.Adopt(created.Group);

        var invitees = step.Strings("invitees").Select(name => _clients[name]).ToList();
        if (invitees.Count == 0)
            return;

        StagedInvite staged = MarmotGroupInvite.Add(
            created.Group, _cs, [.. invitees.Select(i => i.Bundle!.KeyPackage)]);

        string publication = step.StringOrNull("pending") ?? "create";
        PublishWelcomes(creator, staged, invitees, publication);

        creator.Pending.Add(new PendingPublication(publication, staged));
    }

    private void InviteMembers(ScenarioStep step)
    {
        ScenarioClient inviter = _clients[step.String("inviter")];
        var invitees = step.Strings("invitees").Select(name => _clients[name]).ToList();

        StagedInvite staged = MarmotGroupInvite.Add(
            inviter.Group!, _cs, [.. invitees.Select(i => i.Bundle!.KeyPackage)]);

        string publication = step.StringOrNull("pending") ?? step.String("inviter");

        // The commit goes to the members who are already here; the Welcome to
        // the newcomer. Both under one publication label, because a scenario
        // withholds them separately by class.
        _relay.Publish(new Envelope(
            publication,
            EnvelopeClass.Commit,
            inviter.Name,
            Recipient: null,
            inviter.Group!.Epoch,
            ScenarioClient.FrameHandshake(staged.Commit)));

        PublishWelcomes(inviter, staged, invitees, publication);

        inviter.Pending.Add(new PendingPublication(publication, staged));
    }

    private void PublishWelcomes(
        ScenarioClient sender,
        StagedInvite staged,
        IReadOnlyList<ScenarioClient> invitees,
        string publication)
    {
        foreach (ScenarioClient invitee in invitees)
        {
            _relay.Publish(new Envelope(
                publication,
                EnvelopeClass.Welcome,
                sender.Name,
                invitee.Name,
                sender.Group!.Epoch,
                WelcomePublication.Serialize(staged.Welcome!)));
        }
    }

    private void AcknowledgeOutbound(ScenarioStep step)
    {
        ScenarioClient client = _clients[step.String("client")];
        string publication = step.String("publication");

        PendingPublication pending = client.Pending.Single(
            p => string.Equals(p.Label, publication, StringComparison.Ordinal));

        client.Pending.Remove(pending);

        if (!string.Equals(step.String("outcome"), "accepted", StringComparison.Ordinal))
        {
            // Publish-before-apply: a refused publication means the commit never
            // reached anyone, so discarding it is the only safe move.
            pending.Staged.Discard();
            return;
        }

        client.ApplyOwn(
            pending.Staged, ScenarioClient.FrameHandshake(pending.Staged.Commit));

        _log.Add($"   {client.Name} applied {publication}, now at epoch {client.Group!.Epoch}");
    }

    private void Withhold(ScenarioStep step)
    {
        JsonSelector selector = JsonSelector.From(step);
        _relay.Withhold(step.String("label"), selector.Publication, selector.Class);
    }

    private void SendAppMessage(ScenarioStep step)
    {
        ScenarioClient sender = _clients[step.String("sender")];
        string payload = step.String("payload");

        PrivateMessage message = sender.Group!.EncryptApplicationMessage(
            Encoding.UTF8.GetBytes(payload));

        var envelope = new Envelope(
            $"app:{payload}",
            EnvelopeClass.App,
            sender.Name,
            Recipient: null,
            sender.Group.Epoch,
            ScenarioClient.FrameApplication(message));

        _relay.Publish(envelope);

        // Our own send is evidence for the branch we sent it on, exactly as
        // anyone else's is. Leaving it out would make a member unable to
        // corroborate its own history and let it be talked off a branch it
        // demonstrably participated in.
        sender.AppLog.Add(envelope);
    }

    /// <summary>
    /// Hands a client its pending delivery and lets it settle.
    /// </summary>
    /// <remarks>
    /// <b>Order matters, and it is the order a real client has to use.</b>
    /// Application messages are encrypted to a particular branch at a particular
    /// epoch, so a member must know which branch it is on before it can read
    /// any of them. Reading first and converging afterwards discards exactly the
    /// traffic that was addressed to the branch being adopted — and in a
    /// scenario where that traffic is the witness evidence, it silently changes
    /// which branch wins.
    /// </remarks>
    private void Tick(ScenarioClient client)
    {
        foreach (Envelope envelope in _relay.TakeFor(client.Name))
        {
            switch (envelope.Class)
            {
                case EnvelopeClass.Welcome:
                    Join(client, envelope);
                    break;

                case EnvelopeClass.Commit:
                    client.HeldCommits.Add(envelope);
                    break;

                case EnvelopeClass.App:
                    client.AppLog.Add(envelope);
                    client.DeferredApp.Add(envelope);
                    break;
            }
        }

        Converge(client);

        // Whatever still will not decrypt is kept rather than dropped: it may
        // belong to a branch this client has not been shown yet, and a message
        // discarded here is one no later commit can recover.
        foreach (Envelope envelope in client.DeferredApp.ToList())
        {
            if (ReadApplication(client, envelope))
                client.DeferredApp.Remove(envelope);
        }
    }

    private void Join(ScenarioClient client, Envelope envelope)
    {
        if (client.Group is not null)
            return;

        MarmotKeyPackageBundle bundle = client.Bundle!;

        client.Adopt(MlsGroup.ProcessWelcome(
            _cs,
            ReadWelcome(envelope.Payload),
            bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create()));

        _log.Add($"   {client.Name} joined at epoch {client.Group!.Epoch}");
    }

    /// <summary>Reads a Welcome out of its MLSMessage framing.</summary>
    private static Welcome ReadWelcome(byte[] mlsBytes)
    {
        var message = MlsMessage.ReadFrom(new DotnetMls.Codec.TlsReader(mlsBytes));
        return (Welcome)message.Body;
    }

    /// <summary>Reads one application message, if this client can.</summary>
    /// <returns><c>true</c> when it decrypted.</returns>
    private bool ReadApplication(ScenarioClient client, Envelope envelope)
    {
        if (client.Group is null)
            return false;

        try
        {
            var (plaintext, senderLeaf) = client.Group.DecryptApplicationMessage(
                ScenarioClient.ReadApplication(envelope.Payload));

            client.ReceivedPayloads.Add(Encoding.UTF8.GetString(plaintext));
            _ = senderLeaf;
            return true;
        }
        catch (Exception ex)
        {
            // A message for a branch or epoch this client is not on. Kept for a
            // later tick rather than thrown: the scenario decides whether it
            // ever mattered.
            _log.Add($"   {client.Name} deferred an app message: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves whatever competing histories this client is holding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A held commit is one we could not simply apply — either it races another
    /// for our current epoch, or it forks from an epoch we have already left.
    /// Both are the same question: which of these histories does the group keep?
    /// </para>
    /// <para>
    /// Scoring a branch means <i>building</i> it, because a branch's tip epoch
    /// and committer are not in the commit's header — they are what applying it
    /// produces. So each candidate is materialized against a restored snapshot
    /// of its fork epoch, on a throwaway copy. The live group is only moved once
    /// a winner is known.
    /// </para>
    /// </remarks>
    private void Converge(ScenarioClient client)
    {
        if (client.Group is null || client.HeldCommits.Count == 0)
            return;

        var candidates = new List<BranchCandidate>();
        var byId = new Dictionary<string, Envelope>(StringComparer.Ordinal);

        foreach (Envelope held in client.HeldCommits.ToList())
        {
            if (!client.HasSnapshot(held.Epoch))
            {
                _log.Add(
                    $"   {client.Name} cannot reach epoch {held.Epoch} to score a branch");
                continue;
            }

            BranchCandidate? candidate = Materialize(client, held);
            if (candidate is null)
                continue;

            candidates.Add(candidate);
            byId[candidate.Id] = held;
        }

        if (candidates.Count == 0)
            return;

        // The branch we are already on competes on the same terms. Without it a
        // late-arriving commit would win by being the only candidate, which is
        // how a group gets talked off a history it has already delivered.
        ulong forkEpoch = candidates.Min(c => c.ForkEpoch);

        if (client.Group.Epoch > forkEpoch)
        {
            candidates.Add(new BranchCandidate(
                client.CurrentBranchId,
                forkEpoch,
                client.Group.Epoch,
                CommitOrderingPriority.Ordinary,
                client.AccountPublicKey,
                Convert.FromHexString(client.CurrentBranchId),
                client.WitnessesOn(client.Restore(client.Group.Epoch))));
        }

        BranchSelectionTrace trace = BranchSelectionAudit.SelectCanonicalTraced(
            client.Group.Epoch, candidates, Policy);

        BranchCandidate? winner = candidates.SingleOrDefault(
            c => string.Equals(c.Id, trace.SelectedBranchId, StringComparison.Ordinal));

        client.RecordDecision(trace, winner?.TipEpoch);
        _log.Add(
            $"   {client.Name} converged on {trace.SelectedBranchId?[..8]} "
            + $"(tip {winner?.TipEpoch}, decisive "
            + $"{trace.RuleTrace.FirstOrDefault(r => r.Decisive)?.RuleName ?? "none"})");

        client.HeldCommits.Clear();

        if (winner is null
            || string.Equals(winner.Id, client.CurrentBranchId, StringComparison.Ordinal))
        {
            return;
        }

        // Adopting a different branch means going back to the fork and taking
        // the other path. Every epoch after the fork is discarded, which is what
        // makes the rewind horizon a real limit rather than a formality.
        client.Adopt(client.Restore(winner.ForkEpoch));
        client.ApplyCommit(byId[winner.Id]);
    }

    /// <summary>
    /// Builds a candidate by applying a commit to a restored fork epoch.
    /// </summary>
    /// <returns>The candidate, or null when the commit does not apply at all.</returns>
    private BranchCandidate? Materialize(ScenarioClient client, Envelope held)
    {
        MlsGroup probe = client.Restore(held.Epoch);
        PublicMessage commit = ScenarioClient.ReadCommit(held.Payload);

        byte[] committer;
        try
        {
            committer = ScenarioClient.IdentityOf(probe, commit.Content.Sender.LeafIndex);
            probe.ProcessCommit(commit);
        }
        catch (Exception ex)
        {
            // A commit that cannot be applied is not a branch. Refusing it here
            // keeps an unprocessable message out of a comparison it could
            // otherwise win.
            _log.Add($"   {client.Name} refused a candidate: {ex.Message}");
            return null;
        }

        string id = ScenarioClient.BranchIdOf(held);

        return new BranchCandidate(
            id,
            held.Epoch,
            probe.Epoch,
            CommitOrderingPriority.Ordinary,
            committer,
            ScenarioClient.Digest(held.Payload),
            client.WitnessesOn(probe));
    }

    private readonly record struct JsonSelector(string Publication, EnvelopeClass Class)
    {
        public static JsonSelector From(ScenarioStep step)
        {
            var selector = step.Raw.GetProperty("selector");
            string publication = selector.GetProperty("publication").GetString()!;
            string cls = selector.GetProperty("class").GetString()!;

            return new JsonSelector(publication, cls switch
            {
                "commit" => EnvelopeClass.Commit,
                "welcome" => EnvelopeClass.Welcome,
                "app" => EnvelopeClass.App,
                _ => throw new NotSupportedException($"Unknown message class '{cls}'."),
            });
        }
    }
}
