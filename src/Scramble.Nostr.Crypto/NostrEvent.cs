using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Scramble.Nostr.Crypto;

/// <summary>
/// An unsigned Nostr event, and its NIP-01 canonical identifier.
/// </summary>
/// <remarks>
/// Generic Nostr, deliberately not in a Marmot namespace.
/// </remarks>
public sealed record NostrEventTemplate(
    string PublicKeyHex,
    long CreatedAt,
    int Kind,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string Content)
{
    /// <summary>
    /// The NIP-01 canonical serialisation:
    /// <c>[0, pubkey, created_at, kind, tags, content]</c>.
    /// </summary>
    /// <remarks>
    /// Byte-exactness matters: the event id is a hash of these bytes, so any
    /// difference in escaping or spacing changes the id and invalidates every
    /// signature over it. Uses relaxed escaping because NIP-01 escapes only the
    /// JSON-mandatory characters, where .NET's default would additionally
    /// escape non-ASCII and HTML-sensitive ones.
    /// </remarks>
    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(0);
            writer.WriteStringValue(PublicKeyHex);
            writer.WriteNumberValue(CreatedAt);
            writer.WriteNumberValue(Kind);

            writer.WriteStartArray();
            foreach (var tag in Tags)
            {
                writer.WriteStartArray();
                foreach (string value in tag)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteStringValue(Content);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The 32-byte event id: SHA-256 over <see cref="Serialize"/>.</summary>
    public byte[] ComputeId() =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Serialize()));
}
