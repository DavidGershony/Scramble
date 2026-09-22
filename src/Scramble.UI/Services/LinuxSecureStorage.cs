using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Services;

namespace Scramble.UI.Services;

/// <summary>
/// Secure storage using AES-256-GCM with a locally generated key file.
/// Used on Linux and macOS where DPAPI is not available.
/// The key is stored in ~/.config/Scramble/storage.key with user-only permissions (chmod 600).
/// </summary>
public class LinuxSecureStorage : ISecureStorage
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;
    // One definition, in Scramble.Core. See SecureStorageFormat for why these
    // four bytes must never change and why the test doubles keep their own copy.
    private static readonly byte[] MagicPrefix = SecureStorageFormat.MagicPrefix;

    private readonly ILogger<LinuxSecureStorage> _logger;
    private readonly byte[] _key;

    public LinuxSecureStorage()
    {
        _logger = LoggingConfiguration.CreateLogger<LinuxSecureStorage>();
        _key = LoadOrCreateKey();
        _logger.LogInformation("LinuxSecureStorage initialized (AES-256-GCM, file-backed key)");
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

    private byte[] LoadOrCreateKey()
    {
        var keyPath = GetKeyFilePath();
        var keyDir = Path.GetDirectoryName(keyPath)!;

        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == KeySize)
            {
                _logger.LogInformation("Loaded existing encryption key from {Path}", keyPath);
                return existing;
            }
            _logger.LogWarning("Key file {Path} has invalid size {Size}, regenerating", keyPath, existing.Length);
        }

        // Generate new key
        Directory.CreateDirectory(keyDir);
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        File.WriteAllBytes(keyPath, key);

        // Set permissions to user-only (chmod 600)
        try
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _logger.LogInformation("Generated new encryption key at {Path} (permissions: 600)", keyPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set permissions on key file — verify manually: chmod 600 {Path}", keyPath);
        }

        return key;
    }

    private static string GetKeyFilePath()
    {
        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(configDir, "Scramble", "storage.key");
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
