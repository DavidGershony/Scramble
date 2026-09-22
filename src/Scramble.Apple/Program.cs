using Avalonia;
using ReactiveUI.Avalonia;
using Scramble.Core.Configuration;
using System;

namespace Scramble.Apple;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Parse CLI arguments before anything else
        string? profileName = null;
        bool allowLocalRelays = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                profileName = args[++i];
            else if (args[i].Equals("--allow-local-relays", StringComparison.OrdinalIgnoreCase))
                allowLocalRelays = true;
        }

        if (profileName != null)
        {
            if (profileName.StartsWith("npub1", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--profile cannot be an npub. Profiles are auto-derived from npub on login.");

            ProfileConfiguration.SetProfile(profileName, explicitOverride: true);
        }
        else
        {
            AccountRegistryService.Load();
            var activeAccount = AccountRegistryService.GetActiveAccount();
            if (activeAccount != null)
            {
                ProfileConfiguration.SetProfileForAccount(activeAccount.PublicKeyHex);
            }
        }

        if (allowLocalRelays)
            ProfileConfiguration.SetAllowLocalRelays(true);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI(rxui => rxui.WithExceptionHandler(App.CreateRxExceptionObserver()));
}
