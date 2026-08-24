using System.Security.Cryptography;
using System.Text.Json;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Wire.Nostr;

/// <summary>
/// A validated kind-30443 KeyPackage publication.
/// </summary>
/// <param name="SlotId">
/// The <c>d</c> tag: the publication slot this KeyPackage occupies. Kind 30443
/// is addressable, so a slot is replaced in place by (author, kind, d).
/// </param>
/// <param name="KeyPackageRefHex">
/// The <c>i</c> tag. A <i>hint</i>: the spec requires it to be checked against
/// the KeyPackageRef computed over the decoded KeyPackage, which this layer
/// cannot do because it does not decode MLS.
/// </param>
/// <param name="KeyPackageBytes">
/// The serialized <c>MLSMessage</c> whose wire format is <c>mls_key_package</c>
/// — not a bare <c>KeyPackage</c> struct.
/// </param>
/// <param name="AuthorPublicKeyHex">
/// The verified event author. This is a <i>claim</i> to be the account that
/// owns the KeyPackage; binding it to the decoded credential identity is the
/// caller's job, for the same reason as <paramref name="KeyPackageRefHex"/>.
/// </param>
/// <param name="EventIdHex">The verified NIP-01 event id, which a kind-444 <c>e</c> tag names.</param>
/// <param name="CreatedAt">The publication timestamp, used to rank candidates.</param>
public sealed record KeyPackagePublication(
    string SlotId,
    string KeyPackageRefHex,
    IReadOnlyList<ushort> CipherSuites,
    IReadOnlyList<ushort> MlsExtensions,
    IReadOnlyList<ushort> MlsProposals,
    IReadOnlyList<ushort> AppComponents,
    byte[] KeyPackageBytes,
    string AuthorPublicKeyHex,
    string EventIdHex,
    long CreatedAt);

/// <summary>
/// The kind-30443 KeyPackage event: build, parse, and the exact tag shape.
/// </summary>
/// <remarks>
/// <para>
/// Two things differ sharply from the kind-445 group message and are easy to
/// get backwards. First, this event is signed by the <b>account identity</b>,
/// not by a fresh ephemeral key — publishing a KeyPackage is precisely the act
/// of saying "this account owns this leaf", so an ephemeral author would make
/// the event meaningless. Second, its tag set is <b>not closed</b>: kind 445
/// spells out that no tag beyond <c>h</c> and <c>expiration</c> may appear,
/// while the KeyPackage rules constrain the seven tags below and say nothing
/// about others. Rejecting an unknown tag here would invent a rule and break
/// against a future peer, so unknown tags are carried past.
/// </para>
/// <para>
/// The one unknown tag with a rule of its own is <c>encoding</c>: a sender MUST
/// NOT emit it and a receiver MUST NOT switch decoders on it. Both hold here —
/// it is never built, and inbound it is just another unknown tag, because every
/// field is decoded by the rule that defines it. The previous implementation
/// emitted <c>["encoding", "base64"]</c> on 30443/444/445; against a current
/// peer that is rejected at the envelope.
/// </para>
/// <para>
/// This type does not decode MLS, so the two checks that need the decoded
/// KeyPackage — KeyPackageRef equality and author-to-credential binding — are
/// surfaced on <see cref="KeyPackagePublication"/> rather than performed here.
/// They are mandatory; they belong to the engine.
/// </para>
/// </remarks>
public static class KeyPackageEvent
{
    public const int Kind = 30443;

    public const string SlotTag = "d";
    public const string ProtocolVersionTag = "mls_protocol_version";
    public const string KeyPackageRefTag = "i";
    public const string CipherSuiteTag = "mls_ciphersuite";
    public const string ExtensionsTag = "mls_extensions";
    public const string ProposalsTag = "mls_proposals";
    public const string AppComponentsTag = "app_components";

    /// <summary>The only MLS version this binding defines.</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>Length of the publication-slot id in bytes.</summary>
    public const int SlotIdLength = 32;

    /// <summary>
    /// The account-identity-proof component every Marmot KeyPackage must advertise.
    /// </summary>
    /// <remarks>
    /// Duplicated from <c>AccountIdentityProof.ComponentId</c> rather than
    /// referenced: the wire layer must not take a dependency on the identity
    /// layer merely to know which id belongs on a tag.
    /// </remarks>
    public const ushort AccountIdentityProofComponentId = 0x8009;

    /// <summary>
    /// Upper bound on a KeyPackageRef, in bytes.
    /// </summary>
    /// <remarks>
    /// The three MLS-registered hash sizes are 32, 48 and 64, so this bounds
    /// allocation without pinning one ciphersuite and rejecting valid peers on
    /// the others.
    /// </remarks>
    public const int MaxKeyPackageRefLength = 64;

    /// <summary>Upper bound on the values in one id-list tag.</summary>
    public const int MaxIdListValues = 64;

    /// <summary>
    /// Generates a new publication-slot id.
    /// </summary>
    /// <remarks>
    /// Call this <b>once per slot</b> and persist the result. Every later
    /// KeyPackage for that slot reuses it, which is what makes a relay replace
    /// the old publication rather than accumulate a new one beside it. The
    /// value must be random: deriving it from the account key, the leaf key,
    /// the KeyPackageRef or a device label is forbidden, because the slot id is
    /// public and would then leak or link identity material.
    /// </remarks>
    public static string NewSlotId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(SlotIdLength)).ToLowerInvariant();

    /// <summary>
    /// Builds the tag set for a kind-30443 event.
    /// </summary>
    /// <param name="appComponents">
    /// Supported Marmot component ids. Must contain
    /// <see cref="AccountIdentityProofComponentId"/>.
    /// </param>
    public static IReadOnlyList<IReadOnlyList<string>> BuildTags(
        string slotId,
        string keyPackageRefHex,
        IReadOnlyList<ushort> cipherSuites,
        IReadOnlyList<ushort> mlsExtensions,
        IReadOnlyList<ushort> mlsProposals,
        IReadOnlyList<ushort> appComponents)
    {
        ArgumentNullException.ThrowIfNull(slotId);
        ArgumentNullException.ThrowIfNull(keyPackageRefHex);
        ArgumentNullException.ThrowIfNull(appComponents);

        if (slotId.Length != SlotIdLength * 2 || !IsLowercaseHex(slotId))
            throw new ArgumentException(
                $"The slot id must be {SlotIdLength} bytes of lowercase hex.", nameof(slotId));

        if (keyPackageRefHex.Length == 0
            || keyPackageRefHex.Length % 2 != 0
            || keyPackageRefHex.Length > MaxKeyPackageRefLength * 2
            || !IsLowercaseHex(keyPackageRefHex))
        {
            throw new ArgumentException(
                "The KeyPackageRef must be non-empty lowercase hex.", nameof(keyPackageRefHex));
        }

        if (!appComponents.Contains(AccountIdentityProofComponentId))
        {
            // The account-identity proof is what makes a KeyPackage a Marmot
            // KeyPackage. Advertising it is a producer MUST, and a package
            // without it is malformed rather than merely unsupported.
            throw new ArgumentException(
                $"A KeyPackage must advertise app component {FormatId(AccountIdentityProofComponentId)}.",
                nameof(appComponents));
        }

        return new IReadOnlyList<string>[]
        {
            new[] { SlotTag, slotId },
            new[] { ProtocolVersionTag, ProtocolVersion },
            new[] { KeyPackageRefTag, keyPackageRefHex },
            IdListTag(CipherSuiteTag, cipherSuites, nameof(cipherSuites)),
            IdListTag(ExtensionsTag, mlsExtensions, nameof(mlsExtensions)),
            IdListTag(ProposalsTag, mlsProposals, nameof(mlsProposals)),
            IdListTag(AppComponentsTag, appComponents, nameof(appComponents)),
        };
    }

    /// <summary>
    /// Builds the unsigned kind-30443 event for the account to sign.
    /// </summary>
    /// <remarks>
    /// Returned unsigned, unlike <see cref="ITransportPeeler.WrapGroupMessage"/>
    /// which signs a group message itself. The difference is not stylistic: a
    /// group message must be signed by a fresh key this layer generates, while
    /// this one must be signed by the account identity — which in Scramble's
    /// common case lives in Amber or behind NIP-46, is reached asynchronously,
    /// and needs a human to approve.
    /// </remarks>
    /// <param name="keyPackageBytes">
    /// The serialized <c>MLSMessage</c> wrapping the <b>public</b> KeyPackage.
    /// Private <c>init_key</c> material is never published — it is retained
    /// locally, because the Welcome that consumes this KeyPackage needs it.
    /// </param>
    public static NostrEventTemplate BuildTemplate(
        string accountPublicKeyHex,
        ReadOnlySpan<byte> keyPackageBytes,
        string slotId,
        string keyPackageRefHex,
        IReadOnlyList<ushort> cipherSuites,
        IReadOnlyList<ushort> mlsExtensions,
        IReadOnlyList<ushort> mlsProposals,
        IReadOnlyList<ushort> appComponents,
        long? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(accountPublicKeyHex);

        if (accountPublicKeyHex.Length != 64 || !IsLowercaseHex(accountPublicKeyHex))
        {
            throw new ArgumentException(
                "The account public key must be 32 bytes of lowercase hex.",
                nameof(accountPublicKeyHex));
        }

        if (keyPackageBytes.IsEmpty)
        {
            throw new ArgumentException(
                "The KeyPackage bytes must not be empty.", nameof(keyPackageBytes));
        }

        var tags = BuildTags(
            slotId, keyPackageRefHex, cipherSuites, mlsExtensions, mlsProposals, appComponents);

        return new NostrEventTemplate(
            accountPublicKeyHex,
            createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind,
            tags,
            Convert.ToBase64String(keyPackageBytes));
    }

    /// <summary>
    /// Verifies and parses an inbound kind-30443 event.
    /// </summary>
    /// <remarks>
    /// The id and signature are checked before any field is read, so nothing
    /// below treats attacker-chosen bytes as authenticated. Failures are
    /// terminal: a malformed publication does not become well-formed later, and
    /// a fetch can simply move to the next candidate.
    /// </remarks>
    /// <exception cref="PeelFailedException">The event is not a conformant publication.</exception>
    public static KeyPackagePublication Parse(string envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(envelope);
        }
        catch (JsonException ex)
        {
            throw new PeelFailedException($"The KeyPackage event is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var signed = SignedNostrEvent.Parse(document.RootElement);

            if (signed.Kind != Kind)
            {
                throw new PeelFailedException(
                    $"Expected a kind-{Kind} KeyPackage event, got kind {signed.Kind}.");
            }

            byte[] eventId = signed.VerifyAndComputeId();

            return Read(
                signed.Tags,
                signed.Content,
                signed.PublicKeyHex.ToLowerInvariant(),
                Convert.ToHexString(eventId).ToLowerInvariant(),
                signed.CreatedAt);
        }
    }

    /// <summary>
    /// Validates the tag shape and content of an already-verified event.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Parse"/> so a caller holding an event verified
    /// elsewhere does not verify it twice — while keeping that shortcut out of
    /// reach of anyone holding only an envelope.
    /// </remarks>
    /// <exception cref="PeelFailedException">The event is not a conformant publication.</exception>
    public static KeyPackagePublication Read(
        IReadOnlyList<IReadOnlyList<string>> tags,
        string content,
        string authorPublicKeyHex,
        string eventIdHex,
        long createdAt)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(authorPublicKeyHex);
        ArgumentNullException.ThrowIfNull(eventIdHex);

        string slotId = SingleValue(tags, SlotTag);
        if (slotId.Length != SlotIdLength * 2 || !IsLowercaseHex(slotId))
            throw Malformed($"the {SlotTag} tag must be {SlotIdLength} bytes of lowercase hex");

        string version = SingleValue(tags, ProtocolVersionTag);
        if (version != ProtocolVersion)
            throw Malformed($"the {ProtocolVersionTag} tag must be '{ProtocolVersion}', found '{version}'");

        string keyPackageRefHex = SingleValue(tags, KeyPackageRefTag);
        if (keyPackageRefHex.Length == 0
            || keyPackageRefHex.Length % 2 != 0
            || keyPackageRefHex.Length > MaxKeyPackageRefLength * 2
            || !IsLowercaseHex(keyPackageRefHex))
        {
            // Deliberately not pinned to 32 bytes: the KeyPackageRef is the
            // ciphersuite's hash, so 48 and 64 are equally valid, and the value
            // is a hint the caller must re-derive from the decoded KeyPackage
            // regardless. Strictness here would only reject valid peers.
            throw Malformed(
                $"the {KeyPackageRefTag} tag must be non-empty lowercase hex of at most {MaxKeyPackageRefLength} bytes");
        }

        var cipherSuites = IdList(tags, CipherSuiteTag);
        var mlsExtensions = IdList(tags, ExtensionsTag);
        var mlsProposals = IdList(tags, ProposalsTag);
        var appComponents = IdList(tags, AppComponentsTag);

        if (!appComponents.Contains(AccountIdentityProofComponentId))
        {
            throw Malformed(
                $"the {AppComponentsTag} tag must advertise {FormatId(AccountIdentityProofComponentId)}");
        }

        byte[] keyPackageBytes;
        try
        {
            keyPackageBytes = Convert.FromBase64String(content);
        }
        catch (FormatException ex)
        {
            throw Malformed($"the content is not base64: {ex.Message}");
        }

        if (keyPackageBytes.Length == 0)
            throw Malformed("the content decoded to no KeyPackage bytes");

        return new KeyPackagePublication(
            slotId,
            keyPackageRefHex,
            cipherSuites,
            mlsExtensions,
            mlsProposals,
            appComponents,
            keyPackageBytes,
            authorPublicKeyHex,
            eventIdHex,
            createdAt);
    }

    /// <summary>
    /// Orders two publications from one account, best candidate first.
    /// </summary>
    /// <remarks>
    /// The transport-level ranking only, applied after the foundation ranking
    /// the engine owns: newest <c>created_at</c> wins; equal timestamps fall
    /// back to the lower event id within one slot, and to the lower decoded
    /// KeyPackageRef across slots. Both tie-breaks exist so two clients reading
    /// the same relay pick the same KeyPackage — a coin flip here means two
    /// inviters consume two different single-use packages.
    /// </remarks>
    public static int CompareCandidates(KeyPackagePublication left, KeyPackagePublication right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        int byRecency = right.CreatedAt.CompareTo(left.CreatedAt);
        if (byRecency != 0)
            return byRecency;

        if (string.Equals(left.SlotId, right.SlotId, StringComparison.Ordinal))
            return string.CompareOrdinal(left.EventIdHex, right.EventIdHex);

        // Compared as decoded bytes rather than as hex text. Same ordering for
        // refs of equal length, but not once the lengths differ, and the spec
        // says the tag is hex-decoded before comparison.
        return CompareHexAsBytes(left.KeyPackageRefHex, right.KeyPackageRefHex);
    }

    /// <summary>
    /// Formats a 16-bit id the way an id-list tag carries it: <c>0x</c> plus
    /// four lowercase hex digits, always zero-padded.
    /// </summary>
    /// <remarks>
    /// Consumers compare these values as exact strings, so <c>0x1</c> and
    /// <c>0X0001</c> are simply different values, not lenient spellings of the
    /// same one.
    /// </remarks>
    public static string FormatId(ushort id) => $"0x{id:x4}";

    private static int CompareHexAsBytes(string left, string right)
    {
        byte[] leftBytes = Convert.FromHexString(left);
        byte[] rightBytes = Convert.FromHexString(right);
        return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
    }

    private static IReadOnlyList<string> IdListTag(
        string name, IReadOnlyList<ushort> ids, string parameterName)
    {
        if (ids is null)
            throw new ArgumentNullException(parameterName);

        if (ids.Count == 0)
            throw new ArgumentException($"The {name} tag must carry at least one id.", parameterName);

        if (ids.Count > MaxIdListValues)
        {
            throw new ArgumentException(
                $"The {name} tag carries {ids.Count} ids; the limit is {MaxIdListValues}.",
                parameterName);
        }

        var tag = new List<string>(ids.Count + 1) { name };
        var seen = new HashSet<ushort>();
        foreach (ushort id in ids)
        {
            if (!seen.Add(id))
                throw new ArgumentException($"The {name} tag repeats id {FormatId(id)}.", parameterName);

            tag.Add(FormatId(id));
        }

        return tag;
    }

    private static IReadOnlyList<ushort> IdList(
        IReadOnlyList<IReadOnlyList<string>> tags, string name)
    {
        var values = SingleTag(tags, name);

        if (values.Count == 0)
            throw Malformed($"the {name} tag must carry at least one id");

        if (values.Count > MaxIdListValues)
            throw Malformed($"the {name} tag carries {values.Count} ids; the limit is {MaxIdListValues}");

        var ids = new List<ushort>(values.Count);
        var seen = new HashSet<ushort>();
        foreach (string value in values)
        {
            ushort id = ParseId(name, value);
            if (!seen.Add(id))
                throw Malformed($"the {name} tag repeats id {value}");

            ids.Add(id);
        }

        return ids;
    }

    private static ushort ParseId(string name, string value)
    {
        if (value.Length != 6
            || value[0] != '0'
            || value[1] != 'x'
            || !IsLowercaseHex(value.AsSpan(2)))
        {
            throw Malformed(
                $"the {name} tag's ids must be '0x' followed by four lowercase hex digits, found '{value}'");
        }

        return ushort.Parse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber);
    }

    /// <summary>
    /// The single value of a required singleton tag.
    /// </summary>
    /// <remarks>
    /// A repeated tag, or an extra value on it, makes the event malformed. It
    /// is rejected rather than resolved by taking the first, which is spelled
    /// out as a MUST NOT precisely because reading the first occurrence lets an
    /// attacker prepend a value that some other implementation ignores.
    /// </remarks>
    private static string SingleValue(IReadOnlyList<IReadOnlyList<string>> tags, string name)
    {
        var values = SingleTag(tags, name);
        return values.Count == 1
            ? values[0]
            : throw Malformed($"the {name} tag must carry exactly one value");
    }

    private static IReadOnlyList<string> SingleTag(
        IReadOnlyList<IReadOnlyList<string>> tags, string name)
    {
        IReadOnlyList<string>? found = null;
        foreach (var tag in tags)
        {
            if (tag is null || tag.Count == 0 || tag[0] != name)
                continue;

            if (found is not null)
                throw Malformed($"a KeyPackage event must carry exactly one {name} tag");

            found = tag;
        }

        if (found is null)
            throw Malformed($"a KeyPackage event must carry a {name} tag");

        return found.Skip(1).ToArray();
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static PeelFailedException Malformed(string reason) =>
        new($"Malformed kind-{Kind} KeyPackage event: {reason}.");
}
