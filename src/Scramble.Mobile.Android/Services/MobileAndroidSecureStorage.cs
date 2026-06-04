using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Services;

namespace Scramble.MobileAndroid.Services;

/// <summary>
/// Android Keystore-based secure storage for the Avalonia Android head.
/// Uses AES-GCM with a hardware-backed key, identical algorithm to
/// <c>Scramble.Android.Services.AndroidSecureStorage</c> but lives in this
/// project so the two app heads remain independent packages.
/// </summary>
public class MobileAndroidSecureStorage : ISecureStorage
{
    private const string KeyAlias = "Scramble_SecureStorage";
    private const string AndroidKeyStore = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmIvLength = 12;
    private const int GcmTagLength = 128; // bits

    private static readonly byte[] MagicPrefix = { 0xEE, 0xCC, 0x01, 0x00 };
    private readonly ILogger<MobileAndroidSecureStorage> _logger;
    private readonly object _initLock = new();
    private bool _keyEnsured;
    private IKey? _cachedKey;

    public MobileAndroidSecureStorage()
    {
        _logger = LoggingConfiguration.CreateLogger<MobileAndroidSecureStorage>();
        // Keystore/TEE access is deferred to first use to avoid blocking the UI thread.
        _logger.LogInformation("MobileAndroidSecureStorage created (lazy keystore init)");
    }

    /// <summary>
    /// Ensures the AES key exists in the Android Keystore. Thread-safe, idempotent.
    /// Called lazily on first Protect/Unprotect to avoid blocking the UI thread during
    /// app startup (Android Keystore operations involve TEE/HSM communication).
    /// </summary>
    private void EnsureLazyInit()
    {
        if (_keyEnsured) return;
        lock (_initLock)
        {
            if (_keyEnsured) return;
            EnsureKeyExists();
            _keyEnsured = true;
        }
    }

    public byte[] Protect(byte[] data)
    {
        EnsureLazyInit();
        _logger.LogInformation("Protecting {Length} bytes with Android Keystore", data.Length);

        var key = GetCachedKey();
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, key);

        var iv = cipher.GetIV()!;
        var encrypted = cipher.DoFinal(data)!;

        // Layout: [4-byte magic] [12-byte IV] [encrypted data with GCM tag]
        var result = new byte[MagicPrefix.Length + iv.Length + encrypted.Length];
        MagicPrefix.CopyTo(result, 0);
        Array.Copy(iv, 0, result, MagicPrefix.Length, iv.Length);
        Array.Copy(encrypted, 0, result, MagicPrefix.Length + iv.Length, encrypted.Length);

        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        EnsureLazyInit();
        if (data.Length < MagicPrefix.Length || !HasMagicPrefix(data))
        {
            _logger.LogInformation("Data lacks encryption prefix ({Length} bytes), returning as-is (unencrypted)", data.Length);
            return data;
        }

        _logger.LogInformation("Unprotecting {Length} bytes with Android Keystore", data.Length);

        var iv = new byte[GcmIvLength];
        Array.Copy(data, MagicPrefix.Length, iv, 0, GcmIvLength);

        var encryptedLength = data.Length - MagicPrefix.Length - GcmIvLength;
        var encrypted = new byte[encryptedLength];
        Array.Copy(data, MagicPrefix.Length + GcmIvLength, encrypted, 0, encryptedLength);

        var key = GetCachedKey();
        var cipher = Cipher.GetInstance(Transformation)!;
        var spec = new GCMParameterSpec(GcmTagLength, iv);
        cipher.Init(CipherMode.DecryptMode, key, spec);

        return cipher.DoFinal(encrypted)!;
    }

    private void EnsureKeyExists()
    {
        var keyStore = KeyStore.GetInstance(AndroidKeyStore)!;
        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias))
        {
            _logger.LogInformation("Android Keystore key '{Alias}' already exists", KeyAlias);
            return;
        }

        _logger.LogInformation("Generating new Android Keystore key '{Alias}'", KeyAlias);

        var keyGen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, AndroidKeyStore)!;
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build()!;

        keyGen.Init(spec);
        keyGen.GenerateKey();

        _logger.LogInformation("Android Keystore key generated successfully");
    }

    private static IKey GetKey()
    {
        var keyStore = KeyStore.GetInstance(AndroidKeyStore)!;
        keyStore.Load(null);
        return keyStore.GetKey(KeyAlias, null)!;
    }

    /// <summary>
    /// Returns the cached AES key, loading it from the Android Keystore on
    /// first call. Avoids repeated <c>KeyStore.GetInstance</c> + <c>Load</c>
    /// round-trips through the TEE/HSM on every Protect/Unprotect call.
    /// Thread-safe: worst case two threads both load the key and one write
    /// is lost — both produce the same IKey reference from the Keystore.
    /// </summary>
    private IKey GetCachedKey()
    {
        return _cachedKey ??= GetKey();
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
