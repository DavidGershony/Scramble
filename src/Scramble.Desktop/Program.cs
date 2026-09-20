using Avalonia;
using ReactiveUI.Avalonia;
using Scramble.Core.Configuration;
using System;

namespace Scramble.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Parse CLI arguments before anything else
        string? profileName = null;
        string? mdkBackendArg = null;
        bool allowLocalRelays = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                profileName = args[++i];
            else if (args[i].Equals("--mdk", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                mdkBackendArg = args[++i];
            else if (args[i].Equals("--allow-local-relays", StringComparison.OrdinalIgnoreCase))
                allowLocalRelays = true;
        }

        if (profileName != null)
        {
            // Explicit --profile override
            if (profileName.StartsWith("npub1", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--profile cannot be an npub. Profiles are auto-derived from npub on login.");

            ProfileConfiguration.SetProfile(profileName, explicitOverride: true);
        }
        else
        {
            // Auto-derive profile from last active account in registry
            AccountRegistryService.Load();
            var activeAccount = AccountRegistryService.GetActiveAccount();
            if (activeAccount != null)
            {
                ProfileConfiguration.SetProfileForAccount(activeAccount.PublicKeyHex);
            }
            // else: default profile (login screen)
        }

        if (mdkBackendArg != null)
        {
            // Refused rather than ignored. It used to select between the two
            // marmot-cs backends; since P11's flip the Dark Matter engine is the
            // only one registered, and accepting the flag silently would tell a
            // reader their choice took effect. The flag goes with marmot-cs.
            throw new ArgumentException(
                $"--mdk '{mdkBackendArg}' is no longer selectable: the Dark Matter engine is the "
                + "only MLS backend. Remove the flag.");
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
