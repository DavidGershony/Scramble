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
/// Also unwraps NIP-59 gift wraps carrying a kind-444 Welcome. That path needs
/// the local account secret, so a peeler constructed without one still handles
/// group messages but rejects Welcomes rather than silently ignoring them.
/// </para>
/// </remarks>
public sealed class NostrGroupPeeler : ITransportPeeler
{
    private readonly byte[]? _accountSecret;

    /// <param name="accountSecret">
    /// The local account's 32-byte secret, used to open gift-wrapped Welcomes.
    /// Omit it for a peeler that only handles group messages.
    /// </param>
    public NostrGroupPeeler(ReadOnlySpan<byte> accountSecret = default)
    {
        if (accountSecret.Length is not (0 or 32))
            throw new ArgumentException("The account secret must be 32 bytes.", nameof(accountSecret));

        _accountSecret = accountSecret.Length == 32 ? accountSecret.ToArray() : null;
    }

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
            var signed = SignedNostrEvent.Parse(document.RootElement);

            if (signed.Kind == Nip59GiftWrap.WrapKind)
                return PeelWelcome(envelope, signed);

            if (signed.Kind != GroupMessageEvent.Kind)
                throw new PeelFailedException($"Unsupported event kind {signed.Kind}.");

            // The transport spec makes this a MUST, twice over: verify the id
            // and signature BEFORE treating any field — kind, tags, content, or
            // the id itself — as authenticated. The AEAD below protects the
            // payload, but nothing else here is protected without this.
            byte[] computedId = signed.VerifyAndComputeId();

            var tags = GroupMessageEvent.ReadTags(document.RootElement);
            byte[] transportGroupId = GroupMessageEvent.ReadTransportGroupId(tags);

            byte[]? exporterSecret = exporterSecretFor(transportGroupId);
            if (exporterSecret is null)
            {
                // The commit that produces this epoch's secret may simply not
                // have arrived yet, so this is deferrable rather than terminal.
                throw new PeelFailedException(
                    "No exporter secret is available for this routing id.", retryable: true);
            }

            byte[] mlsBytes;
            try
            {
                mlsBytes = ChaCha20Poly1305Envelope.Open(signed.Content, exporterSecret);
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
                // Bound to the event hash rather than the self-reported id. The
                // transport id keys deduplication, so accepting an attacker's
                // chosen value lets them pre-poison it and have a legitimate
                // message dropped as a duplicate.
                Convert.ToHexString(computedId).ToLowerInvariant(),
                mlsBytes);
        }
    }

    /// <summary>
    /// Opens a gift-wrapped Welcome addressed to the local account.
    /// </summary>
    /// <remarks>
    /// Failures here are terminal rather than retryable. A wrap that will not
    /// open for us is either not ours or forged, and neither improves on a
    /// later attempt — unlike a group message, whose epoch secret may still be
    /// on its way.
    /// </remarks>
    private PeeledMessage PeelWelcome(string envelope, SignedNostrEvent wrap)
    {
        if (_accountSecret is null)
        {
            throw new PeelFailedException(
                "This peeler has no account secret, so it cannot open gift-wrapped Welcomes.");
        }

        Rumor rumor;
        try
        {
            rumor = Nip59GiftWrap.Unwrap(envelope, _accountSecret);
        }
        catch (GiftWrapException ex)
        {
            throw new PeelFailedException($"Could not open the gift-wrapped Welcome: {ex.Message}");
        }

        var welcome = WelcomeEvent.Read(rumor);

        return new PeeledMessage(
            PeeledContentKind.Welcome,
            TransportGroupId: null,
            // Bound to the verified wrap hash, not the self-reported id.
            Convert.ToHexString(wrap.VerifyAndComputeId()).ToLowerInvariant(),
            welcome.WelcomeBytes)
        {
            Welcome = new WelcomeDetails(
                welcome.KeyPackageEventId, welcome.Relays, welcome.SenderPublicKeyHex),
        };
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

        // Signed here, with a key generated here, on purpose. The spec requires
        // a fresh ephemeral key per event and forbids the account identity —
        // signing a group message with it would deanonymise every message the
        // account sends. Returning an unsigned fragment for a caller to sign
        // would leave that choice, and that mistake, available to them.
        var (ephemeralSecret, ephemeralPublicKey) = Bip340.GenerateKeyPair();

        var template = new NostrEventTemplate(
            Convert.ToHexString(ephemeralPublicKey).ToLowerInvariant(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            GroupMessageEvent.Kind,
            tags,
            content);

        byte[] id = template.ComputeId();
        byte[] signature = Bip340.Sign(ephemeralSecret, id);

        return NostrEnvelope.Write(template, id, signature);
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
        // Keyed on a hash of (key, nonce) rather than the key's own hex. The
        // hex form put the group's exporter secret into an immutable string
        // that cannot be zeroed and outlived the epoch it belonged to.
        var input = new byte[key.Length + nonce.Length];
        key.CopyTo(input);
        nonce.CopyTo(input.AsSpan(key.Length));
        byte[] digest = SHA256.HashData(input);
        CryptographicOperations.ZeroMemory(input);

        lock (_nonceLock)
        {
            if (!_usedNonces.Add(Convert.ToHexString(digest)))
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



}
