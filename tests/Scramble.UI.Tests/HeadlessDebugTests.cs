using System.Reactive;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for debug features: log viewer open/close/refresh and open log folder.
/// </summary>
public class HeadlessDebugTests : HeadlessTestBase
{
    private async Task<(RealTestContext Ctx, SettingsViewModel SettingsVm)> CreateSettingsViewModel(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var settingsVm = new SettingsViewModel(
            ctx.MockNostr.Object, ctx.Storage, ctx.MlsService, ctx.MessageService, ctx.MockLauncher.Object);

        return (ctx, settingsVm);
    }

    // --- Log Viewer Open ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ViewLogs_OpensLogViewer(string backend)
    {
        var (ctx, settingsVm) = await CreateSettingsViewModel(backend);

        Assert.False(settingsVm.ShowLogViewer);

        settingsVm.ViewLogsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(settingsVm.ShowLogViewer);
        // LogContent should be populated (may be empty in test environment but should be set)
        Assert.NotNull(settingsVm.LogContent);
    }

    // --- Log Viewer Close ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CloseLogViewer_ClosesPanel(string backend)
    {
        var (ctx, settingsVm) = await CreateSettingsViewModel(backend);

        // Open first
        settingsVm.ViewLogsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.True(settingsVm.ShowLogViewer);

        // Close
        settingsVm.CloseLogViewerCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.False(settingsVm.ShowLogViewer);
    }

    // --- Log Viewer Refresh ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task RefreshLogs_UpdatesLogContent(string backend)
    {
        var (ctx, settingsVm) = await CreateSettingsViewModel(backend);

        // Open log viewer
        settingsVm.ViewLogsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        var initialContent = settingsVm.LogContent;

        // Refresh
        settingsVm.RefreshLogsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        // LogContent should be re-read (content is set, not null)
        Assert.NotNull(settingsVm.LogContent);
    }

    // --- Open Log Folder ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task OpenLogFolder_CallsLauncher(string backend)
    {
        var (ctx, settingsVm) = await CreateSettingsViewModel(backend);

        ctx.MockLauncher.Setup(l => l.OpenFolder(It.IsAny<string>()));

        settingsVm.OpenLogFolderCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        ctx.MockLauncher.Verify(l => l.OpenFolder(It.IsAny<string>()), Times.Once);
    }
}
