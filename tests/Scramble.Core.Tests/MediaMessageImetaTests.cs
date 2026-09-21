using System.Text.Json;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;
namespace Scramble.Core.Tests;

/// <summary>
/// Verifies that MLS-encrypted media messages include imeta tags in the rumor
/// so cross-implementation clients can download and decrypt the media.
/// </summary>
public class MediaMessageImetaTests
{
    private readonly ITestOutputHelper _output;

    public MediaMessageImetaTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// When a media message is sent, the encrypted rumor (kind 9) must include
    /// an imeta tag with the URL, mime type, SHA-256 hash, nonce, and encryption
    /// version. Without this, the web client only sees placeholder text like
    /// "[Encrypted image: file.jpg]".
    /// </summary>
    [Fact]
    public async Task EncryptMessageAsync_WithMediaTags_IncludesImetaInRumor()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);

        // Two engines, two stores: a store is a device, and a Welcome only means
        // anything if the side opening it kept its own KeyPackage material.
        using var a = await MlsTestEngine.StartAsync("imeta-a");
        using var b = await MlsTestEngine.StartAsync("imeta-b");

        var mlsA = a.Service;
        var mlsB = b.Service;

        // Create group and add member
        var group = await mlsA.CreateGroupAsync("Test", new[] { "wss://relay.test" });

        // Signed for real and bound on B. The old fixture handed the service an
        // event with the id "fake" and "fake" for a signature, which the previous
        // engine accepted; this one verifies the envelope before it will read the
        // invitee's account key out of it.
        var kp = await b.PublishKeyPackageAsync();

        var welcome = await a.AddMemberAsync(group.GroupId, kp);
        await mlsB.ProcessWelcomeAsync(
            welcome.WelcomeData, new string('0', 64), kp.NostrEventId);

        // Build imeta tags for a media message
        var mediaUrl = "https://blossom.primal.net/abc123def456";
        var sha256 = "a0528807e6b22980";
        var nonce = "deadbeef12345678abcd";
        var mimeType = "image/jpeg";
        var filename = "photo.jpg";

        var tags = new List<List<string>>
        {
            new() { "imeta",
                $"url {mediaUrl}",
                $"m {mimeType}",
                $"x {sha256}",
                $"n {nonce}",
                $"v mip04-v2",
                $"filename {filename}" }
        };

        // Content is empty per MIP-04 — metadata is in imeta tags
        var eventJson = await mlsA.EncryptMessageAsync(
            group.GroupId, "", tags);

        // Decrypt on the other side, and hand over the whole kind-445 event
        // rather than the ciphertext inside it. The peeler is the only thing that
        // verifies an id and a signature, so an envelope stripped down to its
        // content is attacker-chosen routing with the check removed -- this
        // engine refuses it (ignored/InvalidEncoding) instead of guessing.
        var decrypted = await mlsB.DecryptMessageAsync(group.GroupId, eventJson);
        _output.WriteLine($"Decrypted content: {decrypted.Plaintext}");

        // Parse the decrypted rumor to check tags
        Assert.NotNull(decrypted.RumorJson);
        using var rumorDoc = JsonDocument.Parse(decrypted.RumorJson!);
        var rumor = rumorDoc.RootElement;

        var rumorTags = rumor.GetProperty("tags");
        _output.WriteLine($"Rumor tags count: {rumorTags.GetArrayLength()}");

        Assert.True(rumorTags.GetArrayLength() > 0,
            "Rumor must have tags — the imeta tag with media metadata is missing. " +
            "The web client needs imeta tags to download and decrypt the media file.");

        // Find the imeta tag
        bool foundImeta = false;
        for (int i = 0; i < rumorTags.GetArrayLength(); i++)
        {
            var tag = rumorTags[i];
            if (tag.GetArrayLength() > 0 && tag[0].GetString() == "imeta")
            {
                foundImeta = true;
                _output.WriteLine($"Found imeta tag with {tag.GetArrayLength()} entries");

                // Verify it contains the expected fields
                var tagValues = new List<string>();
                for (int j = 0; j < tag.GetArrayLength(); j++)
                    tagValues.Add(tag[j].GetString() ?? "");

                Assert.Contains(tagValues, v => v.StartsWith("url "));
                Assert.Contains(tagValues, v => v.StartsWith("m "));
                Assert.Contains(tagValues, v => v.StartsWith("x "));
                Assert.Contains(tagValues, v => v.StartsWith("n ") && !v.StartsWith("n ") == false); // nonce
                Assert.Contains(tagValues, v => v.StartsWith("v ")); // version
                Assert.Contains(tagValues, v => v.StartsWith("filename ")); // filename
                break;
            }
        }

        Assert.True(foundImeta, "imeta tag not found in decrypted rumor");
        _output.WriteLine("PASS: imeta tag present in encrypted rumor");
    }
}
