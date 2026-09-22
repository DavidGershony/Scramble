using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Marmot.Storage.Sqlite;

namespace Scramble.Core.Services;

/// <summary>
/// Builds the <see cref="IMlsService"/> every UI head registers.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because the alternative was three.</b> Desktop, Android and
/// macOS each own an <c>MlsServiceFactory</c> delegate, and each would otherwise
/// spell out a connection string, a table prefix and an engine choice — three
/// copies of a decision that is not platform-specific, drifting independently.
/// The heads pass what only they know (their storage service) and nothing else.
/// </para>
/// <para>
/// <b>The engine is not selectable.</b> <c>--backend managed|rust</c> chose
/// between two <c>marmot-cs</c> backends, and after the cutover there is nothing
/// to choose: the flip is the decision. A fallback would also be a fiction —
/// the legacy backends need a kind-445 event signer this app no longer wires,
/// and switching engines abandons the group state anyway, which is what makes
/// a "fall back for one release" story untrue rather than merely unused.
/// </para>
/// </remarks>
public static class DarkMatterMlsServiceFactory
{
    /// <summary>
    /// Table prefix for the engine's own tables.
    /// </summary>
    /// <remarks>
    /// The engine shares the profile's database file rather than taking one of
    /// its own, so that a profile stays one file to back up, copy or delete —
    /// which is what the legacy engine did with <c>mls_</c>, and the reason the
    /// prefix exists at all. The two prefixes never collide, and after step 5
    /// only this one is left.
    /// </remarks>
    public const string TablePrefix = "marmot_";

    /// <summary>
    /// Creates the Dark Matter MLS service over a profile's database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Existing MLS groups do not come across, by decision.</b> The engine
    /// reads its own tables and nothing else, so a profile that had groups under
    /// the old engine opens with none. Account identity is untouched — nsec,
    /// contacts, relay lists and signer pairing live in
    /// <see cref="StorageService"/>, which never had an MLS dependency — so this
    /// is the same account with no groups, not a new account. See
    /// <c>ai-tasks/p11-cutover-plan-2026-09.md</c> §2.
    /// </para>
    /// <para>
    /// <b>The engine's rows are encrypted at rest by a decorator, not by the
    /// engine.</b> <see cref="SqliteMarmotStorageProvider"/> knows nothing about
    /// <see cref="ISecureStorage"/> — <c>Scramble.Marmot.*</c> is standalone and
    /// must not depend on <c>Scramble.Core</c>, where that interface lives — so
    /// <see cref="SecureMarmotStorageProvider"/> sits between it and the engine
    /// and puts MLS state, welcome data, message content and KeyPackage private
    /// material through the platform's protection (DPAPI, Android Keystore),
    /// which is what <c>EncryptedSqliteStorageProvider</c> did for the legacy
    /// engine. Wiring it here rather than inside the provider is what keeps that
    /// dependency direction intact. Which columns are protected, which are
    /// deliberately in the clear, and why an identifier cannot be encrypted at
    /// all, are recorded on that class and enforced by a column census in
    /// <c>SecureMarmotStorageProviderTests</c>.
    /// </para>
    /// </remarks>
    /// <param name="storageService">The active profile's storage service.</param>
    public static IMlsService Create(IStorageService storageService)
    {
        ArgumentNullException.ThrowIfNull(storageService);

        // Fail closed rather than build an unprotected store. IStorageService
        // documents SecureStorage as nullable "in tests", and under the legacy
        // engine a null there cost nothing the MLS store noticed — the encrypted
        // provider was registered separately. Now it decides whether ratchet
        // state and leaf private keys land on disk in the clear, and that is not
        // a decision to make by falling through. Every host and every caller in
        // the tree supplies one; a caller that genuinely wants an unprotected
        // ephemeral store can compose DarkMatterMlsService over a bare
        // SqliteMarmotStorageProvider and say so in the open.
        ISecureStorage secure = storageService.SecureStorage
            ?? throw new InvalidOperationException(
                "The Dark Matter MLS store cannot be created without an ISecureStorage: " +
                "the engine's rows carry MLS ratchet state and leaf private keys, and " +
                $"{nameof(SecureMarmotStorageProvider)} is what keeps them encrypted at rest. " +
                $"Pass one to {nameof(StorageService)}'s constructor.");

        var logger = LoggingConfiguration.CreateLogger<object>();

        // Empty means no profile database — a test or an unconfigured host. A
        // private in-memory database is the honest answer: the engine works and
        // nothing survives the process, which is what the caller has asked for
        // by having no path.
        var databasePath = string.IsNullOrEmpty(storageService.DatabasePath)
            ? ":memory:"
            : storageService.DatabasePath;

        var storage = new SecureMarmotStorageProvider(
            new SqliteMarmotStorageProvider($"Data Source={databasePath}", TablePrefix),
            secure);

        logger.LogInformation(
            "MLS backend: Dark Matter engine over {Database} (prefix {Prefix}), " +
            "protected at rest by {Protection}",
            databasePath == ":memory:" ? ":memory:" : "the profile database",
            TablePrefix,
            secure.GetType().Name);

        return new DarkMatterMlsService(storage);
    }
}
