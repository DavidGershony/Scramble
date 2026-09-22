using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Scramble.Core.Models;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for login, key generation, key import, and logout flows.
/// </summary>
public class HeadlessLoginTests : HeadlessTestBase
{
    [AvaloniaTheory(Skip = "Requires ShellViewModel - login flow moved out of MainViewModel")]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ImportPrivateKey_LogsInAndShowsMainUI(string backend)
    {
        var ctx = await CreateRealContext(backend, saveUser: false);

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();

        Assert.False(mainVm.IsLoggedIn);

        // Import the private key via LoginViewModel
        // TODO: Update for ShellViewModel — mainVm.LoginViewModel.PrivateKeyInput = ctx.User.Nsec;
        // TODO: Update for ShellViewModel — mainVm.LoginViewModel.ImportKeyCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.True(mainVm.IsLoggedIn);
        Assert.NotNull(mainVm.CurrentUser);
        Assert.Equal(ctx.User.PublicKeyHex, mainVm.CurrentUser!.PublicKeyHex);
    }

    [AvaloniaTheory(Skip = "Requires ShellViewModel refactor")]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task GenerateNewKey_CreatesValidKeysAndLogsIn(string backend) { }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task Logout_ClearsStateAndShowsLogin(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();

        // Simulate login
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(mainVm.IsLoggedIn);

        // Create a group so chat list has content
        var groupInfo = await ctx.MlsService.CreateGroupAsync("Pre-Logout Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Pre-Logout Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);
        await mainVm.ChatListViewModel.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(mainVm.ChatListViewModel.Chats);

        // Logout
        mainVm.LogoutCommand.Execute().Subscribe();
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();

        Assert.False(mainVm.IsLoggedIn);
        Assert.Null(mainVm.CurrentUser);
        Assert.Empty(mainVm.ChatListViewModel.Chats);
    }
}
