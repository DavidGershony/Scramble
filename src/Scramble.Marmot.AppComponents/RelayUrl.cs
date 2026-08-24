namespace Scramble.Marmot.AppComponents;

/// <summary>
/// The Marmot relay-URL profile, shared by every field that carries one.
/// </summary>
/// <remarks>
/// <para>
/// One profile, applied identically to the kind-444 Welcome's <c>relays</c> tag
/// and to the routing component's signed relay list. That matters more than it
/// looks: the two lists describe the same relays, and a URL that one accepts
/// and the other rejects makes a group reachable through the Welcome but not
/// through its own state.
/// </para>
/// <para>
/// Userinfo and fragments are forbidden rather than stripped. Credentials in a
/// URL would be handed to whatever connects, and a fragment produces a distinct
/// byte string for what is really the same relay — and these strings are
/// compared as exact bytes, never re-parsed.
/// </para>
/// </remarks>
public static class RelayUrl
{
    /// <summary>Maximum length of a relay URL, in bytes.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// Whether <paramref name="value"/> satisfies the profile.
    /// </summary>
    /// <param name="error">The reason it does not, when it does not.</param>
    public static bool IsValid(string value, out string? error)
    {
        if (string.IsNullOrEmpty(value))
        {
            error = "a relay URL must not be empty";
            return false;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(value) > MaxLength)
        {
            error = $"a relay URL must be at most {MaxLength} bytes";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = $"'{value}' is not an absolute URL";
            return false;
        }

        if (uri.Scheme is not ("ws" or "wss"))
        {
            error = $"'{value}' is not a ws or wss relay URL";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = $"'{value}' has no host";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = $"'{value}' must not carry credentials";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = $"'{value}' must not carry a fragment";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Throws unless <paramref name="value"/> satisfies the profile.</summary>
    /// <exception cref="AppComponentException">It does not.</exception>
    public static void Require(string value)
    {
        if (!IsValid(value, out string? error))
            throw new AppComponentException(error!);
    }

    /// <summary>
    /// Orders relay URLs the way the canonical list is sorted: by UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Ordinal string comparison would agree for ASCII hostnames and diverge
    /// for anything else, and the list is signed group state — so the
    /// comparison that decides "sorted" has to be the one over the bytes that
    /// are actually encoded.
    /// </remarks>
    public static int CompareByBytes(string left, string right)
    {
        byte[] leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
    }
}
