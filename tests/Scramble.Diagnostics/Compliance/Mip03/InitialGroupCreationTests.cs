using System.Text.Json;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Scramble.Diagnostics.RelayHarness;
using Scramble.Diagnostics.TestHelpers;
using Scramble.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scramble.Diagnostics.Compliance.Mip03;

/// <summary>
/// MIP-03 §"Exception - Initial Group Creation":
/// "The very first Commit that creates a group MUST NOT be sent to relays.
///  Initial Commit establishes epoch 0 and exists only locally."
///
/// This test verifies that CreateGroupAsync does not publish any kind:445 events.
/// </summary>
[Trait("Category", "MIP-Compliance")]
[Trait("MIP", "MIP-03")]
public class InitialGroupCreationTests : IAsyncLifetime
{
    private FaultyRelay? _relay;
    private NostrService? _nostrService;
    private StorageService? _storage;
    private IMlsService? _mlsService;
    private MessageService? _messageService;
    private string? _dbPath;
    private string? _privateKeyHex;

    public async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _relay = new FaultyRelay();
        await _relay.StartAsync();

        _nostrService = new NostrService();
        var keys = _nostrService.GenerateKeyPair();
        _privateKeyHex = keys.privateKeyHex;

        _dbPath = Path.Combine(Path.GetTempPath(), $"scramble_mip03_create_{Guid.NewGuid()}.db");
        _storage = new StorageService(_dbPath, new MockSecureStorage());
        await _storage.InitializeAsync();
        await _storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub,
            Nsec = keys.nsec,
            DisplayName = "Alice",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsService = DarkMatterMlsServiceFactory.Create(_storage);
        _messageService = new MessageService(_storage, _nostrService, _mlsService);
        await _messageService.InitializeAsync();

        await _nostrService.ConnectAsync(_relay.WsUrl);
        await Task.Delay(500);
    }

    public async ValueTask DisposeAsync()
    {
        _messageService?.Dispose();
        if (_nostrService != null)
        {
            try { await _nostrService.DisconnectAsync(); } catch { }
            (_nostrService as IDisposable)?.Dispose();
        }
        if (_relay != null) await _relay.DisposeAsync();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (_dbPath != null)
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task CreateGroup_DoesNotPublish_KindFourFourFive()
    {
        Assert.NotNull(_relay);
        Assert.NotNull(_mlsService);

        var eventCountBefore = _relay.StoredEventCount;

        // Create group — this establishes epoch 0 locally only
        var groupInfo = await _mlsService.CreateGroupAsync("MIP-03 Test Group", new[] { _relay.WsUrl });

        // Give a moment for any async publishes to land
        await Task.Delay(1000);

        // Check relay for kind:445 events
        var storedEvents = _relay.StoredEventsJson;
        var kind445Events = new List<string>();
        foreach (var rawJson in storedEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.TryGetProperty("kind", out var kindProp) && kindProp.GetInt32() == 445)
                    kind445Events.Add(rawJson);
            }
            catch { }
        }

        // MIP-03 §"Initial Group Creation": epoch-0 commit MUST NOT be sent to relays
        Assert.Empty(kind445Events);
    }
}
