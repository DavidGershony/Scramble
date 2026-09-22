namespace Scramble.Marmot.Storage;

/// <summary>
/// A KeyPackage this device published, together with the private material a
/// Welcome needs to consume it.
/// </summary>
/// <remarks>
/// <para>
/// The private half is the point of this record. Only the public KeyPackage
/// goes on the wire; the <c>init_key</c> stays here, and a Welcome addressed to
/// that KeyPackage cannot be opened without it. The previous implementation
/// discarded it, so a join could never complete.
/// </para>
/// <para>
/// Keyed by KeyPackageRef because that is the key the MLS layer looks a bundle
/// up under while processing a Welcome. A bundle stored under any other key is
/// not reachable at the moment it is needed, so it is not really stored at all.
/// </para>
/// </remarks>
/// <param name="KeyPackageRefHex">
/// The RFC 9420 KeyPackageRef, lowercase hex — computed over the inner
/// <c>KeyPackage</c>, not over the <c>MLSMessage</c> that frames it on the wire.
/// </param>
/// <param name="SlotId">
/// The kind-30443 <c>d</c> tag this KeyPackage was published under. Several
/// records share a slot over time: publishing a replacement supersedes the
/// previous occupant rather than removing it here.
/// </param>
/// <param name="PublicKeyPackage">The published <c>MLSMessage</c> bytes.</param>
/// <param name="PrivateMaterial">
/// The serialized bundle holding the private <c>init_key</c>. Null once erased,
/// which is one-way: see <see cref="KeyPackageRecordState"/>.
/// </param>
/// <param name="LastResort">
/// Whether the KeyPackage carries the last-resort component. It decides when
/// the private material must go: a normal KeyPackage loses it as soon as a
/// Welcome consumes it, a last-resort one may keep it so the published package
/// stays usable.
/// </param>
/// <param name="NotBefore">The MLS <c>Lifetime</c> lower bound, Unix seconds.</param>
/// <param name="NotAfter">
/// The MLS <c>Lifetime</c> upper bound, Unix seconds. Also the outer deadline
/// for erasing a last-resort KeyPackage's private material.
/// </param>
public sealed record KeyPackageRecord(
    string KeyPackageRefHex,
    string SlotId,
    byte[] PublicKeyPackage,
    byte[]? PrivateMaterial,
    bool LastResort,
    long NotBefore,
    long NotAfter,
    KeyPackageRecordState State,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// The kind-30443 event that carried this KeyPackage, once a relay
    /// confirmed the publish.
    /// </summary>
    /// <remarks>
    /// A Welcome names the KeyPackage it consumed by this event id, so without
    /// it an inbound Welcome cannot be matched to the private material it
    /// needs. Null until publication is confirmed.
    /// </remarks>
    public string? EventIdHex { get; init; }

    /// <summary>Whether this record can still open an inbound Welcome.</summary>
    public bool CanConsume => PrivateMaterial is not null;
}

/// <summary>
/// Lifecycle of a locally held KeyPackage.
/// </summary>
/// <remarks>
/// The states exist to make one question answerable after a crash: may this
/// KeyPackage's private material still be used, and if not, has it actually
/// been erased? Movement is one-way, and <see cref="Retired"/> is terminal.
/// </remarks>
public enum KeyPackageRecordState
{
    /// <summary>
    /// Built and persisted, but no relay has confirmed the publish.
    /// </summary>
    /// <remarks>
    /// The private material exists from this moment, before anything is on the
    /// wire, because the alternative — publish first, persist after — loses the
    /// material for a KeyPackage other people can already see. A publish that
    /// ultimately fails leaves an orphan here, which the caller must delete
    /// rather than leave to accumulate across retries.
    /// </remarks>
    Created,

    /// <summary>Publication confirmed; the KeyPackage is discoverable.</summary>
    Published,

    /// <summary>
    /// A Welcome has been processed against this KeyPackage.
    /// </summary>
    /// <remarks>
    /// Terminal for a normal KeyPackage, whose material is erased in the same
    /// step. A last-resort KeyPackage stays consumable here and can be consumed
    /// again — that reuse is exactly what "last resort" means — until it is
    /// retired.
    /// </remarks>
    Consumed,

    /// <summary>
    /// The private material has been erased. Terminal.
    /// </summary>
    /// <remarks>
    /// The record outlives the material deliberately: an inbound Welcome naming
    /// a retired KeyPackage must be answered with "consumed already", not with
    /// the silence of an unknown reference.
    /// </remarks>
    Retired,
}
