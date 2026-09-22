using System.Text.Json;
using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;

namespace Scramble.Core.Services;

/// <summary>
/// Signs account-identity proofs through a NIP-46 remote signer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <see cref="INostrEventSigner"/>.</b> A kind-450 proof
/// binds an MLS leaf key to a Nostr account, and what it commits to is an exact
/// event template — the verifier recomputes that template's id and checks the
/// signature against it. <c>INostrEventSigner.SignEventAsync</c> takes no
/// <c>created_at</c>, so <c>ExternalNostrEventSigner</c> stamps
/// <c>DateTime.UtcNow</c> itself; the signature that comes back verifies over a
/// different id than the proof commits to, and no amount of adapting recovers
/// the binding.
/// </para>
/// <para>
/// <see cref="IExternalSigner.SignEventAsync"/> does take a
/// <c>created_at</c>, and <c>ExternalSignerService</c> puts it on the NIP-46
/// wire verbatim. So this maps our template onto that call and keeps the
/// template as the thing being signed.
/// </para>
/// <para>
/// <b>It trusts the remote signer with nothing.</b>
/// <see cref="AccountIdentityProofSigning"/> verifies every signature against
/// the template before the proof is used, so a signer that rewrote a field
/// produces a verification failure rather than a bad proof. The field-by-field
/// check below exists only to say <i>which</i> field moved — a bare
/// "signature did not verify" would send a reader looking at our own codec,
/// which is the expensive wrong place.
/// </para>
/// </remarks>
public sealed class ExternalAccountProofSigner : IAccountIdentityProofSigner
{
    private readonly IExternalSigner _signer;

    public ExternalAccountProofSigner(IExternalSigner signer)
    {
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    }

    /// <summary>The connected signer's account key, x-only, 32 bytes.</summary>
    /// <remarks>
    /// Read on each access rather than captured at construction: the signer is
    /// wired before it finishes connecting, and a key captured then would be
    /// null for the life of the object.
    /// </remarks>
    public ReadOnlyMemory<byte> AccountPublicKey
    {
        get
        {
            string hex = _signer.PublicKeyHex
                ?? throw new InvalidOperationException(
                    "The external signer has no public key yet, so there is no account to "
                    + "issue a proof for. Wait for it to connect.");

            byte[] key = Convert.FromHexString(hex);

            if (key.Length != 32)
            {
                throw new InvalidOperationException(
                    $"The external signer's public key is {key.Length} bytes; a Nostr account "
                    + "key is 32.");
            }

            return key;
        }
    }

    public async Task<byte[]> SignAsync(
        NostrEventTemplate template, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (!_signer.IsConnected)
        {
            throw new InvalidOperationException(
                "The external signer is not connected, so it cannot sign an account-identity "
                + "proof.");
        }

        var unsigned = new UnsignedNostrEvent
        {
            Kind = template.Kind,
            Content = template.Content,
            Tags = [.. template.Tags.Select(tag => tag.ToList())],

            // Ours, not the signer's. This is the whole reason the proof cannot
            // go through INostrEventSigner.
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(template.CreatedAt).UtcDateTime,
        };

        string signedJson = await _signer.SignEventAsync(unsigned).WaitAsync(ct);

        using JsonDocument doc = ParseOrThrow(signedJson);
        JsonElement root = doc.RootElement;

        RequireSame(root, "created_at", template.CreatedAt);
        RequireSame(root, "kind", template.Kind);
        RequireSame(root, "pubkey", template.PublicKeyHex);
        RequireSame(root, "content", template.Content);

        string sigHex = root.TryGetProperty("sig", out JsonElement sig)
            ? sig.GetString() ?? ""
            : throw new InvalidOperationException(
                "The external signer returned an event with no signature.");

        byte[] signature = Convert.FromHexString(sigHex);

        if (signature.Length != 64)
        {
            throw new InvalidOperationException(
                $"The external signer returned a {signature.Length}-byte signature; BIP-340 "
                + "is 64.");
        }

        return signature;
    }

    private static JsonDocument ParseOrThrow(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The external signer returned something that is not a Nostr event: {ex.Message}",
                ex);
        }
    }

    private static void RequireSame(JsonElement root, string field, object expected)
    {
        object? actual = root.TryGetProperty(field, out JsonElement element)
            ? element.ValueKind switch
            {
                JsonValueKind.Number when expected is long => element.GetInt64(),
                JsonValueKind.Number => element.GetInt32(),
                _ => element.GetString(),
            }
            : null;

        if (Equals(actual, expected))
            return;

        throw new InvalidOperationException(
            $"The external signer changed '{field}' from '{expected}' to '{actual}'. An "
            + "account-identity proof commits to the exact template it was built from, so a "
            + "signature over anything else cannot be used.");
    }
}
