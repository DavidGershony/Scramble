using System.Text.Json;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Wire.Nostr;

/// <summary>
/// A signed Nostr event parsed from an untrusted envelope.
/// </summary>
/// <remarks>
/// Every accessor here is total: the input is attacker-controlled, and
/// <see cref="ITransportPeeler"/> requires failures to arrive as
/// <see cref="PeelFailedException"/> carrying a retryable flag. An escaped
/// <c>FormatException</c> or <c>InvalidOperationException</c> bypasses that
/// classification entirely, so the engine cannot tell "defer" from "drop".
/// </remarks>
internal sealed record SignedNostrEvent(
    string Id,
    string PublicKeyHex,
    long CreatedAt,
    int Kind,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string Content,
    string Signature)
{
    public static SignedNostrEvent Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PeelFailedException("The envelope is not a JSON object.");

        return new SignedNostrEvent(
            RequireString(root, "id"),
            RequireString(root, "pubkey"),
            RequireInt64(root, "created_at"),
            RequireInt32(root, "kind"),
            ReadTags(root),
            RequireString(root, "content"),
            RequireString(root, "sig"));
    }

    /// <summary>
    /// Recomputes the NIP-01 id, checks it matches the claimed one, and
    /// verifies the BIP-340 signature over it.
    /// </summary>
    /// <returns>The verified 32-byte event id.</returns>
    /// <exception cref="PeelFailedException">Any check fails.</exception>
    public byte[] VerifyAndComputeId()
    {
        byte[] publicKey = RequireHex(PublicKeyHex, 32, "pubkey");
        byte[] signature = RequireHex(Signature, 64, "sig");

        byte[] computedId;
        try
        {
            computedId = new NostrEventTemplate(PublicKeyHex, CreatedAt, Kind, Tags, Content)
                .ComputeId();
        }
        catch (ArgumentException ex)
        {
            // Unpaired surrogates cannot be canonically encoded, so the event
            // could never have had a valid id.
            throw new PeelFailedException($"The event cannot be canonically encoded: {ex.Message}");
        }

        if (!Convert.ToHexString(computedId).Equals(Id, StringComparison.OrdinalIgnoreCase))
            throw new PeelFailedException("The event id does not match its content.");

        if (!Bip340.Verify(publicKey, computedId, signature))
            throw new PeelFailedException("The event signature does not verify.");

        return computedId;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tagsElement))
            throw new PeelFailedException("The event has no tags array.");
        if (tagsElement.ValueKind != JsonValueKind.Array)
            throw new PeelFailedException("The event's tags field is not an array.");

        var tags = new List<IReadOnlyList<string>>();
        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind != JsonValueKind.Array)
                throw new PeelFailedException("Every tag must be an array.");

            var values = new List<string>();
            foreach (var value in tagElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new PeelFailedException("Every tag element must be a string.");
                values.Add(value.GetString()!);
            }

            tags.Add(values);
        }

        return tags;
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new PeelFailedException($"The event has no {name}.");
        if (value.ValueKind != JsonValueKind.String)
            throw new PeelFailedException($"The event's {name} is not a string.");

        return value.GetString()!;
    }

    private static long RequireInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new PeelFailedException($"The event has no {name}.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long parsed))
            throw new PeelFailedException($"The event's {name} is not a 64-bit integer.");

        return parsed;
    }

    private static int RequireInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new PeelFailedException($"The event has no {name}.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed))
            throw new PeelFailedException($"The event's {name} is not a 32-bit integer.");

        return parsed;
    }

    private static byte[] RequireHex(string value, int length, string name)
    {
        if (value.Length != length * 2)
            throw new PeelFailedException($"The event's {name} must be {length} bytes of hex.");

        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new PeelFailedException($"The event's {name} is not valid hex.");
        }
    }
}
