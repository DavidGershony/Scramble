using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Presentation.Services;

namespace Scramble.UI.Services;

public class AvaloniaClipboard : IPlatformClipboard
{
    private static readonly ILogger<AvaloniaClipboard> Logger =
        LoggingConfiguration.CreateLogger<AvaloniaClipboard>();

    /// <summary>
    /// Resolves the platform clipboard by finding the TopLevel from the current
    /// ApplicationLifetime. Supports desktop (IClassicDesktopStyleApplicationLifetime)
    /// and mobile/single-view (ISingleViewApplicationLifetime) hosts.
    ///
    /// Uses TopLevel.GetTopLevel() — the recommended Avalonia 12 pattern — instead of
    /// accessing Window.Clipboard directly, which may return null depending on the
    /// platform backend initialisation state.
    /// </summary>
    private static IClipboard? GetClipboard()
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime;

        if (lifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            return TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard;

        if (lifetime is ISingleViewApplicationLifetime singleView && singleView.MainView != null)
            return TopLevel.GetTopLevel(singleView.MainView)?.Clipboard;

        Logger.LogWarning("GetClipboard: could not resolve clipboard — ApplicationLifetime is {Type}",
            lifetime?.GetType().Name ?? "null");
        return null;
    }

    public async Task SetTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
        }
        else
        {
            Logger.LogWarning("SetTextAsync: clipboard is null, text was NOT copied");
        }
    }

    public async Task<string?> GetTextAsync()
    {
        var clipboard = GetClipboard();
        if (clipboard != null)
            return await clipboard.TryGetTextAsync();

        Logger.LogWarning("GetTextAsync: clipboard is null, returning null");
        return null;
    }
}
