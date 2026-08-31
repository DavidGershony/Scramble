using DotnetMls.Types;

namespace Scramble.Marmot.Engine.KeyPackages;

/// <summary>
/// The validity window a Marmot KeyPackage may carry.
/// </summary>
/// <remarks>
/// <para>
/// RFC 9420 §10 requires an application to define a maximum acceptable total
/// lifetime for a KeyPackage and to reject anything longer, but leaves the
/// number to the application. That makes the number an <b>interop constant,
/// not a preference</b>: whatever the peer enforces is what we must fit inside,
/// and a KeyPackage outside it is refused before anything else about it is
/// looked at.
/// </para>
/// <para>
/// The values below are OpenMLS's, read off
/// <c>openmls/src/key_packages/lifetime.rs</c> at
/// <c>erskingardner/openmls@59e7d3b2</c> — the exact revision <c>mdk</c> pins
/// at <c>wn-agent-v0.9.15</c>. They are not from any Marmot document, and no
/// Marmot document restates them, so this is the only place they are written
/// down on our side. <b>If the peer starts rejecting our KeyPackages after an
/// upstream bump, check here first.</b>
/// </para>
/// <para>
/// The default window sits exactly on the maximum, because upstream's own
/// default does: <c>Lifetime::default()</c> is <c>now - margin</c> to
/// <c>now + validity</c>, and the acceptable range is <c>margin + validity</c>.
/// The comparison is <c>&lt;=</c>, so equality passes — but there is no room
/// above it, which is why <see cref="Create"/> takes a duration rather than
/// letting a caller add to the default.
/// </para>
/// </remarks>
public static class KeyPackageLifetimePolicy
{
    /// <summary>
    /// How far into the past <c>not_before</c> is set, in seconds.
    /// </summary>
    /// <remarks>
    /// Clock skew, not slack: a peer whose clock is a little behind ours would
    /// otherwise see a KeyPackage that is not valid yet and reject it. One
    /// hour, matching upstream.
    /// </remarks>
    public const ulong ClockSkewMarginSeconds = 60 * 60;

    /// <summary>Default validity ahead of now, in seconds: 3 × 28 days.</summary>
    public const ulong DefaultValiditySeconds = 60 * 60 * 24 * 28 * 3;

    /// <summary>
    /// The largest <c>not_after - not_before</c> a peer will accept, in seconds.
    /// </summary>
    public const ulong MaxRangeSeconds = ClockSkewMarginSeconds + DefaultValiditySeconds;

    /// <summary>
    /// Builds a window valid from shortly before <paramref name="now"/>.
    /// </summary>
    /// <param name="now">Unix seconds.</param>
    /// <param name="validitySeconds">
    /// How long ahead of <paramref name="now"/> the KeyPackage stays usable.
    /// Defaults to <see cref="DefaultValiditySeconds"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The resulting window exceeds <see cref="MaxRangeSeconds"/>, or
    /// <paramref name="now"/> is inside the skew margin of the epoch.
    /// </exception>
    public static Lifetime Create(ulong now, ulong? validitySeconds = null)
    {
        ulong validity = validitySeconds ?? DefaultValiditySeconds;

        if (validity == 0)
            throw new ArgumentOutOfRangeException(
                nameof(validitySeconds), "A KeyPackage that is valid for no time cannot be used.");

        if (validity > DefaultValiditySeconds)
            throw new ArgumentOutOfRangeException(
                nameof(validitySeconds),
                $"A validity of {validity}s puts the total window over the {MaxRangeSeconds}s a peer accepts.");

        if (now < ClockSkewMarginSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(now), "The clock is inside the skew margin of the Unix epoch; it is not set.");

        return new Lifetime(now - ClockSkewMarginSeconds, checked(now + validity));
    }

    /// <summary>
    /// Whether a window is one a conformant peer will accept.
    /// </summary>
    /// <remarks>
    /// Applied to <b>inbound</b> KeyPackages too. The MLS library does not
    /// enforce a maximum range — RFC 9420 leaves it to the application, so
    /// there is nothing for a generic library to enforce — which means a
    /// KeyPackage with an unbounded window decodes and verifies perfectly well
    /// and must be refused here or not at all.
    /// </remarks>
    public static bool IsAcceptableRange(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        return lifetime.NotBefore < lifetime.NotAfter
            && lifetime.NotAfter - lifetime.NotBefore <= MaxRangeSeconds;
    }

    /// <summary>
    /// Whether <paramref name="now"/> falls inside the window.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsAcceptableRange"/> on purpose: a window can be
    /// well-formed and expired, and the two failures mean different things. One
    /// says the publisher is misconfigured, the other says the KeyPackage is
    /// merely stale and a fresh one should be fetched.
    /// </remarks>
    public static bool IsValidAt(Lifetime lifetime, ulong now)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        return now >= lifetime.NotBefore && now < lifetime.NotAfter;
    }
}
