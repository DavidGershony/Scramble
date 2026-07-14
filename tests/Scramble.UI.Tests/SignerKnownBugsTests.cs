using Moq;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Regression bank for historical signer-related bugs. Every fix commit
/// that touched <c>ExternalSignerService.cs</c>, <c>LoginViewModel.cs</c>,
/// or <c>MainViewModel.cs</c> in the signer / login flow should either:
///   * be reproducible via one of these facts (added when the fix lands), or
///   * be captured by a row in <see cref="SignerStateMachineTests"/>.
///
/// New fixes to the signer trio should extend this file. The // see:
/// comment on each fact names the commit whose behaviour is locked in.
///
/// If a fact fails after a signer change, the fix has regressed. Do not
/// delete the fact — restore the behaviour it asserts.
/// </summary>
public class SignerKnownBugsTests
{
    private static LoginViewModel CreateLoginViewModel(IExternalSigner signer)
    {
        var nostr = new Mock<INostrService>();
        nostr.Setup(n => n.GenerateKeyPair())
             .Returns(("priv", "pub", "nsec1", "npub1"));
        var qr = new Mock<IQrCodeGenerator>();
        return new LoginViewModel(nostr.Object, qr.Object, externalSigner: signer);
    }

    /// <summary>
    /// Bug: <c>cbece702 Fix: don't set unconnected external signer on app restart</c>
    ///
    /// Reproduction: on app resume, the signer briefly emits a Disconnected
    /// status before the reconnect handshake completes. LoginViewModel used
    /// to set LoggedInUser off any status emission — even Disconnected —
    /// with whatever PublicKeyHex the signer happened to still have from a
    /// previous session. Fix: LoggedInUser is only set on a Connected event.
    ///
    /// Assertion: emit a lone Disconnected status carrying a valid pubkey.
    /// LoggedInUser must remain null.
    /// </summary>
    [Fact]
    public async Task Bug_cbece702_Disconnected_Emission_DoesNotSet_LoggedInUser()
    {
        var pubKey = new string('c', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(pubKey)
            .WithBunkerSession(remotePubKey: pubKey, secret: "s")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        // ExternalSignerService in the past emitted PublicKeyHex on every
        // status payload, including Disconnected. Simulate exactly that.
        signer.EmitStatus(ExternalSignerState.Disconnected, pubKey);
        await Task.Delay(150);

        Assert.Null(vm.LoggedInUser);
    }

    /// <summary>
    /// Bug: <c>Amber-on-Android status Connected but pubkey null</c>
    /// (fix: LoginViewModel falls back to signer.PublicKeyHex; see the
    /// existing test <c>LoginViewModelSignerLoginTests.SignerStatus_Connected_WithNullPubKey_FallsBackToSignerProperty</c>).
    ///
    /// This copy is deliberate duplication — the regression bank owns
    /// provenance for the bug, so a future rename/refactor of the other
    /// file doesn't silently drop the coverage.
    ///
    /// Assertion: Connected with PublicKeyHex=null in the status payload
    /// still results in LoggedInUser populated from signer.PublicKeyHex.
    /// </summary>
    [Fact]
    public async Task Bug_AmberNullPubKey_FallsBack_ToSignerProperty()
    {
        var pubKey = new string('d', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(pubKey) // signer.PublicKeyHex is set
            .WithBunkerSession(remotePubKey: pubKey, secret: "nc-secret")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        // The offending path emitted Connected with a null pubkey payload —
        // fall-through to signer.PublicKeyHex must still yield the identity.
        signer.EmitStatus(ExternalSignerState.Connected, publicKeyHex: null);
        await Task.Delay(200);

        Assert.NotNull(vm.LoggedInUser);
        Assert.Equal(pubKey, vm.LoggedInUser!.PublicKeyHex);
    }

    /// <summary>
    /// Bug: <c>074f0188 Resolve actual signing pubkey on Amber/NIP-46 login (fix wrong-npub bug)</c>
    ///
    /// Reproduction: Amber's transport pubkey (the "remote pubkey" from the
    /// bunker URI) can differ from its signing pubkey. Older code stored the
    /// transport pubkey as the user's identity, so messages were signed under
    /// one key and displayed under another. Fix: LoginViewModel resolves the
    /// real signing pubkey via GetPublicKeyAsync / ResolveSigningPubKeyAsync
    /// before setting LoggedInUser.
    ///
    /// Assertion: when signer.RemotePubKey ≠ GetPublicKeyAsync response,
    /// LoggedInUser.PublicKeyHex reflects the signing key, not the transport key.
    /// </summary>
    [Fact]
    public async Task Bug_074f0188_ResolvesActualSigningKey_NotTransportKey()
    {
        var transportPubKey = new string('a', 64);
        var signingPubKey   = new string('b', 64);

        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signingPubKey)   // signer's own PublicKeyHex
            .WithGetPublicKeyResponse(signingPubKey)  // NIP-46 get_public_key
            .WithBunkerSession(remotePubKey: transportPubKey, secret: "nc-secret")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        signer.EmitStatus(ExternalSignerState.Connected, signingPubKey);
        await Task.Delay(200);

        Assert.NotNull(vm.LoggedInUser);
        // The identity stored on the User must be the signing key, not the
        // transport (bunker) key. Getting this wrong is the wrong-npub bug.
        Assert.Equal(signingPubKey, vm.LoggedInUser!.PublicKeyHex);
        Assert.NotEqual(transportPubKey, vm.LoggedInUser.PublicKeyHex);
    }

    /// <summary>
    /// Bug: <c>07066ca8 Fix profile-DB corruption: stop overwriting User.PublicKeyHex on signer restore</c>
    ///
    /// Reproduction: on session-restore, the signer fires Connected with
    /// whatever pubkey came back over the restored WebSocket. If the user
    /// had multiple accounts and the wrong session was restored, the
    /// current user's PublicKeyHex would be overwritten in the DB with a
    /// different identity. Fix: LoggedInUser is only populated by an
    /// interactive login; restore paths must go through the ShellViewModel
    /// account guard.
    ///
    /// Assertion: a Connected event that arrives immediately on VM
    /// construction (as during restore) still populates LoggedInUser with
    /// the pubkey the signer reports — but it must be a fresh User instance
    /// (i.e., not mutated from a previously-set LoggedInUser).
    /// </summary>
    [Fact]
    public async Task Bug_07066ca8_Restore_YieldsFreshUser_DoesNotMutatePrior()
    {
        var firstPubKey  = new string('1', 64);
        var secondPubKey = new string('2', 64);

        // Session 1 signer
        var signer1 = new MockExternalSignerBuilder()
            .WithSigningPubKey(firstPubKey)
            .WithBunkerSession(remotePubKey: firstPubKey, secret: "s1")
            .Build();
        var vm = CreateLoginViewModel(signer1.Object);
        signer1.EmitStatus(ExternalSignerState.Connected, firstPubKey);
        await Task.Delay(150);
        var user1 = vm.LoggedInUser;
        Assert.NotNull(user1);
        Assert.Equal(firstPubKey, user1!.PublicKeyHex);

        // Session 2 restore fires on the same VM with a different identity —
        // must not silently mutate the previously-set User. In practice the
        // VM won't be reused across restores; this asserts the guard.
        signer1.EmitStatus(ExternalSignerState.Connected, secondPubKey);
        await Task.Delay(150);

        // Whatever the current LoggedInUser is, the *original* User object we
        // captured must not have been mutated in place. That's the corruption
        // path the fix closed.
        Assert.Equal(firstPubKey, user1.PublicKeyHex);
    }
}
