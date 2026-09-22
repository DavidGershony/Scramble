namespace Scramble.Marmot.Storage;

/// <summary>Durable Welcome records.</summary>
public interface IWelcomeStorage
{
    Task PutWelcomeAsync(WelcomeRecord welcome, CancellationToken ct = default);

    Task<WelcomeRecord?> GetWelcomeAsync(MessageId id, CancellationToken ct = default);

    Task<IReadOnlyList<WelcomeRecord>> ListWelcomesAsync(
        WelcomeRecordState? state = null,
        CancellationToken ct = default);
}
