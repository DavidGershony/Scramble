using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Services;

namespace Scramble.Desktop;

/// <summary>
/// Windows DPAPI-based secure storage. Encrypts data tied to the current Windows user account.
/// </summary>
[SupportedOSPlatform("windows")]
public class DesktopSecureStorage : ISecureStorage
{
    // One definition, in Scramble.Core. See SecureStorageFormat for why these
    // four bytes must never change and why the test doubles keep their own copy.
    private static readonly byte[] MagicPrefix = SecureStorageFormat.MagicPrefix;
    private readonly ILogger<DesktopSecureStorage> _logger;

    public DesktopSecureStorage()
    {
        _logger = LoggingConfiguration.CreateLogger<DesktopSecureStorage>();
        _logger.LogInformation("DesktopSecureStorage initialized (DPAPI, CurrentUser scope)");
    }

    public byte[] Protect(byte[] data)
    {
        _logger.LogInformation("Protecting {Length} bytes with DPAPI", data.Length);
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

        // Prepend magic prefix to identify encrypted data
        var result = new byte[MagicPrefix.Length + encrypted.Length];
        MagicPrefix.CopyTo(result, 0);
        encrypted.CopyTo(result, MagicPrefix.Length);

        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < MagicPrefix.Length || !HasMagicPrefix(data))
        {
            _logger.LogInformation("Data lacks encryption prefix ({Length} bytes), returning as-is (unencrypted)", data.Length);
            return data;
        }

        _logger.LogInformation("Unprotecting {Length} bytes with DPAPI", data.Length);
        var encrypted = new byte[data.Length - MagicPrefix.Length];
        Array.Copy(data, MagicPrefix.Length, encrypted, 0, encrypted.Length);

        return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
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
