namespace Scramble.Marmot.Storage;

/// <summary>
/// Durable KeyPackage records, including the private material a Welcome consumes.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle is expressed as narrow transitions rather than a general
/// upsert, and deliberately so. Erasing private material is a security
/// obligation with a deadline; if callers could write a whole record back, a
/// stale copy held across an erase would silently resurrect the key material
/// this interface exists to destroy. <see cref="PutKeyPackageAsync"/> therefore
/// inserts and never replaces, and every later change is its own one-way step.
/// </para>
/// <para>
/// Erasure is not optional. A normal KeyPackage's private material must go as
/// soon as a Welcome has been processed against it; a last-resort one may
/// outlive that, but must be erased at the earlier of a confirmed replacement
/// in the same slot and its own <c>not_after</c>. Retaining it longer widens
/// the window in which compromise decrypts every recorded Welcome sent to that
/// KeyPackage.
/// </para>
/// </remarks>
public interface IKeyPackageStorage
{
    /// <summary>
    /// Stores a freshly built KeyPackage and its private material.
    /// </summary>
    /// <remarks>
    /// Insert-only. Call it before publishing: material persisted after a
    /// successful publish is material that can be lost for a KeyPackage other
    /// people can already fetch and encrypt Welcomes to.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A record already exists for this KeyPackageRef.
    /// </exception>
    Task PutKeyPackageAsync(KeyPackageRecord record, CancellationToken ct = default);

    Task<KeyPackageRecord?> GetKeyPackageAsync(string keyPackageRefHex, CancellationToken ct = default);

    /// <summary>
    /// Finds the KeyPackage a Welcome names by its kind-30443 event id.
    /// </summary>
    /// <remarks>
    /// The join path's entry point: a Welcome carries the event id, and the
    /// private material is what has to be found from it.
    /// </remarks>
    Task<KeyPackageRecord?> GetKeyPackageByEventAsync(string eventIdHex, CancellationToken ct = default);

    /// <param name="slotId">Restrict to one publication slot.</param>
    /// <param name="state">Restrict to one lifecycle state.</param>
    Task<IReadOnlyList<KeyPackageRecord>> ListKeyPackagesAsync(
        string? slotId = null,
        KeyPackageRecordState? state = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records a confirmed publish, binding the record to its event id.
    /// </summary>
    /// <returns>False when no record matches, or it is past this state.</returns>
    Task<bool> MarkPublishedAsync(
        string keyPackageRefHex, string eventIdHex, CancellationToken ct = default);

    /// <summary>
    /// Records that a Welcome was successfully processed against this KeyPackage.
    /// </summary>
    /// <remarks>
    /// Only on success. A failed Welcome must leave the KeyPackage exactly as
    /// it was, so the inviter can retry against it or pick another candidate.
    /// </remarks>
    /// <returns>False when no record matches, or it is already retired.</returns>
    Task<bool> MarkConsumedAsync(string keyPackageRefHex, CancellationToken ct = default);

    /// <summary>
    /// Erases the private material and retires the record.
    /// </summary>
    /// <remarks>
    /// One-way and idempotent. The record itself survives so a Welcome naming a
    /// retired KeyPackage can be told it is spent rather than unrecognised.
    /// </remarks>
    /// <returns>False when no record matches.</returns>
    Task<bool> ErasePrivateMaterialAsync(string keyPackageRefHex, CancellationToken ct = default);

    /// <summary>
    /// Removes the record outright.
    /// </summary>
    /// <remarks>
    /// For the orphan case only: a KeyPackage that was built, persisted, and
    /// then never published. Nothing on any relay refers to it, so there is no
    /// Welcome to answer and nothing to keep. Without this, retries against a
    /// failing relay accumulate unused private key material indefinitely.
    /// </remarks>
    /// <returns>False when no record matches.</returns>
    Task<bool> DeleteKeyPackageAsync(string keyPackageRefHex, CancellationToken ct = default);
}
