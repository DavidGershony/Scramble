using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Scramble.Nostr.Crypto;

/// <summary>An unsigned inner event (a NIP-59 "rumor").</summary>
public sealed record Rumor(
    string PublicKeyHex,
    long CreatedAt,
    int Kind,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string Content);

/// <summary>Raised when a gift wrap cannot be opened or fails a security check.</summary>
public sealed class GiftWrapException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// NIP-59 gift wrapping: a rumor inside a signed seal inside an
/// ephemerally-signed wrap.
/// </summary>
/// <remarks>
/// <para>
/// The point of the two layers is metadata privacy. The outer kind-1059 wrap is
/// signed by a throwaway key, so a relay learns only the recipient; the inner
/// kind-13 seal is signed by the real sender and is only visible after the
/// recipient decrypts.
/// </para>
/// <para>
/// Generic Nostr, deliberately not in a Marmot namespace.
/// </para>
/// </remarks>
public static class Nip59GiftWrap
{
    public const int SealKind = 13;
    public const int WrapKind = 1059;

    /// <summary>
    /// Ceiling on each encrypted layer, applied before base64 decoding.
    /// </summary>
    /// <remarks>
    /// A policy limit, not a spec rule. Gift wraps arrive unsolicited from a
    /// relay, so without a bound an attacker can make every inbound event cost
    /// a full decode, HMAC and allocation. One mebibyte is far above any real
    /// Welcome while keeping the cost of junk bounded.
    /// </remarks>
    public const int MaxLayerPayloadLength = 1024 * 1024;

    /// <summary>Default backward jitter applied to seal and wrap timestamps.</summary>
    /// <remarks>
    /// NIP-59 recommends randomising up to two days. Applied by default because
    /// a privacy control that must be opted into is one that is usually off.
    /// </remarks>
    public static readonly TimeSpan DefaultTimestampJitter = TimeSpan.FromDays(2);

    /// <summary>
    /// Opens a gift wrap and returns the rumor inside.
    /// </summary>
    /// <param name="wrapJson">The kind-1059 event as JSON.</param>
    /// <param name="recipientPrivateKey">The recipient's 32-byte secret.</param>
    /// <remarks>
    /// Verifies both signatures and, critically, that the rumor's author is the
    /// same key that signed the seal. Without that last check anyone could
    /// place another person's pubkey on the inner event and impersonate them,
    /// since only the seal is signed.
    /// </remarks>
    /// <exception cref="GiftWrapException">Malformed, undecryptable, or failing a check.</exception>
    public static Rumor Unwrap(
        string wrapJson,
        ReadOnlySpan<byte> recipientPrivateKey,
        ReadOnlySpan<byte> recipientPublicKey = default)
    {
        ArgumentNullException.ThrowIfNull(wrapJson);

        var wrap = ParseSignedEvent(wrapJson, "gift wrap");
        if (wrap.Kind != WrapKind)
            throw new GiftWrapException($"Expected a kind-{WrapKind} gift wrap, got kind {wrap.Kind}.");
        RequireValidSignature(wrap, "gift wrap");
        RequireAddressedToUs(wrap, recipientPublicKey);

        string sealJson = Decrypt(wrap.Content, recipientPrivateKey, wrap.PublicKeyHex, "gift wrap");

        var seal = ParseSignedEvent(sealJson, "seal");
        if (seal.Kind != SealKind)
            throw new GiftWrapException($"Expected a kind-{SealKind} seal, got kind {seal.Kind}.");
        RequireValidSignature(seal, "seal");

        string rumorJson = Decrypt(seal.Content, recipientPrivateKey, seal.PublicKeyHex, "seal");
        var rumor = ParseRumor(rumorJson);

        // The security hinge of NIP-59: only the seal is signed, so an inner
        // author that differs from the seal's signer is an impersonation
        // attempt, not a quirk.
        if (!string.Equals(rumor.PublicKeyHex, seal.PublicKeyHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new GiftWrapException(
                "The rumor's author does not match the seal's signer; the inner sender is forged.");
        }

        return rumor;
    }

    /// <summary>
    /// Wraps a rumor for a recipient.
    /// </summary>
    /// <param name="sign">
    /// Signs a 32-byte event id under a given secret, returning 64 bytes. Passed
    /// in so this stays free of any particular signing implementation.
    /// </param>
    /// <param name="ephemeralPrivateKey">
    /// A throwaway secret for the outer wrap. Reusing one across wraps would
    /// link them together and defeat the purpose of the outer layer.
    /// </param>
    /// <param name="timestampJitter">
    /// Applied to the seal and wrap timestamps. Real timestamps would leak when
    /// the message was actually written.
    /// </param>
    public static string Wrap(
        Rumor rumor,
        ReadOnlySpan<byte> senderPrivateKey,
        ReadOnlySpan<byte> senderPublicKey,
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> ephemeralPrivateKey,
        ReadOnlySpan<byte> ephemeralPublicKey,
        Func<byte[], byte[], byte[]> sign,
        TimeSpan? timestampJitter = null)
    {
        ArgumentNullException.ThrowIfNull(rumor);
        ArgumentNullException.ThrowIfNull(sign);

        string senderPublicKeyHex = Convert.ToHexString(senderPublicKey).ToLowerInvariant();

        // The seal's author is taken from the signing identity, never from the
        // rumor. Deriving it from the rumor would let a caller emit a seal
        // labelled with someone else's key, producing a wrap that no recipient
        // can open — a confusing failure in place of a clear one here.
        if (!string.Equals(rumor.PublicKeyHex, senderPublicKeyHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The rumor's author must be the sending identity.", nameof(rumor));
        }

        byte[] senderKey = senderPrivateKey.ToArray();
        byte[] ephemeralKey = ephemeralPrivateKey.ToArray();

        byte[] sealConversationKey = Nip44.DeriveConversationKey(senderKey, recipientPublicKey);
        string sealContent = Nip44.Encrypt(SerializeRumor(rumor), sealConversationKey);

        long sealCreatedAt = Jitter(timestampJitter);
        var sealTemplate = new NostrEventTemplate(
            senderPublicKeyHex, sealCreatedAt, SealKind,
            Array.Empty<IReadOnlyList<string>>(), sealContent);
        byte[] sealId = sealTemplate.ComputeId();
        byte[] sealSignature = RequireValidSignature(
            sign(senderKey, sealId), senderPublicKey, sealId, "seal");
        string sealJson = SerializeSignedEvent(sealTemplate, sealId, sealSignature);

        byte[] wrapConversationKey = Nip44.DeriveConversationKey(ephemeralKey, recipientPublicKey);
        string wrapContent = Nip44.Encrypt(sealJson, wrapConversationKey);

        var wrapTemplate = new NostrEventTemplate(
            Convert.ToHexString(ephemeralPublicKey).ToLowerInvariant(),
            Jitter(timestampJitter),
            WrapKind,
            new[] { new[] { "p", Convert.ToHexString(recipientPublicKey).ToLowerInvariant() } },
            wrapContent);
        byte[] wrapId = wrapTemplate.ComputeId();
        byte[] wrapSignature = RequireValidSignature(
            sign(ephemeralKey, wrapId), ephemeralPublicKey, wrapId, "gift wrap");
        return SerializeSignedEvent(wrapTemplate, wrapId, wrapSignature);
    }

    /// <summary>
    /// Checks a signature the caller's delegate produced before it is embedded.
    /// </summary>
    /// <remarks>
    /// A delegate that returns the wrong length, garbage, or a signature under
    /// a key that does not match the advertised one otherwise yields a
    /// well-formed wrap that no recipient can open — discovered far from its
    /// cause. The account-identity-proof signer already refuses to trust its
    /// signer's response; this keeps the two consistent, because asymmetric
    /// trust in one assembly is how the laxer one gets copied.
    /// </remarks>
    private static byte[] RequireValidSignature(
        byte[] signature, ReadOnlySpan<byte> publicKey, byte[] message, string layer)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (signature.Length != 64 || !Bip340.Verify(publicKey, message, signature))
        {
            throw new InvalidOperationException(
                $"The signing delegate returned a signature for the {layer} that does not verify "
                + "under the supplied public key.");
        }

        return signature;
    }

    private static long Jitter(TimeSpan? window)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var span = window ?? DefaultTimestampJitter;
        if (span <= TimeSpan.Zero)
            return now;

        // Backwards only: a future timestamp is more conspicuous than an old one.
        return now - RandomNumberGenerator.GetInt32(0, (int)span.TotalSeconds + 1);
    }

    private static string Decrypt(
        string content, ReadOnlySpan<byte> recipientPrivateKey, string senderPublicKeyHex, string layer)
    {
        byte[] senderPublicKey;
        try
        {
            senderPublicKey = Convert.FromHexString(senderPublicKeyHex);
        }
        catch (FormatException ex)
        {
            throw new GiftWrapException($"The {layer}'s author key is not valid hex.", ex);
        }

        try
        {
            byte[] conversationKey = Nip44.DeriveConversationKey(recipientPrivateKey, senderPublicKey);
            return Nip44.Decrypt(content, conversationKey, MaxLayerPayloadLength);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new GiftWrapException($"Could not decrypt the {layer}.", ex);
        }
    }

    /// <summary>
    /// Checks the wrap's p tag names us, when the caller supplies its own key.
    /// </summary>
    /// <remarks>
    /// Decryption is the real gate — a wrap for someone else will not open —
    /// but the spec requires rejecting a Welcome not addressed to our account,
    /// and failing on the tag gives a clear reason instead of a decrypt error.
    /// </remarks>
    private static void RequireAddressedToUs(SignedEvent wrap, ReadOnlySpan<byte> recipientPublicKey)
    {
        if (recipientPublicKey.IsEmpty)
            return;

        string expected = Convert.ToHexString(recipientPublicKey).ToLowerInvariant();
        foreach (var tag in wrap.Tags)
        {
            if (tag.Count >= 2 && tag[0] == "p"
                && string.Equals(tag[1], expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new GiftWrapException("The gift wrap is not addressed to this account.");
    }

    private static void RequireValidSignature(SignedEvent value, string layer)
    {
        var template = new NostrEventTemplate(
            value.PublicKeyHex, value.CreatedAt, value.Kind, value.Tags, value.Content);

        byte[] computedId = template.ComputeId();
        if (!Convert.ToHexString(computedId).Equals(value.Id, StringComparison.OrdinalIgnoreCase))
            throw new GiftWrapException($"The {layer}'s id does not match its content.");

        byte[] publicKey, signature;
        try
        {
            publicKey = Convert.FromHexString(value.PublicKeyHex);
            signature = Convert.FromHexString(value.Signature);
        }
        catch (FormatException ex)
        {
            throw new GiftWrapException($"The {layer} has malformed hex fields.", ex);
        }

        if (!Bip340.Verify(publicKey, computedId, signature))
            throw new GiftWrapException($"The {layer}'s signature does not verify.");
    }

    private sealed record SignedEvent(
        string Id,
        string PublicKeyHex,
        long CreatedAt,
        int Kind,
        IReadOnlyList<IReadOnlyList<string>> Tags,
        string Content,
        string Signature);

    private static SignedEvent ParseSignedEvent(string json, string layer)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new SignedEvent(
                root.GetProperty("id").GetString()!,
                root.GetProperty("pubkey").GetString()!,
                root.GetProperty("created_at").GetInt64(),
                root.GetProperty("kind").GetInt32(),
                ReadTags(root),
                root.GetProperty("content").GetString()!,
                root.GetProperty("sig").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GiftWrapException($"The {layer} is not a well-formed Nostr event.", ex);
        }
    }

    private static Rumor ParseRumor(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new Rumor(
                root.GetProperty("pubkey").GetString()!,
                root.GetProperty("created_at").GetInt64(),
                root.GetProperty("kind").GetInt32(),
                ReadTags(root),
                root.GetProperty("content").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GiftWrapException("The rumor is not a well-formed event.", ex);
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tagsElement)
            || tagsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IReadOnlyList<string>>();
        }

        var tags = new List<IReadOnlyList<string>>();
        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            var values = new List<string>();
            foreach (var value in tagElement.EnumerateArray())
                values.Add(value.GetString() ?? string.Empty);
            tags.Add(values);
        }

        return tags;
    }

    private static string SerializeRumor(Rumor rumor)
    {
        var template = new NostrEventTemplate(
            rumor.PublicKeyHex, rumor.CreatedAt, rumor.Kind, rumor.Tags, rumor.Content);
        return WriteEvent(template, Convert.ToHexString(template.ComputeId()).ToLowerInvariant(), null);
    }

    private static string SerializeSignedEvent(
        NostrEventTemplate template, byte[] id, byte[] signature) =>
        WriteEvent(
            template,
            Convert.ToHexString(id).ToLowerInvariant(),
            Convert.ToHexString(signature).ToLowerInvariant());

    private static string WriteEvent(NostrEventTemplate template, string id, string? signature)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("pubkey", template.PublicKeyHex);
            writer.WriteNumber("created_at", template.CreatedAt);
            writer.WriteNumber("kind", template.Kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in template.Tags)
            {
                writer.WriteStartArray();
                foreach (string value in tag)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteString("content", template.Content);
            if (signature is not null)
                writer.WriteString("sig", signature);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
