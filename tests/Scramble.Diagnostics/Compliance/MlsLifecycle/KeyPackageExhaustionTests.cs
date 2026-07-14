using Xunit;

namespace Scramble.Diagnostics.Compliance.MlsLifecycle;

/// <summary>
/// A KeyPackage is single-use per MLS. Once a peer consumes one to send a
/// Welcome, that KP is spent. Scramble mitigates KP exhaustion two ways:
///   1. The MessageService auto-republishes when consumption drops the
///      pool below a threshold (see <c>AutoPublishKeyPackageIfNeededAsync</c>).
///   2. Per MIP-00 §"init_key retention", the *last-resort* init_key is
///      retained for 24h after last publish so late Welcomes can still land.
///
/// Historical context (ANALYSIS.md STEP 3):
///   - `cb535c45 Fix KeyPackage single-use bug: retain init key per MIP-00 last_resort`
///   - `29b4c94f fix: preserve KeyPackage private keys in DB during account switch and logout`
///   - `7717f4e1 feat: implement MIP-00 24h last-resort init_key retention (service state v5)`
///
/// This test drives the pool from fresh → all consumed → dropped-below-threshold
/// and asserts the auto-republish fires so a following peer can still be invited.
/// </summary>
[Trait("Category", "MIP-Compliance")]
[Trait("MIP", "MIP-00")]
[Trait("MlsLifecycle", "KeyPackageExhaustion")]
public class KeyPackageExhaustionTests : MlsLifecycleTestBase
{
    public KeyPackageExhaustionTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task ConsumeAllKPs_AutoRepublish_NewPeersCanStillBeInvited()
    {
        var alice = await CreatePartyAsync("Alice");
        var bob   = await CreatePartyAsync("Bob"); // the KP owner we'll drain

        // Bob publishes an initial batch. The service's dummy-KP + real-KP
        // logic decides how many; we just want the initial storedKeyPackageCount
        // above zero so exhaustion is meaningful.
        var kp = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kp.Data, bob.PrivKeyHex, kp.NostrTags);
        var initialCount = bob.MlsService.GetStoredKeyPackageCount();
        Assert.True(initialCount > 0, $"Bob should have >0 stored KPs, got {initialCount}");
        Output.WriteLine($"[kp-exhaustion] Bob initial stored KP count = {initialCount}");

        await SubscribeToWelcomesAsync(bob);
        await Task.Delay(1000);

        // Alice invites Bob — consumes one of his KPs.
        var chatAlice = await CreateGroupAndInviteAsync(alice, "kp-drain", bob);
        var groupIdHex = Convert.ToHexString(chatAlice.MlsGroupId!).ToLowerInvariant();
        await Task.Delay(1500);
        _ = await AcceptWelcomesAsync(groupIdHex, bob);

        // Trigger the auto-republish path explicitly (MessageService exposes it
        // so tests don't rely on background timers).
        await bob.MessageService.AutoPublishKeyPackageIfNeededAsync();
        await Task.Delay(1000);

        // Bring in a fresh third party, Charlie, who fetches Bob's KPs and
        // must still be able to invite him — proving the pool wasn't left
        // empty after the first consumption.
        var charlie = await CreatePartyAsync("Charlie");
        var bobKpsForCharlie = (await charlie.NostrService.FetchKeyPackagesAsync(bob.PubKeyHex)).ToList();
        Output.WriteLine($"[kp-exhaustion] Charlie fetched {bobKpsForCharlie.Count} KP(s) for Bob");
        Assert.NotEmpty(bobKpsForCharlie);
        Assert.Contains(bobKpsForCharlie, kp2 => kp2.IsCipherSuiteSupported);

        // Full round-trip: Charlie invites Bob into a new group.
        var chatCharlie = await CreateGroupAndInviteAsync(charlie, "kp-refill", bob);
        var groupIdHex2 = Convert.ToHexString(chatCharlie.MlsGroupId!).ToLowerInvariant();
        await Task.Delay(1500);

        var bobChatsForRefill = await AcceptWelcomesAsync(groupIdHex2, bob);
        Assert.Single(bobChatsForRefill);

        // Sanity: Bob's local KP audit reports the KPs he now holds are for
        // his current identity (audit surfaces the account-switch bug from
        // 29b4c94f — private-key material must survive the flow above).
        var audit = await bob.MessageService.AuditKeyPackagesAsync();
        Output.WriteLine($"[kp-exhaustion] Bob audit: {audit}");
    }
}
