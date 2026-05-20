using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Scramble.Core.Crypto;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;

namespace Scramble.Presentation.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly ILogger<LoginViewModel> _logger;
    private readonly INostrService _nostrService;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly IPlatformLauncher? _launcher;
    private readonly IPlatformClipboard? _clipboard;

    /// <summary>
    /// The external signer instance, available for wiring into NostrService after login.
    /// </summary>
    public IExternalSigner? ExternalSigner { get; }

    [Reactive] public partial string PrivateKeyInput { get; set; } = string.Empty;
    [Reactive] public partial string BunkerUrl { get; set; } = string.Empty;
    [Reactive] public partial string? ErrorMessage { get; set; }
    [Reactive] public partial bool IsLoading { get; set; }
    [Reactive] public partial User? LoggedInUser { get; set; }

    /// <summary>
    /// True when the login screen was opened via "Add Account" (shows a Cancel button).
    /// Set by ShellViewModel.
    /// </summary>
    [Reactive] public partial bool IsAddAccountMode { get; set; }

    /// <summary>
    /// When true, the signer status subscription will NOT trigger auto-login via
    /// HandleSignerConnectedAsync. Set by ShellViewModel before calling
    /// InitializeAfterLoginAsync to prevent a duplicate ActivateSession race:
    /// RestoreSessionAsync emits Connected, which the subscription picks up and
    /// would otherwise create a second User + trigger OnLoginCompleted while the
    /// first session is already active.
    /// </summary>
    public bool SuppressSignerAutoLogin { get; set; }

    // Generated key display
    [Reactive] public partial string? GeneratedNsec { get; set; }
    [Reactive] public partial string? GeneratedNpub { get; set; }
    [Reactive] public partial bool ShowGeneratedKeys { get; set; }

    // External signer
    [Reactive] public partial bool ShowExternalSigner { get; set; }
    [Reactive] public partial bool IsExternalSignerConnecting { get; set; }
    [Reactive] public partial string ExternalSignerStatus { get; set; } = string.Empty;
    [Reactive] public partial string? NostrConnectUri { get; set; }
    /// <summary>QR code as PNG bytes (platform-neutral). Views convert to their image type.</summary>
    [Reactive] public partial byte[]? NostrConnectQrPngBytes { get; set; }
    [Reactive] public partial string SignerRelayInput { get; set; } = "nos.lol";

    // Login method selection
    [Reactive] public partial LoginMethod SelectedLoginMethod { get; set; } = LoginMethod.PrivateKey;

    public ReactiveCommand<Unit, Unit> GenerateNewKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> UseGeneratedKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectExternalSignerCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNsecCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNpubCommand { get; }
    public ReactiveCommand<LoginMethod, Unit> SelectLoginMethodCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshSignerRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSignerAppCommand { get; }

    public LoginViewModel(INostrService nostrService, IQrCodeGenerator qrCodeGenerator,
        IPlatformLauncher? launcher = null, IExternalSigner? externalSigner = null,
        IPlatformClipboard? clipboard = null)
    {
        _logger = LoggingConfiguration.CreateLogger<LoginViewModel>();
        _logger.LogDebug("LoginViewModel initializing");

        _nostrService = nostrService;
        _qrCodeGenerator = qrCodeGenerator;
        _launcher = launcher;
        _clipboard = clipboard;
        ExternalSigner = externalSigner ?? new ExternalSignerService();

        GenerateNewKeyCommand = ReactiveCommand.Create(GenerateNewKey);

        var canImport = this.WhenAnyValue(
            x => x.PrivateKeyInput,
            x => x.IsLoading,
            (key, loading) => !string.IsNullOrWhiteSpace(key) && !loading);

        ImportKeyCommand = ReactiveCommand.CreateFromTask(ImportKeyAsync, canImport);

        var canUseGenerated = this.WhenAnyValue(
            x => x.GeneratedNsec,
            x => x.IsLoading,
            (nsec, loading) => !string.IsNullOrEmpty(nsec) && !loading);

        UseGeneratedKeyCommand = ReactiveCommand.CreateFromTask(UseGeneratedKeyAsync, canUseGenerated);

        // IsExternalSignerConnecting is intentionally NOT part of CanExecute. It's set
        // true while the nostrconnect QR listener is waiting for approval — but that's
        // a passive flow the user may want to abandon by pasting a bunker URL instead.
        // Concurrent runs are still prevented by IsLoading and ReactiveCommand's own
        // IsExecuting gate.
        var canConnectSigner = this.WhenAnyValue(
            x => x.BunkerUrl,
            x => x.IsLoading,
            (url, loading) => !string.IsNullOrWhiteSpace(url) && !loading);

        ConnectExternalSignerCommand = ReactiveCommand.CreateFromTask(ConnectExternalSignerAsync, canConnectSigner);

        CopyNsecCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_clipboard != null && !string.IsNullOrEmpty(GeneratedNsec))
            {
                try
                {
                    await _clipboard.SetTextAsync(GeneratedNsec);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to copy nsec to clipboard");
                }
            }
        });

        CopyNpubCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_clipboard != null && !string.IsNullOrEmpty(GeneratedNpub))
            {
                try
                {
                    await _clipboard.SetTextAsync(GeneratedNpub);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to copy npub to clipboard");
                }
            }
        });

        SelectLoginMethodCommand = ReactiveCommand.CreateFromTask<LoginMethod>(SelectLoginMethodAsync);

        RefreshSignerRelayCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            NostrConnectUri = null;
            NostrConnectQrPngBytes = null;
            await GenerateNostrConnectAsync();
        });

        // Open the nostrconnect URI in an external signer app (e.g. Amber on Android).
        // Only enabled when a URI has been generated and a launcher is available.
        var canOpenSigner = this.WhenAnyValue(
            x => x.NostrConnectUri,
            uri => !string.IsNullOrEmpty(uri) && _launcher != null);

        OpenSignerAppCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrEmpty(NostrConnectUri) && _launcher != null)
            {
                if (!_launcher.LaunchSignerUri(NostrConnectUri))
                {
                    ErrorMessage = "No signer app found. Install Amber or paste a bunker:// URL below.";
                }
            }
        }, canOpenSigner);

        // Subscribe to external signer status and auto-login on connect
        if (ExternalSigner != null)
        {
            ExternalSigner.Status
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(async status =>
                {
                    ExternalSignerStatus = status.State switch
                    {
                        ExternalSignerState.Connecting => "Connecting to relay...",
                        ExternalSignerState.WaitingForApproval => "Scan the QR code with your signer app...",
                        ExternalSignerState.Connected => "Connected!",
                        ExternalSignerState.Error => $"Error: {status.Error}",
                        _ => ""
                    };

                    IsExternalSignerConnecting = status.State == ExternalSignerState.Connecting ||
                                                 status.State == ExternalSignerState.WaitingForApproval;

                    // Auto-login when signer connects via nostrconnect.
                    // status.PublicKeyHex CAN be null here — the reconnect path in
                    // ExternalSignerService emits Connected with whatever PublicKeyHex
                    // happens to be set on the service, which may be null if reconnect
                    // fires before the initial connect handshake populated it. Try the
                    // signer's PublicKeyHex property as a fallback, then GetPublicKeyAsync()
                    // as a last resort, before giving up — otherwise the user sees
                    // "Connected!" but nothing happens (this was the Amber-on-Android bug).
                    if (status.State == ExternalSignerState.Connected)
                    {
                        _logger.LogInformation(
                            "Connected event received. status.PublicKeyHex={StatusKey}, signer.PublicKeyHex={SignerKey}",
                            string.IsNullOrEmpty(status.PublicKeyHex) ? "<null>" : status.PublicKeyHex[..Math.Min(16, status.PublicKeyHex.Length)] + "...",
                            string.IsNullOrEmpty(ExternalSigner!.PublicKeyHex) ? "<null>" : ExternalSigner.PublicKeyHex[..Math.Min(16, ExternalSigner.PublicKeyHex.Length)] + "...");

                        var pubKey = status.PublicKeyHex ?? ExternalSigner!.PublicKeyHex;
                        if (string.IsNullOrEmpty(pubKey))
                        {
                            try
                            {
                                _logger.LogWarning("Connected event has no PublicKeyHex — calling get_public_key as last resort");
                                pubKey = await ExternalSigner!.GetPublicKeyAsync();
                                _logger.LogInformation("get_public_key returned {PubKey}",
                                    string.IsNullOrEmpty(pubKey) ? "<empty>" : pubKey[..Math.Min(16, pubKey.Length)] + "...");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Connected fired but signer has no PublicKeyHex and GetPublicKeyAsync failed");
                                ErrorMessage = $"Signer connected but did not return a public key: {ex.Message}";
                            }
                        }

                        if (!string.IsNullOrEmpty(pubKey))
                        {
                            await HandleSignerConnectedAsync(pubKey);
                        }
                    }
                });
        }
    }

    private async Task SelectLoginMethodAsync(LoginMethod method)
    {
        SelectedLoginMethod = method;
        ShowExternalSigner = method == LoginMethod.ExternalSigner;
        ShowGeneratedKeys = false;
        ErrorMessage = null;

        // Auto-generate nostrconnect URI when Extension tab is selected
        if (method == LoginMethod.ExternalSigner && NostrConnectUri == null && ExternalSigner != null)
        {
            await GenerateNostrConnectAsync();
        }
    }

    private async Task GenerateNostrConnectAsync()
    {
        try
        {
            var rawInput = string.IsNullOrWhiteSpace(SignerRelayInput) ? "relay.damus.io" : SignerRelayInput.Trim();
            var signerRelay = SettingsViewModel.NormalizeRelayUrl(rawInput);
            var relays = new[] { signerRelay, "wss://relay.nsec.app" }.Distinct().ToList();
            var uri = await ExternalSigner!.GenerateAndListenForConnectionAsync(relays);
            NostrConnectUri = uri;
            NostrConnectQrPngBytes = _qrCodeGenerator.GeneratePng(uri);
            _logger.LogInformation("Generated nostrconnect QR code (URI redacted — contains secret)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate nostrconnect URI");
            ErrorMessage = $"Failed to connect to relay: {ex.Message}";
        }
    }

    private void GenerateNewKey()
    {
        _logger.LogInformation("User requested to generate new key");

        try
        {
            var (_, _, nsec, npub) = _nostrService.GenerateKeyPair();
            GeneratedNsec = nsec;
            GeneratedNpub = npub;
            ShowGeneratedKeys = true;
            ErrorMessage = null;
            _logger.LogInformation("Successfully generated new key. Npub: {Npub}", npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate new key");
            ErrorMessage = $"Failed to generate key: {ex.Message}";
        }
    }

    private async Task ImportKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(PrivateKeyInput))
        {
            _logger.LogWarning("ImportKeyAsync called with empty input");
            return;
        }

        _logger.LogInformation("User attempting to import private key");
        _logger.LogDebug("Key input length: {Length}, starts with nsec: {IsNsec}",
            PrivateKeyInput.Length,
            PrivateKeyInput.Trim().StartsWith("nsec"));

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _logger.LogDebug("Calling NostrService.ImportPrivateKey");
            var (privateKeyHex, publicKeyHex, nsec, npub) = _nostrService.ImportPrivateKey(PrivateKeyInput.Trim());
            _logger.LogInformation("Successfully parsed private key. Derived npub: {Npub}", npub);

            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                PrivateKeyHex = privateKeyHex,
                PublicKeyHex = publicKeyHex,
                Nsec = nsec,
                Npub = npub,
                CreatedAt = DateTime.UtcNow,
                IsCurrentUser = true
            };

            LoggedInUser = user;
            _logger.LogInformation("User successfully logged in with npub: {Npub}", npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import private key");
            ErrorMessage = $"Invalid private key: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task UseGeneratedKeyAsync()
    {
        if (string.IsNullOrEmpty(GeneratedNsec))
        {
            _logger.LogWarning("UseGeneratedKeyAsync called but no generated nsec available");
            return;
        }

        _logger.LogInformation("User continuing with generated key");
        _logger.LogDebug("Setting PrivateKeyInput to generated nsec and calling ImportKeyAsync");
        PrivateKeyInput = GeneratedNsec;
        await ImportKeyAsync();
    }

    private async Task HandleSignerConnectedAsync(string publicKeyHex)
    {
        // Guard: skip if already logged in (prevents duplicate login from signer reconnect)
        if (LoggedInUser != null) return;

        // Guard: skip if the session is being managed by auto-login / restore
        // (RestoreSessionAsync emits Connected which would otherwise race here)
        if (SuppressSignerAutoLogin)
        {
            _logger.LogInformation("HandleSignerConnectedAsync suppressed — session managed by auto-login");
            return;
        }

        try
        {
            // Amber (and other NIP-46 signers) often uses a different pubkey for
            // the kind-24133 transport envelope vs the user's actual signing pubkey.
            // The publicKeyHex passed in came from the envelope (HandleIncomingConnect
            // sets it to senderPubKey) — it's the *transport* key. The source of
            // truth for user identity is whatever get_public_key returns.
            // ResolveSigningPubKeyAsync calls get_public_key AND updates the service's
            // own PublicKeyHex so downstream consumers stay in sync.
            var resolved = await ExternalSigner!.ResolveSigningPubKeyAsync();

            // If we couldn't resolve the signing pubkey via get_public_key, refuse
            // to log in rather than silently using the transport key. Using the
            // transport key as the user identity is the bug that produced the
            // wrong-npub / empty-chats symptom on bunker:// connections.
            if (string.IsNullOrEmpty(resolved))
            {
                _logger.LogError("Could not resolve signing pubkey from signer (get_public_key returned null/invalid). Aborting login to avoid storing the transport key as the user identity.");
                ErrorMessage = "Failed to obtain your public key from the signer. Please check the signer app and try again.";
                await ExternalSigner!.DisconnectAsync();
                return;
            }

            // Sanity check: if the resolved signing key equals the bunker's transport
            // key AND the transport key happens to be 64 hex chars (i.e. it's a real
            // signing-class key, not a different kind of envelope), that's still
            // technically valid — some signers use the same key for both. We don't
            // hard-fail here, but we do log it for diagnostics.
            var transportKey = ExternalSigner!.RemotePubKey;
            if (!string.IsNullOrEmpty(transportKey) && string.Equals(resolved, transportKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Resolved signing pubkey equals transport pubkey {Pub} — signer uses one key for both roles", resolved[..Math.Min(16, resolved.Length)]);
            }

            publicKeyHex = resolved;

            // Derive npub directly from publicKeyHex so User.PublicKeyHex and
            // User.Npub stay in lockstep. Never read ExternalSigner.Npub here —
            // it's a derived property and can be stale or based on the transport key.
            var npub = Bech32.Encode("npub", Convert.FromHexString(publicKeyHex));

            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                PublicKeyHex = publicKeyHex,
                Npub = npub,
                CreatedAt = DateTime.UtcNow,
                IsCurrentUser = true,
                PrivateKeyHex = null,
                Nsec = null,
                // Persist signer session details for auto-reconnect on app restart
                SignerRelayUrl = string.Join(";", ExternalSigner!.RelayUrls),
                SignerRemotePubKey = ExternalSigner.RemotePubKey,
                SignerSecret = ExternalSigner.Secret,
                SignerLocalPrivateKeyHex = ExternalSigner.LocalPrivateKeyHex,
                SignerLocalPublicKeyHex = ExternalSigner.LocalPublicKeyHex
            };

            LoggedInUser = user;
            _logger.LogInformation("Auto-logged in via external signer. Npub: {Npub}", npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete login after signer connected");
            ErrorMessage = $"Failed to complete login: {ex.Message}";
        }
    }

    private async Task ConnectExternalSignerAsync()
    {
        if (string.IsNullOrWhiteSpace(BunkerUrl) || ExternalSigner == null) return;

        IsLoading = true;
        IsExternalSignerConnecting = true;
        ErrorMessage = null;

        try
        {
            // Disconnect any existing nostrconnect listener to prevent cross-contamination:
            // the nostrconnect flow created keypair A + secret A; if we now connect via
            // bunker:// with keypair B, a stale nostrconnect ack (with secret A) arriving
            // on the old WebSocket would fail with "invalid secret".
            if (ExternalSigner.IsConnected || ExternalSigner.RelayUrls.Count > 0)
            {
                _logger.LogInformation("Disconnecting existing signer session before bunker:// connect");
                await ExternalSigner.DisconnectAsync();
            }

            var success = await ExternalSigner.ConnectWithStringAsync(BunkerUrl.Trim());

            if (success && ExternalSigner.PublicKeyHex != null)
            {
                await HandleSignerConnectedAsync(ExternalSigner.PublicKeyHex);
            }
            else
            {
                ErrorMessage = "Failed to connect to external signer. Please check the URL and try again.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to connect: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            IsExternalSignerConnecting = false;
        }
    }

    /// <summary>
    /// Resets UI state for re-display after logout or account switch.
    /// Disconnects any active external signer session so the old account
    /// doesn't auto-login when the login screen appears.
    /// </summary>
    public void Reset()
    {
        LoggedInUser = null;
        PrivateKeyInput = string.Empty;
        BunkerUrl = string.Empty;
        ErrorMessage = null;
        IsLoading = false;
        GeneratedNsec = null;
        GeneratedNpub = null;
        ShowGeneratedKeys = false;
        ShowExternalSigner = false;
        IsExternalSignerConnecting = false;
        ExternalSignerStatus = string.Empty;
        NostrConnectUri = null;
        NostrConnectQrPngBytes = null;
        SelectedLoginMethod = LoginMethod.PrivateKey;
        IsAddAccountMode = false;
        SuppressSignerAutoLogin = false;

        // Disconnect the external signer to prevent the old Amber session
        // from auto-logging in when the login screen is re-displayed.
        // The new account will establish its own signer connection.
        if (ExternalSigner != null)
        {
            _ = ExternalSigner.DisconnectAsync();
        }
    }
}

public enum LoginMethod
{
    PrivateKey,
    GenerateNew,
    ExternalSigner
}
