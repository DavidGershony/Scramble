namespace Scramble.Marmot;

/// <summary>
/// An MLS epoch number within a group.
/// </summary>
public readonly record struct EpochId(ulong Value) : IComparable<EpochId>
{
    public int CompareTo(EpochId other) => Value.CompareTo(other.Value);

    public static bool operator <(EpochId a, EpochId b) => a.Value < b.Value;
    public static bool operator >(EpochId a, EpochId b) => a.Value > b.Value;
    public static bool operator <=(EpochId a, EpochId b) => a.Value <= b.Value;
    public static bool operator >=(EpochId a, EpochId b) => a.Value >= b.Value;

    public override string ToString() => Value.ToString();
}
