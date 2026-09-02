using System.Text;
using System.Text.Json;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Engine.Messages;

/// <summary>
/// The application payload inside an MLS message: an unsigned Nostr-shaped
/// event.
/// </summary>
/// <remarks>
/// <para>
/// It carries no signature, and does not need one. The MLS layer already
/// authenticates the sender, and the receiver checks that this event's
/// <c>pubkey</c> equals the MLS-authenticated sender — so a signature would
/// only restate what MLS has proved. What the receiver does <b>not</b> take on
/// trust is the id: it recomputes the canonical NIP-01 id and rejects a
/// mismatch, which is why <see cref="Create"/> never lets a caller supply one.
/// </para>
/// <para>
/// The wire form is JSON with the fields in <b>declaration order</b> —
/// <c>id, pubkey, created_at, kind, tags, content</c> — because upstream
/// serialises the struct rather than the NIP-01 array. That is only the
/// transport shape; the <i>id</i> is still the canonical NIP-01 hash over
/// <c>[0,pubkey,created_at,kind,tags,content]</c>. Confusing the two produces
/// an event every peer rejects for a mismatched id.
/// </para>
/// </remarks>
/// <param name="Id">Canonical NIP-01 event id, lowercase hex.</param>
/// <param name="PublicKeyHex">The author, which must be the MLS sender.</param>
/// <param name="CreatedAt">Unix seconds.</param>
/// <param name="Kind">Nostr kind; see the constants on this type.</param>
/// <param name="Tags">Nostr tags.</param>
/// <param name="Content">The message body.</param>
public sealed record MarmotAppEvent(
    string Id,
    string PublicKeyHex,
    long CreatedAt,
    long Kind,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string Content)
{
    /// <summary>A chat message.</summary>
    public const long ChatKind = 9;

    /// <summary>A reaction to another event.</summary>
    public const long ReactionKind = 7;

    /// <summary>A deletion request.</summary>
    public const long DeleteKind = 5;

    /// <summary>An edit of a prior message.</summary>
    public const long EditKind = 1009;

    /// <summary>The standard event-reference tag.</summary>
    public const string EventRefTag = "e";

    /// <summary>Builds an event, computing its canonical id.</summary>
    public static MarmotAppEvent Create(
        string publicKeyHex,
        long createdAt,
        long kind,
        IReadOnlyList<IReadOnlyList<string>>? tags,
        string content)
    {
        ArgumentNullException.ThrowIfNull(publicKeyHex);
        ArgumentNullException.ThrowIfNull(content);

        tags ??= [];

        // The id comes from the NIP-01 canonical form, via the hand-written
        // serialiser. Do not compute it from the JSON below: every .NET encoder
        // escapes above the BMP as surrogate pairs, so one emoji would yield an
        // id nobody else computes.
        var template = new NostrEventTemplate(
            publicKeyHex, createdAt, checked((int)kind), tags, content);

        return new MarmotAppEvent(
            Convert.ToHexString(template.ComputeId()).ToLowerInvariant(),
            publicKeyHex,
            createdAt,
            kind,
            tags,
            content);
    }

    /// <summary>A plain chat message.</summary>
    public static MarmotAppEvent Chat(string publicKeyHex, long createdAt, string content) =>
        Create(publicKeyHex, createdAt, ChatKind, null, content);

    /// <summary>Encodes the MLS application plaintext.</summary>
    public byte[] Encode()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Id);
            writer.WriteString("pubkey", PublicKeyHex);
            writer.WriteNumber("created_at", CreatedAt);
            writer.WriteNumber("kind", Kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in Tags)
            {
                writer.WriteStartArray();
                foreach (string value in tag)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteString("content", Content);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes the plaintext and validates its canonical id.
    /// </summary>
    /// <exception cref="MarmotAppEventException">
    /// Not decodable, or the id does not match its own contents.
    /// </exception>
    public static MarmotAppEvent Decode(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray());
        }
        catch (JsonException ex)
        {
            throw new MarmotAppEventException($"The application payload is not JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MarmotAppEventException("The application payload is not a JSON object.");

            var value = new MarmotAppEvent(
                RequireString(root, "id"),
                RequireString(root, "pubkey"),
                RequireInt64(root, "created_at"),
                RequireInt64(root, "kind"),
                ReadTags(root),
                RequireString(root, "content"));

            // Recomputed, never trusted. An id taken on faith lets a sender
            // label one message as another, which every id-keyed thing
            // downstream — replies, reactions, deduplication — would then
            // resolve to the wrong event.
            MarmotAppEvent expected = Create(
                value.PublicKeyHex, value.CreatedAt, value.Kind, value.Tags, value.Content);

            if (!string.Equals(expected.Id, value.Id, StringComparison.Ordinal))
            {
                throw new MarmotAppEventException(
                    $"The application event id is {value.Id} but its contents hash to {expected.Id}.");
            }

            return value;
        }
    }

    /// <summary>
    /// Checks the author against the MLS-authenticated sender.
    /// </summary>
    /// <remarks>
    /// The one check that makes the payload attributable. Without it a member
    /// could send a message claiming any author, and MLS would happily
    /// authenticate the envelope around the lie.
    /// </remarks>
    /// <exception cref="MarmotAppEventException">They differ.</exception>
    public void RequireSender(ReadOnlySpan<byte> mlsSenderIdentity)
    {
        if (mlsSenderIdentity.Length == 0)
        {
            throw new MarmotAppEventException(
                "The application message has no authenticated sender.");
        }

        string sender = Convert.ToHexString(mlsSenderIdentity).ToLowerInvariant();
        if (!string.Equals(sender, PublicKeyHex, StringComparison.Ordinal))
        {
            throw new MarmotAppEventException(
                $"The application event claims author {PublicKeyHex} but MLS authenticated {sender}.");
        }
    }

    /// <summary>First value of the named tag, or null.</summary>
    public string? FirstTagValue(string name)
    {
        foreach (var tag in Tags)
        {
            if (tag.Count > 1 && tag[0] == name)
                return tag[1];
        }

        return null;
    }

    private static string RequireString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new MarmotAppEventException($"The application event has no string '{name}'.");

    private static long RequireInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
            ? number
            : throw new MarmotAppEventException($"The application event has no integer '{name}'.");

    private static IReadOnlyList<IReadOnlyList<string>> ReadTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            throw new MarmotAppEventException("The application event has no tags array.");

        var result = new List<IReadOnlyList<string>>();
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.Array)
                throw new MarmotAppEventException("An application event tag is not an array.");

            var values = new List<string>();
            foreach (var value in tag.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new MarmotAppEventException("An application event tag value is not a string.");

                values.Add(value.GetString()!);
            }

            result.Add(values);
        }

        return result;
    }
}

/// <summary>Raised when an application payload is malformed or unattributable.</summary>
public sealed class MarmotAppEventException(string message) : Exception(message);
