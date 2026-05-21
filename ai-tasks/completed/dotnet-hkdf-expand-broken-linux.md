# .NET HKDF.Expand broken on Linux with OpenSSL 3.x

## Summary

`System.Security.Cryptography.HKDF.Expand()` throws `CryptographicException` on every call when running .NET 9.0 on Linux with OpenSSL 3.x. This causes 48 test failures in `Scramble.Core.Tests` — all related to NIP-44 encryption and MLS exporter key derivation.

## Root Cause

The bug is in .NET's interop layer for calling OpenSSL's EVP_KDF API specifically in EXPAND_ONLY mode. It is not an OpenSSL bug — HKDF works correctly when called via the OpenSSL CLI with identical parameters.

Confirmed behavior on Debian 12 (bookworm) + .NET 9.0.2 + OpenSSL 3.0.19:

| Method | Result |
|--------|--------|
| `HKDF.Extract(SHA256, ikm, salt)` | **Works** |
| `HKDF.DeriveKey(SHA256, ikm, len, salt, info)` | **Works** (Extract+Expand combined) |
| `HKDF.Expand(SHA256, prk, len, info)` | **Fails at every output length** |
| `openssl kdf -kdfopt mode:EXPAND_ONLY ... HKDF` | **Works** (CLI, same params) |
| `HMAC-SHA256` | **Works** |

## Affected Platforms

Any Linux distribution using OpenSSL 3.x with .NET 9.0:

| Distro | OpenSSL | Affected |
|--------|---------|----------|
| Debian 12 (bookworm) | 3.0.x | Yes |
| Ubuntu 22.04+ | 3.0.x | Yes |
| Fedora 36+ | 3.x | Yes |
| RHEL 9 / Rocky 9 | 3.0.x | Yes |
| Alpine 3.17+ | 3.x | Likely |
| Debian 11 (bullseye) | 1.1.1 | No — uses old API path |
| Ubuntu 20.04 | 1.1.1 | No |
| Windows / macOS | N/A | No — different crypto backend |

## Affected Code (4 call sites in marmot-cs)

1. **`MarmotCs.Protocol/Nip44/Nip44MessageKeys.cs:51`**
   ```csharp
   byte[] expanded = HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, 76, nonce);
   ```
   Per-message key derivation: splits 76 bytes into encryption key (32) + ChaCha nonce (12) + HMAC key (32).

2. **`MarmotCs.Protocol/Nip44/Nip44Encryption.cs:71`**
   ```csharp
   byte[] conversationKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedX, HkdfSalt);
   ```
   This one actually works (Extract, not Expand), but should be considered for consistency.

3. **`MarmotCs.Protocol/Crypto/ExporterSecretKeyDerivation.cs:36,50`**
   ```csharp
   byte[] privateKeyBytes = HKDF.Expand(HashAlgorithmName.SHA256, exporterSecret, 32, KeyDerivationLabel);
   // and retry path:
   privateKeyBytes = HKDF.Expand(HashAlgorithmName.SHA256, exporterSecret, 32, retryLabel);
   ```
   Deriving Nostr private keys from MLS exporter secrets.

4. **`MarmotCs.Protocol/Crypto/ImageEncryption.cs:123`**
   ```csharp
   return HKDF.Expand(HashAlgorithmName.SHA256, exporterSecret, 32, ImageKeyLabel);
   ```
   Deriving group image encryption key.

## 48 Failing Tests

All tests that exercise NIP-44 encryption/decryption or MLS key derivation, including:
- `Nip44VectorTests` — all NIP-44 v2 test vectors
- `Nip44EncryptionTests` — encrypt/decrypt round trips
- `Nip44MessageKeysTests` — key derivation
- `Mip02InteropTests` — gift wrap seal/unseal (uses NIP-44 internally)
- `SecurityTests.M1_Nip44EncryptDecrypt_RoundTrip`
- `WelcomeRumorFormatTests` — welcome message construction
- `MediaMessageImetaTests` — media message encryption
- Any test using `ExporterSecretKeyDerivation` or `ImageEncryption`

## Recommended Fix

Add a managed HKDF helper using HMAC-SHA256 (which works on all platforms) and replace the 3 `HKDF.Expand` call sites. This is a ~30 line class:

```csharp
// MarmotCs.Protocol/Crypto/HkdfHelper.cs
using System.Security.Cryptography;

public static class HkdfHelper
{
    public static byte[] Expand(byte[] prk, int outputLength, byte[] info)
    {
        int hashLen = 32; // SHA-256
        int n = (int)Math.Ceiling((double)outputLength / hashLen);
        var output = new byte[n * hashLen];
        var t = Array.Empty<byte>();

        using var hmac = new HMACSHA256(prk);
        for (int i = 1; i <= n; i++)
        {
            var input = new byte[t.Length + info.Length + 1];
            Buffer.BlockCopy(t, 0, input, 0, t.Length);
            Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
            input[^1] = (byte)i;

            t = hmac.ComputeHash(input);
            Buffer.BlockCopy(t, 0, output, (i - 1) * hashLen, hashLen);
        }

        var result = new byte[outputLength];
        Buffer.BlockCopy(output, 0, result, 0, outputLength);
        return result;
    }
}
```

Then replace each `HKDF.Expand(HashAlgorithmName.SHA256, prk, len, info)` with `HkdfHelper.Expand(prk, len, info)`.

`HKDF.Extract` can optionally be replaced too for consistency, but it works today.

## Why Not Other Approaches

- **Switching to Debian 11 / OpenSSL 1.1**: Temporary fix, OpenSSL 1.1 is EOL.
- **Using `HKDF.DeriveKey` instead**: It combines Extract+Expand — semantically wrong when you already have a PRK and only need Expand.
- **Waiting for .NET fix**: Unknown timeline. The managed helper is trivial, spec-compliant (RFC 5869), and portable.

## Related Issues

- https://github.com/dotnet/runtime/issues/46526 (OpenSSL 3.0 support)
- https://github.com/dotnet/runtime/issues/79153 (.NET crashes with OpenSSL 3 deprecated APIs)
- https://github.com/dotnet/dotnet-docker/issues/5849 (crypto fails in containers on FIPS kernels)

## Impact

This only affects the **managed C# MLS path**. The Rust native library (`scramble_native.so`) has its own HKDF implementation and is not affected.

On Windows and macOS (where Scramble runs as a user-facing app), this issue does not occur — .NET uses CNG (Windows) or Apple Security (macOS) instead of OpenSSL. The issue is specific to Linux server/CI/Docker environments.
