using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Scramble.MobileAndroid.Services;
using Scramble.MobileAndroid.Views;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Services;
using System.Linq;

namespace Scramble.MobileAndroid;

/// <summary>
/// Avalonia Application for the Android head. Composes the shared ShellViewModel
/// (login + chat) with platform services and hosts the unified Avalonia UI from
/// Scramble.UI. The intent is to share UI/code 1:1 with desktop so this head can
/// be evaluated as a possible replacement for the native Scramble.Android app.
///
/// What's intentionally NOT wired here (yet):
///   - Push notifications (UnifiedPush/ntfy), QR scanner.
/// Each will be added only when its absence blocks an evaluation milestone.
///
/// Background notifications (foreground service + message notifications) are wired
/// in MainActivity.cs — notification channels, NotificationOrchestrator, and the
/// relay foreground service lifecycle are all managed there.
/// </summary>
public partial class App : Avalonia.Application
{
    private static readonly string[] ThemeNames = { "Nostr", "Golden Axe", "Cyber Teal", "AMOLED Black", "AMOLED Purple" };
    private static readonly string[] ThemeUris =
    {
        "avares://Scramble.UI/Themes/NostrColors.axaml",
        "avares://Scramble.UI/Themes/GoldenAxeTheme.axaml",
        "avares://Scramble.UI/Themes/CyberTealTheme.axaml",
        "avares://Scramble.UI/Themes/AmoledBlackTheme.axaml",
        "avares://Scramble.UI/Themes/AmoledPurpleTheme.axaml",
    };

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Profile-independent services. NostrService is cross-platform (Core);
            // clipboard / QR / launcher come from Scramble.UI's Avalonia implementations
            // which are framework-only and work fine on Android.
            ISecureStorage secureStorage = new MobileAndroidSecureStorage();
            var nostrService = new NostrService();
            var clipboard = new AvaloniaClipboard();
            var qrCodeGenerator = new AvaloniaQrCodeGenerator();
            var launcher = new MobileAndroidLauncher();
            var platform = new PlatformContext
            {
                IsMobile = true,
                HasFilePicker = true,
                HasAudioRecording = true,
                HasAudioPlayback = true,
                HasMediaUpload = true,
            };
            PlatformContext.IsMobileGlobal = true;

            // Audio and upload services (set as static on ChatViewModel, same as desktop)
            var context = Android.App.Application.Context;
            ChatViewModel.AudioRecordingService = new MobileAndroidAudioRecordingService(context);
            ChatViewModel.AudioPlaybackService = new MobileAndroidAudioPlaybackService(context);
            ChatViewModel.MediaUploadService = new BlossomUploadService();

            // Runtime permission request delegate — Android 6+ requires runtime consent
            // for dangerous permissions (RECORD_AUDIO, etc.) even if declared in the manifest.
            Func<string[], Task<bool>> requestPermissions = async permissions =>
            {
                var activity = MainActivity.Current;
                if (activity == null) return false;
                return await activity.RequestPermissionsAsync(permissions);
            };
            ChatViewModel.PermissionRequestFunc = requestPermissions;
            SettingsViewModel.PermissionRequestFunc = requestPermissions;

            var shellViewModel = new ShellViewModel(
                nostrService, secureStorage, clipboard, qrCodeGenerator, launcher, platform);

            // Expose ShellViewModel so MainActivity can handle the back button
            // and wire the foreground service login-state subscription (handles
            // the case where OnCreate ran before Shell was available).
            MainActivity.SetShell(shellViewModel);

            // MLS service factory — Android cannot load the Rust uniffi backend (MlsService)
            // without the native libs cross-compiled for ARM; force Managed (pure-C#) here.
            shellViewModel.MlsServiceFactory = storage => new ManagedMlsService(storage);

            // File picker — use Avalonia's cross-platform StorageProvider API
            ChatViewModel.FilePickerFunc = async () =>
            {
                // Get the TopLevel from the MainView once it's set
                var singleViewLifetime = (ISingleViewApplicationLifetime)ApplicationLifetime!;
                var topLevel = TopLevel.GetTopLevel(singleViewLifetime.MainView);
                if (topLevel == null) return null;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Select image to send",
                        AllowMultiple = false,
                        FileTypeFilter = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("Images")
                            {
                                Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" },
                                MimeTypes = new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }
                            },
                            Avalonia.Platform.Storage.FilePickerFileTypes.All
                        }
                    });

                if (files.Count == 0) return null;

                var file = files[0];
                await using var stream = await file.OpenReadAsync();
                using var ms = new System.IO.MemoryStream();
                await stream.CopyToAsync(ms);
                var data = ms.ToArray();

                var ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "application/octet-stream"
                };

                return (data, file.Name, mime);
            };

            // Theme switching — default to AMOLED Purple (same as desktop)
            const int defaultThemeIndex = 4;
            SettingsViewModel.AvailableThemeNames = ThemeNames;
            SettingsViewModel.OnThemeChanged = index => Dispatcher.UIThread.Post(() => ApplyTheme(index));
            ApplyTheme(defaultThemeIndex);

            singleView.MainView = new MobileMainView
            {
                DataContext = shellViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(int index)
    {
        if (index < 0 || index >= ThemeUris.Length) return;

        try
        {
            var uri = new System.Uri(ThemeUris[index]);
            var resources = Resources as Avalonia.Controls.ResourceDictionary;
            if (resources == null) return;

            // Remove old color dictionary
            var existing = resources.MergedDictionaries
                .OfType<Avalonia.Markup.Xaml.Styling.ResourceInclude>()
                .FirstOrDefault(r => r.Source?.ToString().Contains("/Themes/") == true);

            if (existing != null)
                resources.MergedDictionaries.Remove(existing);

            // Add new color dictionary
            var newTheme = new Avalonia.Markup.Xaml.Styling.ResourceInclude(uri) { Source = uri };
            resources.MergedDictionaries.Add(newTheme);
        }
        catch
        {
            // Silently fail — theme is cosmetic, not critical
        }
    }
}
