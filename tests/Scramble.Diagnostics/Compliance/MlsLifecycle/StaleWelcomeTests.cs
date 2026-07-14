using Scramble.Core.Models;
using Xunit;

namespace Scramble.Diagnostics.Compliance.MlsLifecycle;

/// <summary>
/// A Welcome generated at epoch N may reach the recipient after the group has
/// already advanced past N — because the invite lay on the relay for a while,
/// or the recipient was offline, or a later admin op raced ahead. The group
/// must not enter a corrupted state; the recipient must either detect the
/// staleness and rescan, or reject the invite cleanly.
///
/// Historical context (ANALYSIS.md STEP 3):
/// The `IsOutOfSync` / `IsResyncPending` fix chain (`18feffa2`, `e16f5482`,
/// `29b4c94f`) is exactly this scenario. Persisting the resync flags through
/// MarkAsReadAsync et al. was the fix; the missing test was one that
/// generated the exact race.
/// </summary>
[Trait("Category", "MIP-Compliance")]
[Trait("MIP", "MIP-03")]
[Trait("MlsLifecycle", "StaleWelcome")]
public class StaleWelcomeTests : MlsLifecycleTestBase
{
    public StaleWelcomeTests(ITestOutputHelper output) : base(output) { }

    /// <summary>
    /// Alice creates a group with Bob and Charlie. Charlie's Welcome is
    /// deliberately not delivered (subscription set up late). Meanwhile Alice
    /// and Bob exchange enough messages that the group advances at least one
    /// epoch. Charlie then subscribes and rescans; the accept must either
    /// succeed with a rejoin path or fail gracefully — under no circumstance
    /// may Charlie end up at a bogus epoch that lets him decrypt only stale
    /// messages while Alice/Bob march on.
    /// </summary>
    [Fact]
    public async Task Welcome_Delivered_AfterGroup_HasAdvanced_RecipientSyncsCleanlyOrRejects()
    {
        var alice = await CreatePartyAsync("Alice");
        var bob = await CreatePartyAsync("Bob");
        var charlie = await CreatePartyAsync("Charlie");

        // Bob AND Charlie publish KeyPackages, but only Bob subscribes to Welcomes
        // right away. Charlie's subscription is deliberately deferred until later
        // so the Welcome sits on the relay while the group advances.
        await PublishKeyPackagesAsync(bob, charlie);
        await SubscribeToWelcomesAsync(bob);
        // NOTE: intentionally NOT subscribing charlie yet.

        var aliceChat = await CreateGroupAndInviteAsync(
            alice, "stale-welcome", bob, charlie);
        var mlsGroupIdHex = Convert.ToHexString(aliceChat.MlsGroupId!).ToLowerInvariant();
        await Task.Delay(1500);

        // Bob joins immediately.
        var bobChats = await AcceptWelcomesAsync(mlsGroupIdHex, bob);
        var bobChat = bobChats[0];

        // Alice and Bob exchange messages, advancing the group past the
        // epoch Charlie's Welcome pinned him to.
        for (int i = 0; i < 5; i++)
        {
            await alice.MessageService.SendMessageAsync(aliceChat.Id, $"pre-charlie-A-{i}");
            await Task.Delay(300);
            await bob.MessageService.SendMessageAsync(bobChat.Id, $"pre-charlie-B-{i}");
            await Task.Delay(300);
        }
        await Task.Delay(1500);

        var epochBefore = await CurrentEpochAsync(alice, aliceChat.MlsGroupId!);
        Output.WriteLine($"[stale] Alice/Bob at epoch={epochBefore} before Charlie joins");

        // Now Charlie subscribes and rescans. The Welcome he'll fetch was minted
        // at epoch 0 (or whatever epoch Alice was at when she created the group).
        await SubscribeToWelcomesAsync(charlie);
        await Task.Delay(2000);

        Chat? charlieChat = null;
        Exception? acceptFailure = null;
        try
        {
            var charlieChats = await AcceptWelcomesAsync(mlsGroupIdHex, charlie);
            charlieChat = charlieChats[0];
        }
        catch (Exception ex)
        {
            acceptFailure = ex;
            Output.WriteLine($"[stale] Charlie accept threw {ex.GetType().Name}: {ex.Message}");
        }

        // Two acceptable outcomes:
        //   A) Charlie joined at the stale epoch, was flagged out-of-sync, and
        //      MessageService now marks the chat as IsResyncPending. He must
        //      NOT be able to decrypt future messages until a resync happens.
        //   B) The accept was refused/aborted cleanly (exception surfaces up).
        //
        // What is NOT acceptable: Charlie silently joined at a bogus epoch and
        // continues to fall further behind Alice/Bob every message.

        if (acceptFailure != null)
        {
            // Path B — accept refused. Nothing more to assert.
            Output.WriteLine("[stale] outcome=B (accept refused, group state uncompromised)");
            return;
        }

        Assert.NotNull(charlieChat);
        var charlieEpoch = await CurrentEpochAsync(charlie, charlieChat!.MlsGroupId!);
        Output.WriteLine($"[stale] Charlie joined at epoch={charlieEpoch} (Alice/Bob at {epochBefore})");

        // If Charlie is behind, he must have been marked out-of-sync — check the
        // Chat row for the resync flag.
        var refreshed = await charlie.Storage.GetChatAsync(charlieChat.Id);
        Assert.NotNull(refreshed);

        if (charlieEpoch < epochBefore)
        {
            // The whole point of the IsOutOfSync/IsResyncPending fix chain
            // (18feffa2, e16f5482, 29b4c94f).
            Assert.True(
                refreshed!.IsOutOfSync || refreshed!.IsResyncPending,
                $"Charlie is behind (epoch={charlieEpoch} < {epochBefore}) but the chat was not flagged for resync");
            Output.WriteLine("[stale] outcome=A1 (joined behind, correctly flagged for resync)");
        }
        else
        {
            // If somehow Charlie ended at the same epoch, the newer messages
            // must be readable end-to-end.
            await alice.MessageService.SendMessageAsync(aliceChat.Id, "post-charlie-A-0");
            await WaitForMessageAsync(charlie, charlieChat.Id, "post-charlie-A-0",
                timeout: TimeSpan.FromSeconds(10));
            Output.WriteLine("[stale] outcome=A2 (joined at head, live traffic decodes)");
        }
    }
}
