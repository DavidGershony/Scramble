namespace Scramble.Marmot.Storage;

/// <summary>
/// A received Welcome, stored so joining survives a restart between receipt
/// and processing.
/// </summary>
/// <remarks>
/// Processing a Welcome consumes the KeyPackage's init key material exactly
/// once, so a Welcome must not be processed twice — <see cref="State"/> is
/// what makes that safe across a crash.
/// </remarks>
public sealed record WelcomeRecord(
    MessageId Id,
    byte[] Wire,
    WelcomeRecordState State,
    DateTimeOffset CreatedAt)
{
    /// <summary>Set once the Welcome has been processed into a group.</summary>
    public GroupId? GroupId { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Lifecycle of a stored Welcome.</summary>
public enum WelcomeRecordState
{
    /// <summary>Stored, not yet processed.</summary>
    Pending,

    /// <summary>Processed into a joined group. Terminal.</summary>
    Accepted,

    /// <summary>Terminally rejected (not for us, invalid, or superseded).</summary>
    Failed,
}
