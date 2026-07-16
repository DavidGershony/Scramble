using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using NBitcoin.Secp256k1;
using MarmotCs.Protocol.Nip44;
using Scramble.Core.Services;
using Xunit;
using SHA256 = System.Security.Cryptography.SHA256;

namespace Scramble.Core.Tests;

/// <summary>
/// Integration tests for Amber (NIP-46 external signer) flow.
/// Uses a TestExternalSigner that performs real NIP-44 crypto and BIP-340 signing
/// to verify end-to-end interop between signer-delegated and local crypto paths.
/// </summary>
public class ExternalSignerIntegrationTests
{
    private readonly NostrService _nostrService = new();

    /// <summary>
    /// Real-crypto IExternalSigner that simulates what Amber does:
    /// NIP-44 encrypt/decrypt with secp256k1 ECDH, BIP-340 Schnorr event signing.
    /// </summary>
    private class TestExternalSigner : IExternalSigner
    {
        private readonly string _privateKeyHex;
        private readonly string _publicKeyHex;
        private readonly Subject<ExternalSignerStatus> _status = new();

        public TestExternalSigner(string privateKeyHex, string publicKeyHex)
        {
            _privateKeyHex = privateKeyHex;
            _publicKeyHex = publicKeyHex;
        }

        public bool IsConnected => true;
        public string? PublicKeyHex => _publicKeyHex;
        public string? Npub => null;
        public IReadOnlyList<string> RelayUrls => Array.Empty<string>();
        public string? RelayUrl => null;
        public string? RemotePubKey => null;
        public string? Secret => null;
        public string? LocalPrivateKeyHex => null;
        public string? LocalPublicKeyHex => null;
        public int ProofOfWorkDifficulty { get; set; }
        public IObservable<ExternalSignerStatus> Status => _status;

        public Task<string> SignEventAsync(UnsignedNostrEvent unsignedEvent)
        {
            var privateKeyBytes = Convert.FromHexString(_privateKeyHex);
            var createdAt = new DateTimeOffset(unsignedEvent.CreatedAt).ToUnixTimeSeconds();

            // NIP-01 event ID = SHA256([0, pubkey, created_at, kind, tags, content])
            var serialized = NostrService.SerializeForEventId(
                _publicKeyHex, createdAt, unsignedEvent.Kind,
                unsignedEvent.Tags, unsignedEvent.Content);
            var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
            var idHex = Convert.ToHexString(idBytes).ToLowerInvariant();

            // BIP-340 Schnorr signature
            var sigBytes = NostrService.SignSchnorr(idBytes, privateKeyBytes);
            var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

            // Build signed event JSON
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("id", idHex);
                w.WriteString("pubkey", _publicKeyHex);
                w.WriteNumber("created_at", createdAt);
                w.WriteNumber("kind", unsignedEvent.Kind);
                w.WritePropertyName("tags");
                w.WriteStartArray();
                foreach (var tag in unsignedEvent.Tags)
                {
                    w.WriteStartArray();
                    foreach (var v in tag) w.WriteStringValue(v);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteString("content", unsignedEvent.Content);
                w.WriteString("sig", sigHex);
                w.WriteEndObject();
            }

            return Task.FromResult(Encoding.UTF8.GetString(ms.ToArray()));
        }

        public Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey)
        {
            var convKey = Nip44Encryption.DeriveConversationKey(
                Convert.FromHexString(_privateKeyHex),
                Convert.FromHexString(recipientPubKey));
            return Task.FromResult(Nip44Encryption.Encrypt(plaintext, convKey));
        }

        public Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey)
        {
            var convKey = Nip44Encryption.DeriveConversationKey(
                Convert.FromHexString(_privateKeyHex),
                Convert.FromHexString(senderPubKey));
            return Task.FromResult(Nip44Encryption.Decrypt(ciphertext, convKey));
        }

        public Task<string> GetPublicKeyAsync() => Task.FromResult(_publicKeyHex);
        public Task<string?> ResolveSigningPubKeyAsync() => Task.FromResult<string?>(_publicKeyHex);
        public Task<bool> ConnectAsync(string bunkerUrl) => throw new NotImplementedException();
        public Task<bool> ConnectWithStringAsync(string connectionString) => throw new NotImplementedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public string GenerateConnectionUri(IEnumerable<string> relayUrls) => throw new NotImplementedException();
        public Task<string> GenerateAndListenForConnectionAsync(IEnumerable<string> relayUrls) => throw new NotImplementedException();
        public Task ReconnectAsync() => throw new NotImplementedException();
        public Task<bool> RestoreSessionAsync(IEnumerable<string> relayUrls, string remotePubKey,
            string localPrivateKeyHex, string localPublicKeyHex, string? secret = null) =>
            throw new NotImplementedException();
    }

    #region NIP-44 Cross-Path Interop

    [Fact]
    public async Task Nip44_SignerEncrypt_LocalDecrypt_RoundTrips()
    {
        // Signer (Alice) encrypts → local code (Bob) decrypts
        var (alicePriv, alicePub, _, _) = _nostrService.GenerateKeyPair();
        var (bobPriv, bobPub, _, _) = _nostrService.GenerateKeyPair();

        var signer = new TestExternalSigner(alicePriv, alicePub);
        var plaintext = "Hello from Amber signer! Special chars: émojis 🎉 and \"quotes\"";

        // Encrypt via signer (simulates Amber encrypting)
        var ciphertext = await signer.Nip44EncryptAsync(plaintext, bobPub);

        // Decrypt locally (simulates recipient with local key)
        var convKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(bobPriv), Convert.FromHexString(alicePub));
        var decrypted = Nip44Encryption.Decrypt(ciphertext, convKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task Nip44_LocalEncrypt_SignerDecrypt_RoundTrips()
    {
        // Local code (Alice) encrypts → signer (Bob) decrypts
        var (alicePriv, alicePub, _, _) = _nostrService.GenerateKeyPair();
        var (bobPriv, bobPub, _, _) = _nostrService.GenerateKeyPair();

        var signer = new TestExternalSigner(bobPriv, bobPub);

        // Encrypt locally
        var convKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(alicePriv), Convert.FromHexString(bobPub));
        var plaintext = "Hello from local key! Newlines:\nLine 2\nLine 3";
        var ciphertext = Nip44Encryption.Encrypt(plaintext, convKey);

        // Decrypt via signer (simulates Amber decrypting)
        var decrypted = await signer.Nip44DecryptAsync(ciphertext, alicePub);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task Nip44_NostrServiceSignerPath_LocalDecrypt_RoundTrips()
    {
        // NostrService.Nip44EncryptAsync (signer path) → local Nip44Encryption.Decrypt
        var (alicePriv, alicePub, _, _) = _nostrService.GenerateKeyPair();
        var (bobPriv, bobPub, _, _) = _nostrService.GenerateKeyPair();

        var signer = new TestExternalSigner(alicePriv, alicePub);
        _nostrService.SetExternalSigner(signer);

        var plaintext = "Encrypted via NostrService signer path";
        var ciphertext = await _nostrService.Nip44EncryptAsync(plaintext, bobPub);

        // Decrypt locally as Bob
        var convKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(bobPriv), Convert.FromHexString(alicePub));
        var decrypted = Nip44Encryption.Decrypt(ciphertext, convKey);

        Assert.Equal(plaintext, decrypted);
    }

    #endregion

    #region Event Signing Interop

    [Fact]
    public async Task SignerSignedEvent_HasValidNip01Structure()
    {
        var (privKey, pubKey, _, _) = _nostrService.GenerateKeyPair();
        var signer = new TestExternalSigner(privKey, pubKey);
        var extSigner = new ExternalNostrEventSigner(signer);

        var tags = new List<List<string>>
        {
            new() { "h", "deadbeef" },
            new() { "encoding", "mls" }
        };

        var result = await extSigner.SignEventAsync(445, "encrypted-mls-data", tags, pubKey);
        var json = Encoding.UTF8.GetString(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // All NIP-01 fields present
        Assert.Equal(pubKey, root.GetProperty("pubkey").GetString());
        Assert.Equal(445, root.GetProperty("kind").GetInt32());
        Assert.Equal("encrypted-mls-data", root.GetProperty("content").GetString());
        Assert.Equal(64, root.GetProperty("id").GetString()!.Length);
        Assert.Equal(128, root.GetProperty("sig").GetString()!.Length);

        // Tags preserved
        var tagsArr = root.GetProperty("tags");
        Assert.Equal(2, tagsArr.GetArrayLength());
        Assert.Equal("h", tagsArr[0][0].GetString());
        Assert.Equal("deadbeef", tagsArr[0][1].GetString());
    }

    [Fact]
    public async Task SignerSignedEvent_EventIdIsCorrectSha256()
    {
        var (privKey, pubKey, _, _) = _nostrService.GenerateKeyPair();
        var signer = new TestExternalSigner(privKey, pubKey);
        var extSigner = new ExternalNostrEventSigner(signer);

        var tags = new List<List<string>> { new() { "p", "somepubkey" } };
        var result = await extSigner.SignEventAsync(1, "content", tags, pubKey);

        var json = Encoding.UTF8.GetString(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var eventId = root.GetProperty("id").GetString()!;
        var createdAt = root.GetProperty("created_at").GetInt64();

        // Recompute event ID independently
        var serialized = NostrService.SerializeForEventId(pubKey, createdAt, 1, tags, "content");
        var expectedId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();

        Assert.Equal(expectedId, eventId);
    }

    [Fact]
    public async Task SignerSignedEvent_SignatureIsValidBip340()
    {
        var (privKey, pubKey, _, _) = _nostrService.GenerateKeyPair();
        var signer = new TestExternalSigner(privKey, pubKey);
        var extSigner = new ExternalNostrEventSigner(signer);

        var result = await extSigner.SignEventAsync(
            445, "test", new List<List<string>>(), pubKey);

        var json = Encoding.UTF8.GetString(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var eventIdBytes = Convert.FromHexString(root.GetProperty("id").GetString()!);
        var sigBytes = Convert.FromHexString(root.GetProperty("sig").GetString()!);
        var pubKeyBytes = Convert.FromHexString(pubKey);

        // Verify BIP-340 Schnorr signature
        Assert.True(VerifyBip340Signature(pubKeyBytes, eventIdBytes, sigBytes));
    }

    [Fact]
    public async Task LocalSigner_And_ExternalSigner_ProduceVerifiableEvents()
    {
        // Both signers with the same key should produce valid, verifiable events
        var (privKey, pubKey, _, _) = _nostrService.GenerateKeyPair();
        var tags = new List<List<string>> { new() { "h", "group1" } };

        var local = new LocalNostrEventSigner(privKey);
        var external = new ExternalNostrEventSigner(new TestExternalSigner(privKey, pubKey));

        var localResult = await local.SignEventAsync(445, "msg", tags, pubKey);
        var extResult = await external.SignEventAsync(445, "msg", tags, pubKey);

        // Both should produce parseable JSON with valid signatures
        foreach (var bytes in new[] { localResult, extResult })
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            Assert.Equal(pubKey, root.GetProperty("pubkey").GetString());
            Assert.Equal(445, root.GetProperty("kind").GetInt32());

            var idBytes = Convert.FromHexString(root.GetProperty("id").GetString()!);
            var sigBytes2 = Convert.FromHexString(root.GetProperty("sig").GetString()!);
            Assert.True(VerifyBip340Signature(Convert.FromHexString(pubKey), idBytes, sigBytes2));
        }
    }

    #endregion

    #region Full NIP-59 Gift Wrap Round-Trip

    [Fact]
    public async Task GiftWrap_SignerCreated_LocalUnwrap_RecoverOriginalRumor()
    {
        // Simulate the full NIP-59 flow:
        // 1. Signer (Alice) creates rumor → seal (kind 13) → gift wrap (kind 1059)
        // 2. Recipient (Bob) unwraps all 3 layers with local key
        // This is the exact path CreateGiftWrapAsync + UnwrapGiftWrapAsync take.

        var (alicePriv, alicePub, _, _) = _nostrService.GenerateKeyPair();
        var (bobPriv, bobPub, _, _) = _nostrService.GenerateKeyPair();
        var aliceSigner = new TestExternalSigner(alicePriv, alicePub);

        // --- Layer 1: Create unsigned rumor (kind 444 Welcome) ---
        var rumorContent = Convert.ToBase64String(Encoding.UTF8.GetBytes("welcome-payload"));
        var rumorTags = new List<List<string>>
        {
            new() { "encoding", "base64" },
            new() { "relays", "wss://relay.example.com" }
        };
        var rumorCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Serialize rumor JSON (no id, no sig — it's unsigned)
        var rumorJson = SerializeRumor(alicePub, rumorCreatedAt, 444, rumorTags, rumorContent);

        // --- Layer 2: Create seal (kind 13) via signer ---
        // Signer encrypts rumor with NIP-44 to Bob
        var sealContent = await aliceSigner.Nip44EncryptAsync(rumorJson, bobPub);
        var sealEvent = new UnsignedNostrEvent
        {
            Kind = 13,
            Content = sealContent,
            Tags = new List<List<string>>(),
            CreatedAt = DateTime.UtcNow
        };
        var sealJson = await aliceSigner.SignEventAsync(sealEvent);

        // Verify seal is valid JSON with correct structure
        using (var sealDoc = JsonDocument.Parse(sealJson))
        {
            Assert.Equal(13, sealDoc.RootElement.GetProperty("kind").GetInt32());
            Assert.Equal(alicePub, sealDoc.RootElement.GetProperty("pubkey").GetString());
        }

        // --- Layer 3: Create gift wrap (kind 1059) with ephemeral key ---
        var (ephPriv, ephPub, _, _) = _nostrService.GenerateKeyPair();
        var giftConvKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(ephPriv), Convert.FromHexString(bobPub));
        var giftContent = Nip44Encryption.Encrypt(sealJson, giftConvKey);
        var giftCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var giftTags = new List<List<string>> { new() { "p", bobPub } };

        // --- Unwrap as Bob (local key path) ---
        // Layer 3 → 2: Decrypt gift wrap to get seal
        var unwrapConvKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(bobPriv), Convert.FromHexString(ephPub));
        var decryptedSealJson = Nip44Encryption.Decrypt(giftContent, unwrapConvKey);

        // Verify decrypted seal matches original
        using var decSealDoc = JsonDocument.Parse(decryptedSealJson);
        var decSealPubkey = decSealDoc.RootElement.GetProperty("pubkey").GetString()!;
        var decSealContent = decSealDoc.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(alicePub, decSealPubkey);

        // Layer 2 → 1: Decrypt seal to get rumor
        var sealConvKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(bobPriv), Convert.FromHexString(decSealPubkey));
        var decryptedRumorJson = Nip44Encryption.Decrypt(decSealContent, sealConvKey);

        // Verify recovered rumor matches original
        using var rumorDoc = JsonDocument.Parse(decryptedRumorJson);
        var rumor = rumorDoc.RootElement;
        Assert.Equal(444, rumor.GetProperty("kind").GetInt32());
        Assert.Equal(alicePub, rumor.GetProperty("pubkey").GetString());
        Assert.Equal(rumorContent, rumor.GetProperty("content").GetString());

        // Verify tags survived the round-trip
        var recoveredTags = rumor.GetProperty("tags");
        Assert.Equal(2, recoveredTags.GetArrayLength());
        Assert.Equal("encoding", recoveredTags[0][0].GetString());
        Assert.Equal("base64", recoveredTags[0][1].GetString());
    }

    [Fact]
    public async Task GiftWrap_LocalCreated_SignerUnwrap_RecoverOriginalRumor()
    {
        // Reverse direction: local key creates gift wrap, signer unwraps.
        // This tests the UnwrapGiftWrapAsync signer path.

        var (alicePriv, alicePub, _, _) = _nostrService.GenerateKeyPair();
        var (bobPriv, bobPub, _, _) = _nostrService.GenerateKeyPair();
        var bobSigner = new TestExternalSigner(bobPriv, bobPub);

        // --- Create gift wrap locally as Alice ---
        var rumorContent = "welcome-data-base64";
        var rumorTags = new List<List<string>> { new() { "encoding", "base64" } };
        var rumorCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rumorJson = SerializeRumor(alicePub, rumorCreatedAt, 444, rumorTags, rumorContent);

        // Seal: encrypt rumor to Bob, sign as Alice (locally)
        var sealConvKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(alicePriv), Convert.FromHexString(bobPub));
        var sealContent = Nip44Encryption.Encrypt(rumorJson, sealConvKey);
        var sealCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sealSerializedForId = NostrService.SerializeForEventId(
            alicePub, sealCreatedAt, 13, new List<List<string>>(), sealContent);
        var sealIdBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sealSerializedForId));
        var sealId = Convert.ToHexString(sealIdBytes).ToLowerInvariant();
        var sealSig = Convert.ToHexString(
            NostrService.SignSchnorr(sealIdBytes, Convert.FromHexString(alicePriv))).ToLowerInvariant();

        var sealJson = BuildEventJson(sealId, alicePub, sealCreatedAt, 13,
            new List<List<string>>(), sealContent, sealSig);

        // Gift wrap: ephemeral key encrypts seal to Bob
        var (ephPriv, ephPub, _, _) = _nostrService.GenerateKeyPair();
        var giftConvKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(ephPriv), Convert.FromHexString(bobPub));
        var giftContent = Nip44Encryption.Encrypt(sealJson, giftConvKey);

        // --- Unwrap as Bob via signer ---
        // Layer 3 → 2: Signer decrypts gift wrap (counterparty = ephemeral pub)
        var decryptedSealJson = await bobSigner.Nip44DecryptAsync(giftContent, ephPub);

        using var decSealDoc = JsonDocument.Parse(decryptedSealJson);
        var decSealPubkey = decSealDoc.RootElement.GetProperty("pubkey").GetString()!;
        var decSealContent = decSealDoc.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(alicePub, decSealPubkey);

        // Layer 2 → 1: Signer decrypts seal (counterparty = Alice)
        var decryptedRumorJson = await bobSigner.Nip44DecryptAsync(decSealContent, decSealPubkey);

        using var rumorDoc = JsonDocument.Parse(decryptedRumorJson);
        Assert.Equal(444, rumorDoc.RootElement.GetProperty("kind").GetInt32());
        Assert.Equal(rumorContent, rumorDoc.RootElement.GetProperty("content").GetString());
        Assert.Equal(alicePub, rumorDoc.RootElement.GetProperty("pubkey").GetString());
    }

    [Fact]
    public async Task GiftWrap_SignerBothSides_RoundTrips()
    {
        // Both sender and recipient use external signers (both Amber users).
        // Alice's signer creates gift wrap, Bob's signer unwraps it.

        var (alicePriv, alicePub, _, _) = _nostrService.GenerateKeyPair();
        var (bobPriv, bobPub, _, _) = _nostrService.GenerateKeyPair();
        var aliceSigner = new TestExternalSigner(alicePriv, alicePub);
        var bobSigner = new TestExternalSigner(bobPriv, bobPub);

        // Alice creates gift wrap via signer
        var originalMessage = "secret group invite payload with unicode: 日本語テスト";
        var rumorCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rumorTags = new List<List<string>>
        {
            new() { "encoding", "base64" },
            new() { "e", "referenced-event-id" }
        };
        var rumorJson = SerializeRumor(alicePub, rumorCreatedAt, 444, rumorTags, originalMessage);

        // Seal via Alice's signer
        var sealContent = await aliceSigner.Nip44EncryptAsync(rumorJson, bobPub);
        var sealEvent = new UnsignedNostrEvent
        {
            Kind = 13,
            Content = sealContent,
            Tags = new List<List<string>>()
        };
        var sealJson = await aliceSigner.SignEventAsync(sealEvent);

        // Gift wrap with ephemeral key
        var (ephPriv, ephPub, _, _) = _nostrService.GenerateKeyPair();
        var giftConvKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(ephPriv), Convert.FromHexString(bobPub));
        var giftContent = Nip44Encryption.Encrypt(sealJson, giftConvKey);

        // Bob unwraps via signer
        var unwrappedSeal = await bobSigner.Nip44DecryptAsync(giftContent, ephPub);
        using var sealDoc = JsonDocument.Parse(unwrappedSeal);
        var sealPubkey = sealDoc.RootElement.GetProperty("pubkey").GetString()!;
        var sealContentDecrypted = sealDoc.RootElement.GetProperty("content").GetString()!;

        var unwrappedRumor = await bobSigner.Nip44DecryptAsync(sealContentDecrypted, sealPubkey);
        using var rumorDoc = JsonDocument.Parse(unwrappedRumor);
        var rumor = rumorDoc.RootElement;

        Assert.Equal(444, rumor.GetProperty("kind").GetInt32());
        Assert.Equal(alicePub, rumor.GetProperty("pubkey").GetString());
        Assert.Equal(originalMessage, rumor.GetProperty("content").GetString());

        // Verify referenced event tag survived
        var tags = rumor.GetProperty("tags");
        Assert.Equal(2, tags.GetArrayLength());
        Assert.Equal("e", tags[1][0].GetString());
        Assert.Equal("referenced-event-id", tags[1][1].GetString());
    }

    #endregion

    #region PublishKeyPackageAsync via Signer

    // NOTE: the region header used to claim "no relay needed" but the test below
    // connects to wss://relay2.angor.io to receive an OK — so it IS a relay
    // test. Tagged Category=Integration so the required unit-tier build in
    // dotnet-desktop.yml doesn't break when the external relay is unreachable.
    // The integration workflow runs it against docker-relay via
    // SCRAMBLE_TEST_RELAY — but only after the hardcoded URL below is migrated
    // to TestRelayConfig.RelayUrl.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PublishKeyPackageAsync_WithSigner_ProducesValidEventId()
    {
        // PublishKeyPackageAsync with null privkey should route through the signer
        // and return a valid 64-char hex event ID.

        var (privKey, pubKey, _, _) = _nostrService.GenerateKeyPair();
        var signer = new TestExternalSigner(privKey, pubKey);
        _nostrService.SetExternalSigner(signer);

        // Connect to a relay so the publish path can send and receive OK responses
        await _nostrService.ConnectAsync("wss://relay2.angor.io");
        await Task.Delay(500);

        var kpData = Encoding.UTF8.GetBytes("fake-keypackage");
        var tags = new List<List<string>>
        {
            new() { "mls_protocol_version", "mls10" },
            new() { "relays", "wss://relay.example.com" }
        };

        var eventId = await _nostrService.PublishKeyPackageAsync(kpData, null, tags);

        Assert.NotEmpty(eventId);
        Assert.Equal(64, eventId.Length);
        // Event ID should be valid hex
        Assert.True(eventId.All(c => "0123456789abcdef".Contains(c)));
    }

    #endregion

    #region Helpers

    private static string SerializeRumor(string pubkey, long createdAt, int kind,
        List<List<string>> tags, string content)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("pubkey", pubkey);
            w.WriteNumber("created_at", createdAt);
            w.WriteNumber("kind", kind);
            w.WritePropertyName("tags");
            w.WriteStartArray();
            foreach (var tag in tags)
            {
                w.WriteStartArray();
                foreach (var item in tag) w.WriteStringValue(item);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteString("content", content);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool VerifyBip340Signature(byte[] publicKeyBytes, byte[] messageBytes, byte[] signatureBytes)
    {
        var xOnlyPub = ECXOnlyPubKey.Create(publicKeyBytes);
        if (!SecpSchnorrSignature.TryCreate(signatureBytes, out var sig) || sig is null)
            return false;
        return xOnlyPub.SigVerifyBIP340(sig, messageBytes);
    }

    private static string BuildEventJson(string id, string pubkey, long createdAt, int kind,
        List<List<string>> tags, string content, string sig)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("pubkey", pubkey);
            w.WriteNumber("created_at", createdAt);
            w.WriteNumber("kind", kind);
            w.WritePropertyName("tags");
            w.WriteStartArray();
            foreach (var tag in tags)
            {
                w.WriteStartArray();
                foreach (var v in tag) w.WriteStringValue(v);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteString("content", content);
            w.WriteString("sig", sig);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    #endregion
}
