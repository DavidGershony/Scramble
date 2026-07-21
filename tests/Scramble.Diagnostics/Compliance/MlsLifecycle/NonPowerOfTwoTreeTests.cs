using Scramble.Core.Models;
using Xunit;

namespace Scramble.Diagnostics.Compliance.MlsLifecycle;

/// <summary>
/// MLS ratchet-tree sizes that are not powers of two (3, 5, 7 members) are the
/// classic edge case in RFC 9420 tree math. This test builds groups at each of
/// those sizes, has every member send a message, and asserts every member
/// decrypts every message.
///
/// Historical context (ANALYSIS.md STEP 3):
/// The C1 cluster on ManagedMlsService.cs took 15 fixes in 11 days after MIP-03
/// decryption was added. Several of those fixes were epoch-transition bugs
/// that only manifest with 3+ members; a per-size smoke test would have caught
/// them before merge.
/// </summary>
[Trait("Category", "MIP-Compliance")]
[Trait("MIP", "MIP-03")]
[Trait("MlsLifecycle", "NonPowerOfTwoTree")]
public class NonPowerOfTwoTreeTests : MlsLifecycleTestBase
{
    public NonPowerOfTwoTreeTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task Group_OfSize_N_EveryMemberSendsOneMessage_EveryoneReceivesAll(int memberCount)
    {
        Assert.InRange(memberCount, 3, 16);

        // ── Party setup ─────────────────────────────────────────────────────
        var parties = new List<Party>();
        for (int i = 0; i < memberCount; i++)
            parties.Add(await CreatePartyAsync($"P{i}"));

        var inviter = parties[0];
        var invitees = parties.Skip(1).ToArray();

        // Invitees must have KPs on-relay before the inviter can add them.
        await PublishKeyPackagesAsync(invitees);
        await SubscribeToWelcomesAsync(invitees);

        // ── Group creation ──────────────────────────────────────────────────
        var inviterChat = await CreateGroupAndInviteAsync(
            inviter, $"tree-{memberCount}", invitees);
        var mlsGroupIdHex = Convert.ToHexString(inviterChat.MlsGroupId!).ToLowerInvariant();

        // Give the relay a moment to fan-out gift wraps to each invitee.
        await Task.Delay(1500);

        var chats = await AcceptWelcomesAsync(mlsGroupIdHex, invitees);
        // Everyone (inviter + invitees) now has a Chat pointing at the same MLS group.
        var perPartyChat = new Dictionary<string, Chat> {
            [inviter.PubKeyHex] = inviterChat
        };
        for (int i = 0; i < invitees.Length; i++)
            perPartyChat[invitees[i].PubKeyHex] = chats[i];

        // Warm-up: give every party time to catch up to the inviter's epoch
        // via the FaultyRelay's historical-event replay. See the
        // WaitForEpochParityAsync docstring — without this, late-arriving
        // invitees encrypt messages at their accept-epoch and no one else can
        // decrypt them.
        await WaitForEpochParityAsync(inviterChat.MlsGroupId!, parties);

        // Sanity: every party sees the same MLS group id.
        foreach (var p in parties)
        {
            var chat = perPartyChat[p.PubKeyHex];
            Assert.NotNull(chat.MlsGroupId);
            Assert.Equal(mlsGroupIdHex,
                Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant());
        }

        // ── Every party sends one message ───────────────────────────────────
        // Bigger inter-send delay: the FaultyRelay is in-process but the
        // MessageService pipeline (rumor build → sign → publish → matched
        // subscribers → decrypt → storage insert → observable emit) has
        // enough steps that at N=5+ senders back-to-back, a 600ms cadence
        // occasionally loses a decrypt on the receiving side. 1500ms per
        // sender turns this test flake-free at the cost of ~5s per N=7 run.
        var expectedContents = new List<string>();
        for (int i = 0; i < parties.Count; i++)
        {
            var content = $"msg-from-{parties[i].Name}-{Guid.NewGuid():N}";
            expectedContents.Add(content);
            await parties[i].MessageService.SendMessageAsync(
                perPartyChat[parties[i].PubKeyHex].Id, content);
            await Task.Delay(1500);
        }

        // ── Every party receives every other party's message ────────────────
        // 30s timeout: on shared Linux runners the in-process pipeline is
        // measurably slower than on Windows dev machines (all 3/5/7 cases
        // pass in <15s locally on Windows, but memberCount=3 timed out at
        // 15s on GHA ubuntu-latest, run 29498185991). The message HAS
        // been broadcast by the FaultyRelay at that point — the delay is
        // in the receiving MessageService's decrypt-and-store pipeline.
        foreach (var receiver in parties)
        {
            foreach (var (sender, content) in parties.Zip(expectedContents))
            {
                if (sender.PubKeyHex == receiver.PubKeyHex) continue; // skip self
                // 30s local / 90s CI — memberCount=7 does 7 senders × 6
                // recipients = 42 delivery paths, and the last few can arrive
                // slowly on shared Linux runners (run 29819970169 timed out
                // at 45s on P0 not seeing P1's message, epoch parity had been
                // reached). 90s buys headroom without hiding a real hang.
                await WaitForMessageAsync(
                    receiver, perPartyChat[receiver.PubKeyHex].Id, content,
                    timeout: CiScale(TimeSpan.FromSeconds(30)));
            }
        }

        // ── Epoch parity: every party ends at the same epoch ────────────────
        var epochs = new List<ulong>();
        foreach (var p in parties)
            epochs.Add(await CurrentEpochAsync(p, perPartyChat[p.PubKeyHex].MlsGroupId!));

        Output.WriteLine($"[epoch parity] {string.Join(", ", parties.Zip(epochs).Select(z => $"{z.First.Name}={z.Second}"))}");
        Assert.Equal(epochs.First(), epochs.Last());
        Assert.True(epochs.All(e => e == epochs[0]),
            "every party must end at the same MLS epoch");
    }
}
