using Scramble.Core.Models;
using Scramble.Core.Services;

namespace Scramble.Diagnostics.TestHelpers;

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
/// Tests that reach past it to <c>INostrService</c> directly were doing only the
/// first, which was invisible while the engine had no binding to look up and
/// became a failed invite at P11's flip. It reads as "the private key is no
/// longer available", because that is how <c>AcceptInviteAsync</c> reports any
/// refusal naming a KeyPackage.
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
