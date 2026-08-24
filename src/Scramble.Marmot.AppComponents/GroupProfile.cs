namespace Scramble.Marmot.AppComponents;

/// <summary>
/// <c>marmot.group.profile.v1</c> (<c>0x8001</c>) — the group's name and description.
/// </summary>
/// <remarks>
/// Two length-prefixed UTF-8 strings, in that order. Both may be empty: an
/// empty description is a real value, distinct from an absent field, and it
/// round-trips as one.
/// </remarks>
/// <param name="Name">Display name, at most 256 bytes encoded.</param>
/// <param name="Description">Description, at most 4096 bytes encoded.</param>
public sealed record GroupProfile(string Name, string Description)
{
    /// <summary>Maximum encoded name length, in bytes.</summary>
    public const int MaxNameLength = 256;

    /// <summary>Maximum encoded description length, in bytes.</summary>
    public const int MaxDescriptionLength = 4096;

    /// <summary>Encodes the component.</summary>
    /// <exception cref="AppComponentException">A field is over its bound.</exception>
    public byte[] Encode()
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(Name);
        byte[] description = System.Text.Encoding.UTF8.GetBytes(Description);

        // Bounded in bytes, not characters. The limit is on what goes on the
        // wire, and one emoji is four bytes — a character-based check would let
        // an over-long profile through and be rejected by every peer.
        if (name.Length > MaxNameLength)
            throw new AppComponentException($"A group name is at most {MaxNameLength} bytes.");
        if (description.Length > MaxDescriptionLength)
            throw new AppComponentException(
                $"A group description is at most {MaxDescriptionLength} bytes.");

        return ComponentCodec.EncodeVectors(name, description);
    }

    /// <summary>Decodes and validates the component.</summary>
    /// <exception cref="AppComponentException">The bytes are not a valid profile.</exception>
    public static GroupProfile Decode(ReadOnlySpan<byte> bytes)
    {
        var cursor = bytes;
        byte[] name = ComponentCodec.ReadVarBytes(ref cursor, MaxNameLength, "group profile name");
        byte[] description = ComponentCodec.ReadVarBytes(
            ref cursor, MaxDescriptionLength, "group profile description");
        ComponentCodec.RequireSpent(cursor, "group profile");

        return new GroupProfile(Utf8(name, "name"), Utf8(description, "description"));
    }

    private static string Utf8(byte[] bytes, string field)
    {
        try
        {
            // Throwing rather than substituting U+FFFD: a replacement character
            // re-encodes to different bytes from the ones the group signed, so
            // this member's profile would stop matching everyone else's.
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException ex)
        {
            throw new AppComponentException($"The group profile {field} is not valid UTF-8: {ex.Message}");
        }
    }
}
