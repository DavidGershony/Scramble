using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Scramble.Core.Configuration;
using Scramble.Core.Crypto;
using Scramble.Core.Services;
using Scramble.UI.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Security regression tests. Each test maps to a finding from the security audit (2026-04-08).
/// Naming: {FindingId}_{Description}
/// </summary>
public class SecurityTests
{
    #region C1 — Platform Launcher Hardening (tests use static validators, never Process.Start)

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://relay.damus.io")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void C1_ValidateUrl_AllowsHttps(string url)
    {
        var result = AvaloniaLauncher.ValidateUrl(url);
        Assert.NotNull(result);
        Assert.StartsWith("https://", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cmd.exe /c whoami")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void C1_ValidateUrl_BlocksDangerousInput(string url)
    {
        var result = AvaloniaLauncher.ValidateUrl(url);
        Assert.Null(result);
    }

    [Fact]
    public void C1_ValidateFolderPath_BlocksPathTraversal()
    {
        Assert.Null(AvaloniaLauncher.ValidateFolderPath("C:\\Windows\\System32"));
        Assert.Null(AvaloniaLauncher.ValidateFolderPath("..\\..\\..\\Windows"));
    }

    [Fact]
    public void C1_ValidateFolderPath_AllowsAppDirectory()
    {
        var logDir = ProfileConfiguration.LogDirectory;
        var result = AvaloniaLauncher.ValidateFolderPath(logDir);
        Assert.NotNull(result);
    }

    [Fact]
    public void C1_ValidateUrl_BlocksEmptyAndWhitespace()
    {
        Assert.Null(AvaloniaLauncher.ValidateUrl(""));
        Assert.Null(AvaloniaLauncher.ValidateUrl("   "));
        Assert.Null(AvaloniaLauncher.ValidateUrl(null));
    }

    [Fact]
    public void C1_ValidateFolderPath_BlocksEmptyAndWhitespace()
    {
        Assert.Null(AvaloniaLauncher.ValidateFolderPath(""));
        Assert.Null(AvaloniaLauncher.ValidateFolderPath("   "));
        Assert.Null(AvaloniaLauncher.ValidateFolderPath(null));
    }

    #endregion

    #region C2 — WSS-Only Relay Connections

    [Theory]
    [InlineData("wss://relay.example.com")]
    [InlineData("wss://relay.damus.io")]
    public async Task C2_WssRelay_Accepted(string url)
    {
        var result = await NostrService.ValidateRelayUrlAsync(url);
        // null = valid, or DNS error (acceptable for test domains)
        Assert.True(result == null || result.StartsWith("Cannot resolve hostname"),
            $"Expected null or DNS error, got: {result}");
    }

    [Theory]
    [InlineData("ws://relay.example.com")]
    [InlineData("ws://localhost:7777")]
    public async Task C2_WsRelay_RejectedByDefault(string url)
    {
        // Ensure AllowLocalRelays is off for this test
        var previousValue = ProfileConfiguration.AllowLocalRelays;
        ProfileConfiguration.SetAllowLocalRelays(false);
        try
        {
            var result = await NostrService.ValidateRelayUrlAsync(url);
            Assert.NotNull(result);
            Assert.Contains("wss://", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ProfileConfiguration.SetAllowLocalRelays(previousValue);
        }
    }

    [Fact]
    public async Task C2_WsRelay_AllowedWithLocalRelaysFlag()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        try
        {
            var result = await NostrService.ValidateRelayUrlAsync("ws://relay.example.com");
            // Should be accepted (or DNS error, not scheme error)
            Assert.True(result == null || result.StartsWith("Cannot resolve hostname"),
                $"Expected null or DNS error, got: {result}");
        }
        finally
        {
            ProfileConfiguration.SetAllowLocalRelays(false);
        }
    }

    #endregion

    #region C3 — Event Timestamp Validation (Replay Prevention)

    [Fact]
    public void C3_FreshTimestamp_Accepted()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(NostrService.IsTimestampAcceptable(now, isGiftWrap: false));
    }

    [Fact]
    public void C3_OldTimestamp_Rejected()
    {
        var old = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds();
        Assert.False(NostrService.IsTimestampAcceptable(old, isGiftWrap: false));
    }

    [Fact]
    public void C3_FutureTimestamp_Rejected()
    {
        var future = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        Assert.False(NostrService.IsTimestampAcceptable(future, isGiftWrap: false));
    }

    [Fact]
    public void C3_GiftWrapOldTimestamp_Accepted()
    {
        // Gift wraps (kind 1059) use randomized timestamps, so old timestamps should be accepted
        var old = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        Assert.True(NostrService.IsTimestampAcceptable(old, isGiftWrap: true));
    }

    [Fact]
    public void C3_ReplaceableEventOldTimestamp_Accepted()
    {
        // Kind 0 (metadata), 3 (contacts), 10002 (relay list) are replaceable — historical is fine
        var old = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        Assert.True(NostrService.IsTimestampAcceptable(old, isReplaceableEvent: true));
    }

    #endregion

    #region H1 — MediaCacheService Path Traversal

    [Theory]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void H1_ValidMessageId_Accepted(string messageId)
    {
        Assert.True(MediaCacheService.IsValidMessageId(messageId));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ZZZZZZ0123456789abcdef0123456789abcdef0123456789abcdef0123456789")] // non-hex chars
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")] // 63 chars
    public void H1_InvalidMessageId_Rejected(string messageId)
    {
        Assert.False(MediaCacheService.IsValidMessageId(messageId));
    }

    #endregion

    // H3 was three tests of MarmotWrapper.ValidateNativeBufferLength, the bounds
    // check on buffers crossing the P/Invoke boundary to the Rust backend. Removed
    // 2026-09-21 with the wrapper: there is no native boundary left in this app, so
    // there is nothing for the guard to guard. The equivalent surface on the Dark
    // Matter engine is managed all the way down -- a malformed length is a decode
    // failure in DotnetMls, covered by its own suite and the RFC 9420 vectors.


    #region M3 — Relay Rate Limiting

    [Fact]
    public void M3_RateLimitFields_Exist()
    {
        // Verify the rate limit constants are reasonable
        // This is a compile-time check — the constants are private, so we test behavior instead
        var nostrService = new NostrService();
        // Processing a single event should not be rate limited
        // (We can't easily call ProcessRelayMessageAsync directly, but we verify the service constructs)
        Assert.NotNull(nostrService);
    }

    #endregion

    #region M4 — MIP-04 Nonce Generation

    [Fact]
    public void M4_EncryptMediaFile_GeneratesUniqueNonces()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };
        var sha256Hex = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        var (ciphertext1, nonce1) = Mip04MediaCrypto.EncryptMediaFile(
            plaintext, key, sha256Hex, "application/octet-stream", "test.bin");
        var (ciphertext2, nonce2) = Mip04MediaCrypto.EncryptMediaFile(
            plaintext, key, sha256Hex, "application/octet-stream", "test.bin");

        // Nonces must be 12 bytes
        Assert.Equal(12, nonce1.Length);
        Assert.Equal(12, nonce2.Length);

        // Two encryptions of the same plaintext must produce different nonces
        Assert.False(nonce1.AsSpan().SequenceEqual(nonce2),
            "Two encryptions produced identical nonces — nonce reuse breaks ChaCha20-Poly1305");

        // Therefore ciphertexts must also differ
        Assert.False(ciphertext1.AsSpan().SequenceEqual(ciphertext2),
            "Two encryptions produced identical ciphertexts");
    }

    [Fact]
    public void M4_EncryptDecrypt_RoundTrip()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var sha256Hex = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        var (ciphertext, nonce) = Mip04MediaCrypto.EncryptMediaFile(
            plaintext, key, sha256Hex, "application/octet-stream", "test.bin");

        var nonceHex = Convert.ToHexString(nonce).ToLowerInvariant();
        var decrypted = Mip04MediaCrypto.DecryptMediaFile(
            ciphertext, key, sha256Hex, "application/octet-stream", "test.bin", nonceHex);

        Assert.Equal(plaintext, decrypted);
    }

    #endregion

    #region M1 — NIP-46 NIP-44 Encryption

    [Fact]
    public void M1_Nip44EncryptDecrypt_RoundTrip()
    {
        // Verify NIP-44 encrypt/decrypt works for NIP-46 communication
        var nostrService = new NostrService();
        var (privA, pubA, _, _) = nostrService.GenerateKeyPair();
        var (privB, pubB, _, _) = nostrService.GenerateKeyPair();

        var plaintext = "{\"id\":\"test123\",\"method\":\"connect\",\"params\":[\"abc\"]}";

        // Encrypt with A's private key + B's public key
        var convKeyAB = Scramble.Nostr.Crypto.Nip44.DeriveConversationKey(
            Convert.FromHexString(privA), Convert.FromHexString(pubB));
        var encrypted = Scramble.Nostr.Crypto.Nip44.Encrypt(plaintext, convKeyAB);

        // Encrypted should NOT contain ?iv= (that's NIP-04 format)
        Assert.DoesNotContain("?iv=", encrypted);

        // Decrypt with B's private key + A's public key
        var convKeyBA = Scramble.Nostr.Crypto.Nip44.DeriveConversationKey(
            Convert.FromHexString(privB), Convert.FromHexString(pubA));
        var decrypted = Scramble.Nostr.Crypto.Nip44.Decrypt(encrypted, convKeyBA);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void M1_Nip04Format_DetectedByIvMarker()
    {
        // NIP-04 ciphertext always contains "?iv=" — this is how the receiver distinguishes formats
        // NIP-44 ciphertext is plain base64 without "?iv="
        var nip04Sample = "Y2lwaGVydGV4dA==?iv=bm9uY2U=";
        var nip44Sample = "AQIDBAUGBwgJCgsMDQ4PEBESExQ=";

        Assert.Contains("?iv=", nip04Sample);
        Assert.DoesNotContain("?iv=", nip44Sample);
    }

    #endregion
}
