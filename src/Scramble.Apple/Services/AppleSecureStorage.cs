using System.Runtime.Versioning;
using System.Security.Cryptography;
using Foundation;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Services;
using Security;

namespace Scramble.Apple.Services;

/// <summary>
/// Apple Keychain-backed secure storage. Uses AES-256-GCM for encryption with the
/// symmetric key stored in the macOS/iOS Keychain via the managed Security framework
/// bindings. The Keychain entry is scoped to this app and protected by the OS
/// (hardware-backed on devices with Secure Enclave).
///
/// Data layout: [4-byte magic] [12-byte nonce] [16-byte tag] [ciphertext]
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public class AppleSecureStorage : ISecureStorage
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;
    // One definition, in Scramble.Core. See SecureStorageFormat for why these
    // four bytes must never change and why the test doubles keep their own copy.
    private static readonly byte[] MagicPrefix = SecureStorageFormat.MagicPrefix;

    private const string KeychainService = "Scramble";
    private const string KeychainAccount = "storage_key";

    private readonly ILogger<AppleSecureStorage> _logger;
    private readonly byte[] _key;

    public AppleSecureStorage()
    {
        _logger = LoggingConfiguration.CreateLogger<AppleSecureStorage>();
        _key = LoadOrCreateKey();
        _logger.LogInformation("AppleSecureStorage initialized (AES-256-GCM, Keychain-backed key)");
    }

    public byte[] Protect(byte[] data)
    {
        _logger.LogDebug("Protecting {Length} bytes with AES-256-GCM", data.Length);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[data.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, ciphertext, tag);

        // Layout: [4-byte magic] [12-byte nonce] [16-byte tag] [ciphertext]
        var result = new byte[MagicPrefix.Length + NonceSize + TagSize + ciphertext.Length];
        MagicPrefix.CopyTo(result, 0);
        var offset = MagicPrefix.Length;
        nonce.CopyTo(result, offset); offset += NonceSize;
        tag.CopyTo(result, offset); offset += TagSize;
        ciphertext.CopyTo(result, offset);

        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < MagicPrefix.Length || !HasMagicPrefix(data))
        {
            _logger.LogDebug("Data lacks encryption prefix ({Length} bytes), returning as-is", data.Length);
            return data;
        }

        _logger.LogDebug("Unprotecting {Length} bytes with AES-256-GCM", data.Length);

        var offset = MagicPrefix.Length;
        var nonce = new byte[NonceSize];
        Array.Copy(data, offset, nonce, 0, NonceSize); offset += NonceSize;

        var tag = new byte[TagSize];
        Array.Copy(data, offset, tag, 0, TagSize); offset += TagSize;

        var ciphertextLength = data.Length - offset;
        var ciphertext = new byte[ciphertextLength];
        Array.Copy(data, offset, ciphertext, 0, ciphertextLength);

        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    // ── Keychain key management ──

    private byte[] LoadOrCreateKey()
    {
        var existing = KeychainGetData(KeychainService, KeychainAccount);
        if (existing != null && existing.Length == KeySize)
        {
            _logger.LogInformation("Loaded existing encryption key from Apple Keychain");
            return existing;
        }

        if (existing != null)
        {
            _logger.LogWarning("Keychain entry has invalid size {Size}, regenerating", existing.Length);
            KeychainDelete(KeychainService, KeychainAccount);
        }

        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        KeychainSetData(KeychainService, KeychainAccount, key);
        _logger.LogInformation("Generated new encryption key and stored in Apple Keychain");
        return key;
    }

    /// <summary>
    /// Retrieve raw bytes from a generic-password Keychain item.
    /// Returns null if the item does not exist.
    /// </summary>
    private static byte[]? KeychainGetData(string service, string account)
    {
        var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = service,
            Account = account,
        };

        var match = SecKeyChain.QueryAsData(query, false, out var status);
        if (status == SecStatusCode.ItemNotFound || match == null)
            return null;
        if (status != SecStatusCode.Success)
            throw new CryptographicException($"Keychain query failed with status {status}");

        return match.ToArray();
    }

    /// <summary>
    /// Store raw bytes as a generic-password Keychain item.
    /// Deletes any existing entry first to avoid duplicates.
    /// </summary>
    private static void KeychainSetData(string service, string account, byte[] data)
    {
        // Remove existing entry (ignore errors — item may not exist yet)
        KeychainDelete(service, account);

        var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = service,
            Account = account,
            ValueData = NSData.FromArray(data),
            Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
        };

        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.Success)
            throw new CryptographicException($"Keychain add failed with status {status}");
    }

    /// <summary>
    /// Delete a generic-password Keychain item. Silently succeeds if the item does not exist.
    /// </summary>
    private static void KeychainDelete(string service, string account)
    {
        var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = service,
            Account = account,
        };
        SecKeyChain.Remove(query);
    }

    private static bool HasMagicPrefix(byte[] data)
    {
        for (int i = 0; i < MagicPrefix.Length; i++)
        {
            if (data[i] != MagicPrefix[i])
                return false;
        }
        return true;
    }
}
