namespace Scramble.Marmot.AppComponents;

/// <summary>
/// The MLS <c>app_data_dictionary</c> extension (extension type <c>0x0006</c>):
/// component id to component bytes.
/// </summary>
/// <remarks>
/// <para>
/// Where Dark Matter keeps group state. A GroupContext dictionary holds the
/// group's components; a LeafNode dictionary holds per-member ones, notably the
/// account-identity proof. Entries are ordered by id and there is at most one
/// per id — both checked on construction and on decode, because this is a
/// canonical MLS structure whose bytes are covered by the group's signatures.
/// </para>
/// <para>
/// <b>Length encoding differs from the component payloads inside it.</b> MLS
/// variable-length vectors (RFC 9420 §2.1.2) use 1, 2 or 4 bytes and treat the
/// 8-byte form as invalid, capping a length at 2^30-1. The QUIC varint the
/// component schemas use for their own internal fields allows all four widths.
/// The two coincide for every realistic size, which is exactly why the
/// difference is easy to miss — so the dictionary encodes through
/// <see cref="WriteMlsLength"/>, which refuses to emit a length MLS cannot
/// carry, rather than through the general varint.
/// </para>
/// </remarks>
public sealed class AppDataDictionary
{
    /// <summary>The MLS extension type this dictionary is carried in.</summary>
    public const ushort ExtensionType = 0x0006;

    /// <summary>Largest length an MLS variable-length vector can express.</summary>
    public const uint MaxMlsLength = (1u << 30) - 1;

    private readonly SortedDictionary<ushort, byte[]> _entries;

    /// <summary>An empty dictionary.</summary>
    public AppDataDictionary() => _entries = new SortedDictionary<ushort, byte[]>();

    private AppDataDictionary(SortedDictionary<ushort, byte[]> entries) => _entries = entries;

    /// <summary>The component ids present, ascending.</summary>
    public IReadOnlyCollection<ushort> ComponentIds => _entries.Keys;

    /// <summary>How many entries the dictionary holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Whether an entry exists for <paramref name="componentId"/>.</summary>
    public bool Contains(ushort componentId) => _entries.ContainsKey(componentId);

    /// <summary>The bytes stored for <paramref name="componentId"/>, or null.</summary>
    public byte[]? Get(ushort componentId) =>
        _entries.TryGetValue(componentId, out byte[]? data) ? data : null;

    /// <summary>Inserts or replaces an entry.</summary>
    public void Set(ushort componentId, ReadOnlySpan<byte> data) =>
        _entries[componentId] = data.ToArray();

    /// <summary>Removes an entry.</summary>
    /// <returns>Whether one was there.</returns>
    /// <remarks>
    /// Whether a removal is <i>permitted</i> is not decided here — the
    /// admin-policy component may never be removed, and a required component's
    /// state may not disappear without the same commit unrequiring it. Those
    /// are commit-level rules; this is the container.
    /// </remarks>
    public bool Remove(ushort componentId) => _entries.Remove(componentId);

    /// <summary>
    /// The required-component list from the <c>app_components</c> entry
    /// (<c>0x0001</c>), or null when the entry is absent.
    /// </summary>
    /// <remarks>
    /// In a GroupContext dictionary this entry lists the components the group
    /// <i>requires</i>; in a LeafNode dictionary the same entry lists what that
    /// member <i>supports</i>. Same encoding, opposite meaning, decided by
    /// where the dictionary lives — which is why this returns the raw set and
    /// leaves the interpretation to the caller that knows.
    /// </remarks>
    public IReadOnlySet<ushort>? ComponentList()
    {
        byte[]? bytes = Get(AppComponent.AppComponents);
        return bytes is null ? null : ComponentCodec.DecodeComponentsList(bytes);
    }

    /// <summary>Writes the <c>app_components</c> entry.</summary>
    public void SetComponentList(IReadOnlySet<ushort> ids) =>
        Set(AppComponent.AppComponents, ComponentCodec.EncodeComponentsList(ids));

    /// <summary>
    /// Encodes the dictionary: an MLS vector length, then each entry as a
    /// big-endian <c>uint16</c> id followed by its length-prefixed bytes.
    /// </summary>
    public byte[] Encode()
    {
        var entries = new List<byte>();
        foreach ((ushort id, byte[] data) in _entries)
        {
            entries.Add((byte)(id >> 8));
            entries.Add((byte)id);
            WriteMlsLength((uint)data.Length, entries);
            entries.AddRange(data);
        }

        var output = new List<byte>(entries.Count + 4);
        WriteMlsLength((uint)entries.Count, output);
        output.AddRange(entries);
        return output.ToArray();
    }

    /// <summary>
    /// Decodes a dictionary.
    /// </summary>
    /// <remarks>
    /// Out-of-order or duplicate entries are rejected rather than reordered or
    /// collapsed. A dictionary is inside signed group state, so a member that
    /// tidied one up would be holding bytes nobody else has.
    /// </remarks>
    /// <exception cref="AppComponentException">The bytes are not a valid dictionary.</exception>
    public static AppDataDictionary Decode(ReadOnlySpan<byte> bytes)
    {
        (ulong totalLength, int prefixLength) = ComponentCodec.ReadVarint(bytes);

        if (totalLength > int.MaxValue)
            throw new AppComponentException("The app-data dictionary length is too large.");

        int end = prefixLength + (int)totalLength;
        if (end > bytes.Length)
            throw new AppComponentException("The app-data dictionary is truncated.");
        if (end != bytes.Length)
            throw new AppComponentException("The app-data dictionary has trailing bytes.");

        var entries = new SortedDictionary<ushort, byte[]>();
        var cursor = bytes[prefixLength..end];
        int? previous = null;

        while (!cursor.IsEmpty)
        {
            if (cursor.Length < 2)
                throw new AppComponentException("An app-data dictionary entry is truncated.");

            ushort id = (ushort)((cursor[0] << 8) | cursor[1]);
            cursor = cursor[2..];

            byte[] data = ComponentCodec.ReadVarBytes(
                ref cursor, (int)MaxMlsLength, $"app-data dictionary entry 0x{id:x4}");

            if (previous is { } p)
            {
                if (id == p)
                    throw new AppComponentException(
                        $"The app-data dictionary has more than one entry for 0x{id:x4}.");
                if (id < p)
                    throw new AppComponentException(
                        "The app-data dictionary entries are not ordered by component id.");
            }

            entries[id] = data;
            previous = id;
        }

        return new AppDataDictionary(entries);
    }

    /// <summary>
    /// Appends an MLS variable-length vector length.
    /// </summary>
    /// <remarks>
    /// Refuses the 8-byte form. RFC 9420 §2.1.2 defines only the 1, 2 and
    /// 4-byte encodings and treats a <c>0b11</c> prefix as invalid, so a length
    /// beyond 2^30-1 cannot be expressed at all — emitting one would produce
    /// bytes no MLS implementation will parse.
    /// </remarks>
    public static void WriteMlsLength(uint value, List<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (value > MaxMlsLength)
        {
            throw new AppComponentException(
                $"An MLS vector length is at most {MaxMlsLength}; {value} cannot be encoded.");
        }

        ComponentCodec.WriteVarint(value, output);
    }
}
