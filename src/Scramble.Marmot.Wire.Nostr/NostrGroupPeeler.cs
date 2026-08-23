using System.Security.Cryptography;
using System.Text.Json;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Wire.Nostr;

/// <summary>
/// The Nostr implementation of <see cref="ITransportPeeler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Wraps and unwraps kind-445 events. The MLS message is sealed with
/// ChaCha20-Poly1305 under the group's MLS exporter secret, which is the
/// confidentiality layer — the MLS framing inside is plaintext, so this wrap is
/// what actually protects the message on a relay.
/// </para>
/// <para>
/// Welcome handling (kind 444 inside a NIP-59 gift wrap) is not implemented
/// yet; <see cref="Peel"/> rejects those envelopes rather than pretending.
/// </para>
/// </remarks>
public sealed class NostrGroupPeeler : ITransportPeeler
{
    /// <summary>MLS exporter label the Marmot protocol derives group-event keys under.</summary>
    public const string ExporterLabel = "marmot";

    /// <summary>MLS exporter context for group message encryption.</summary>
    public static readonly byte[] ExporterContext = "group-event"u8.ToArray();

    /// <summary>Length of the exporter secret, which is used directly as the AEAD key.</summary>
    public const int ExporterLength = 32;

    private readonly HashSet<string> _usedNonces = new();
    private readonly object _nonceLock = new();

    /// <inheritdoc />
    public PeeledMessage Peel(string envelope, Func<byte[], byte[]?> exporterSecretFor)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(exporterSecretFor);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(envelope);
        }
        catch (JsonException ex)
        {
            throw new PeelFailedException($"The envelope is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            int kind = ReadKind(root);
            if (kind != GroupMessageEvent.Kind)
                throw new PeelFailedException($"Unsupported event kind {kind}.");

            var tags = GroupMessageEvent.ReadTags(root);
            byte[] transportGroupId = GroupMessageEvent.ReadTransportGroupId(tags);

            byte[]? exporterSecret = exporterSecretFor(transportGroupId);
            if (exporterSecret is null)
            {
                // The commit that produces this epoch's secret may simply not
                // have arrived yet, so this is deferrable rather than terminal.
                throw new PeelFailedException(
                    "No exporter secret is available for this routing id.", retryable: true);
            }

            string content = ReadContent(root);
            byte[] mlsBytes;
            try
            {
                mlsBytes = ChaCha20Poly1305Envelope.Open(content, exporterSecret);
            }
            catch (CryptographicException ex)
            {
                // Could be a message from an epoch we cannot open, so let the
                // engine retry under another retained secret.
                throw new PeelFailedException(
                    $"Could not open the group message: {ex.Message}", retryable: true);
            }

            return new PeeledMessage(
                PeeledContentKind.GroupMessage,
                transportGroupId,
                ReadId(root),
                mlsBytes);
        }
    }

    /// <inheritdoc />
    public string WrapGroupMessage(
        ReadOnlySpan<byte> mlsBytes,
        ReadOnlySpan<byte> transportGroupId,
        ReadOnlySpan<byte> exporterSecret,
        long? expiresAt = null)
    {
        if (exporterSecret.Length != ExporterLength)
            throw new ArgumentException(
                $"The exporter secret must be {ExporterLength} bytes.", nameof(exporterSecret));

        string content = ChaCha20Poly1305Envelope.Seal(mlsBytes, exporterSecret, out byte[] nonce);
        RequireFreshNonce(exporterSecret, nonce);

        var tags = GroupMessageEvent.BuildTags(transportGroupId, expiresAt);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("kind", GroupMessageEvent.Kind);
            writer.WriteString("content", content);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (string value in tag)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Enforces the rule that a nonce must never repeat under one key.
    /// </summary>
    /// <remarks>
    /// A repeat is catastrophic for this construction, so the message must not
    /// be transmitted. With a 12-byte random nonce this is vanishingly
    /// unlikely, but the protocol mandates the check. Scoped to this instance
    /// rather than a process-wide static: the previous implementation used a
    /// static dictionary that grew without bound and outlived the keys it
    /// tracked.
    /// </remarks>
    private void RequireFreshNonce(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        string composite = Convert.ToHexString(key) + ":" + Convert.ToHexString(nonce);
        lock (_nonceLock)
        {
            if (!_usedNonces.Add(composite))
                throw new CryptographicException(
                    "A duplicate outbound nonce was generated; the message must not be transmitted.");
        }
    }

    /// <summary>Forgets tracked nonces for keys that are no longer in use.</summary>
    public void ResetNonceTracking()
    {
        lock (_nonceLock)
            _usedNonces.Clear();
    }

    private static int ReadKind(JsonElement root) =>
        root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Number
            ? kind.GetInt32()
            : throw new PeelFailedException("The envelope has no kind.");

    private static string ReadContent(JsonElement root) =>
        root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : throw new PeelFailedException("The envelope has no content.");

    private static string? ReadId(JsonElement root) =>
        root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}
