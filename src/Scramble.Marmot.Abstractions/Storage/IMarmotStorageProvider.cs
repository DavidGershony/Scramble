namespace Scramble.Marmot.Storage;

/// <summary>
/// Aggregate storage surface the engine depends on.
/// </summary>
/// <remarks>
/// Split into focused sub-interfaces rather than one wide interface so later
/// subsystems (convergence passes, disband, device maintenance) can be added as
/// their own capability without reshaping what already exists.
/// </remarks>
public interface IMarmotStorageProvider :
    IGroupStorage,
    IMessageStorage,
    IOutboundIntentStorage,
    ILeaveRequestStorage,
    IWelcomeStorage,
    IKeyPackageStorage,
    ISnapshotStorage
{
    /// <summary>Opens a transaction covering every sub-store.</summary>
    Task<IStorageTransaction> BeginTransactionAsync(CancellationToken ct = default);
}
