namespace Scramble.Marmot.Storage;

/// <summary>Durable intent to leave a group.</summary>
public interface ILeaveRequestStorage
{
    Task PutLeaveRequestAsync(LeaveRequest request, CancellationToken ct = default);

    Task<LeaveRequest?> GetLeaveRequestAsync(GroupId groupId, CancellationToken ct = default);

    Task<IReadOnlyList<LeaveRequest>> ListLeaveRequestsAsync(CancellationToken ct = default);

    Task ClearLeaveRequestAsync(GroupId groupId, CancellationToken ct = default);
}
