using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Scramble.Presentation.ViewModels;

namespace Scramble.MobileAndroid.Views;

/// <summary>
/// Top-level mobile shell hosted by App.OnFrameworkInitializationCompleted as the
/// ISingleViewApplicationLifetime.MainView. Reuses LoginView / ChatListView /
/// ChatView / SettingsView / AccountSwitcherView / MyProfileDialogView from
/// Scramble.UI verbatim so the unified Avalonia UI stays the single source of
/// truth — see CLAUDE.md (Platform targets section).
/// </summary>
public partial class MobileMainView : UserControl
{
    public MobileMainView()
    {
        AvaloniaXamlLoader.Load(this);

        // Keep content clear of the status bar and the gesture pill.
        //
        // The window is edge-to-edge -- Android enforces that for apps targeting
        // SDK 35+ -- so without this the top of the shell draws underneath the
        // status bar. The obvious-looking fix is a top padding on the Android view
        // in MainActivity, and it is a trap: padding an Android view moves what
        // Avalonia draws without moving where it hit-tests, so every control in
        // the app answers taps offset by the status-bar height. That shipped in
        // v0.7.0. See the remarks on MainActivity.ImeInsetListener.
        //
        // Avalonia's own safe-area padding is the right layer: it is a Thickness
        // on a control, so layout and hit-testing move together by construction.
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private IInsetsManager? _insets;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _insets = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (_insets is null)
        {
            return;
        }

        _insets.SafeAreaChanged += OnSafeAreaChanged;
        Padding = _insets.SafeAreaPadding;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _insets = null;
        }
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => Padding = e.SafeAreaPadding;

    /// <summary>
    /// Top-bar avatar tap → toggle the account switcher. Mirrors
    /// MainWindow.axaml.cs AvatarButton_Click on desktop.
    /// </summary>
    private void AvatarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.ToggleAccountSwitcherCommand.Execute().Subscribe();
        }
    }
}
