using DotnetMls.Codec;
using DotnetMls.Types;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// The MLS <c>required_capabilities</c> GroupContext extension, RFC 9420 §11.1.
/// </summary>
/// <remarks>
/// <para>
/// Three vectors of <c>uint16</c>, in this order:
/// </para>
/// <code>
/// struct {
///   ExtensionType  extension_types&lt;V&gt;;
///   ProposalType   proposal_types&lt;V&gt;;
///   CredentialType credential_types&lt;V&gt;;
/// } RequiredCapabilities;
/// </code>
/// <para>
/// Lives here rather than in <c>dotnet-mls</c> because the library references
/// the extension <i>type</i> when validating a GroupContext but never parses its
/// body — there is nothing to extend. It is generic RFC 9420 with no Marmot
/// semantics, so it is a fair candidate to upstream later; it is not done now
/// because a library change costs a permission and this needs none.
/// </para>
/// <para>
/// Producers canonicalise and decoders do not, the same rule the app components
/// follow: <see cref="Create"/> sorts and deduplicates because nothing is
/// committed yet, while <see cref="Decode"/> accepts whatever order it is given.
/// The asymmetry is deliberate and the reason differs from the app-component
/// case — RFC 9420 states no ordering requirement here, so rejecting an
/// unsorted list would invent a rule and refuse a conformant peer. What must
/// not happen is <i>repairing</i> it: this is signed group state, and a member
/// that quietly rewrites it holds a canonical form nobody else has.
/// </para>
/// </remarks>
/// <param name="ExtensionTypes">Extension types every member must support.</param>
/// <param name="ProposalTypes">Proposal types every member must support.</param>
/// <param name="CredentialTypes">
/// Credential types every member must support. Marmot leaves this empty:
/// the profile fixes BasicCredential, so requiring it would add a constraint
/// upstream does not emit and make our groups differ on the wire for no gain.
/// </param>
public sealed record RequiredCapabilities(
    IReadOnlyList<ushort> ExtensionTypes,
    IReadOnlyList<ushort> ProposalTypes,
    IReadOnlyList<ushort> CredentialTypes)
{
    /// <summary>The extension type, RFC 9420 §17.3.</summary>
    public const ushort ExtensionType = 0x0003;

    /// <summary>
    /// Upper bound on entries in one vector, to bound allocation on decode.
    /// </summary>
    /// <remarks>
    /// The length prefix is attacker-controlled. There are far fewer than this
    /// many registered types, so the bound cannot reject anything real.
    /// </remarks>
    public const int MaxEntries = 256;

    /// <summary>
    /// Builds a canonical instance: each vector sorted and deduplicated.
    /// </summary>
    public static RequiredCapabilities Create(
        IEnumerable<ushort> extensionTypes,
        IEnumerable<ushort> proposalTypes,
        IEnumerable<ushort>? credentialTypes = null)
    {
        ArgumentNullException.ThrowIfNull(extensionTypes);
        ArgumentNullException.ThrowIfNull(proposalTypes);

        return new RequiredCapabilities(
            Canonical(extensionTypes),
            Canonical(proposalTypes),
            Canonical(credentialTypes ?? []));
    }

    private static IReadOnlyList<ushort> Canonical(IEnumerable<ushort> values) =>
        new SortedSet<ushort>(values).ToArray();

    /// <summary>Encodes the extension body.</summary>
    public byte[] Encode() => TlsCodec.Serialize(writer =>
    {
        WriteVector(writer, ExtensionTypes);
        WriteVector(writer, ProposalTypes);
        WriteVector(writer, CredentialTypes);
    });

    /// <summary>Wraps the body as the MLS extension that carries it.</summary>
    public Extension ToExtension() => new(ExtensionType, Encode());

    /// <summary>
    /// Decodes the extension body.
    /// </summary>
    /// <exception cref="TlsDecodingException">
    /// Truncated, over-long, or with bytes left over.
    /// </exception>
    public static RequiredCapabilities Decode(ReadOnlySpan<byte> data)
    {
        var reader = new TlsReader(data.ToArray());

        var value = new RequiredCapabilities(
            ReadVector(reader, "extension_types"),
            ReadVector(reader, "proposal_types"),
            ReadVector(reader, "credential_types"));

        // Trailing bytes are a different structure, not padding. Accepting them
        // would let two peers read the same signed extension differently.
        if (!reader.IsEmpty)
            throw new TlsDecodingException("required_capabilities has trailing bytes.");

        return value;
    }

    /// <summary>
    /// Reads the extension out of a GroupContext extension list, or null.
    /// </summary>
    public static RequiredCapabilities? FromExtensions(IEnumerable<Extension> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        foreach (var extension in extensions)
        {
            if (extension.ExtensionType == ExtensionType)
                return Decode(extension.ExtensionData);
        }

        return null;
    }

    private static void WriteVector(TlsWriter writer, IReadOnlyList<ushort> values) =>
        writer.WriteVectorV(inner =>
        {
            foreach (ushort value in values)
                inner.WriteUint16(value);
        });

    private static IReadOnlyList<ushort> ReadVector(TlsReader reader, string field)
    {
        TlsReader entries = reader.ReadVectorV();
        var values = new List<ushort>();

        while (!entries.IsEmpty)
        {
            if (values.Count == MaxEntries)
                throw new TlsDecodingException($"required_capabilities {field} has too many entries.");

            values.Add(entries.ReadUint16());
        }

        return values;
    }
}
