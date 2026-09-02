using System.Text;

namespace Scramble.Marmot.AppComponents;

/// <summary>Where one kind of media locator may be stored.</summary>
/// <param name="LocatorKind">The locator scheme, e.g. <c>blossom-v1</c>.</param>
/// <param name="BaseUrl">The blob store's base URL.</param>
public sealed record BlobStoreEndpoint(string LocatorKind, string BaseUrl);

/// <summary>
/// <c>marmot.group.encrypted-media.v2</c> (<c>0x800b</c>) — where a group's
/// encrypted media may live.
/// </summary>
/// <remarks>
/// <para>
/// A policy, not content: which locator kinds the group permits and which blob
/// stores are its defaults. Supporting the component means being able to read
/// and honour that policy — a client that never uploads media satisfies it
/// trivially by never violating it, which is why carrying it is honest without
/// implementing media transfer.
/// </para>
/// <para>
/// <b>Note the v1/v2 asymmetry.</b> Version 1 (<c>0x8008</c>) is <i>frozen</i>:
/// a Current-profile group may neither require it nor hold its state, and
/// <see cref="CurrentProfile"/> refuses it. Version 2 is live. They are not two
/// versions of one supported thing.
/// </para>
/// <para>
/// Producers canonicalise and decoders do not: <see cref="Create"/> trims,
/// lowercases and deduplicates, while <see cref="Decode"/> refuses anything it
/// would have had to repair. This is signed group state, so a member that
/// quietly normalises what it was given holds a canonical form nobody else has.
/// </para>
/// </remarks>
/// <param name="MediaFormat">Always <see cref="FormatV2"/>.</param>
/// <param name="AllowedLocatorKinds">Permitted locator kinds, at least one.</param>
/// <param name="DefaultBlobEndpoints">Default stores, at least one.</param>
public sealed record EncryptedMediaPolicy(
    string MediaFormat,
    IReadOnlyList<string> AllowedLocatorKinds,
    IReadOnlyList<BlobStoreEndpoint> DefaultBlobEndpoints)
{
    /// <summary>The component id.</summary>
    public const ushort ComponentId = AppComponent.EncryptedMediaV2;

    /// <summary>The schema name.</summary>
    public const string Schema = AppComponent.EncryptedMediaV2Schema;

    /// <summary>The only media format this version defines.</summary>
    public const string FormatV2 = "encrypted-media-v2";

    /// <summary>The Blossom locator kind.</summary>
    public const string BlossomLocatorKind = "blossom-v1";

    /// <summary>Maximum length of a locator kind, in bytes.</summary>
    public const int MaxLocatorKindLength = 64;

    /// <summary>Maximum length of an endpoint URL, in bytes.</summary>
    public const int MaxEndpointUrlLength = 2048;

    /// <summary>Maximum number of locator kinds.</summary>
    public const int MaxLocatorKinds = 16;

    /// <summary>Maximum number of default blob endpoints.</summary>
    public const int MaxBlobEndpoints = 16;

    /// <summary>Builds a canonical policy.</summary>
    /// <exception cref="AppComponentException">The policy is not valid.</exception>
    public static EncryptedMediaPolicy Create(
        string mediaFormat,
        IEnumerable<string> allowedLocatorKinds,
        IEnumerable<BlobStoreEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(mediaFormat);
        ArgumentNullException.ThrowIfNull(allowedLocatorKinds);
        ArgumentNullException.ThrowIfNull(endpoints);

        string format = mediaFormat.Trim();
        if (format != FormatV2)
            throw new AppComponentException($"The encrypted-media format must be {FormatV2}.");

        var kinds = new List<string>();
        foreach (string kind in allowedLocatorKinds)
        {
            string normalized = NormalizeLocatorKind(kind, "locator kind");
            if (!kinds.Contains(normalized))
                kinds.Add(normalized);
        }

        if (kinds.Count == 0)
            throw new AppComponentException("The policy must allow at least one locator kind.");
        if (kinds.Count > MaxLocatorKinds)
            throw new AppComponentException($"The policy allows more than {MaxLocatorKinds} locator kinds.");

        var stores = new List<BlobStoreEndpoint>();
        foreach (BlobStoreEndpoint endpoint in endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoint);

            string kind = NormalizeLocatorKind(endpoint.LocatorKind, "endpoint locator kind");
            if (!kinds.Contains(kind))
                throw new AppComponentException("An endpoint's locator kind is not in the allowed set.");

            var normalized = new BlobStoreEndpoint(kind, NormalizeEndpointUrl(endpoint.BaseUrl));
            if (!stores.Contains(normalized))
                stores.Add(normalized);
        }

        if (stores.Count == 0)
            throw new AppComponentException("The policy must include at least one blob endpoint.");
        if (stores.Count > MaxBlobEndpoints)
            throw new AppComponentException($"The policy includes more than {MaxBlobEndpoints} endpoints.");

        return new EncryptedMediaPolicy(format, kinds, stores);
    }

    /// <summary>The default Blossom policy over the given stores.</summary>
    public static EncryptedMediaPolicy BlossomDefault(IEnumerable<string> baseUrls)
    {
        ArgumentNullException.ThrowIfNull(baseUrls);

        return Create(
            FormatV2,
            [BlossomLocatorKind],
            baseUrls.Select(url => new BlobStoreEndpoint(BlossomLocatorKind, url)));
    }

    /// <summary>Encodes the component state.</summary>
    public byte[] Encode()
    {
        var allowed = new List<byte>();
        foreach (string kind in AllowedLocatorKinds)
            ComponentCodec.WriteVarBytes(Encoding.UTF8.GetBytes(kind), allowed);

        var endpoints = new List<byte>();
        foreach (BlobStoreEndpoint endpoint in DefaultBlobEndpoints)
        {
            ComponentCodec.WriteVarBytes(Encoding.UTF8.GetBytes(endpoint.LocatorKind), endpoints);
            ComponentCodec.WriteVarBytes(Encoding.UTF8.GetBytes(endpoint.BaseUrl), endpoints);
        }

        return ComponentCodec.EncodeVectors(
            Encoding.UTF8.GetBytes(MediaFormat), allowed.ToArray(), endpoints.ToArray());
    }

    /// <summary>
    /// Decodes the component state, refusing anything non-canonical.
    /// </summary>
    /// <exception cref="AppComponentException">Malformed, or not canonical.</exception>
    public static EncryptedMediaPolicy Decode(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> cursor = data;

        string format = Encoding.UTF8.GetString(
            ComponentCodec.ReadVarBytes(ref cursor, 64, "encrypted media format"));

        byte[] allowedBytes = ComponentCodec.ReadVarBytes(
            ref cursor, MaxLocatorKinds * (MaxLocatorKindLength + 2), "allowed locator kinds");

        byte[] endpointBytes = ComponentCodec.ReadVarBytes(
            ref cursor,
            MaxBlobEndpoints * (MaxLocatorKindLength + MaxEndpointUrlLength + 4),
            "default blob endpoints");

        ComponentCodec.RequireSpent(cursor, "encrypted-media-v2");

        var kinds = new List<string>();
        ReadOnlySpan<byte> allowedCursor = allowedBytes;
        while (!allowedCursor.IsEmpty)
        {
            kinds.Add(Encoding.UTF8.GetString(
                ComponentCodec.ReadVarBytes(ref allowedCursor, MaxLocatorKindLength, "locator kind")));
        }

        var stores = new List<BlobStoreEndpoint>();
        ReadOnlySpan<byte> endpointCursor = endpointBytes;
        while (!endpointCursor.IsEmpty)
        {
            string kind = Encoding.UTF8.GetString(ComponentCodec.ReadVarBytes(
                ref endpointCursor, MaxLocatorKindLength, "endpoint locator kind"));
            string url = Encoding.UTF8.GetString(ComponentCodec.ReadVarBytes(
                ref endpointCursor, MaxEndpointUrlLength, "endpoint URL"));

            stores.Add(new BlobStoreEndpoint(kind, url));
        }

        // Rebuilt through the canonicalising constructor and compared. Anything
        // the producer should have normalised — case, whitespace, duplicates,
        // ordering — differs here and is refused rather than repaired, because
        // repairing signed group state leaves us holding a form nobody else has.
        var value = new EncryptedMediaPolicy(format, kinds, stores);
        EncryptedMediaPolicy canonical = Create(format, kinds, stores);

        if (!value.MediaFormat.Equals(canonical.MediaFormat, StringComparison.Ordinal)
            || !value.AllowedLocatorKinds.SequenceEqual(canonical.AllowedLocatorKinds, StringComparer.Ordinal)
            || !value.DefaultBlobEndpoints.SequenceEqual(canonical.DefaultBlobEndpoints))
        {
            throw new AppComponentException(
                "The encrypted-media policy is not in canonical form.");
        }

        return canonical;
    }

    private static string NormalizeLocatorKind(string value, string label)
    {
        ArgumentNullException.ThrowIfNull(value);

        string normalized = value.Trim().ToLowerInvariant();

        if (normalized.Length == 0)
            throw new AppComponentException($"The {label} must not be empty.");
        if (Encoding.UTF8.GetByteCount(normalized) > MaxLocatorKindLength)
            throw new AppComponentException($"The {label} exceeds {MaxLocatorKindLength} bytes.");

        foreach (char c in normalized)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                throw new AppComponentException($"The {label} may only be lowercase, digits and '-'.");
        }

        return normalized;
    }

    private static string NormalizeEndpointUrl(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string value = raw.Trim();
        if (value.Length == 0)
            throw new AppComponentException("An endpoint URL must not be empty.");
        if (Encoding.UTF8.GetByteCount(value) > MaxEndpointUrlLength)
            throw new AppComponentException($"An endpoint URL exceeds {MaxEndpointUrlLength} bytes.");

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? url))
            throw new AppComponentException("An endpoint URL is not a valid absolute URL.");

        if (url.Scheme is not ("http" or "https"))
            throw new AppComponentException("An endpoint URL scheme must be http or https.");
        if (!string.IsNullOrEmpty(url.UserInfo))
            throw new AppComponentException("An endpoint URL must not include credentials.");
        if (string.IsNullOrEmpty(url.Host))
            throw new AppComponentException("An endpoint URL must include a host.");
        if (!string.IsNullOrEmpty(url.Query))
            throw new AppComponentException("An endpoint URL must not include a query.");
        if (!string.IsNullOrEmpty(url.Fragment))
            throw new AppComponentException("An endpoint URL must not include a fragment.");

        string normalized = url.AbsoluteUri;
        if (Encoding.UTF8.GetByteCount(normalized) > MaxEndpointUrlLength)
            throw new AppComponentException($"An endpoint URL exceeds {MaxEndpointUrlLength} bytes.");

        return normalized;
    }
}
