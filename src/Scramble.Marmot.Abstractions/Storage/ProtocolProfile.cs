namespace Scramble.Marmot.Storage;

/// <summary>
/// Which account-identity-proof construction a group is built on.
/// </summary>
/// <remarks>
/// A group is exactly one profile; mixing is invalid and must be rejected.
/// Scramble only ever <i>creates</i> <see cref="Current"/> groups.
/// <see cref="Legacy"/> exists so the discriminator can round-trip if we ever
/// have to read one, but the legacy construction is deliberately not
/// implemented — see the migration plan.
/// </remarks>
public enum ProtocolProfile
{
    /// <summary>Account-identity proof carried as app component 0x8009.</summary>
    Current = 0,

    /// <summary>Account-identity proof carried as leaf extension 0xf2f1. Not implemented.</summary>
    Legacy = 1,
}
