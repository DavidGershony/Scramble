using System.Text;

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
    /// <para>
    /// Written by hand rather than with <c>Utf8JsonWriter</c>. Every available
    /// .NET encoder — including <c>UnsafeRelaxedJsonEscaping</c> — escapes code
    /// points above U+FFFF as surrogate pairs (<c>😀</c>), because
    /// <c>UnicodeRanges.All</c> means the BMP only. NIP-01 requires non-ASCII to
    /// be emitted verbatim, so any event containing an emoji would otherwise get
    /// an id no other implementation agrees with, and therefore a signature
    /// every peer rejects.
    /// </para>
    /// <para>
    /// Escaped: <c>"</c>, <c>\</c>, and the named controls (\n \r \t \b \f).
    /// Remaining characters below U+0020 use <c>\u00XX</c>. Everything else,
    /// including U+007F and U+2028/U+2029, is emitted as-is.
    /// </para>
    /// </remarks>
    public string Serialize()
    {
        var builder = new StringBuilder();
        builder.Append("[0,");
        AppendString(builder, PublicKeyHex);
        builder.Append(',').Append(CreatedAt);
        builder.Append(',').Append(Kind);

        builder.Append(",[");
        for (int i = 0; i < Tags.Count; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append('[');
            var tag = Tags[i];
            for (int j = 0; j < tag.Count; j++)
            {
                if (j > 0)
                    builder.Append(',');
                AppendString(builder, tag[j]);
            }

            builder.Append(']');
        }

        builder.Append("],");
        AppendString(builder, Content);
        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>The 32-byte event id: SHA-256 over <see cref="Serialize"/>.</summary>
    public byte[] ComputeId() =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Serialize()));

    private static void AppendString(StringBuilder builder, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        builder.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // A well-formed pair is emitted verbatim, so the UTF-8
                        // encoding carries the real code point. A lone surrogate
                        // cannot be encoded and must not be silently replaced
                        // with U+FFFD: that would corrupt the bytes being signed.
                        if (i + 1 < value.Length && char.IsSurrogatePair(c, value[i + 1]))
                        {
                            builder.Append(c).Append(value[i + 1]);
                            i++;
                        }
                        else
                        {
                            throw new ArgumentException(
                                "The value contains an unpaired surrogate and cannot be canonically encoded.",
                                nameof(value));
                        }
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
