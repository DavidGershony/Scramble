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
    /// <b>The engine's rows are not encrypted at rest, and the legacy engine's
    /// were.</b> <c>EncryptedSqliteStorageProvider</c> put MLS state, welcome
    /// data and message content through <see cref="ISecureStorage"/> (DPAPI,
    /// Android Keystore); <see cref="SqliteMarmotStorageProvider"/> writes
    /// <c>groups.live_state</c>, <c>key_packages.private_material</c>,
    /// <c>epoch_archive.group_state</c> and <c>messages.wire</c> as plain BLOBs.
    /// The mechanism survives the cutover and the engine simply does not call it.
    /// Closing that is a storage-layer change of its own — twelve sub-interfaces,
    /// where a missed field is a silent leak — so it is recorded as a release
    /// blocker rather than bolted onto the flip. Nothing ships from this branch
    /// yet, which is the only reason that ordering is acceptable.
    /// </para>
    /// </remarks>
    /// <param name="storageService">The active profile's storage service.</param>
    public static IMlsService Create(IStorageService storageService)
    {
        ArgumentNullException.ThrowIfNull(storageService);

        var logger = LoggingConfiguration.CreateLogger<object>();

        // Empty means no profile database — a test or an unconfigured host. A
        // private in-memory database is the honest answer: the engine works and
        // nothing survives the process, which is what the caller has asked for
        // by having no path.
        var databasePath = string.IsNullOrEmpty(storageService.DatabasePath)
            ? ":memory:"
            : storageService.DatabasePath;

        var storage = new SqliteMarmotStorageProvider(
            $"Data Source={databasePath}", TablePrefix);

        logger.LogInformation(
            "MLS backend: Dark Matter engine over {Database} (prefix {Prefix})",
            databasePath == ":memory:" ? ":memory:" : "the profile database",
            TablePrefix);

        return new DarkMatterMlsService(storage);
    }
}
