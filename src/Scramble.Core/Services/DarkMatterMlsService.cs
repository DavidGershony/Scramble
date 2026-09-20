using System.Text;
using System.Text.Json;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Marmot;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Engine.Session;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
// The contract's KeyPackage is Core's model; the engine's is the MLS struct.
// Aliased rather than qualified at every use, because the ambiguity is between
// two types that mean genuinely different things and a reader has to be able
// to tell which one a signature is talking about.
using KeyPackage = Scramble.Core.Models.KeyPackage;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;
using MlsKeyPackage = DotnetMls.Types.KeyPackage;
using MlsWelcomeBody = DotnetMls.Types.Welcome;

namespace Scramble.Core.Services;

/// <summary>
/// <see cref="IMlsService"/> over the Dark Matter engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing constructs this yet, and that is deliberate.</b> P11 step 2 lands
/// the second implementation; flipping the registration is a separate commit,
/// because that is the moment I5's freeze starts on the desktop head.
/// </para>
/// <para>
/// <b>It is not a transcription, and the places it is not are the interesting
/// ones.</b> <c>IMlsService</c> is service-shaped — a caller stages a commit,
/// publishes the bytes itself, and comes back later to say what happened. The
/// session layer is the opposite shape: <see cref="CommitPublisher"/> owns the
/// publish line end to end, because the one thing a member must never do is
/// clear a commit a relay may already hold. Neither model is wrong; they simply
/// disagree about who decides when a commit lands. Every member below that had
/// to bridge that gap says how, in its own remarks. Every member that could not
/// be bridged throws <see cref="NotSupportedException"/> with the reason rather
/// than inventing behaviour — a member quietly given something plausible is far
/// worse here than one that refuses out loud, because this is the layer the
/// whole app sits on.
/// </para>
/// <para>
/// <b>Sessions come from <see cref="MarmotSessionHost.SessionForAsync"/>, never
/// <c>OpenAsync</c>.</b> Two sessions over one group each own a live
/// <c>MlsGroup</c> and each write it down, so whichever writes second silently
/// discards the other's epoch — a fork with no peer involved and nothing to
/// detect it. The host's cache is the ownership, not a performance device.
/// </para>
/// <para>
/// <b>One gate around everything.</b> A <c>MarmotSession</c> owns mutable,
/// order-sensitive ratchet state and the host's session cache is a plain
/// dictionary; <see cref="InboundFanIn"/> says plainly that it must be driven
/// from one loop. <c>IMlsService</c> is called from at least the UI thread and
/// the Nostr subscription loop, so the serialisation has to happen somewhere,
/// and here is the only place that can see both.
/// </para>
/// </remarks>
public sealed class DarkMatterMlsService : IMlsService, IDisposable
{
    private readonly ILogger<DarkMatterMlsService> _logger;
    private readonly IMarmotStorageProvider _storage;
    private readonly ICipherSuite _cipherSuite = new CipherSuite0x0001();
    private readonly Func<DateTimeOffset> _clock;
    private readonly IAccountIdentityProofSigner? _injectedProofSigner;
    private readonly NostrGroupPeeler _peeler;

    /// <summary>
    /// Serialises every entry point. See the class remarks.
    /// </summary>
    /// <remarks>
    /// A <see cref="SemaphoreSlim"/> rather than a <c>lock</c> because most of
    /// what it protects is awaited. Public members take it and private cores do
    /// not, so nothing here can wait on a gate its own caller already holds.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MarmotSessionHost? _host;
    private IAccountIdentityProofSigner? _proofSigner;
    private string? _publicKeyHex;

    /// <param name="storage">
    /// The engine's durable store. Supplied rather than constructed, because
    /// where it lives and how it is encrypted is a platform decision and this
    /// class has no business making it.
    /// </param>
    /// <param name="proofSigner">
    /// Signs account-identity proofs (component <c>0x8009</c>). Optional: when
    /// omitted, <see cref="InitializeAsync"/> builds a local one from the
    /// private key it is handed. See <see cref="SetNostrEventSigner"/> for why
    /// the external-signer case cannot be served through
    /// <see cref="INostrEventSigner"/>.
    /// </param>
    /// <param name="clock">Injectable for tests.</param>
    public DarkMatterMlsService(
        IMarmotStorageProvider storage,
        IAccountIdentityProofSigner? proofSigner = null,
        Func<DateTimeOffset>? clock = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _injectedProofSigner = proofSigner;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _peeler = new NostrGroupPeeler();
        _logger = LoggingConfiguration.CreateLogger<DarkMatterMlsService>();
    }

    /// <inheritdoc />
    public string? LastEncryptedRumorEventId { get; private set; }

    public void Dispose() => _gate.Dispose();

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Binds an identity and builds the session host.
    /// </summary>
    /// <remarks>
    /// <b>The session layer has no equivalent of this, and the difference is
    /// not cosmetic.</b> A <see cref="MarmotSessionHost"/> is constructed with
    /// everything it needs and never re-identified; an account switch is a new
    /// host over a different store. So this member is where the service's
    /// "initialize, later reset, later initialize again" lifecycle is
    /// translated into "build a host, drop it, build another". The storage
    /// provider outlives all of that, which is why it is a constructor
    /// argument and this is not.
    /// <para>
    /// <paramref name="privateKeyHex"/> may be empty — <c>MainViewModel</c>
    /// passes an empty string for external-signer users. That is accepted here
    /// and refused later, by the members that actually need to sign, so that
    /// logging in with a remote signer does not fail at startup for a capability
    /// most sessions never use.
    /// </para>
    /// </remarks>
    public async Task InitializeAsync(string privateKeyHex, string publicKeyHex)
    {
        ArgumentNullException.ThrowIfNull(privateKeyHex);
        ArgumentNullException.ThrowIfNull(publicKeyHex);

        await _gate.WaitAsync();
        try
        {
            _publicKeyHex = publicKeyHex.ToLowerInvariant();

            _proofSigner = _injectedProofSigner
                ?? (privateKeyHex.Length == 64
                    ? new LocalAccountProofSigner(privateKeyHex, _publicKeyHex)
                    : null);

            _host = new MarmotSessionHost(
                _storage,
                _cipherSuite,
                CallerPublishes.Instance,
                messages: null,
                peeler: _peeler,
                ConvergencePolicy.V1,
                _clock);

            // Before anything is opened, and exactly once per host. It replays
            // every stored epoch state in one pass, so a group mid-publish comes
            // back refusing to ingest rather than silently applying somebody
            // else's commit over our own.
            int restored = await _host.RestoreAsync();

            _logger.LogInformation(
                "DarkMatter MLS initialised for {PubKey}, {Restored} epoch states restored, signer={Signer}",
                _publicKeyHex[..Math.Min(16, _publicKeyHex.Length)],
                restored,
                _proofSigner is null ? "none" : "local");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the host and the identity. The store is left alone.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not a delete.</b> Erasing KeyPackage private material on
    /// logout would make every Welcome already addressed to this device
    /// unopenable, and the material is the only thing that can open them. The
    /// old service made the same choice for the same reason.
    /// <para>
    /// The session cache is cleared rather than closed: closing writes each
    /// session's live state, and a logout is not a moment to be writing group
    /// state from objects nobody is going to read again.
    /// </para>
    /// </remarks>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _host?.Clear();
            _host = null;
            _proofSigner = null;
            _publicKeyHex = null;
            LastEncryptedRumorEventId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------- KeyPackages

    /// <summary>
    /// Builds a KeyPackage, stores its private material, and hands back the
    /// event the caller must publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine's own publisher — <see cref="KeyPackagePublisher"/> — builds,
    /// stores <i>and</i> publishes, and refuses to hand over a bundle it has not
    /// published. It is not used here: this contract's caller publishes, so the
    /// publisher's whole reason for existing (the ordering between persisting
    /// private material and putting bytes on a relay) belongs to somebody else.
    /// What is borrowed is the part that matters —
    /// <see cref="IKeyPackageStorage.PutKeyPackageAsync"/> is awaited before the
    /// caller is given anything to publish, so there can be no KeyPackage on a
    /// relay whose material this device never wrote down.
    /// </para>
    /// <para>
    /// <b>The record is left unbound to an event id, and that is a real gap.</b>
    /// A Welcome names the KeyPackage it consumed by its kind-30443 event id,
    /// and <see cref="IKeyPackageStorage.MarkPublishedAsync"/> is what binds the
    /// two — but this contract has no member through which a caller can say
    /// "I published it, here is the id". See <see cref="ProcessWelcomeAsync"/>
    /// for what that costs.
    /// </para>
    /// <para>
    /// <b>The bytes differ from the old engine's.</b> <c>Data</c> is the
    /// MLSMessage-framed KeyPackage, which is what MIP-00 puts in the event
    /// content and what every interop test is green against; the old service
    /// published a bare <c>KeyPackage</c> struct. Nothing bridges the two, and
    /// nothing needs to: existing groups are abandoned by the cutover plan.
    /// </para>
    /// </remarks>
    public async Task<KeyPackage> GenerateKeyPackageAsync()
    {
        await _gate.WaitAsync();
        try
        {
            RequireHost();
            IAccountIdentityProofSigner signer = RequireProofSigner();
            string account = RequireIdentity();

            ulong now = (ulong)_clock().ToUnixTimeSeconds();

            MarmotKeyPackageBundle bundle = await MarmotKeyPackageBuilder.CreateAsync(
                _cipherSuite, signer, now);

            // The slot a rotation replaces in place, derived from the newest
            // record rather than kept in a table of its own: a slot with no
            // KeyPackage in it is not a thing worth remembering, and two places
            // to record one value is two places to disagree.
            string slotId = await CurrentSlotIdAsync();

            await _storage.PutKeyPackageAsync(
                bundle.ToRecord(slotId, _clock()));

            IReadOnlyList<IReadOnlyList<string>> tags = KeyPackageEvent.BuildTags(
                slotId,
                bundle.KeyPackageRefHex,
                bundle.CipherSuites,
                bundle.MlsExtensions,
                bundle.MlsProposals,
                bundle.AppComponents);

            var keyPackage = KeyPackage.Create(
                account, bundle.PublishedBytes, _cipherSuite.Id);

            keyPackage.SlotId = slotId;
            keyPackage.NostrTags = tags.Select(t => t.ToList()).ToList();
            keyPackage.ExpiresAt = DateTimeOffset
                .FromUnixTimeSeconds(checked((long)bundle.Lifetime.NotAfter)).UtcDateTime;

            _logger.LogInformation(
                "DarkMatter: generated KeyPackage {Ref} in slot {Slot}",
                bundle.KeyPackageRefHex[..16], slotId[..16]);

            return keyPackage;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public int GetStoredKeyPackageCount() =>
        Blocking(async () =>
            (await _storage.ListKeyPackagesAsync()).Count(r => r.CanConsume));

    /// <inheritdoc />
    public bool HasKeyMaterialForKeyPackage(byte[] keyPackageData)
    {
        ArgumentNullException.ThrowIfNull(keyPackageData);

        return Blocking(async () =>
            (await _storage.ListKeyPackagesAsync())
                .Any(r => r.CanConsume && r.PublicKeyPackage.AsSpan().SequenceEqual(keyPackageData)));
    }

    /// <inheritdoc />
    public string? GetLocalKeyPackageSlotId() =>
        Blocking(async () =>
        {
            var records = await _storage.ListKeyPackagesAsync();
            return records.Count > 0 ? records[^1].SlotId : null;
        });

    /// <summary>
    /// Always false, and the reason is not "unimplemented".
    /// </summary>
    /// <remarks>
    /// <b>The condition this exists to repair cannot arise here.</b> Its job is
    /// to adopt a <c>d</c> tag when the local slot id is null but local
    /// KeyPackage bytes are already on a relay — a v3 state-migration artefact
    /// of the old service, whose slot id lived in an exportable service-state
    /// blob separate from the KeyPackages it named. The engine derives the slot
    /// from the newest KeyPackage record, so the two cannot come apart: if there
    /// is a record there is a slot, and if there is no record there is nothing
    /// to adopt a slot onto. Returning true would claim an adoption that did not
    /// happen.
    /// <para>
    /// Not a silent no-op: the contract's own wording is "false if already set
    /// or no match found", and both of those are exactly what this answers.
    /// </para>
    /// </remarks>
    public bool TryReconcileSlotId(IEnumerable<KeyPackage> relayKeyPackages)
    {
        ArgumentNullException.ThrowIfNull(relayKeyPackages);
        return false;
    }

    // ------------------------------------------------------------------ groups

    /// <summary>
    /// Creates a group and takes it into durable custody.
    /// </summary>
    /// <remarks>
    /// <b>The session <see cref="MarmotSessionHost.AdoptAsync"/> returns is
    /// thrown away on purpose.</b> Adopt writes the record, captures the epoch
    /// and registers the routing address, but the session it hands back is not
    /// in the host's cache — so keeping it would leave two owners the moment
    /// anything else asked for the group, which is the fork
    /// <c>SessionForAsync</c> exists to prevent. The canonical session is
    /// fetched immediately afterwards, from the record Adopt just wrote.
    /// </remarks>
    public async Task<MlsGroupInfo> CreateGroupAsync(string groupName, string[] relayUrls)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        ArgumentNullException.ThrowIfNull(relayUrls);

        if (relayUrls.Length == 0)
        {
            throw new ArgumentException(
                "A group must name at least one relay: the routing component has nowhere "
                + "to point and no peer could address the group.",
                nameof(relayUrls));
        }

        await _gate.WaitAsync();
        try
        {
            MarmotSessionHost host = RequireHost();
            IAccountIdentityProofSigner signer = RequireProofSigner();

            CreatedGroup created = await MarmotGroupBuilder.CreateAsync(
                _cipherSuite,
                signer,
                groupName,
                description: string.Empty,
                now: (ulong)_clock().ToUnixTimeSeconds(),
                relays: relayUrls);

            await host.AdoptAsync(created.ToRecord(_clock()), created.Group);

            var groupId = new GroupId(created.GroupId);
            MarmotSession session = await RequireSessionAsync(groupId);

            return InfoOf(groupId, session.Group);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MlsGroupInfo?> GetGroupInfoAsync(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await _gate.WaitAsync();
        try
        {
            var id = new GroupId(groupId);
            MarmotSession? session = (await RequireHost().SessionForAsync(id)).Session;

            return session is null ? null : InfoOf(id, session.Group);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public byte[]? GetNostrGroupId(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        return Blocking(async () =>
        {
            MarmotSession session = await RequireSessionAsync(new GroupId(groupId));

            try
            {
                return GroupMessages.TransportGroupId(session.Group);
            }
            catch (AppComponentException)
            {
                // A group with no 0x8004 component has no address. Null rather
                // than a throw, matching the contract's own wording -- and
                // matching SyncRoutingAsync, which skips such a group rather
                // than failing whatever operation happened to notice.
                return null;
            }
        });
    }

    /// <inheritdoc />
    public List<string> GetAdminPubkeys(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        return Blocking(async () =>
        {
            MarmotSession session = await RequireSessionAsync(new GroupId(groupId));
            byte[]? encoded = GroupComponent(session.Group, AppComponent.GroupAdminPolicy);

            if (encoded is null)
                return new List<string>();

            return AdminPolicy.Decode(encoded).Admins
                .Select(a => Convert.ToHexString(a).ToLowerInvariant())
                .ToList();
        });
    }

    /// <inheritdoc />
    public byte[] GetMediaExporterSecret(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        return Blocking(async () =>
        {
            MarmotSession session = await RequireSessionAsync(new GroupId(groupId));
            return session.Group.ExportSecret("marmot", "encrypted-media"u8.ToArray(), 32);
        });
    }

    /// <summary>
    /// The group's exported MLS state.
    /// </summary>
    /// <remarks>
    /// Read-only and therefore harmless, unlike its counterpart —
    /// see <see cref="ImportGroupStateAsync"/>.
    /// </remarks>
    public async Task<byte[]> ExportGroupStateAsync(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await _gate.WaitAsync();
        try
        {
            MarmotSession session = await RequireSessionAsync(new GroupId(groupId));
            return session.Group.Export();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Refused.</summary>
    /// <remarks>
    /// <b>Writing a group's MLS state from outside is the silent fork the
    /// session layer is built to make impossible.</b> The engine's durability
    /// is not a thing the caller does for it: <c>GroupRecord.LiveState</c>
    /// advances only when a commit's fate is settled, a staged commit of ours
    /// lives in its own row so that abandoning it has something untouched to
    /// come back to, and hydration reconciles the two at open. An import here
    /// would overwrite the state a cached session is still holding, and the
    /// loser's epoch would go with no error and nothing to detect it.
    /// <para>
    /// It has no production caller — only tests, and
    /// <see cref="ExportGroupStateAsync"/>'s round trip is what they were
    /// testing. If a real need appears, the honest shape is a host-level
    /// operation that forgets the session first and rebuilds from the imported
    /// bytes, not a write behind a live owner's back.
    /// </para>
    /// </remarks>
    public Task ImportGroupStateAsync(byte[] groupId, byte[] state) =>
        throw new NotSupportedException(
            "The Dark Matter engine owns group-state durability: LiveState advances only "
            + "when a commit is settled, and a staged commit has a row of its own so that "
            + "abandoning it is recoverable. Importing state from outside would overwrite "
            + "what a live session holds, and the discarded epoch would be unrecoverable "
            + "and silent.");

    /// <summary>Refused.</summary>
    /// <remarks>
    /// <b>There is nothing honest this can be.</b> The old service's "service
    /// state" is a serialised copy of <c>ManagedMlsService</c>'s own fields —
    /// the MLS signing keypair, the stored KeyPackages with their init and HPKE
    /// private keys, the slot id, per-KeyPackage consumption timestamps —
    /// because those lived in memory on that object and had nowhere else to go.
    /// In the engine every one of them is already durable and already
    /// schema'd: KeyPackages and their material are rows in
    /// <see cref="IKeyPackageStorage"/>, the slot is derived from them, and the
    /// leaf signature key lives inside each group's exported state. There is no
    /// remaining in-memory state to export.
    /// <para>
    /// Returning null — which the contract permits for "not supported or no
    /// state to export" — was considered and rejected. Null is indistinguishable
    /// from "this identity has nothing yet", and
    /// <c>MainViewModel</c>/<c>SettingsViewModel</c> pair export with import on
    /// paths where a silent null would look like a successful save of an empty
    /// state. A refusal that names the reason is the only answer that cannot be
    /// mistaken for success.
    /// </para>
    /// </remarks>
    public Task<byte[]?> ExportServiceStateAsync() =>
        throw new NotSupportedException(
            "There is no service-level state to export. Everything the old blob carried — "
            + "signing keys, KeyPackage private material, the publication slot — is durable "
            + "in the engine's own store, so an export here would either be empty or a "
            + "second, divergent copy of rows that already exist.");

    /// <summary>Refused. See <see cref="ExportServiceStateAsync"/>.</summary>
    /// <remarks>
    /// The destructive half of the pair, and the more dangerous one:
    /// <c>ManagedMlsService.ImportServiceStateAsync</c> begins by clearing the
    /// stored KeyPackage list, which is the bug
    /// <c>KeyPackageAuditPersistenceTests</c> exists for. Accepting a blob here
    /// would mean deciding what it does to rows the engine already owns, and
    /// every answer to that is invented.
    /// </remarks>
    public Task ImportServiceStateAsync(byte[] state) =>
        throw new NotSupportedException(
            "There is no service-level state to import. The engine's store is the source of "
            + "truth for KeyPackage material and the publication slot; applying an external "
            + "blob over it would mean deciding whether it adds to or replaces rows that are "
            + "already authoritative, and neither answer is one this layer can make.");

    // ----------------------------------------------------------------- commits

    /// <summary>Refused.</summary>
    /// <remarks>
    /// <b>Auto-merge is the one move that cannot be undone.</b> This member
    /// applies the commit locally and then hands the caller bytes to publish; if
    /// the publish fails, the committer is alone in an epoch nobody else can
    /// reach, and MLS refuses to let a member process a commit it authored, so
    /// the bytes coming back off a relay are no help. The engine has no path
    /// that does this — <see cref="StagedCommit"/> exists precisely to make
    /// publish-before-apply the only order available. Use
    /// <see cref="StageAddMemberAsync"/>.
    /// </remarks>
    public Task<MlsWelcome> AddMemberAsync(byte[] groupId, KeyPackage keyPackage) =>
        throw new NotSupportedException(
            "AddMemberAsync applies the commit before it is published, which forks the "
            + "committer into an epoch nobody can reach if the publish then fails — and MLS "
            + "will not let a member re-process a commit it authored, so that fork is "
            + "permanent. Use StageAddMemberAsync, publish CommitData, then MergeStagedAsync.");

    /// <summary>Refused. See <see cref="AddMemberAsync"/>.</summary>
    public Task<byte[]> RemoveMemberAsync(byte[] groupId, string memberPublicKey) =>
        throw new NotSupportedException(
            "RemoveMemberAsync applies the commit before it is published. Use "
            + "StageRemoveMemberAsync, publish the returned envelope, then MergeStagedAsync.");

    /// <summary>
    /// Stages an add-member commit and hands back the envelope to publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where the two models are reconciled, so read it carefully.</b>
    /// <see cref="MarmotSession.CommitAsync"/> owns the whole publish line: it
    /// writes the durable attempt row, declares the commit handed to a
    /// transport, sends, and resolves the result. The transport it is given here
    /// is <see cref="CallerPublishes"/>, which captures the envelope and answers
    /// <see cref="CommitPublishOutcome.Indeterminate"/>. That is not a
    /// placeholder — it is the literally true answer. At the moment the bytes
    /// leave this method nobody knows whether a relay will take them, and
    /// <c>Indeterminate</c> is exactly the engine's word for that. Everything
    /// stays: the staged state, the attempt row, the pending epoch state, and
    /// the group still at its old epoch. That is the same state the contract
    /// calls "staged but not merged".
    /// </para>
    /// <para>
    /// <b>The caller's later answer is then written as the transport's answer</b>
    /// — see <see cref="MergeStagedAsync"/> and <see cref="ClearStagedAsync"/>,
    /// which resolve the row and let the engine's own hydration decide what to
    /// do with the staged commit. The decision procedure is unchanged; only the
    /// moment the answer arrives is.
    /// </para>
    /// <para>
    /// <b><c>CommitData</c> means something different from before.</b> It is now
    /// a complete, signed kind-445 event, ready to publish as-is. It used to be
    /// raw MIP-03 ciphertext that the caller passed through
    /// <see cref="EncryptCommitAsync"/>. The change is forced: the envelope is
    /// sealed under the <i>pre-commit</i> exporter secret, which is only
    /// readable while the commit is staged, and the engine's peeler seals and
    /// signs in one step so there is no half-wrapped intermediate to hand out.
    /// <c>MessageService</c> has to stop calling <c>EncryptCommitAsync</c> on
    /// it; that is a step-3 change and it is named in the step-2 report.
    /// </para>
    /// </remarks>
    public Task<MlsWelcome> StageAddMemberAsync(byte[] groupId, KeyPackage keyPackage) =>
        StageAddMemberCoreAsync(groupId, keyPackage);

    private async Task<MlsWelcome> StageAddMemberCoreAsync(byte[] groupId, KeyPackage keyPackage)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(keyPackage);

        if (string.IsNullOrEmpty(keyPackage.EventJson))
        {
            throw new InvalidOperationException(
                "The KeyPackage is missing its kind-30443 event JSON. The engine validates the "
                + "publication — signature, tag shape, and that the event's author is the leaf's "
                + "credential — and none of that can be checked from the bytes alone.");
        }

        await _gate.WaitAsync();
        try
        {
            var id = new GroupId(groupId);
            MarmotSession session = await RequireSessionAsync(id);

            // Fail-closed by construction: Parse verifies the event id and
            // signature before any field of it is read, and refuses a
            // non-conformant publication outright.
            KeyPackagePublication publication = KeyPackageEvent.Parse(keyPackage.EventJson);
            MlsKeyPackage invitee = DecodeKeyPackage(publication.KeyPackageBytes);

            var captured = new CallerPublishes.Capture();

            CommitPublishOutcome outcome = await session.CommitAsync(
                group => MarmotGroupInvite.Add(group, _cipherSuite, [invitee]),
                staged => captured.Take(session, staged, WrapCommit),
                PendingKind.GroupEvolution);

            RequireDeferred(outcome);

            byte[] welcomeBytes = RequireStagedWelcome(id, captured);

            return new MlsWelcome
            {
                WelcomeData = welcomeBytes,
                CommitData = Encoding.UTF8.GetBytes(captured.Require()),
                RecipientPublicKey = publication.AuthorPublicKeyHex,
                KeyPackageEventId = publication.EventIdHex,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stages a remove-member commit and hands back the envelope to publish.
    /// </summary>
    /// <remarks>
    /// Same reconciliation as <see cref="StageAddMemberAsync"/>. The
    /// <c>byte[]</c> is a complete signed kind-445 event, not raw commit bytes:
    /// <c>MessageService</c> currently passes this value to
    /// <c>NostrService.PublishGroupMessageAsync</c>, which does its own MIP-03
    /// wrapping — a second implementation of the wire format that the cutover
    /// removes. It has to publish the envelope as-is instead.
    /// </remarks>
    public async Task<byte[]> StageRemoveMemberAsync(byte[] groupId, string memberPublicKey)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(memberPublicKey);

        await _gate.WaitAsync();
        try
        {
            MarmotSession session = await RequireSessionAsync(new GroupId(groupId));
            byte[] account = Convert.FromHexString(memberPublicKey);

            var captured = new CallerPublishes.Capture();

            CommitPublishOutcome outcome = await session.CommitAsync(
                group => MarmotGroupInvite.Remove(group, [account]),
                staged => captured.Take(session, staged, WrapCommit),
                PendingKind.GroupEvolution);

            RequireDeferred(outcome);

            return Encoding.UTF8.GetBytes(captured.Require());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rotates this member's leaf, staged. Returns the envelope to publish.
    /// </summary>
    /// <remarks>
    /// Named for forward secrecy in the contract and staged like every other
    /// commit here: a rotation applied locally and never published is the worst
    /// of both, since the member leaves the epoch everyone else is in having
    /// gained no secrecy the group agrees about. Finish it with
    /// <see cref="MergeStagedAsync"/> or <see cref="ClearStagedAsync"/> — unlike
    /// the old service, which merged this one immediately.
    /// </remarks>
    public async Task<byte[]> UpdateKeysAsync(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await _gate.WaitAsync();
        try
        {
            MarmotSession session = await RequireSessionAsync(new GroupId(groupId));
            var captured = new CallerPublishes.Capture();

            CommitPublishOutcome outcome = await session.CommitAsync(
                MarmotSelfUpdate.Stage,
                staged => captured.Take(session, staged, WrapCommit),
                PendingKind.GroupEvolution);

            RequireDeferred(outcome);

            return Encoding.UTF8.GetBytes(captured.Require());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records that the relay took the commit, and lets hydration adopt it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It does not merge anything itself, and that is the point.</b> The
    /// engine already knows how to move a group onto a commit whose bytes a
    /// relay confirmed — archive the epoch, write the live state, then drop the
    /// staged and attempt rows, in that order so that a crash in the middle
    /// comes back reconciling rather than with no record of a commit it cannot
    /// vouch for. All this does is supply the answer that decision was waiting
    /// for: the attempt row is resolved <c>Accepted</c>, the cached session is
    /// dropped, and <see cref="MarmotSessionHost.SessionForAsync"/> re-opens the
    /// group, whose hydration reads the row, returns
    /// <see cref="StrandedCommitVerdict.Adopt"/> and does the move.
    /// </para>
    /// <para>
    /// <b>A fresh row is written rather than the old one resolved.</b>
    /// <c>CommitPublishAttempt.Resolved</c> refuses a second answer, and
    /// <see cref="CallerPublishes"/> already gave the first one; the new row
    /// carries the same commit id and epoch, so it describes the same commit. It
    /// is not a rewrite of what a transport said — it is the first answer from
    /// the transport that actually published, which is the caller.
    /// </para>
    /// <para>
    /// <b>The epoch state is settled last and by hand.</b> Hydration does not
    /// touch <see cref="DurableEpochManager"/>, because on its ordinary path the
    /// pending state was never created in this process. Here it was, by
    /// <c>CommitAsync</c>, and leaving it pending would make the group refuse to
    /// ingest anything for the rest of the session. <c>SetStableAsync</c> is the
    /// public way to say so.
    /// </para>
    /// </remarks>
    public async Task MergeStagedAsync(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await _gate.WaitAsync();
        try
        {
            await ResolveStagedAsync(new GroupId(groupId), CommitPublishState.Accepted);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records that the relay refused the commit, and lets hydration drop it.
    /// </summary>
    /// <remarks>
    /// <b>Only call this when the publish provably failed.</b> The contract says
    /// "call when the commit publish failed", and the engine reads the same
    /// distinction the other way round: <c>Rejected</c> is the only state that
    /// authorises abandoning a commit, because a timeout is not a refusal and
    /// clearing a commit a relay is serving to everyone else is the one move
    /// that cannot be repaired from this side. <c>MessageService</c> already
    /// gets this right — it clears only on <c>PublishUnconfirmedException</c>
    /// with no pending commit, and leaves it staged otherwise.
    /// </remarks>
    public async Task ClearStagedAsync(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await _gate.WaitAsync();
        try
        {
            await ResolveStagedAsync(new GroupId(groupId), CommitPublishState.Rejected);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool HasPendingCommit(byte[] groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        // The staged row, not MlsGroup.HasPendingCommit. The two answer
        // different questions: the in-memory flag belongs to whichever MlsGroup
        // object is being asked, and CommitAsync deliberately points the session
        // back at a fresh import that has no pending commit at all while the row
        // says one is outstanding. The row is what survives a restart, and it is
        // what MergeStagedAsync and ClearStagedAsync act on.
        return Blocking(async () =>
            await _storage.GetStagedCommitAsync(new GroupId(groupId)) is not null);
    }

    /// <summary>
    /// Replaces the group's admin set, staged. Returns the envelope to publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole set, not a delta.</b> The <c>0x8003</c> component is a
    /// canonical sorted list, and a delta would need a base that every caller
    /// agrees on. Read the current set with <see cref="GetAdminPubkeys"/>,
    /// change it, pass all of it.
    /// </para>
    /// <para>
    /// <b>Every rule lives in the engine, deliberately.</b>
    /// <see cref="MarmotGroupAdminPolicy.Stage"/> refuses an empty set, an
    /// admin with no member leaf, a key that is not an account key, an
    /// unchanged set, and a committer who is not already an active admin — and
    /// it judges the commit it built by reading the proposal back off the
    /// wire, because those bytes are what a peer sees. None of that is
    /// re-checked here: a second copy of a governance rule is a second copy to
    /// disagree, and this layer is the one with no way to verify it.
    /// </para>
    /// <para>
    /// Staged like every other commit here, so it is still outstanding when the
    /// caller is handed the bytes. Finish it with
    /// <see cref="MergeStagedAsync"/> or <see cref="ClearStagedAsync"/>.
    /// </para>
    /// </remarks>
    public async Task<byte[]> StageUpdateAdminPubkeysAsync(
        byte[] groupId, List<string> adminPubkeysHex)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(adminPubkeysHex);

        // Converted here rather than in the engine because hex is this
        // contract's idea, not the protocol's -- the admin list is raw 32-byte
        // account keys everywhere it is signed or compared. A bad string is
        // named as one: Convert.FromHexString's own FormatException says
        // nothing about which entry, and the caller is a settings screen.
        var admins = new List<byte[]>(adminPubkeysHex.Count);

        foreach (string hex in adminPubkeysHex)
        {
            try
            {
                admins.Add(Convert.FromHexString(hex));
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(
                    $"Admin pubkey '{hex}' is not hex.", nameof(adminPubkeysHex), ex);
            }
        }

        await _gate.WaitAsync();
        try
        {
            MarmotSession session = await RequireSessionAsync(new GroupId(groupId));
            var captured = new CallerPublishes.Capture();

            CommitPublishOutcome outcome = await session.CommitAsync(
                group => MarmotGroupAdminPolicy.Stage(group, _cipherSuite, admins),
                staged => captured.Take(session, staged, WrapCommit),
                PendingKind.GroupEvolution);

            RequireDeferred(outcome);

            return Encoding.UTF8.GetBytes(captured.Require());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Refused.</summary>
    /// <remarks>
    /// <b>By the time a caller could hold commit bytes to encrypt, the moment to
    /// encrypt them has passed.</b> A kind-445 commit is sealed under the
    /// exporter secret of the epoch it was framed in — the epoch <i>before</i>
    /// the commit — and the staged commit is the only point at which that secret
    /// and the commit exist together. So the engine wraps at stage time, and the
    /// <c>CommitData</c> that <see cref="StageAddMemberAsync"/> returns is
    /// already a complete signed event.
    /// <para>
    /// The old implementation also emitted an <c>["encoding","base64"]</c> tag
    /// on the events it built here. Current peers reject a kind-445 carrying any
    /// tag beyond <c>h</c> and <c>expiration</c>, before any MLS processing — so
    /// it is not only unnecessary, it is a bug this refusal retires.
    /// </para>
    /// </remarks>
    public Task<byte[]> EncryptCommitAsync(byte[] groupId, byte[] commitData) =>
        throw new NotSupportedException(
            "A commit is wrapped while it is still staged, under the pre-commit exporter "
            + "secret, and that is the only moment the secret and the commit coexist. "
            + "StageAddMemberAsync's CommitData and StageRemoveMemberAsync's return value are "
            + "already complete, signed kind-445 events — publish them as they are.");

    // ---------------------------------------------------------------- welcomes

    /// <inheritdoc />
    /// <remarks>
    /// Matched on the published bytes rather than on a reference the caller
    /// carries, because the contract's <c>KeyPackage</c> has no field for a
    /// KeyPackageRef and adding one would put an MLS concept in a model the
    /// ViewModels bind to. The bytes are unique per record and already in hand.
    /// </remarks>
    public async Task MarkKeyPackagePublishedAsync(KeyPackage keyPackage, string eventIdHex)
    {
        ArgumentNullException.ThrowIfNull(keyPackage);
        ArgumentException.ThrowIfNullOrEmpty(eventIdHex);

        await _gate.WaitAsync();
        try
        {
            KeyPackageRecord record =
                (await _storage.ListKeyPackagesAsync())
                    .FirstOrDefault(r => r.PublicKeyPackage.AsSpan().SequenceEqual(keyPackage.Data))
                ?? throw new InvalidOperationException(
                    "This device holds no KeyPackage matching those bytes, so there is nothing "
                    + "to bind the event id to. Publish what GenerateKeyPackageAsync returned.");

            if (!await _storage.MarkPublishedAsync(record.KeyPackageRefHex, eventIdHex))
            {
                throw new InvalidOperationException(
                    $"The KeyPackage could not be marked published under event {eventIdHex}; "
                    + "its record is past that state.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Joins the group a Welcome admits us to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pass <paramref name="keyPackageEventId"/> whenever it is known.</b>
    /// It is the kind-30443 event id from the Welcome rumor's <c>e</c> tag, and
    /// it makes this fail closed the way the engine's own join path does
    /// (<see cref="GroupJoin.JoinFromEnvelopeAsync"/>): a Welcome naming a
    /// KeyPackage this device never published is refused outright rather than
    /// tried. <c>NostrService</c> already parses that tag, and it is carried on
    /// <c>PendingInvite.KeyPackageEventId</c>.
    /// </para>
    /// <para>
    /// <b>Without it, the KeyPackage is found by trial decryption</b> — every
    /// record still holding private material, in randomised order. That is a
    /// weaker binding but not a heuristic match: a Welcome's group secrets are
    /// HPKE-sealed to one KeyPackage's init key, so what decides is the
    /// decryption rather than a resemblance, and a trial that succeeds is proof
    /// of possession. The randomisation keeps which index matched from being
    /// readable in how long this took. What the fallback loses is the check that
    /// the inviter used a KeyPackage we actually published, which is exactly
    /// what the parameter restores.
    /// </para>
    /// <para>
    /// <b>It mirrors <see cref="GroupJoin.Join"/> rather than calling it</b>,
    /// because that method takes a <c>WelcomeRumor</c> and building one here
    /// would mean fabricating the two fields — the KeyPackage event id and the
    /// inviter's verified sender key — that this signature does not supply.
    /// Fabricating a protocol object to satisfy a parameter is how a value
    /// nobody checked ends up looking checked. The same three public calls are
    /// made in the same order, profile validation included.
    /// </para>
    /// </remarks>
    public async Task<MlsGroupInfo> ProcessWelcomeAsync(
        byte[] welcomeData, string wrapperEventId, string? keyPackageEventId = null)
    {
        ArgumentNullException.ThrowIfNull(welcomeData);

        await _gate.WaitAsync();
        try
        {
            MarmotSessionHost host = RequireHost();

            MlsWelcomeBody body = DecodeWelcome(welcomeData);

            List<KeyPackageRecord> candidates = await CandidatesForAsync(keyPackageEventId);

            int[] order = Enumerable.Range(0, candidates.Count).ToArray();
            Random.Shared.Shuffle(order);

            Exception? last = null;

            foreach (int i in order)
            {
                KeyPackageRecord record = candidates[i];

                try
                {
                    MlsKeyPackage keyPackage = DecodeKeyPackage(record.PublicKeyPackage);
                    var material = KeyPackagePrivateMaterial.Decode(record.PrivateMaterial!);

                    MlsGroup group = MlsGroup.ProcessWelcome(
                        _cipherSuite,
                        body,
                        keyPackage,
                        material.InitPrivateKey,
                        material.LeafPrivateKey,
                        material.SignaturePrivateKey,
                        config: MarmotGroupSettings.Create());

                    // After the MLS join and before the caller is told it
                    // worked. A group requiring something we cannot honour is
                    // one we must not stay in -- joining and silently ignoring
                    // mandatory state is worse than refusing the invite.
                    MarmotGroupBuilder.ValidateCreated(group, "joined group");

                    var groupId = new GroupId(group.GroupId.ToArray());

                    await host.AdoptAsync(JoinRecord(groupId, group, _clock()), group);

                    // Only on success, so a failed join leaves the KeyPackage
                    // usable for a retry.
                    await _storage.MarkConsumedAsync(record.KeyPackageRefHex);

                    // The Adopt session is uncached -- see CreateGroupAsync.
                    MarmotSession session = await RequireSessionAsync(groupId);

                    _logger.LogInformation(
                        "DarkMatter: joined group {GroupId} at epoch {Epoch} from welcome {Event}",
                        Convert.ToHexString(groupId.Value)[..16],
                        session.Group.Epoch,
                        wrapperEventId[..Math.Min(16, wrapperEventId.Length)]);

                    return InfoOf(groupId, session.Group);
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                            or TlsDecodingException
                                            or AppComponentException)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException(
                $"None of the {candidates.Count} stored KeyPackages opened this Welcome. The "
                + "private material for the one it was sealed to may have been consumed or "
                + "never held on this device.",
                last);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether any stored KeyPackage could open a Welcome.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately does not attempt the join.</b> The contract asks whether
    /// we have the key material "without committing to joining the group", and a
    /// trial decryption is not free of consequence: a successful one leaves an
    /// MLS group object built from a Welcome nobody decided to accept. So this
    /// answers the weaker question the contract's own second sentence describes
    /// — "returns false immediately if no KeyPackages are stored locally" — and
    /// nothing more.
    /// </remarks>
    public async Task<bool> CanProcessWelcomeAsync(byte[] welcomeData)
    {
        ArgumentNullException.ThrowIfNull(welcomeData);

        await _gate.WaitAsync();
        try
        {
            return (await _storage.ListKeyPackagesAsync()).Any(r => r.CanConsume);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- messages

    /// <inheritdoc />
    public Task<byte[]> EncryptMessageAsync(
        byte[] groupId, string plaintext, List<List<string>>? rumorTags = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return EncryptEventAsync(
            groupId, MarmotAppEvent.ChatKind, plaintext, rumorTags ?? []);
    }

    /// <inheritdoc />
    public Task<byte[]> EncryptReactionAsync(
        byte[] groupId, string emoji, string targetRumorEventId)
    {
        ArgumentNullException.ThrowIfNull(emoji);
        ArgumentNullException.ThrowIfNull(targetRumorEventId);

        // NIP-25 on the wire, emoji in the app. The mapping is the old
        // service's, kept because peers read "+" and "-" and a bare thumb
        // emoji is not what they send back.
        string content = emoji switch
        {
            "\U0001F44D" => "+",
            "\U0001F44E" => "-",
            _ => emoji,
        };

        return EncryptEventAsync(
            groupId,
            MarmotAppEvent.ReactionKind,
            content,
            [[MarmotAppEvent.EventRefTag, targetRumorEventId]]);
    }

    /// <summary>
    /// Builds the kind-445 envelope for one application event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one place the byte[] contract and the session's send path
    /// could not be reconciled, so the send path is not used.</b>
    /// <see cref="MarmotSession.SendAsync"/> queues the message durably and then
    /// hands it to an <c>IMessageRelay</c>, and it resolves that row by what the
    /// relay said. There is no relay here — the contract's whole shape is "give
    /// me the bytes and I will publish them" — so any answer put into that
    /// machinery is a lie in one direction or the other: <c>Accepted</c> deletes
    /// the row for a message nothing has sent, and <c>Indeterminate</c> keeps a
    /// row forever that a later drain would re-send as a genuine duplicate,
    /// since a drain builds a fresh MLS message rather than replaying bytes.
    /// </para>
    /// <para>
    /// So it calls <see cref="GroupMessages.Send"/>, which despite its name puts
    /// nothing on a wire: it is the engine's public "make me the envelope"
    /// function, and it is exactly what this member is being asked for.
    /// </para>
    /// <para>
    /// <b>What that costs, and what is done about it.</b> Sealing moved the
    /// sender ratchet in memory, so the advanced state is written down before
    /// the bytes are returned — mirroring <c>MarmotSession.AttemptAsync</c>,
    /// whose comment explains why: a crash in between comes back with the
    /// generation counter rewound, and the next message encrypts a different
    /// plaintext under a key and nonce this one already used.
    /// <c>WriteLiveStateAsync</c> is internal, so the write is made here against
    /// the same record, epoch and state together as it insists. Its other half,
    /// the routing re-sync, is deliberately not reproduced: only a commit can
    /// move a group's address, and this is not one.
    /// </para>
    /// <para>
    /// <b>The two gates the send path applies, and what became of them.</b> A
    /// group we have been removed from is refused, as it must be. An unsettled
    /// group is <i>not</i> refused, though <c>SendAsync</c> would queue it: this
    /// contract has no way to say "queued", and publish-before-apply means the
    /// live group really is still at the epoch it is sealing under — which is
    /// the same reason <c>DrainAsync</c> gives for draining an unsettled group.
    /// </para>
    /// </remarks>
    private async Task<byte[]> EncryptEventAsync(
        byte[] groupId, long kind, string content, IReadOnlyList<IReadOnlyList<string>> tags)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await _gate.WaitAsync();
        try
        {
            var id = new GroupId(groupId);
            MarmotSession session = await RequireSessionAsync(id);

            if (await _storage.GetGroupAsync(id) is { Removed: true })
            {
                throw new InvalidOperationException(
                    $"Group {Convert.ToHexString(id.Value).ToLowerInvariant()} has removed this "
                    + "member; nothing it sends can be read by the group.");
            }

            var appEvent = MarmotAppEvent.Create(
                RequireIdentity(),
                _clock().ToUnixTimeSeconds(),
                kind,
                tags,
                content);

            string envelope = GroupMessages.Send(
                session.Group, _peeler, appEvent, SenderIdentityOf(session.Group));

            await PersistRatchetAsync(id, session.Group);

            LastEncryptedRumorEventId = appEvent.Id;

            return Encoding.UTF8.GetBytes(envelope);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts one inbound kind-445 event through the group's ingest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The envelope goes in whole.</b> Nothing in a Nostr event is
    /// trustworthy until its id and signature verify, and the peeler is the only
    /// thing that checks them — so a caller that had already stripped the event
    /// down to MIP-03 ciphertext would be handing us attacker-chosen routing.
    /// Raw MLS bytes are accepted too, for the internal callers that have them,
    /// but ciphertext without its envelope is refused rather than guessed at.
    /// </para>
    /// <para>
    /// <b>The two positional hints are unused, and cannot be otherwise.</b>
    /// <c>nostrEventId</c> and <c>nostrCreatedAt</c> exist for the old engine's
    /// MIP-03 tiebreaker, which decided a commit race by transport metadata. The
    /// engine decides it by <see cref="CommitOrdering"/> over the commits
    /// themselves — priority class, then committer, then content digest — all of
    /// which every member can compute identically and none of which a relay can
    /// influence. Feeding a created_at into that would reintroduce a tiebreak
    /// peers do not share.
    /// </para>
    /// <para>
    /// <b>Refusals throw.</b> Ingest classifies rather than throwing, which is
    /// right for a subscription loop, but this contract's caller expects a
    /// message or an exception. The classification is carried in the message so
    /// nothing is lost.
    /// </para>
    /// </remarks>
    public async Task<MlsDecryptedMessage> DecryptMessageAsync(
        byte[] groupId,
        byte[] ciphertext,
        string? nostrEventId = null,
        DateTimeOffset? nostrCreatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(ciphertext);

        await _gate.WaitAsync();
        try
        {
            var id = new GroupId(groupId);
            MarmotSession session = await RequireSessionAsync(id);

            IngestResult result = LooksLikeJson(ciphertext)
                ? await session.ReceiveAsync(Encoding.UTF8.GetString(ciphertext))
                : await session.IngestAsync(ciphertext);

            return Interpret(result, session);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies an inbound commit.
    /// </summary>
    /// <remarks>
    /// The same door as <see cref="DecryptMessageAsync"/>, because in the engine
    /// there is only one: <see cref="MessageIngest"/> classifies a handshake and
    /// an application message from the same bytes and keeps the durable record
    /// either way. Splitting them at this layer would mean deciding what a
    /// message is before the thing that knows has looked.
    /// </remarks>
    public async Task ProcessCommitAsync(byte[] groupId, byte[] commitData)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(commitData);

        await _gate.WaitAsync();
        try
        {
            var id = new GroupId(groupId);
            MarmotSession session = await RequireSessionAsync(id);

            IngestResult result = LooksLikeJson(commitData)
                ? await session.ReceiveAsync(Encoding.UTF8.GetString(commitData))
                : await session.IngestAsync(commitData);

            if (result.Outcome is not IngestOutcome.Processed)
            {
                throw new MlsIngestRefusedException(
                    result.Outcome, $"The commit was not applied: {Describe(result.Outcome)}.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ signer

    /// <summary>Refused.</summary>
    /// <remarks>
    /// <para>
    /// <b>The engine never signs a kind-445 with an account key, so the seam
    /// this names does not exist.</b> MIP-03 requires a fresh ephemeral key per
    /// group event and forbids the account identity — signing one with it would
    /// deanonymise every message the account sends — and
    /// <see cref="NostrGroupPeeler.WrapGroupMessage"/> generates that key
    /// itself rather than leaving the choice, and the mistake, available.
    /// </para>
    /// <para>
    /// <b>An external signer is still needed, at a different seam, and
    /// <see cref="INostrEventSigner"/> cannot serve it.</b> What needs the
    /// account key is the account-identity proof (component <c>0x8009</c>): a
    /// kind-450 template whose <c>created_at</c> is fixed by the caller and
    /// whose signature is verified against that exact template before it is
    /// trusted. <c>SignEventAsync</c> chooses its own <c>created_at</c> and
    /// returns a finished event, so a signature obtained through it verifies
    /// over a different id than the one the proof commits to. Adapting it would
    /// mean either accepting a signature we did not verify or re-deriving the
    /// template from what the signer chose — inventing the binding the proof
    /// exists to establish. Pass an
    /// <see cref="IAccountIdentityProofSigner"/> to the constructor instead.
    /// </para>
    /// </remarks>
    /// <inheritdoc />
    /// <remarks>
    /// <b>Replaces whatever <see cref="InitializeAsync"/> derived.</b> A local
    /// key and a remote signer are two claims on one account, and the remote one
    /// wins: it is the later, more deliberate act, and on a signer login there
    /// is no local key to lose. Null restores the injected signer if there was
    /// one, so clearing a connection does not strip an explicitly supplied
    /// signer that never came from the connection.
    /// </remarks>
    public void SetExternalSigner(IExternalSigner? signer)
    {
        _gate.Wait();
        try
        {
            _proofSigner = signer is null
                ? _injectedProofSigner
                : new ExternalAccountProofSigner(signer);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void SetNostrEventSigner(INostrEventSigner signer) =>
        throw new NotSupportedException(
            "Kind-445 events are signed with a fresh ephemeral key, which MIP-03 requires and "
            + "the peeler generates itself; there is nothing here for a Nostr event signer to "
            + "do. The account key is needed for the kind-450 account-identity proof, and "
            + "INostrEventSigner cannot supply that: it picks its own created_at, so its "
            + "signature verifies over a different template than the proof commits to. "
            + "Construct the service with an IAccountIdentityProofSigner.");

    // ----------------------------------------------------------------- helpers

    private MarmotSessionHost RequireHost() =>
        _host ?? throw new InvalidOperationException(
            "The MLS service is not initialised. Call InitializeAsync first.");

    private string RequireIdentity() =>
        _publicKeyHex ?? throw new InvalidOperationException(
            "The MLS service has no identity. Call InitializeAsync first.");

    /// <summary>
    /// The KeyPackage records a Welcome may be tried against.
    /// </summary>
    /// <remarks>
    /// One record when the Welcome names it, every consumable record when it
    /// does not. The named case is the fail-closed one and its refusals are
    /// deliberately distinct: a KeyPackage we never published is a different
    /// fact from one whose material we have already erased, and a reader
    /// chasing a failed join needs to know which.
    /// </remarks>
    private async Task<List<KeyPackageRecord>> CandidatesForAsync(string? keyPackageEventId)
    {
        if (!string.IsNullOrEmpty(keyPackageEventId))
        {
            KeyPackageRecord named = await _storage.GetKeyPackageByEventAsync(keyPackageEventId)
                ?? throw new InvalidOperationException(
                    $"The Welcome names KeyPackage event {keyPackageEventId}, which this device "
                    + "never published. Refusing it: an inviter that did not use one of our "
                    + "KeyPackages has not been admitted by us.");

            if (!named.CanConsume)
            {
                throw new InvalidOperationException(
                    $"The Welcome names KeyPackage event {keyPackageEventId}, whose private "
                    + "material has been erased. It cannot be opened, and no other KeyPackage "
                    + "may stand in for the one the inviter chose.");
            }

            return [named];
        }

        var candidates = (await _storage.ListKeyPackagesAsync())
            .Where(r => r.CanConsume)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No stored KeyPackage still holds private material, so no Welcome can be "
                + "opened on this device.");
        }

        return candidates;
    }

    private IAccountIdentityProofSigner RequireProofSigner() =>
        _proofSigner ?? throw new NotSupportedException(
            "This operation needs to sign an account-identity proof (component 0x8009) and no "
            + "signer is available: InitializeAsync was given no usable private key and no "
            + "IAccountIdentityProofSigner was supplied. See SetNostrEventSigner for why an "
            + "INostrEventSigner cannot stand in.");

    /// <summary>
    /// The session for a group, through the one door that owns them.
    /// </summary>
    /// <remarks>
    /// <see cref="MarmotSessionHost.OpenAsync"/> is never used: it hands back a
    /// fresh session every call, so two callers on one group get two owners,
    /// each holding an <c>MlsGroup</c> and each writing it down — and whichever
    /// writes second discards the other's epoch, silently. The refusal is turned
    /// into a throw here because this contract has nowhere to put "the group is
    /// not ours to open", but the two reasons are kept apart in the message:
    /// not stored is routine, unreadable state is a fault somebody has to see.
    /// </remarks>
    private async Task<MarmotSession> RequireSessionAsync(GroupId groupId)
    {
        SessionOpenResult result = await RequireHost().SessionForAsync(groupId);

        if (result.Session is { } session)
            return session;

        string hex = Convert.ToHexString(groupId.Value).ToLowerInvariant();

        throw result.Refusal switch
        {
            SessionOpenRefusal.NotStored =>
                new InvalidOperationException($"No group {hex} is stored on this device."),

            SessionOpenRefusal.StateUnreadable =>
                new InvalidOperationException(
                    $"Group {hex} is stored and its MLS state will not load. Its history is "
                    + "here and cannot be reached."),

            _ => new InvalidOperationException(
                $"Group {hex} has no usable state and nothing retained can rebuild it."),
        };
    }

    /// <summary>
    /// Brings the group record into line with the MLS state it describes.
    /// </summary>
    /// <remarks>
    /// A local copy of <c>MarmotSessionHost.WriteLiveStateAsync</c>, which is
    /// internal. Epoch and live state are written together, always: they are one
    /// fact in two columns and a record whose epoch says one thing while its
    /// bytes say another is worse than either being stale. The routing re-sync
    /// that method also performs is not reproduced, and does not need to be —
    /// only a commit can move a group's address, and the one caller here is a
    /// send.
    /// </remarks>
    private async Task PersistRatchetAsync(GroupId groupId, MlsGroup group)
    {
        if (await _storage.GetGroupAsync(groupId) is not { } record)
            return;

        await _storage.PutGroupAsync(
            record with
            {
                Epoch = new EpochId(group.Epoch),
                LiveState = group.Export(),
                UpdatedAt = _clock(),
            });
    }

    /// <summary>
    /// This member's account key, as the ratchet tree has it.
    /// </summary>
    /// <remarks>
    /// Taken from the tree rather than from the identity this service was
    /// initialised with, so that <c>MarmotAppEvent.RequireSender</c> is still
    /// comparing one thing against another. Passing our own configured pubkey
    /// would make that check compare a value with itself and pass for a message
    /// every receiver rejects.
    /// </remarks>
    private static byte[] SenderIdentityOf(MlsGroup group)
    {
        foreach ((uint index, byte[] identity) in group.GetMembers())
        {
            if (index == group.MyLeafIndex)
                return identity;
        }

        throw new InvalidOperationException(
            $"The group does not list our own leaf {group.MyLeafIndex} as a member.");
    }

    /// <summary>
    /// Wraps a staged commit for the wire, under the pre-commit exporter secret.
    /// </summary>
    /// <remarks>
    /// <c>session.Group</c> is read fresh, and at the moment this runs it is the
    /// group as it was <i>before</i> the commit: <c>CommitAsync</c> stages on a
    /// throwaway and points the session back at a re-import of the prior state
    /// before it publishes anything. A commit sealed under the post-commit
    /// secret would be unreadable by every member who has not yet applied it,
    /// which is all of them.
    /// </remarks>
    private string WrapCommit(MarmotSession session, StagedCommit staged)
    {
        byte[] mlsBytes = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, staged.Commit).WriteTo);

        return _peeler.WrapGroupMessage(
            mlsBytes,
            GroupMessages.TransportGroupId(session.Group),
            GroupMessages.ExporterSecret(session.Group));
    }

    /// <summary>
    /// Supplies the caller's answer to a publish the engine left unresolved.
    /// </summary>
    /// <remarks>See <see cref="MergeStagedAsync"/> for why it is shaped this way.</remarks>
    private async Task ResolveStagedAsync(GroupId groupId, CommitPublishState state)
    {
        MarmotSessionHost host = RequireHost();

        if (await _storage.GetStagedCommitAsync(groupId) is null)
        {
            throw new InvalidOperationException(
                $"Group {Convert.ToHexString(groupId.Value).ToLowerInvariant()} has no staged "
                + "commit to resolve.");
        }

        CommitPublishAttempt? existing = await _storage.GetCommitPublishAttemptAsync(groupId);

        if (existing is null)
        {
            // No row means "nobody can have seen this commit", which hydration
            // reads as Abandon. Reaching here with a staged commit and no row
            // would mean CommitAsync never got as far as the transport, so
            // there is nothing for a caller to have published.
            throw new InvalidOperationException(
                "The staged commit was never handed to a transport, so there is no publish for "
                + "the caller to be answering for.");
        }

        DateTimeOffset now = _clock();

        await _storage.PutCommitPublishAttemptAsync(
            CommitPublishAttempt
                .HandedToTransport(groupId, existing.CommitId, existing.NewEpoch, existing.HandedOffAt)
                .Resolved(state, now));

        // Dropped rather than closed: closing writes the session's live state,
        // and this session is holding the pre-commit state that hydration is
        // about to decide the fate of.
        host.Forget(groupId);

        MarmotSession session = await RequireSessionAsync(groupId);

        // The pending epoch state was created in this process by CommitAsync and
        // hydration does not touch it. Left pending, the group would refuse to
        // ingest anything for the rest of the session.
        await host.Epochs.SetStableAsync(groupId, new EpochId(session.Group.Epoch));

        _logger.LogInformation(
            "DarkMatter: staged commit for {GroupId} resolved {State}; group now at epoch {Epoch}",
            Convert.ToHexString(groupId.Value)[..16], state, session.Group.Epoch);
    }

    /// <summary>
    /// The Welcome bytes a staged add produced, framed as an MLSMessage.
    /// </summary>
    private static byte[] RequireStagedWelcome(GroupId groupId, CallerPublishes.Capture captured)
    {
        if (captured.Welcome is not { } welcome)
        {
            throw new InvalidOperationException(
                $"The staged commit for {Convert.ToHexString(groupId.Value).ToLowerInvariant()} "
                + "added members but produced no Welcome; they would be in the tree and unable "
                + "to derive a single group secret.");
        }

        return TlsCodec.Serialize(new MlsMessage(WireFormat.MlsWelcome, welcome).WriteTo);
    }

    /// <summary>
    /// Asserts that the capture transport behaved as this class needs it to.
    /// </summary>
    /// <remarks>
    /// <b>Not defensive tidiness.</b> Anything other than <c>Indeterminate</c>
    /// means the staged commit was resolved inside <c>CommitAsync</c> — applied
    /// or discarded — and the caller is about to be handed bytes for a commit
    /// whose fate has already been decided without them. That is the exact
    /// confusion between the two models this class exists to keep straight, so
    /// it fails loudly rather than returning an envelope that means something
    /// else.
    /// </remarks>
    private static void RequireDeferred(CommitPublishOutcome outcome)
    {
        if (outcome != CommitPublishOutcome.Indeterminate)
        {
            throw new InvalidOperationException(
                $"The staging transport answered {outcome}. It must defer, so that the staged "
                + "commit is still outstanding when the caller is handed the bytes to publish.");
        }
    }

    private static MlsGroupInfo InfoOf(GroupId groupId, MlsGroup group) => new()
    {
        GroupId = groupId.Value,
        GroupName = NameOf(group),
        Epoch = group.Epoch,
        MemberPublicKeys = group.GetMembers()
            .Select(m => Convert.ToHexString(m.identity).ToLowerInvariant())
            .ToList(),
    };

    private static string NameOf(MlsGroup group)
    {
        byte[]? encoded = GroupComponent(group, AppComponent.GroupProfile);
        return encoded is null ? string.Empty : GroupProfile.Decode(encoded).Name;
    }

    /// <summary>One component out of the group's signed app-data dictionary.</summary>
    private static byte[]? GroupComponent(MlsGroup group, ushort componentId)
    {
        foreach (Extension extension in group.GroupContext.Extensions)
        {
            if (extension.ExtensionType != MarmotDictionary.ExtensionType)
                continue;

            return MarmotDictionary.Decode(extension.ExtensionData).Get(componentId);
        }

        return null;
    }

    private static MlsKeyPackage DecodeKeyPackage(byte[] bytes)
    {
        var message = MlsMessage.ReadFrom(new TlsReader(bytes));

        if (message.WireFormat != WireFormat.MlsKeyPackage || message.Body is not MlsKeyPackage keyPackage)
            throw new InvalidOperationException($"Expected a KeyPackage, got {message.WireFormat}.");

        return keyPackage;
    }

    private static MlsWelcomeBody DecodeWelcome(byte[] bytes)
    {
        MlsMessage message;
        try
        {
            message = MlsMessage.ReadFrom(new TlsReader(bytes));
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"The Welcome is not a decodable MLSMessage: {ex.Message}", ex);
        }

        if (message.WireFormat != WireFormat.MlsWelcome || message.Body is not MlsWelcomeBody welcome)
            throw new InvalidOperationException($"Expected a Welcome, got {message.WireFormat}.");

        return welcome;
    }

    /// <summary>
    /// The durable record for a group we joined.
    /// </summary>
    /// <remarks>
    /// <b>A copy of <see cref="JoinedGroup.ToRecord"/> rather than a call to
    /// it</b>, for the same reason <see cref="ProcessWelcomeAsync"/> does not
    /// call <see cref="GroupJoin.Join"/>: building a <c>JoinedGroup</c> means
    /// supplying an inviter identity and a KeyPackage event id, and this
    /// signature carries neither. Inventing them to satisfy a constructor is
    /// how a value nobody checked comes to look checked.
    /// <para>
    /// Both of its non-obvious fields are kept. <c>JoinEpoch</c> is the epoch we
    /// were admitted at, not zero: everything before it happened without us and
    /// is not decryptable, so treating those messages as delivery failures would
    /// be wrong. <c>ValidatedTree</c> is false, because we have verified only
    /// our own leaf's proof and claiming otherwise would skip every other
    /// member's permanently.
    /// </para>
    /// </remarks>
    private static GroupRecord JoinRecord(GroupId groupId, MlsGroup group, DateTimeOffset joinedAt)
    {
        var epoch = new EpochId(group.Epoch);

        return new GroupRecord(groupId, epoch, ProtocolProfile.Current, joinedAt, joinedAt)
        {
            JoinEpoch = epoch,
            ValidatedTree = false,
            LiveState = group.Export(),
        };
    }

    private async Task<string> CurrentSlotIdAsync()
    {
        var existing = await _storage.ListKeyPackagesAsync();
        return existing.Count > 0 ? existing[^1].SlotId : KeyPackageEvent.NewSlotId();
    }

    private static bool LooksLikeJson(byte[] bytes) =>
        bytes.Length > 0 && (bytes[0] == (byte)'{' || bytes[0] == (byte)'[');

    /// <summary>Turns an ingest outcome into the contract's shape.</summary>
    private static MlsDecryptedMessage Interpret(IngestResult result, MarmotSession session)
    {
        if (result.Message is { } received)
            return Describe(received, session.Group.Epoch);

        if (result.Outcome is IngestOutcome.Processed)
        {
            // A handshake: the epoch moved, and there is no user-visible
            // content. Unlike the old service, the epoch is real rather than
            // zero -- the session is holding the group that just advanced.
            return new MlsDecryptedMessage
            {
                IsCommit = true,
                Epoch = session.Group.Epoch,
            };
        }

        throw new MlsIngestRefusedException(
            result.Outcome, $"The message was not delivered: {Describe(result.Outcome)}.");
    }

    /// <summary>One outcome in words, for a log line or an exception message.</summary>
    /// <remarks>
    /// <b>Every variant that carries a reason states it.</b>
    /// <see cref="IngestOutcome.Stale"/> and <see cref="IngestOutcome.Rejected"/>
    /// used to fall to the type-name arm, so a commit from before we joined and a
    /// commit that does not apply to the history we hold both read as bare
    /// <c>"Stale"</c> — the one distinction a reader chasing either of them needs.
    /// The classification also travels as a value on
    /// <see cref="MlsIngestRefusedException.Outcome"/>; this text is for humans,
    /// and no caller should be parsing it.
    /// </remarks>
    private static string Describe(IngestOutcome outcome) => outcome switch
    {
        IngestOutcome.Ignored ignored => $"ignored ({ignored.Category})",
        IngestOutcome.Buffered => "buffered — the group cannot take input while a commit of ours is unresolved",
        IngestOutcome.TransportDeferred => "deferred — held for replay once the group can read it",
        IngestOutcome.LocalState local => $"local state ({local.State})",
        IngestOutcome.Stale stale => $"stale ({stale.Reason})",
        IngestOutcome.Rejected rejected => $"rejected ({rejected.Category})",
        IngestOutcome.ResourceRefused => "resource refused — a local bound stopped us keeping it",
        _ => outcome.GetType().Name,
    };

    /// <summary>
    /// Projects a decrypted Marmot event onto the contract's message shape.
    /// </summary>
    /// <remarks>
    /// The tag reading is the old service's rules, kept deliberately: they are
    /// what the app's own view models and what peers in the wild actually emit —
    /// an <c>e</c> tag with a <c>reply</c> marker for replies and without one
    /// for reaction targets, NIP-22 <c>q</c> as a second reply form, and MIP-04
    /// <c>imeta</c> entries as space-separated key/value strings. None of it is
    /// engine semantics; it is Nostr convention, and changing it here would
    /// change what the UI shows for messages the engine handled correctly.
    /// </remarks>
    private static MlsDecryptedMessage Describe(ReceivedGroupMessage received, ulong epoch)
    {
        MarmotAppEvent appEvent = received.Event;

        var message = new MlsDecryptedMessage
        {
            SenderPublicKey = Convert.ToHexString(received.SenderIdentity).ToLowerInvariant(),
            Plaintext = appEvent.Content,
            RumorJson = Encoding.UTF8.GetString(appEvent.Encode()),
            Epoch = epoch,
            RumorEventId = appEvent.Id,
            RumorKind = checked((int)appEvent.Kind),
        };

        foreach (IReadOnlyList<string> tag in appEvent.Tags)
        {
            if (tag.Count < 2)
                continue;

            switch (tag[0])
            {
                case "e" when tag.Count >= 4 && tag[3] == "reply":
                    message.ReplyToRumorEventId = tag[1];
                    break;

                case "e":
                    message.ReactionTargetEventId = tag[1];
                    break;

                case "q" when message.ReplyToRumorEventId is null:
                    message.ReplyToRumorEventId = tag[1];
                    break;

                case "imeta":
                    ReadImeta(tag, message);
                    break;
            }
        }

        if (message.RumorKind == MarmotAppEvent.ReactionKind && message.ReactionTargetEventId is not null)
        {
            message.ReactionEmoji = appEvent.Content switch
            {
                "+" => "\U0001F44D",
                "-" => "\U0001F44E",
                _ => appEvent.Content,
            };
        }

        return message;
    }

    private static void ReadImeta(IReadOnlyList<string> tag, MlsDecryptedMessage message)
    {
        for (int i = 1; i < tag.Count; i++)
        {
            string entry = tag[i];

            if (string.IsNullOrEmpty(entry))
                continue;

            if (entry.StartsWith("url ", StringComparison.Ordinal))
                message.ImageUrl = entry[4..];
            else if (entry.StartsWith("m ", StringComparison.Ordinal))
                message.MediaType = entry[2..];
            else if (entry.StartsWith("filename ", StringComparison.Ordinal))
                message.FileName = entry[9..];
            else if (entry.StartsWith("x ", StringComparison.Ordinal))
                message.FileSha256 = entry[2..];
            else if (entry.StartsWith("n ", StringComparison.Ordinal) && entry.Length <= 26)
                message.EncryptionNonce = entry[2..];
            else if (entry.StartsWith("nonce ", StringComparison.Ordinal))
                message.EncryptionNonce = entry[6..];
            else if (entry.StartsWith("v ", StringComparison.Ordinal))
                message.EncryptionVersion = entry[2..];
            else if (entry.StartsWith("encryption-version ", StringComparison.Ordinal))
                message.EncryptionVersion = entry[19..];
        }
    }

    /// <summary>
    /// Runs an async body from a synchronous contract member.
    /// </summary>
    /// <remarks>
    /// <b>Every synchronous member of <c>IMlsService</c> needs storage, and the
    /// engine is async throughout — so the blocking happens somewhere, and one
    /// named place is better than eight scattered ones.</b> It is not as
    /// alarming as it looks: the store underneath is Microsoft.Data.Sqlite,
    /// whose async surface completes synchronously, and the engine already does
    /// exactly this in <c>GroupJournal</c> for the same reason. It is still the
    /// wrong shape, and the honest fix is asynchronous signatures on the
    /// contract — which is a change to a file the Presentation layer binds to,
    /// and so not this commit's.
    /// </remarks>
    private T Blocking<T>(Func<Task<T>> body)
    {
        _gate.Wait();
        try
        {
            return body().GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Signs account-identity proofs with a locally held Nostr key.
    /// </summary>
    /// <remarks>
    /// <b>The account key is taken from the identity, not derived from the
    /// secret.</b> <see cref="Bip340"/> deliberately offers no derivation
    /// helper, and reaching for NBitcoin's would put the one secp256k1 path the
    /// handoff's trap table calls broken on .NET Android into the identity
    /// layer. A mismatched pair is not silently accepted:
    /// <see cref="AccountIdentityProofSigning.CreateAsync"/> verifies every
    /// signature against this <see cref="AccountPublicKey"/> before it is
    /// trusted, so a secret that does not belong to it fails at the first proof.
    /// </remarks>
    private sealed class LocalAccountProofSigner : IAccountIdentityProofSigner
    {
        private readonly byte[] _secret;

        public LocalAccountProofSigner(string privateKeyHex, string publicKeyHex)
        {
            _secret = Convert.FromHexString(privateKeyHex);

            if (_secret.Length != 32)
                throw new ArgumentException("A Nostr private key is 32 bytes.", nameof(privateKeyHex));

            byte[] account = Convert.FromHexString(publicKeyHex);

            if (!Bip340.IsValidXOnlyPublicKey(account))
            {
                throw new ArgumentException(
                    "The account public key is not a valid x-only secp256k1 point.",
                    nameof(publicKeyHex));
            }

            AccountPublicKey = account;
        }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(_secret, template.ComputeId()));
    }

    /// <summary>
    /// The transport that does not transport: it keeps the envelope and says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="CommitPublishOutcome.Indeterminate"/> is the honest
    /// answer, not a stand-in.</b> The engine's three outcomes are statements
    /// about what a relay did, and they authorise different destructive moves:
    /// <c>Rejected</c> alone permits discarding a commit, because only a definite
    /// refusal proves nobody else holds it. When this class returns, the bytes
    /// have gone to a caller who has not published them yet, so the truthful
    /// statement is that the commit neither succeeded nor provably failed. The
    /// engine's response to that — keep the staged state, keep the attempt row,
    /// keep the group where it is — is exactly the state the service contract
    /// calls "staged".
    /// </para>
    /// <para>
    /// <b>The envelope is captured through a per-call object, not held on the
    /// relay.</b> A relay shared by every group with a mutable last-envelope
    /// field is a race waiting for a second caller, and the gate around this
    /// service is the only thing that would be preventing it.
    /// </para>
    /// </remarks>
    private sealed class CallerPublishes : ICommitRelay
    {
        public static readonly CallerPublishes Instance = new();

        private CallerPublishes()
        {
        }

        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default) =>
            Task.FromResult(CommitPublishOutcome.Indeterminate);

        /// <summary>What one staging call produced.</summary>
        /// <remarks>
        /// Per staging call rather than a field on the relay. A relay shared by
        /// every group with a mutable last-envelope field is a race waiting for
        /// a second caller, and the gate around this service would be the only
        /// thing preventing it.
        /// </remarks>
        internal sealed class Capture
        {
            public string? Envelope { get; private set; }

            public MlsWelcomeBody? Welcome { get; private set; }

            /// <summary>
            /// Reads everything off the staged commit while it is still staged.
            /// </summary>
            /// <remarks>
            /// The Welcome is taken here rather than later because the staged
            /// commit is disposed as <c>CommitAsync</c> returns; the envelope is
            /// built here because this is the one moment the session's live
            /// group is still at the pre-commit epoch, whose exporter secret is
            /// the one every other member can open.
            /// </remarks>
            public string Take(
                MarmotSession session,
                StagedCommit staged,
                Func<MarmotSession, StagedCommit, string> wrap)
            {
                Welcome = staged.Welcome;
                Envelope = wrap(session, staged);
                return Envelope;
            }

            public string Require() =>
                Envelope ?? throw new InvalidOperationException(
                    "The commit was never wrapped for the wire, so there is nothing to publish.");
        }
    }
}
