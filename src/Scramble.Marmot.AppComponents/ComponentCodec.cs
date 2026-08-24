namespace Scramble.Marmot.AppComponents;

/// <summary>
/// The QUIC-varint and var-bytes primitives every component schema is built from.
/// </summary>
/// <remarks>
/// <para>
/// These bytes are inside signed MLS group state, so encoding is not a private
/// choice: two members that encode the same value differently hold different
/// GroupContexts and the group forks. Everything here is therefore
/// <b>canonical</b> — one value has exactly one encoding, and a non-minimal
/// encoding is rejected on the way in rather than accepted and re-emitted
/// minimally.
/// </para>
/// <para>
/// The varint is QUIC's (RFC 9000 §16): the top two bits of the first byte give
/// the length as <c>1 &lt;&lt; (b0 &gt;&gt; 6)</c>, and the remaining 62 bits are the
/// value, big-endian.
/// </para>
/// </remarks>
public static class ComponentCodec
{
    /// <summary>The largest value a QUIC varint can carry.</summary>
    public const ulong MaxVarint = (1UL << 62) - 1;

    /// <summary>Appends <paramref name="value"/> in its minimal QUIC varint form.</summary>
    public static void WriteVarint(ulong value, List<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (value > MaxVarint)
            throw new ArgumentOutOfRangeException(nameof(value), "A QUIC varint holds at most 62 bits.");

        if (value < 64)
        {
            output.Add((byte)value);
        }
        else if (value < 16_384)
        {
            ushort encoded = (ushort)(0x4000 | value);
            output.Add((byte)(encoded >> 8));
            output.Add((byte)encoded);
        }
        else if (value < 1_073_741_824)
        {
            uint encoded = 0x8000_0000 | (uint)value;
            for (int shift = 24; shift >= 0; shift -= 8)
                output.Add((byte)(encoded >> shift));
        }
        else
        {
            ulong encoded = 0xC000_0000_0000_0000 | value;
            for (int shift = 56; shift >= 0; shift -= 8)
                output.Add((byte)(encoded >> shift));
        }
    }

    /// <summary>
    /// Reads a QUIC varint from the front of <paramref name="bytes"/>.
    /// </summary>
    /// <returns>The value and the number of bytes it occupied.</returns>
    /// <exception cref="AppComponentException">
    /// The varint is missing, truncated, or not minimally encoded.
    /// </exception>
    public static (ulong Value, int Length) ReadVarint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            throw new AppComponentException("Missing QUIC varint.");

        int width = 1 << (bytes[0] >> 6);
        if (bytes.Length < width)
            throw new AppComponentException("Truncated QUIC varint.");

        ulong value = (ulong)(bytes[0] & 0x3f);
        for (int i = 1; i < width; i++)
            value = (value << 8) | bytes[i];

        int minimalWidth = value switch
        {
            < 64 => 1,
            < 16_384 => 2,
            < 1_073_741_824 => 4,
            _ => 8,
        };

        if (width != minimalWidth)
        {
            // Rejected rather than accepted-and-normalised. A padded varint is
            // a second encoding of the same value, and two encodings of one
            // group state is exactly what canonical form exists to prevent.
            throw new AppComponentException("Non-canonical QUIC varint length.");
        }

        return (value, width);
    }

    /// <summary>Appends <paramref name="bytes"/> behind a varint length prefix.</summary>
    public static void WriteVarBytes(ReadOnlySpan<byte> bytes, List<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        WriteVarint((ulong)bytes.Length, output);
        output.AddRange(bytes.ToArray());
    }

    /// <summary>
    /// Reads a length-prefixed byte string, advancing <paramref name="cursor"/>
    /// past it.
    /// </summary>
    /// <param name="maxLength">
    /// Schema bound, checked before anything is allocated — the length prefix
    /// is attacker-controlled, so an unbounded read is a memory amplifier.
    /// </param>
    /// <param name="label">Field name, for the error message.</param>
    /// <exception cref="AppComponentException">Missing, truncated, or over the bound.</exception>
    public static byte[] ReadVarBytes(ref ReadOnlySpan<byte> cursor, int maxLength, string label)
    {
        (ulong length, int prefixLength) = ReadVarint(cursor);

        if (length > (ulong)maxLength)
            throw new AppComponentException($"The {label} exceeds its maximum length.");

        int end = prefixLength + (int)length;
        if (cursor.Length < end)
            throw new AppComponentException($"The {label} is truncated.");

        byte[] value = cursor[prefixLength..end].ToArray();
        cursor = cursor[end..];
        return value;
    }

    /// <summary>
    /// Encodes a sequence of fields as consecutive length-prefixed byte strings.
    /// </summary>
    /// <remarks>
    /// The shape most component schemas use for their fixed field list. There
    /// is no count prefix: the field count comes from the schema, so a decoder
    /// reads exactly what it expects and then requires the input to be spent.
    /// </remarks>
    public static byte[] EncodeVectors(params ReadOnlyMemory<byte>[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var output = new List<byte>();
        foreach (var part in parts)
            WriteVarBytes(part.Span, output);

        return output.ToArray();
    }

    /// <summary>
    /// Encodes the MLS-extensions-draft <c>ComponentsList</c>: a varint byte
    /// length followed by big-endian <c>uint16</c> ids.
    /// </summary>
    /// <remarks>
    /// Takes a sorted set because the encoding is canonical — the ids go out
    /// ascending, and a decoder rejects any other order.
    /// </remarks>
    public static byte[] EncodeComponentsList(IReadOnlySet<ushort> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var output = new List<byte>();
        WriteVarint((ulong)(ids.Count * 2), output);
        foreach (ushort id in ids.OrderBy(id => id))
        {
            output.Add((byte)(id >> 8));
            output.Add((byte)id);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decodes a <c>ComponentsList</c>.
    /// </summary>
    /// <remarks>
    /// Duplicates, wrong order, an odd byte length and trailing bytes are all
    /// rejected. Order matters because this list is compared for equality
    /// across members; accepting an unsorted list would let one encoding of a
    /// set look different from another.
    /// </remarks>
    /// <exception cref="AppComponentException">The list is not canonical.</exception>
    public static IReadOnlySet<ushort> DecodeComponentsList(ReadOnlySpan<byte> bytes)
    {
        (ulong length, int prefixLength) = ReadVarint(bytes);

        if (length > int.MaxValue)
            throw new AppComponentException("The component list length is too large.");

        int end = prefixLength + (int)length;
        if (end > bytes.Length)
            throw new AppComponentException("The component list is truncated.");
        if (end != bytes.Length)
            throw new AppComponentException("The component list has trailing bytes.");
        if (length % 2 != 0)
            throw new AppComponentException("The component list byte length must be even.");

        var ids = new SortedSet<ushort>();
        int? previous = null;
        for (int i = prefixLength; i < end; i += 2)
        {
            ushort id = (ushort)((bytes[i] << 8) | bytes[i + 1]);

            if (!ids.Add(id))
                throw new AppComponentException("The component list contains duplicate ids.");
            if (previous is { } p && id < p)
                throw new AppComponentException("The component list is not sorted.");

            previous = id;
        }

        return ids;
    }

    /// <summary>Requires a decoder to have consumed all of its input.</summary>
    /// <remarks>
    /// Trailing bytes are rejected everywhere rather than ignored. Ignoring
    /// them lets one member read a field a stricter member does not, which
    /// makes the same signed state mean two things.
    /// </remarks>
    public static void RequireSpent(ReadOnlySpan<byte> cursor, string component)
    {
        if (!cursor.IsEmpty)
            throw new AppComponentException($"The {component} component has trailing bytes.");
    }
}
