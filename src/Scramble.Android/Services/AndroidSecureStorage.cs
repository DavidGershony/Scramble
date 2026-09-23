using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Services;

namespace Scramble.Android.Services;

/// <summary>
/// Android Keystore-based secure storage. Uses AES-GCM with a hardware-backed key.
/// </summary>
public class AndroidSecureStorage : ISecureStorage
{
    private const string KeyAlias = "Scramble_SecureStorage";
    private const string AndroidKeyStore = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmIvLength = 12;
    private const int GcmTagLength = 128; // bits

    // One definition, in Scramble.Core. This head kept a private copy while it was
    // abandoned -- it was deliberately excluded from the consolidation in 8c596ae
    // because I1-L forbade touching it and nothing compiled it. It ships again now,
    // so the copy is a live hazard rather than dead weight: these four bytes are how
    // Unprotect decides whether a value was ever protected, and the two Android heads
    // share one profile database. A copy that drifted would make this head read the
    // other's protected values as legacy plaintext and hand ciphertext back as data.
    private static readonly byte[] MagicPrefix = SecureStorageFormat.MagicPrefix;
    private readonly ILogger<AndroidSecureStorage> _logger;

    public AndroidSecureStorage()
    {
        _logger = LoggingConfiguration.CreateLogger<AndroidSecureStorage>();
        EnsureKeyExists();
        _logger.LogInformation("AndroidSecureStorage initialized (Android Keystore, AES-GCM)");
    }

    public byte[] Protect(byte[] data)
    {
        _logger.LogInformation("Protecting {Length} bytes with Android Keystore", data.Length);

        var key = GetKey();
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

        var key = GetKey();
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
