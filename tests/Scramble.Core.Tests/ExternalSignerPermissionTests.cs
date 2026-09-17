using System.Text.RegularExpressions;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// The NIP-46 permission grant, against what the app actually signs.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the two drifted apart and nothing noticed. The grant
/// asked for <c>443, 444, 445, 1059</c> while the app signs eleven kinds, only
/// one of which was on that list — and 443 is signed by nothing at all.
/// </para>
/// <para>
/// <b>It survived because the failure is soft.</b> <c>perms</c> is a request,
/// not an enforcement boundary; Amber prompts for an ungranted kind rather than
/// refusing it. So a missing grant showed up as an approval dialog on every
/// KeyPackage publish, which reads as the signer being noisy rather than as a
/// bug here.
/// </para>
/// </remarks>
public class ExternalSignerPermissionTests
{
    private static string Perms()
    {
        string uri = new ExternalSignerService()
            .GenerateConnectionUri(["wss://relay.example.com"]);

        Match match = Regex.Match(uri, @"[?&]perms=([^&]*)");
        Assert.True(match.Success, $"No perms parameter in the URI: {uri}");

        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [Theory]
    // The three the cutover turns on, named individually so a failure says
    // which one rather than "a list differs".
    [InlineData(30443)] // KeyPackage -- without it, nobody can invite us
    [InlineData(450)]   // account-identity proof
    [InlineData(445)]   // group message
    [InlineData(1059)]  // gift wrap: how a Welcome reaches us
    [InlineData(0)]     // profile metadata
    public void TheGrantCoversTheKindWeSign(int kind)
    {
        Assert.Contains($"sign_event:{kind}", Perms());
    }

    [Fact]
    public void TheGrantCoversEveryKindTheAppDeclares()
    {
        string perms = Perms();

        foreach (int kind in ExternalSignerService.SignedKinds)
            Assert.Contains($"sign_event:{kind}", perms);
    }

    [Fact]
    public void TheGrantAsksForNothingTheAppDoesNotSign()
    {
        // The other direction, and the one that caught 443. An over-broad grant
        // is a worse ask than a narrow one: the user is approving authority
        // this app has no use for, and cannot tell that from the prompt.
        foreach (Match match in Regex.Matches(Perms(), @"sign_event:(\d+)"))
        {
            int kind = int.Parse(match.Groups[1].Value);

            Assert.True(
                ExternalSignerService.SignedKinds.Contains(kind),
                $"The grant asks to sign kind {kind}, which this app never signs.");
        }
    }

    [Fact]
    public void TheEncryptionPermissionsSurvive()
    {
        // Rebuilding the string from a kind list is how the sign_event entries
        // are generated now; these four are not kinds and would be easy to drop
        // in that rewrite.
        string perms = Perms();

        Assert.Contains("nip44_encrypt", perms);
        Assert.Contains("nip44_decrypt", perms);
        Assert.Contains("nip04_encrypt", perms);
        Assert.Contains("nip04_decrypt", perms);
    }
}
