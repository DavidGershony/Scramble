using System.Collections.Concurrent;
using System.Reflection;
using Moq;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Tests for the Bug #1 fix: gift wrap (kind 1059) events that arrive before
/// the external signer is ready are buffered in _pendingGiftWraps and drained
/// when SetExternalSigner provides a connected signer.
///
/// Without the fix, these events were silently dropped and dedup-cached,
/// making Welcomes permanently lost for Amber (NIP-46) users whose signer
/// restores asynchronously after the Welcome subscription starts.
/// </summary>
public class GiftWrapBufferingTests
{
    private readonly ITestOutputHelper _output;
    private readonly NostrService _sut;

    public GiftWrapBufferingTests(ITestOutputHelper output)
    {
        _output = output;
        _sut = new NostrService();
    }

    /// <summary>
    /// Helper: access the private _pendingGiftWraps queue via reflection.
    /// </summary>
    private ConcurrentQueue<NostrEventReceived> GetPendingGiftWraps()
    {
        var field = typeof(NostrService).GetField("_pendingGiftWraps",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (ConcurrentQueue<NostrEventReceived>)field!.GetValue(_sut)!;
    }

    /// <summary>
    /// Helper: read the private MaxPendingGiftWraps constant via reflection.
    /// </summary>
    private int GetMaxPendingGiftWraps()
    {
        var field = typeof(NostrService).GetField("MaxPendingGiftWraps",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (int)field!.GetValue(null)!;
    }

    /// <summary>
    /// Helper: create a fake kind 1059 gift wrap event.
    /// </summary>
    private static NostrEventReceived CreateFakeGiftWrap(int index = 0)
    {
        return new NostrEventReceived
        {
            Kind = 1059,
            EventId = $"giftwrap{index:D4}".PadLeft(64, '0'),
            PublicKey = "aa".PadLeft(64, 'a'),
            Content = "fake-encrypted-content",
            CreatedAt = DateTime.UtcNow,
            RelayUrl = "wss://relay.example.com",
            Tags = new List<List<string>> { new() { "p", "bb".PadLeft(64, 'b') } }
        };
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 1: Buffer starts empty
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Buffer_StartsEmpty()
    {
        var buffer = GetPendingGiftWraps();
        Assert.True(buffer.IsEmpty, "Pending gift wrap buffer should be empty on fresh NostrService");
        _output.WriteLine("Buffer is empty on construction");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 2: MaxPendingGiftWraps is 100
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void MaxPendingGiftWraps_Is100()
    {
        var max = GetMaxPendingGiftWraps();
        Assert.Equal(100, max);
        _output.WriteLine($"MaxPendingGiftWraps = {max}");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 3: SetExternalSigner with connected signer drains buffer
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetExternalSigner_ConnectedSigner_DrainsBuffer()
    {
        var buffer = GetPendingGiftWraps();

        // Enqueue 5 fake gift wraps (simulating events that arrived before signer was ready)
        for (int i = 0; i < 5; i++)
            buffer.Enqueue(CreateFakeGiftWrap(i));

        Assert.Equal(5, buffer.Count);
        _output.WriteLine($"Buffered {buffer.Count} gift wraps");

        // Create a connected mock signer
        var signerMock = new Mock<IExternalSigner>();
        signerMock.Setup(s => s.IsConnected).Returns(true);
        // Nip44DecryptAsync will return null by default (Moq),
        // causing UnwrapGiftWrapAsync to fail — caught by ProcessPendingGiftWrapsAsync

        // Act: set the signer — should trigger ProcessPendingGiftWrapsAsync
        _sut.SetExternalSigner(signerMock.Object);

        // ProcessPendingGiftWrapsAsync is fire-and-forget, give it time to complete
        await Task.Delay(500);

        // Assert: buffer should be fully drained (all events dequeued and attempted)
        Assert.True(buffer.IsEmpty,
            $"Buffer should be empty after signer connected, but has {buffer.Count} events");
        _output.WriteLine("Buffer drained after connected signer was set");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 4: SetExternalSigner with disconnected signer does NOT drain
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void SetExternalSigner_DisconnectedSigner_DoesNotDrain()
    {
        var buffer = GetPendingGiftWraps();

        // Enqueue events
        for (int i = 0; i < 3; i++)
            buffer.Enqueue(CreateFakeGiftWrap(i));

        Assert.Equal(3, buffer.Count);

        // Create a disconnected mock signer
        var signerMock = new Mock<IExternalSigner>();
        signerMock.Setup(s => s.IsConnected).Returns(false);

        // Act
        _sut.SetExternalSigner(signerMock.Object);

        // Assert: buffer should NOT be drained
        Assert.Equal(3, buffer.Count);
        _output.WriteLine("Buffer NOT drained for disconnected signer (correct)");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 5: SetExternalSigner with null does NOT drain
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void SetExternalSigner_Null_DoesNotDrain()
    {
        var buffer = GetPendingGiftWraps();

        for (int i = 0; i < 3; i++)
            buffer.Enqueue(CreateFakeGiftWrap(i));

        Assert.Equal(3, buffer.Count);

        // Act
        _sut.SetExternalSigner(null);

        // Assert: buffer should NOT be drained
        Assert.Equal(3, buffer.Count);
        _output.WriteLine("Buffer NOT drained for null signer (correct)");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 6: SetExternalSigner on empty buffer is no-op
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void SetExternalSigner_EmptyBuffer_NoError()
    {
        var buffer = GetPendingGiftWraps();
        Assert.True(buffer.IsEmpty);

        // Act: connected signer, but empty buffer — should not throw
        var signerMock = new Mock<IExternalSigner>();
        signerMock.Setup(s => s.IsConnected).Returns(true);

        var ex = Record.Exception(() => _sut.SetExternalSigner(signerMock.Object));
        Assert.Null(ex);
        _output.WriteLine("SetExternalSigner with empty buffer completed without error");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 7: Multiple SetExternalSigner calls drain buffer only once
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetExternalSigner_CalledTwice_DrainsOnlyOnce()
    {
        var buffer = GetPendingGiftWraps();

        for (int i = 0; i < 3; i++)
            buffer.Enqueue(CreateFakeGiftWrap(i));

        var signerMock = new Mock<IExternalSigner>();
        signerMock.Setup(s => s.IsConnected).Returns(true);

        // Act: set signer twice
        _sut.SetExternalSigner(signerMock.Object);
        await Task.Delay(300);

        // Buffer should already be drained
        Assert.True(buffer.IsEmpty);

        // Second call with no new events — should be no-op
        var ex = await Record.ExceptionAsync(async () =>
        {
            _sut.SetExternalSigner(signerMock.Object);
            await Task.Delay(100);
        });
        Assert.Null(ex);
        _output.WriteLine("Double SetExternalSigner handled gracefully");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 8: Buffer between disconnect and reconnect
    //  Simulates: signer disconnects → events buffer → signer reconnects → drain
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetExternalSigner_Disconnect_Reconnect_BufferDrained()
    {
        var buffer = GetPendingGiftWraps();

        // Phase 1: set connected signer, drain any initial events (none)
        var signerMock = new Mock<IExternalSigner>();
        signerMock.Setup(s => s.IsConnected).Returns(true);
        _sut.SetExternalSigner(signerMock.Object);
        await Task.Delay(100);

        // Phase 2: clear signer (simulates disconnect)
        _sut.SetExternalSigner(null);

        // Phase 3: events arrive while signer is disconnected
        for (int i = 0; i < 4; i++)
            buffer.Enqueue(CreateFakeGiftWrap(i));

        Assert.Equal(4, buffer.Count);
        _output.WriteLine($"Buffered {buffer.Count} events during signer disconnect");

        // Phase 4: signer reconnects
        _sut.SetExternalSigner(signerMock.Object);
        await Task.Delay(500);

        // Assert: buffer drained
        Assert.True(buffer.IsEmpty,
            $"Buffer should be empty after signer reconnected, but has {buffer.Count} events");
        _output.WriteLine("Buffer drained after signer reconnect");
    }
}
