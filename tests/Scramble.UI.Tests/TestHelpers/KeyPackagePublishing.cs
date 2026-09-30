using Scramble.Core.Models;
using Scramble.Core.Services;

namespace Scramble.UI.Tests.TestHelpers;

/// <summary>
/// Publishing a KeyPackage the way the app does it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Publishing is two steps, and skipping the second one makes the account
/// uninvitable.</b> A Welcome names the KeyPackage it consumed by its kind-30443
/// event id, and <see cref="IMlsService.MarkKeyPackagePublishedAsync"/> is what
/// binds that id to the private material this device kept. Without the binding
/// the engine fails closed on the join — correctly: a Welcome naming a KeyPackage
/// this device never published has not been admitted by us.
/// </para>
/// <para>
/// <c>MessageService.AutoPublishKeyPackageIfNeededAsync</c> does both steps.
/// Tests that reach past it to <see cref="INostrService"/> directly do only the
/// first, which is invisible until a peer actually invites the account.
/// </para>
/// <para>
/// <b>A deliberate copy of the same helper in <c>Scramble.Diagnostics</c>.</b>
/// That project is not referenced from here, and adding a reference to a test
/// project to share forty lines would couple two suites that are otherwise
/// independent — the same trade already made for <c>MockSecureStorage</c>. If
/// the binding contract changes, both copies have to change; the doc comment
/// above is the reason each exists, so neither can be deleted as redundant
/// without noticing.
/// </para>
/// </remarks>
internal static class KeyPackagePublishing
{
    /// <summary>Publishes <paramref name="keyPackage"/> and binds its event id.</summary>
    /// <returns>The kind-30443 event id it went out under.</returns>
    public static async Task<string> PublishAndBindAsync(
        INostrService nostr,
        IMlsService mls,
        KeyPackage keyPackage,
        string privateKeyHex)
    {
        ArgumentNullException.ThrowIfNull(nostr);
        ArgumentNullException.ThrowIfNull(mls);
        ArgumentNullException.ThrowIfNull(keyPackage);

        string eventId = await nostr.PublishKeyPackageAsync(
            keyPackage.Data, privateKeyHex, keyPackage.NostrTags);

        keyPackage.NostrEventId = eventId;
        await mls.MarkKeyPackagePublishedAsync(keyPackage, eventId);

        return eventId;
    }
}
