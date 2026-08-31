using System.Text;
using System.Text.Json;

namespace Scramble.Nostr.Crypto;

/// <summary>
/// Serializes a signed event as the JSON object a relay is given.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="NostrEventTemplate.Serialize"/>, and the two must
/// not be confused. That one produces the canonical array whose SHA-256 is the
/// event id, where a single byte of difference changes the id; this one
/// produces the transport object, where JSON escaping is free to differ because
/// the receiver unescapes before doing anything with it.
/// </para>
/// <para>
/// Which is why <see cref="Utf8JsonWriter"/> is used here and must never be used
/// there. It escapes above the BMP as surrogate pairs, so an emoji in the
/// content yields an id nobody else computes — the defect an earlier round of
/// this code shipped. Here that escaping is invisible: the relay and every
/// reader parse the JSON, and the id travels in its own field.
/// </para>
/// </remarks>
public static class NostrEnvelope
{
    /// <summary>
    /// Writes the envelope for a template and the signature over its id.
    /// </summary>
    /// <param name="template">The event, unsigned.</param>
    /// <param name="id">
    /// The event id, normally <see cref="NostrEventTemplate.ComputeId"/>. Passed
    /// rather than recomputed because a caller has usually just computed it to
    /// sign it, and recomputing invites the two to drift.
    /// </param>
    /// <param name="signature">The 64-byte BIP-340 signature over <paramref name="id"/>.</param>
    public static string Write(
        NostrEventTemplate template, ReadOnlySpan<byte> id, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (id.Length != 32)
            throw new ArgumentException("The event id must be 32 bytes.", nameof(id));
        if (signature.Length != 64)
            throw new ArgumentException("The signature must be 64 bytes.", nameof(signature));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Hex(id));
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
            writer.WriteString("sig", Hex(signature));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();
}
