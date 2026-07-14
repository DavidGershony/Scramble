using Moq;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Enumerated state-machine tests for the LoginViewModel signer subscription.
///
/// Historical context (ANALYSIS.md STEP 4):
///   * <c>ExternalSignerService.cs</c> — 59% fix-density (top of the repo)
///   * <c>LoginViewModel.cs</c>          — 62% fix-density
///   * <c>MainViewModel.cs</c>           — 46% fix-density
///
/// Cluster analysis in STEP 3 (C10 / C11) shows almost every fix on the
/// signer trio handled a specific state-transition edge case that the
/// previous fix hadn't considered — the classic signature of a state
/// machine that has no test enumerating its transition space.
///
/// This file enumerates a set of "hazardous" transition sequences. Each is
/// a concrete <c>[Theory]</c> row — no property-testing library dependency
/// is introduced. New signer bugs should either be caught by an existing
/// row or, if they escape, added as a new row plus a matching regression
/// test in <c>SignerKnownBugsTests</c>.
/// </summary>
public class SignerStateMachineTests
{
    // ── Named transition kinds ──────────────────────────────────────────────
    // Public so xUnit's TheoryData can carry Step[] across accessibility boundaries.
    public enum Step
    {
        Disconnected,
        Connecting,
        WaitingForApproval,
        ConnectedWithPubKey,
        ConnectedWithNullPubKey,
        Error
    }

    private static LoginViewModel CreateLoginViewModel(IExternalSigner signer)
    {
        var nostr = new Mock<INostrService>();
        nostr.Setup(n => n.GenerateKeyPair())
             .Returns(("priv", "pub", "nsec1", "npub1"));
        var qr = new Mock<IQrCodeGenerator>();
        return new LoginViewModel(nostr.Object, qr.Object, externalSigner: signer);
    }

    private static async Task DriveAsync(
        MockExternalSigner signer, string signingPubKey, IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case Step.Disconnected:
                    signer.EmitStatus(ExternalSignerState.Disconnected); break;
                case Step.Connecting:
                    signer.EmitStatus(ExternalSignerState.Connecting); break;
                case Step.WaitingForApproval:
                    signer.EmitStatus(ExternalSignerState.WaitingForApproval); break;
                case Step.ConnectedWithPubKey:
                    signer.EmitStatus(ExternalSignerState.Connected, signingPubKey); break;
                case Step.ConnectedWithNullPubKey:
                    signer.EmitStatus(ExternalSignerState.Connected, publicKeyHex: null); break;
                case Step.Error:
                    signer.EmitStatus(ExternalSignerState.Error, error: "boom"); break;
            }
            // Give the reactive continuation a chance to run — the LoginViewModel
            // signer subscription is `async _ => await Handle…`.
            await Task.Delay(50);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //   Sequences that must end with LoggedInUser correctly populated.
    // ═══════════════════════════════════════════════════════════════════════

    // Underlying arrays are the source of truth for both [Theory] rows and the
    // back-to-back exception check further down. xUnit v3's TheoryData row
    // type is opaque (not object[]), so iterating the TheoryData directly is
    // brittle — hold the arrays here and project into TheoryData on demand.
    internal static readonly (string Label, Step[] Steps)[] HappyPathData =
    {
        ("connect-direct",
          new[] { Step.Disconnected, Step.Connecting, Step.ConnectedWithPubKey }),
        ("connect-via-waiting",
          new[] { Step.Disconnected, Step.Connecting, Step.WaitingForApproval, Step.ConnectedWithPubKey }),
        ("retry-after-error",
          new[] { Step.Disconnected, Step.Connecting, Step.Error, Step.Connecting, Step.ConnectedWithPubKey }),
        ("duplicate-connecting",
          new[] { Step.Disconnected, Step.Connecting, Step.Connecting, Step.ConnectedWithPubKey }),
        ("connected-null-then-real",
          new[] { Step.Disconnected, Step.ConnectedWithNullPubKey, Step.ConnectedWithPubKey }),
    };

    public static TheoryData<string, Step[]> HappyPathSequences
    {
        get
        {
            var td = new TheoryData<string, Step[]>();
            foreach (var (label, steps) in HappyPathData) td.Add(label, steps);
            return td;
        }
    }

    [Theory]
    [MemberData(nameof(HappyPathSequences))]
    public async Task Sequence_EndsWithConnectedPubKey_LoggedInUser_IsSet(
        string label, Step[] steps)
    {
        var signerPubKey = new string('c', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .WithBunkerSession(remotePubKey: signerPubKey, secret: "s")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        await DriveAsync(signer, signerPubKey, steps);

        Assert.NotNull(vm.LoggedInUser);
        Assert.Equal(signerPubKey, vm.LoggedInUser!.PublicKeyHex);
        Assert.True(vm.LoggedInUser.IsRemoteSigner, $"[{label}] IsRemoteSigner must be true after connect");
        Assert.Equal("Connected!", vm.ExternalSignerStatus);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //   Sequences that must NOT set LoggedInUser (bug cbece702, 5d29b6cb).
    // ═══════════════════════════════════════════════════════════════════════

    internal static readonly (string Label, Step[] Steps)[] NeverConnectedData =
    {
        ("disconnect-only",
          new[] { Step.Disconnected }),
        ("disconnect-then-error",
          new[] { Step.Disconnected, Step.Error }),
        ("connecting-then-error",
          new[] { Step.Disconnected, Step.Connecting, Step.Error }),
        ("waiting-then-error",
          new[] { Step.Disconnected, Step.Connecting, Step.WaitingForApproval, Step.Error }),
    };

    public static TheoryData<string, Step[]> NeverConnectedSequences
    {
        get
        {
            var td = new TheoryData<string, Step[]>();
            foreach (var (label, steps) in NeverConnectedData) td.Add(label, steps);
            return td;
        }
    }

    [Theory]
    [MemberData(nameof(NeverConnectedSequences))]
    public async Task Sequence_NeverConnects_LoggedInUser_StaysNull(string label, Step[] steps)
    {
        var signerPubKey = new string('c', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .WithBunkerSession(remotePubKey: signerPubKey, secret: "s")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        await DriveAsync(signer, signerPubKey, steps);

        Assert.Null(vm.LoggedInUser);
        // The visible status must reflect the terminal state — never a stale "Connected!".
        Assert.NotEqual("Connected!", vm.ExternalSignerStatus);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //   Idempotency: once connected, later Connected events must not
    //   clobber LoggedInUser with a different pubkey (identity-swap guard).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconnect_WithSamePubKey_LoggedInUser_UnchangedIdentity()
    {
        var signerPubKey = new string('c', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .WithBunkerSession(remotePubKey: signerPubKey, secret: "s")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        signer.EmitStatus(ExternalSignerState.Connected, signerPubKey);
        await Task.Delay(80);
        Assert.NotNull(vm.LoggedInUser);
        var idBefore = vm.LoggedInUser!.PublicKeyHex;

        // Simulate app-suspend → resume → reconnect.
        signer.EmitStatus(ExternalSignerState.Disconnected);
        await Task.Delay(50);
        signer.EmitStatus(ExternalSignerState.Connecting);
        await Task.Delay(50);
        signer.EmitStatus(ExternalSignerState.Connected, signerPubKey);
        await Task.Delay(80);

        Assert.NotNull(vm.LoggedInUser);
        Assert.Equal(idBefore, vm.LoggedInUser!.PublicKeyHex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //   Cross-cutting: no sequence may cause an exception to escape into
    //   the subscription. If Handle… threw silently in the past, this row
    //   forces the same regression to surface as a test failure. Runs
    //   every combined sequence back-to-back on a single VM.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllSequences_BackToBack_DoNotBubbleExceptions()
    {
        var signerPubKey = new string('c', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .WithBunkerSession(remotePubKey: signerPubKey, secret: "s")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        Exception? caught = null;
        signer.StatusController.Subscribe(
            _ => { },
            ex => caught = ex);

        // Iterate the underlying arrays directly (not the TheoryData — v3's
        // row type is opaque, so a .Cast<object[]>() from an earlier version
        // of this test threw InvalidCastException).
        foreach (var (_, steps) in HappyPathData)
            await DriveAsync(signer, signerPubKey, steps);
        foreach (var (_, steps) in NeverConnectedData)
            await DriveAsync(signer, signerPubKey, steps);

        Assert.Null(caught);
    }
}
