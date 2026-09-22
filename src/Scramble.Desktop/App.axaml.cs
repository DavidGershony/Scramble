using System;
using System.Linq;
using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Scramble.Core.Configuration;
using Scramble.Core.Logging;
using Scramble.UI.Views;
using Scramble.UI.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.Core.Services;
using ReactiveUI;

namespace Scramble.Desktop;

public partial class App : Application
{
    private static ILogger? _logger;

    private static readonly string[] ThemeNames = { "Nostr", "Golden Axe", "Cyber Teal", "AMOLED Black", "AMOLED Purple" };
    private static readonly string[] ThemeUris =
    {
        "avares://Scramble.UI/Themes/NostrColors.axaml",
        "avares://Scramble.UI/Themes/GoldenAxeTheme.axaml",
        "avares://Scramble.UI/Themes/CyberTealTheme.axaml",
        "avares://Scramble.UI/Themes/AmoledBlackTheme.axaml",
        "avares://Scramble.UI/Themes/AmoledPurpleTheme.axaml",
    };

    public override void Initialize()
    {
        // Initialize logging first, before anything else
        var appVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
        LoggingConfiguration.Initialize(logDirectory: ProfileConfiguration.LogDirectory, appVersion: appVersion);
        _logger = LoggingConfiguration.CreateLogger("App");

        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // RxUI exception handler is now configured in Program.BuildAvaloniaApp via ReactiveUIBuilder.WithExceptionHandler

        _logger.LogInformation("Global exception handlers registered");

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _logger?.LogInformation("OnFrameworkInitializationCompleted starting");

        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _logger?.LogDebug("Creating profile-independent services...");

                // Profile-independent services (survive across logins)
                ISecureStorage secureStorage;
                if (OperatingSystem.IsWindows())
                    secureStorage = new DesktopSecureStorage();
                else if (OperatingSystem.IsLinux())
                    secureStorage = new LinuxSecureStorage();
                else
                {
                    // macOS fallback — uses file-backed AES-GCM key (LinuxSecureStorage).
                    // For proper Keychain-backed storage, use the Scramble.Apple project instead.
                    _logger?.LogWarning(
                        "Running on macOS via Scramble.Desktop uses file-backed key storage. " +
                        "For Keychain-backed secure storage, use the Scramble.Apple app head instead.");
                    secureStorage = new LinuxSecureStorage();
                }
                var nostrService = new NostrService();
                var clipboard = new AvaloniaClipboard();
                var qrCodeGenerator = new AvaloniaQrCodeGenerator();
                var launcher = new AvaloniaLauncher();
                var platform = new PlatformContext
                {
                    IsMobile = false,
                    HasFilePicker = true,
                    HasAudioRecording = true,
                    HasAudioPlayback = true,
                    HasMediaUpload = true,
                };

                // Audio and upload services (profile-independent, set as static on ChatViewModel)
                var audioRecording = new DesktopAudioRecordingService();
                var audioPlayback = new DesktopAudioPlaybackService();
                var blossomUpload = new BlossomUploadService();
                ChatViewModel.AudioRecordingService = audioRecording;
                ChatViewModel.AudioPlaybackService = audioPlayback;
                ChatViewModel.MediaUploadService = blossomUpload;

                // File picker (needs desktop.MainWindow, set after window creation)
                // Will be set below after MainWindow is created.

                _logger?.LogDebug("Creating ShellViewModel...");
                var shellViewModel = new ShellViewModel(nostrService, secureStorage, clipboard, qrCodeGenerator, launcher, platform);

                // MLS service — the Dark Matter engine, on every head. P11's flip.
                shellViewModel.MlsServiceFactory = DarkMatterMlsServiceFactory.Create;

                _logger?.LogDebug("Creating MainWindow...");
                // Set platform notification service
                NotificationOrchestrator.NotificationService = new DesktopNotificationService();

                desktop.MainWindow = new MainWindow
                {
                    DataContext = shellViewModel
                };
                desktop.MainWindow.Title = "Scramble" + ProfileConfiguration.WindowTitleSuffix;

                // Track window focus for notification suppression
                desktop.MainWindow.Activated += (_, _) => NotificationOrchestrator.IsAppInForeground = true;
                desktop.MainWindow.Deactivated += (_, _) => NotificationOrchestrator.IsAppInForeground = false;

                // File picker for image/media attach (needs MainWindow reference)
                ChatViewModel.FilePickerFunc = async () =>
                {
                    var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
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

                // Theme switching — default to AMOLED Purple
                const int defaultThemeIndex = 4;
                SettingsViewModel.AvailableThemeNames = ThemeNames;
                SettingsViewModel.OnThemeChanged = index => Dispatcher.UIThread.Post(() => ApplyTheme(index));
                ApplyTheme(defaultThemeIndex);

                _logger?.LogInformation("MainWindow created successfully (Profile: {Profile})", ProfileConfiguration.ProfileName);

                // Handle application shutdown
                desktop.ShutdownRequested += (_, _) =>
                {
                    _logger?.LogInformation("Application shutdown requested");
                    LoggingConfiguration.Shutdown();
                };
            }

            base.OnFrameworkInitializationCompleted();
            _logger?.LogInformation("OnFrameworkInitializationCompleted completed successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "FATAL: Exception in OnFrameworkInitializationCompleted");
            LoggingConfiguration.Shutdown();
            throw;
        }
    }

    private void ApplyTheme(int index)
    {
        if (index < 0 || index >= ThemeUris.Length) return;

        try
        {
            var uri = new Uri(ThemeUris[index]);
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

            _logger?.LogInformation("Theme applied: {Theme} ({Uri})", ThemeNames[index], ThemeUris[index]);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to apply theme index {Index}", index);
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        _logger?.LogCritical(exception, "FATAL: Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
        LoggingConfiguration.Shutdown();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved(); // Prevent crash
    }

    private static void OnRxException(Exception ex)
    {
        _logger?.LogError(ex, "RxUI exception caught");
    }

    internal static IObserver<Exception> CreateRxExceptionObserver() => Observer.Create<Exception>(OnRxException);
}

// Helper for RxUI exception handler
internal static class Observer
{
    public static IObserver<T> Create<T>(Action<T> onNext) => new AnonymousObserver<T>(onNext);

    private class AnonymousObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => _onNext(value);
    }
}
