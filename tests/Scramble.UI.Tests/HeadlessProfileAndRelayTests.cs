using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for profile dialog, copy keys, and relay management.
/// </summary>
public class HeadlessProfileAndRelayTests : HeadlessTestBase
{
    // --- My Profile ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ShowMyProfile_OpensDialogWithKeys(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(mainVm.ShowMyProfileDialog);

        await mainVm.ShowMyProfileCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.True(mainVm.ShowMyProfileDialog);
        Assert.Equal(ctx.User.Npub, mainVm.MyNpub);
        Assert.Equal(ctx.User.Nsec, mainVm.MyNsec);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CloseMyProfile_ClosesDialog(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        await mainVm.ShowMyProfileCommand.Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.True(mainVm.ShowMyProfileDialog);

        mainVm.CloseMyProfileCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.False(mainVm.ShowMyProfileDialog);
    }

    // --- Copy Keys ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CopyNpub_CopiesToClipboard(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        ctx.MockClipboard.Setup(c => c.SetTextAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        // Open profile first to populate MyNpub
        await mainVm.ShowMyProfileCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        // Don't await — the command has internal Task.Delay that clears the status
        mainVm.CopyNpubCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        ctx.MockClipboard.Verify(c => c.SetTextAsync(ctx.User.Npub!), Times.Once);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CopyNsec_CopiesToClipboard(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        ctx.MockClipboard.Setup(c => c.SetTextAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        // Open profile first to populate MyNsec
        await mainVm.ShowMyProfileCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        // Don't await — the command has internal Task.Delay that clears the status
        mainVm.CopyNsecCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        ctx.MockClipboard.Verify(c => c.SetTextAsync(ctx.User.Nsec!), Times.Once);
    }

    // --- Relay Selection ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SelectableRelays_PopulatedOnNewChat(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);

        // Execute NewChatCommand which populates SelectableRelays
        chatListVm.NewChatCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(chatListVm.SelectableRelays);
        Assert.All(chatListVm.SelectableRelays, r => Assert.True(r.IsSelected));
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SelectableRelays_DeselectUpdatesCount(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);

        chatListVm.NewChatCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        var initialCount = chatListVm.SelectedRelayCount;
        Assert.True(initialCount > 0);

        // Deselect one relay
        chatListVm.SelectableRelays[0].IsSelected = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(initialCount - 1, chatListVm.SelectedRelayCount);
    }

    // --- Reconnect Relay ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ReconnectRelay_CallsNostrService(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        ctx.MockNostr.Setup(n => n.ReconnectRelayAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        var mainVm = CreateMainViewModel(ctx);
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        var relayVm = new RelayStatusViewModel { Url = "wss://relay.test", IsConnected = false };
        mainVm.RelayStatuses.Add(relayVm);

        await mainVm.ReconnectRelayCommand.Execute(relayVm);
        Dispatcher.UIThread.RunJobs();

        ctx.MockNostr.Verify(n => n.ReconnectRelayAsync("wss://relay.test"), Times.Once);
    }
}
