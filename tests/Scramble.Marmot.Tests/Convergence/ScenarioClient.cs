using System.Security.Cryptography;
using System.Text;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Tests.Convergence;

/// <summary>A staged commit awaiting its publication acknowledgement.</summary>
/// <param name="Label">The scenario's publication label.</param>
/// <param name="Staged">The staged commit, not yet applied.</param>
public sealed record PendingPublication(string Label, StagedInvite Staged);

/// <summary>
/// One simulated member, holding real MLS state.
/// </summary>
/// <remarks>
/// <para>
/// The MLS state is genuine — a real <see cref="MlsGroup"/> driven through the
/// same engine entry points production uses. What is simulated is only the
/// network and the scheduler: which messages arrive, and when a client gets to
/// process them.
/// </para>
/// <para>
/// <b>Every epoch is snapshotted before it is left.</b> That is what makes a
/// late-arriving competing commit evaluable at all: to score a branch that
/// forks from epoch N, the client has to return to epoch N and apply it, and an
/// MLS group cannot rewind. <see cref="MlsGroup.Export"/> is the only way back.
/// </para>
/// </remarks>
public sealed class ScenarioClient
{
    private readonly ICipherSuite _cs;
    private readonly Dictionary<ulong, byte[]> _snapshots = [];

    /// <summary>Creates a client with a fresh identity.</summary>
    public ScenarioClient(string name, ICipherSuite cs)
    {
        Name = name;
        _cs = cs;

        var (secret, publicKey) = Bip340.GenerateKeyPair();
        Secret = secret;
        AccountPublicKey = publicKey.ToArray();
    }

    /// <summary>The scenario's name for this client.</summary>
    public string Name { get; }

    /// <summary>The account's secret key.</summary>
    public byte[] Secret { get; }

    /// <summary>The account's public key.</summary>
    public byte[] AccountPublicKey { get; }

    /// <summary>The live group, once created or joined.</summary>
    public MlsGroup? Group { get; private set; }

    /// <summary>The KeyPackage bundle this client can be invited with.</summary>
    public MarmotKeyPackageBundle? Bundle { get; set; }

    /// <summary>Publications staged and not yet acknowledged.</summary>
    public List<PendingPublication> Pending { get; } = [];

    /// <summary>Application payloads this client has read.</summary>
    public List<string> ReceivedPayloads { get; } = [];

    /// <summary>Commits received that could not be applied when they arrived.</summary>
    public List<Envelope> HeldCommits { get; } = [];

    /// <summary>Application messages awaiting a branch that can read them.</summary>
    public List<Envelope> DeferredApp { get; } = [];

    /// <summary>
    /// Every application message this member has seen or sent, kept for replay.
    /// </summary>
    /// <remarks>
    /// <b>Witnesses are computed by replaying this against a branch, not by
    /// remembering which branch we were on when a message arrived.</b> The
    /// second is member-local: two members who received the same messages in a
    /// different order, or who were on different branches at the time, would
    /// count different witnesses for the same branch and select differently.
    /// Replaying a shared log against a materialized branch gives every member
    /// holding the same messages the same answer, which is the only version of
    /// this that converges. Own sends are included — a message we sent on a
    /// branch is evidence for it exactly as anyone else's is.
    /// </remarks>
    public List<Envelope> AppLog { get; } = [];

    /// <summary>The branch this client currently sits on.</summary>
    public string CurrentBranchId { get; private set; } = "genesis";

    /// <summary>The last convergence decision this client made.</summary>
    public BranchSelectionTrace? LastDecision { get; private set; }

    /// <summary>The tip epoch of the branch last selected.</summary>
    public ulong? LastSelectedTipEpoch { get; private set; }

    /// <summary>This client's account key, hex.</summary>
    public string Hex => Convert.ToHexString(AccountPublicKey).ToLowerInvariant();

    /// <summary>Adopts a freshly created or joined group.</summary>
    public void Adopt(MlsGroup group)
    {
        Group = group;
        Snapshot();
    }

    /// <summary>Records the current epoch so a branch can be scored from it later.</summary>
    public void Snapshot()
    {
        if (Group is not null)
            _snapshots[Group.Epoch] = Group.Export();
    }

    /// <summary>Whether an epoch can be returned to.</summary>
    public bool HasSnapshot(ulong epoch) => _snapshots.ContainsKey(epoch);

    /// <summary>A throwaway copy of the group as it was at an epoch.</summary>
    public MlsGroup Restore(ulong epoch) => MlsGroup.Import(_snapshots[epoch], _cs);

    /// <summary>Applies a commit, snapshotting the epoch it leaves behind.</summary>
    public void ApplyCommit(Envelope envelope)
    {
        Snapshot();

        PublicMessage commit = ReadCommit(envelope.Payload);
        Group!.ProcessCommit(commit);

        CurrentBranchId = BranchIdOf(envelope);
        Snapshot();
    }

    /// <summary>Applies our own staged commit once its publication is confirmed.</summary>
    public void ApplyOwn(StagedInvite staged, byte[] commitBytes)
    {
        Snapshot();
        staged.Applied();
        CurrentBranchId = DigestHex(commitBytes);
        Snapshot();
    }



    /// <summary>Records the outcome of a convergence pass.</summary>
    public void RecordDecision(BranchSelectionTrace trace, ulong? selectedTipEpoch)
    {
        LastDecision = trace;
        LastSelectedTipEpoch = selectedTipEpoch;
    }

    /// <summary>
    /// The witnesses a branch has, by replaying the log against it.
    /// </summary>
    /// <remarks>
    /// The probe is a throwaway copy: decrypting advances ratchet state, and
    /// scoring a branch must not consume the keys the live group still needs.
    /// </remarks>
    public IReadOnlyList<AppWitness> WitnessesOn(MlsGroup probe)
    {
        var witnesses = new List<AppWitness>();

        foreach (Envelope envelope in AppLog)
        {
            try
            {
                var (_, senderLeaf) = probe.DecryptApplicationMessage(
                    ReadApplication(envelope.Payload));

                witnesses.Add(new AppWitness(envelope.Epoch, IdentityOf(probe, senderLeaf)));
            }
            catch
            {
                // Not on this branch, or not at an epoch it can reach. Either
                // way it is not evidence for it.
            }
        }

        return witnesses;
    }

    /// <summary>A branch's stable id: the digest of the commit that produced it.</summary>
    public static string BranchIdOf(Envelope envelope) => DigestHex(envelope.Payload);

    /// <summary>
    /// The 32-byte digest a branch is tie-broken on.
    /// </summary>
    /// <remarks>
    /// <b>Unverified against upstream.</b> SHA-256 over the serialized commit is
    /// the natural reading, and neither convergence vector exercises it — one is
    /// decided on <c>tip_committer</c> and the other on the witness rule. A
    /// vector that turns on <c>tip_digest</c> would settle it; until one exists
    /// this is an assumption, and a wrong one only shows up when two members
    /// tie on everything else.
    /// </remarks>
    public static byte[] Digest(byte[] mlsBytes) => SHA256.HashData(mlsBytes);

    /// <summary>The digest, hex-encoded.</summary>
    public static string DigestHex(byte[] mlsBytes) =>
        Convert.ToHexString(Digest(mlsBytes)).ToLowerInvariant();

    /// <summary>Reads a commit out of its MLSMessage framing.</summary>
    public static PublicMessage ReadCommit(byte[] mlsBytes)
    {
        var message = MlsMessage.ReadFrom(new TlsReader(mlsBytes));
        return (PublicMessage)message.Body;
    }

    /// <summary>Frames a handshake message for the relay.</summary>
    public static byte[] FrameHandshake(PublicMessage message) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, message).WriteTo);

    /// <summary>Frames an application message for the relay.</summary>
    public static byte[] FrameApplication(PrivateMessage message) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPrivateMessage, message).WriteTo);

    /// <summary>Reads an application message out of its MLSMessage framing.</summary>
    public static PrivateMessage ReadApplication(byte[] mlsBytes)
    {
        var message = MlsMessage.ReadFrom(new TlsReader(mlsBytes));
        return (PrivateMessage)message.Body;
    }

    /// <summary>The account key of a leaf in a group.</summary>
    public static byte[] IdentityOf(MlsGroup group, uint leafIndex) =>
        group.GetMembers().Single(m => m.leafIndex == leafIndex).identity;

    /// <summary>A signer over this client's identity, for the engine's seams.</summary>
    public IAccountIdentityProofSigner Signer() => new ClientSigner(this);

    private sealed class ClientSigner(ScenarioClient client) : IAccountIdentityProofSigner
    {
        public ReadOnlyMemory<byte> AccountPublicKey => client.AccountPublicKey;

        public Task<byte[]> SignAsync(
            NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(client.Secret, template.ComputeId()));
    }
}
