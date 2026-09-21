using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Nostr.Crypto;

namespace Scramble.Core.Tests.TestHelpers;

/// <summary>
/// One identity on the Dark Matter engine, its own durable store, and the two
/// steps that publishing a KeyPackage actually takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The tests that used to build
/// <c>ManagedMlsService</c> handed it a kind-30443 event with a literal
/// <c>"fake"</c> id and 128 <c>'a'</c>s for a signature, and it took them — its
/// <see cref="IMlsService.MarkKeyPackagePublishedAsync"/> was
/// <c>Task.CompletedTask</c>. The Dark Matter engine verifies the event before it
/// reads a field out of it, because an invitee's account key is only as
/// trustworthy as the event carrying it, and it refuses a Welcome naming a
/// KeyPackage this device never published. Both are correct, and both mean the
/// fixture has to do the real thing.
/// </para>
/// <para>
/// <b>Publishing is two steps and the second one is the one that gets forgotten.</b>
/// <see cref="SignKeyPackageEvent"/> is the relay's half — the event a caller
/// signs and sends. <see cref="BindAsync"/> is this device's half: it ties the id
/// that event went out under to the private material kept back for it. Skip it and
/// the join fails closed, which is the engine being right rather than a fixture
/// being unlucky.
/// </para>
/// <para>
/// The engine's store is <em>not</em> <c>StorageService</c>. Chats, invites and
/// the user record live in the app's database; epochs, KeyPackage material and the
/// publication slot live in this one. Tests that need both carry both, and two
/// parties never share either — a store is a device.
/// </para>
/// </remarks>
internal sealed class MlsTestEngine : IDisposable
{
    private readonly SqliteMarmotStorageProvider _provider;
    private readonly string _dbPath;

    private MlsTestEngine(
        DarkMatterMlsService service,
        SqliteMarmotStorageProvider provider,
        string dbPath,
        string privateKeyHex,
        string publicKeyHex)
    {
        Service = service;
        _provider = provider;
        _dbPath = dbPath;
        PrivateKeyHex = privateKeyHex;
        PublicKeyHex = publicKeyHex;
    }

    public DarkMatterMlsService Service { get; }

    public string PrivateKeyHex { get; }

    public string PublicKeyHex { get; }

    private byte[] PrivateKey => Convert.FromHexString(PrivateKeyHex);

    /// <summary>
    /// Starts an initialised engine over a fresh store.
    /// </summary>
    /// <param name="label">Only for the temp filename, so a leak is traceable.</param>
    /// <param name="privateKeyHex">
    /// Omit for a fresh <see cref="Bip340.GenerateKeyPair"/>. Pass one only to
    /// share an identity with an app-side <c>StorageService</c> user record — and
    /// pass a real one: <c>InitializeAsync</c> builds an account-proof signer from
    /// it and refuses a public key that is not a curve point, so the old
    /// <c>"e9b03d7d" + 56 zeroes</c> placeholders no longer initialise.
    /// </param>
    public static async Task<MlsTestEngine> StartAsync(
        string label, string? privateKeyHex = null, string? publicKeyHex = null)
    {
        if (privateKeyHex is null || publicKeyHex is null)
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            privateKeyHex = Convert.ToHexString(secret).ToLowerInvariant();
            publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();
        }

        string dbPath = Path.Combine(
            Path.GetTempPath(), $"scramble_dm_{label}_{Guid.NewGuid():N}.db");

        var provider = new SqliteMarmotStorageProvider($"Data Source={dbPath}");
        var service = new DarkMatterMlsService(provider);

        await service.InitializeAsync(privateKeyHex, publicKeyHex);

        return new MlsTestEngine(service, provider, dbPath, privateKeyHex, publicKeyHex);
    }

    /// <summary>Generates a KeyPackage, signs its event, and binds the id.</summary>
    /// <remarks>
    /// What the app's <c>AutoPublishKeyPackageIfNeededAsync</c> does, minus the
    /// relay. Use the three steps separately only when a test needs to be in
    /// between them.
    /// </remarks>
    public async Task<KeyPackage> PublishKeyPackageAsync()
    {
        KeyPackage keyPackage = await Service.GenerateKeyPackageAsync();
        SignKeyPackageEvent(keyPackage);
        await BindAsync(keyPackage);
        return keyPackage;
    }

    /// <summary>
    /// Fills in the kind-30443 event a caller would have published, signed for
    /// real, and sets <see cref="KeyPackage.NostrEventId"/> to its id.
    /// </summary>
    /// <remarks>
    /// The id is the hash of the canonical event, not a value anyone picks: it is
    /// what the Welcome's <c>e</c> tag will name and what <see cref="BindAsync"/>
    /// binds, so a fixture that overwrites it afterwards with a fresh
    /// <c>Guid</c> breaks the join even though every individual step looks done.
    /// The tags are the engine's own — required components included — so this
    /// stays honest about tag shape as well as about the signature.
    /// </remarks>
    /// <returns>The event id, lowercase hex.</returns>
    public string SignKeyPackageEvent(KeyPackage keyPackage)
    {
        ArgumentNullException.ThrowIfNull(keyPackage);

        var template = new NostrEventTemplate(
            PublicKeyHex,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            30443,
            keyPackage.NostrTags.Select(t => (IReadOnlyList<string>)t).ToList(),
            Convert.ToBase64String(keyPackage.Data));

        byte[] id = template.ComputeId();

        keyPackage.EventJson = NostrEnvelope.Write(template, id, Bip340.Sign(PrivateKey, id));
        keyPackage.NostrEventId = Convert.ToHexString(id).ToLowerInvariant();
        keyPackage.OwnerPublicKey = PublicKeyHex;

        return keyPackage.NostrEventId;
    }

    /// <summary>
    /// Records the id this device's KeyPackage went out under.
    /// </summary>
    /// <remarks>
    /// Must be called on the engine that generated it — the one holding the
    /// private material. Binding on the inviter's engine throws "this device holds
    /// no KeyPackage matching those bytes", which is the engine telling you the
    /// call is on the wrong side.
    /// </remarks>
    public Task BindAsync(KeyPackage keyPackage)
    {
        ArgumentNullException.ThrowIfNull(keyPackage);
        return Service.MarkKeyPackagePublishedAsync(keyPackage, keyPackage.NostrEventId!);
    }

    /// <summary>
    /// Stages an add, publishes nothing, and merges — the caller's half of
    /// publish-before-apply, collapsed for a test with no relay.
    /// </summary>
    /// <remarks>
    /// <see cref="IMlsService.AddMemberAsync"/> auto-merged and is
    /// <see cref="NotSupportedException"/> here: this engine will not clear a
    /// commit a relay might already hold on the strength of nobody having tried to
    /// send it. A test standing in for the relay says so in two calls.
    /// </remarks>
    public async Task<MlsWelcome> AddMemberAsync(byte[] groupId, KeyPackage keyPackage)
    {
        MlsWelcome staged = await Service.StageAddMemberAsync(groupId, keyPackage);
        await Service.MergeStagedAsync(groupId);
        return staged;
    }

    public void Dispose()
    {
        Service.Dispose();
        _provider.Dispose();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A stray temp file is not worth failing a run over.
        }
    }
}
