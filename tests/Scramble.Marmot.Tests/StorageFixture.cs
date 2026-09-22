using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;

namespace Scramble.Marmot.Tests;

/// <summary>
/// A provider over a temporary on-disk database.
/// </summary>
/// <remarks>
/// Deliberately on disk rather than in-memory: these tests care about
/// transaction and rollback behaviour, and an in-memory database would not
/// exercise the same journalling path.
/// </remarks>
public sealed class StorageFixture : IDisposable
{
    private readonly string _path;

    public StorageFixture()
    {
        _path = Path.Combine(Path.GetTempPath(), $"marmot-test-{Guid.NewGuid():N}.db");
        Provider = new SqliteMarmotStorageProvider($"Data Source={_path}");
    }

    public SqliteMarmotStorageProvider Provider { get; }

    public static GroupId NewGroupId() => new(Guid.NewGuid().ToByteArray());

    public static MessageId NewMessageId(string seed = "") =>
        MessageId.FromMlsBytes(System.Text.Encoding.UTF8.GetBytes(seed + Guid.NewGuid()));

    public static GroupRecord Group(GroupId id, ulong epoch = 0) =>
        new(id, new EpochId(epoch), ProtocolProfile.Current,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public static MessageRecord Message(
        GroupId groupId,
        MessageId id,
        ulong epoch = 0,
        MessageRecordState state = MessageRecordState.Created) =>
        new(id, groupId, null, new EpochId(epoch), state, new byte[] { 1, 2, 3 },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Provider.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A stray temp file is not worth failing a test run over.
        }
    }
}
