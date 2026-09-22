using System.Reflection;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The process dying between two storage writes.
/// </summary>
/// <remarks>
/// <para>
/// Several orderings in the engine are load-bearing only across a crash: both
/// orders end with the same rows, so the difference is visible exclusively to a
/// restart that happens after one write and before the other. Nothing else in
/// this suite can produce that moment, which is why four ordering claims stood
/// documented and unverified.
/// </para>
/// <para>
/// <b>It wraps a real provider rather than replacing one.</b> The point of the
/// exercise is what survives on disk, so the writes that do land have to be the
/// SQLite ones. A fake store would prove only that the code calls its methods in
/// a particular order — which is a probe's job, and where a probe suffices this
/// class should not be used at all.
/// </para>
/// <para>
/// <b><see cref="DispatchProxy"/> rather than a decorator.</b>
/// <see cref="IMarmotStorageProvider"/> aggregates a dozen sub-interfaces and
/// some fifty members; a hand-written decorator would be fifty lines of
/// forwarding in which a single typo is a silently wrong test. One
/// <see cref="Invoke"/> covers the lot and cannot drift as the interface grows.
/// </para>
/// <para>
/// <b>Writes are counted, reads are not.</b> A crash window is bounded by the
/// writes either side of it, and how many times the code reads in between is an
/// implementation detail that would make every ordinal fragile. The write set is
/// named by prefix below rather than enumerated, so a method added to the
/// storage contract is counted by default — the safe direction, since the
/// alternative is an uncounted write that silently shifts every ordinal.
/// </para>
/// <para>
/// <b>Not sealed, and it cannot be.</b> <see cref="DispatchProxy"/> generates a
/// subclass at runtime, so sealing this refuses to construct at all.
/// </para>
/// </remarks>
public class CrashingStorage : DispatchProxy
{
    private IMarmotStorageProvider _inner = null!;
    private int _dieAtOrdinal = int.MaxValue;
    private string? _dieOnCall;
    private int _dieOnOccurrence;
    private bool _dieBefore;

    /// <summary>
    /// A real provider, and the handle that decides when it stops working.
    /// </summary>
    /// <remarks>
    /// Two references to one object: the proxy is the control. Returned as a
    /// pair anyway, because the engine must be handed the interface — a test
    /// that passed the control type around would be one refactor away from
    /// arming a provider nothing is using.
    /// </remarks>
    public static (IMarmotStorageProvider Storage, CrashingStorage Control) Over(
        IMarmotStorageProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        IMarmotStorageProvider proxy = Create<IMarmotStorageProvider, CrashingStorage>();
        var control = (CrashingStorage)(object)proxy;
        control._inner = inner;

        return (proxy, control);
    }

    /// <summary>Every write seen since the last arming, in order.</summary>
    /// <remarks>
    /// Kept so a failing test can say which call it actually killed. An ordinal
    /// that quietly names a different write from the one the test meant is the
    /// way this class would produce a convincing test of nothing.
    /// </remarks>
    public List<string> Writes { get; } = [];

    /// <summary>
    /// The write the process dies on, having completed it, by position.
    /// </summary>
    /// <remarks>
    /// <b>By position, not by name, and for a test about which write comes
    /// first that is the only form that works.</b> Nominating a call by name
    /// would follow the call when the order is swapped, and the test would kill
    /// the same write in both worlds and see the same thing — which is exactly
    /// the test that cannot fail. Counted from the moment of arming, so a test
    /// arms immediately before the operation under test rather than counting
    /// everything its fixture did.
    /// </remarks>
    public void DieAfterWrite(int ordinal) => Arm(ordinal, null, 0, dieBefore: false);

    /// <summary>
    /// The write the process dies on, having completed it, by name.
    /// </summary>
    /// <remarks>
    /// For a window bounded by a call the test can name — where the question is
    /// what survives a crash at a known point, rather than which of two writes
    /// went first. Prefer the ordinal form for an ordering claim, and see its
    /// remarks for why.
    /// </remarks>
    public void DieAfterWrite(string call, int occurrence = 1) =>
        Arm(int.MaxValue, call, occurrence, dieBefore: false);

    /// <summary>
    /// The write the process dies on, without performing it.
    /// </summary>
    /// <remarks>
    /// A storage failure rather than a crash — the caller sees the throw and may
    /// still run its own cleanup. Different from
    /// <see cref="DieAfterWrite(int)"/> in exactly the way that matters to a
    /// <c>using</c> block: a crash gets no disposal, a throw does.
    /// </remarks>
    public void DieBeforeWrite(int ordinal) => Arm(ordinal, null, 0, dieBefore: true);

    /// <summary>Stops the storage failing, leaving what is on disk alone.</summary>
    public void Disarm()
    {
        _dieAtOrdinal = int.MaxValue;
        _dieOnCall = null;
    }

    private void Arm(int ordinal, string? call, int occurrence, bool dieBefore)
    {
        if (call is null)
            ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        else
            ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);

        Writes.Clear();
        _dieAtOrdinal = ordinal;
        _dieOnCall = call;
        _dieOnOccurrence = occurrence;
        _dieBefore = dieBefore;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (!IsWrite(method.Name))
            return Forward(method, args);

        Writes.Add(method.Name);

        if (!IsTheOne(method.Name))
            return Forward(method, args);

        // Disarmed on the way past, so the throw cannot be re-raised by a
        // retry the test did not intend to kill. One crash per arming.
        Disarm();

        if (_dieBefore)
            throw new StorageCrashException(method.Name, performed: false);

        // Blocked rather than awaited: this returns object?, and a continuation
        // that faults would have to be built at the method's own Task<T> type
        // through more reflection. The providers here are synchronous
        // underneath -- SQLite has no true async path -- so this waits on work
        // that has already finished. A genuinely asynchronous provider would
        // need the harder version, and would deadlock here rather than lie.
        if (Forward(method, args) is Task completed)
            completed.GetAwaiter().GetResult();

        throw new StorageCrashException(method.Name, performed: true);
    }

    private bool IsTheOne(string call) =>
        _dieOnCall is null
            ? Writes.Count == _dieAtOrdinal
            : call == _dieOnCall && Writes.Count(w => w == call) == _dieOnOccurrence;

    private object? Forward(MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Unwrapped, or every storage failure in these tests arrives as a
            // reflection artefact and no `catch` in the engine would recognise
            // the exception it was written for.
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Whether a storage call changes what a restart would find.
    /// </summary>
    /// <remarks>
    /// By prefix, matching the naming the storage contract already uses. The
    /// snapshot verbs are here too: <c>CreateSnapshotAsync</c> and
    /// <c>ReleaseSnapshotAsync</c> both write, and neither is reachable from the
    /// paths these tests kill, but leaving them out would make an ordinal
    /// silently wrong the day one is.
    /// </remarks>
    private static bool IsWrite(string name) =>
        name.StartsWith("Put", StringComparison.Ordinal)
        || name.StartsWith("Clear", StringComparison.Ordinal)
        || name.StartsWith("Delete", StringComparison.Ordinal)
        || name.StartsWith("Prune", StringComparison.Ordinal)
        || name.StartsWith("Erase", StringComparison.Ordinal)
        || name.StartsWith("Mark", StringComparison.Ordinal)
        || name.StartsWith("Invalidate", StringComparison.Ordinal)
        || name.StartsWith("Rollback", StringComparison.Ordinal)
        || name.StartsWith("CreateSnapshot", StringComparison.Ordinal)
        || name.StartsWith("ReleaseSnapshot", StringComparison.Ordinal);
}

/// <summary>What the storage does instead of working.</summary>
/// <remarks>
/// An <see cref="IOException"/> because that is what a dead database throws, and
/// because the engine's own tests already use one — a distinct hierarchy would
/// invite a <c>catch</c> somewhere that treats a test crash differently from a
/// real one.
/// </remarks>
public sealed class StorageCrashException(string call, bool performed)
    : IOException($"The process died {(performed ? "after" : "during")} {call}.")
{
    /// <summary>The storage call the process died on.</summary>
    public string Call { get; } = call;

    /// <summary>Whether that call completed before the process died.</summary>
    public bool Performed { get; } = performed;
}
