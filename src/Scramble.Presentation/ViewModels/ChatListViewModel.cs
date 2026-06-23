using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Scramble.Core.Crypto;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;

namespace Scramble.Presentation.ViewModels;

public partial class ChatListViewModel : ViewModelBase
{
    private readonly ILogger<ChatListViewModel> _logger;
    private readonly IMessageService _messageService;
    private readonly IStorageService _storageService;
    private readonly IMlsService? _mlsService;
    private readonly INostrService? _nostrService;
    private string? _currentUserPubKeyHex;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private IDisposable? _chatUpdateSubscription;
    private IDisposable? _inviteSubscription;
    private IDisposable? _decryptionErrorSubscription;
    private IDisposable? _skippedInviteSubscription;
    private ChatItemViewModel? _deviceSyncChatItem;

    public ObservableCollection<ChatItemViewModel> Chats { get; } = new();
    public ObservableCollection<ChatItemViewModel> AgentChats { get; } = new();
    public ObservableCollection<ChatItemViewModel> ArchivedChats { get; } = new();
    public ObservableCollection<PendingInviteItemViewModel> PendingInvites { get; } = new();
    public ObservableCollection<FollowContactViewModel> Following { get; } = new();
    [Reactive] public partial bool IsRefreshingFollowing { get; set; }

    private ChatItemViewModel? _selectedChat;
    private bool _isSettingSelectedChat;

    public ChatItemViewModel? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (_isSettingSelectedChat) return;
            _isSettingSelectedChat = true;
            try { this.RaiseAndSetIfChanged(ref _selectedChat, value); }
            finally { _isSettingSelectedChat = false; }
        }
    }
    [Reactive] public partial int ArchivedChatsCount { get; set; }
    [Reactive] public partial int AgentChatsCount { get; set; }
    [Reactive] public partial bool ShowArchivedSection { get; set; }
    [Reactive] public partial bool ShowAgentsSection { get; set; }
    [Reactive] public partial string SearchText { get; set; } = string.Empty;
    [Reactive] public partial bool IsLoading { get; set; }
    [Reactive] public partial int PendingInviteCount { get; set; }
    [Reactive] public partial int SkippedInviteCount { get; set; }
    [Reactive] public partial string? StatusMessage { get; set; }

    // New Chat Dialog (unified: 1+ participants, optional name)
    [Reactive] public partial bool ShowNewChatDialog { get; set; }
    [Reactive] public partial string NewChatName { get; set; } = string.Empty;
    [Reactive] public partial string NewChatDescription { get; set; } = string.Empty;
    [Reactive] public partial string NewChatParticipantInput { get; set; } = string.Empty;
    [Reactive] public partial bool IsParticipantInputInvalid { get; set; }
    public ObservableCollection<FollowContactViewModel> NewChatParticipants { get; } = new();
    [Reactive] public partial string? NewChatError { get; set; }
    [Reactive] public partial bool IsLookingUpKeyPackages { get; set; }
    [Reactive] public partial string? KeyPackageStatus { get; set; }
    [Reactive] public partial string? CreateProgress { get; set; }

    // Relay Selection
    public ObservableCollection<RelaySelectionItemViewModel> SelectableRelays { get; } = new();
    [Reactive] public partial int SelectedRelayCount { get; set; }

    // Join Group Dialog
    [Reactive] public partial bool ShowJoinGroupDialog { get; set; }
    [Reactive] public partial string JoinGroupId { get; set; } = string.Empty;
    [Reactive] public partial string? JoinGroupError { get; set; }

    // Add Bot / DM Dialog
    [Reactive] public partial bool ShowAddBotDialog { get; set; }
    [Reactive] public partial string BotNpub { get; set; } = string.Empty;
    [Reactive] public partial string? AddBotError { get; set; }

    // Relay selection for bot chat
    [Reactive] public partial bool BotRelayModeNip65 { get; set; } = true;
    [Reactive] public partial bool BotRelayModeList { get; set; }
    [Reactive] public partial bool BotRelayModeManual { get; set; }
    [Reactive] public partial string BotManualRelay { get; set; } = string.Empty;
    [Reactive] public partial bool IsFetchingNip65 { get; set; }
    [Reactive] public partial string? Nip65Status { get; set; }
    public ObservableCollection<RelayCheckItem> BotAvailableRelays { get; } = new();
    public ObservableCollection<RelayCheckItem> BotNip65Relays { get; } = new();

    // Delete Chat Dialog
    [Reactive] public partial bool ShowDeleteChatDialog { get; set; }
    [Reactive] public partial ChatItemViewModel? ChatToDelete { get; set; }

    // Reset Group Dialog
    [Reactive] public partial bool ShowResetGroupDialog { get; set; }
    [Reactive] public partial ChatItemViewModel? GroupToReset { get; set; }

    // Rename Chat Dialog
    [Reactive] public partial bool ShowRenameChatDialog { get; set; }
    [Reactive] public partial ChatItemViewModel? ChatToRename { get; set; }
    [Reactive] public partial string RenameChatInput { get; set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> NewChatCommand { get; }
    public ReactiveCommand<Unit, Unit> JoinGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelNewChatCommand { get; }
    public ReactiveCommand<Unit, Unit> AddChatParticipantCommand { get; }
    public ReactiveCommand<string, Unit> AddContactToChatCommand { get; }
    public ReactiveCommand<FollowContactViewModel, Unit> RemoveChatParticipantCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmJoinGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelJoinGroupCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> DeleteChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmDeleteChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ResetGroupCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> RenameChatCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshFollowingCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmRenameChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelRenameChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ArchiveChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> UnarchiveChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleArchivedViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAgentsViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmResetGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelResetGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> LookupKeyPackagesCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissSkippedInviteNoticeCommand { get; }
    public ReactiveCommand<PendingInviteItemViewModel, Unit> AcceptInviteCommand { get; }
    public ReactiveCommand<PendingInviteItemViewModel, Unit> DeclineInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> RescanInvitesCommand { get; }
    [Reactive] public partial bool IsRescanningInvites { get; set; }
    public ReactiveCommand<Unit, Unit> AddBotCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateBotChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddBotCommand { get; }
    public ReactiveCommand<Unit, Unit> LookupBotNip65Command { get; }

    public ChatListViewModel(IMessageService messageService, IStorageService storageService, IMlsService? mlsService = null, INostrService? nostrService = null)
    {
        _logger = LoggingConfiguration.CreateLogger<ChatListViewModel>();
        _messageService = messageService;
        _storageService = storageService;
        _mlsService = mlsService;
        _nostrService = nostrService;

        NewChatCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening new chat dialog");
            NewChatName = string.Empty;
            NewChatDescription = string.Empty;
            NewChatParticipantInput = string.Empty;
            NewChatParticipants.Clear();
            NewChatError = null;
            KeyPackageStatus = null;
            CreateProgress = null;
            PopulateSelectableRelays();
            ShowNewChatDialog = true;
        });

        AddChatParticipantCommand = ReactiveCommand.Create(() =>
        {
            TryAddChatParticipant(NewChatParticipantInput);
            NewChatParticipantInput = string.Empty;
        });

        AddContactToChatCommand = ReactiveCommand.Create<string>(pubkeyHex => TryAddChatParticipant(pubkeyHex));

        RemoveChatParticipantCommand = ReactiveCommand.Create<FollowContactViewModel>(p =>
        {
            if (p != null) NewChatParticipants.Remove(p);
        });

        JoinGroupCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening join group dialog");
            JoinGroupId = string.Empty;
            JoinGroupError = null;
            ShowJoinGroupDialog = true;
        });

        var canJoinGroup = this.WhenAnyValue(
            x => x.JoinGroupId,
            id => !string.IsNullOrWhiteSpace(id) && id.Length >= 8);

        ConfirmJoinGroupCommand = ReactiveCommand.CreateFromTask(JoinGroupAsync, canJoinGroup);

        CancelJoinGroupCommand = ReactiveCommand.Create(() =>
        {
            ShowJoinGroupDialog = false;
            JoinGroupError = null;
        });

        AddBotCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening link device dialog");
            BotNpub = string.Empty;
            AddBotError = null;
            BotRelayModeNip65 = true;
            BotRelayModeList = false;
            BotRelayModeManual = false;
            BotManualRelay = string.Empty;
            Nip65Status = null;
            IsFetchingNip65 = false;
            BotNip65Relays.Clear();
            BotAvailableRelays.Clear();
            if (_nostrService != null)
            {
                var relays = _nostrService.ConnectedRelayUrls.Count > 0
                    ? _nostrService.ConnectedRelayUrls
                    : _nostrService.ConfiguredRelayUrls.Count > 0
                        ? _nostrService.ConfiguredRelayUrls
                        : (IReadOnlyList<string>)Core.NostrConstants.DefaultRelays;
                foreach (var url in relays)
                    BotAvailableRelays.Add(new RelayCheckItem(url, true));
            }
            ShowAddBotDialog = true;
        });

        var canCreateBot = this.WhenAnyValue(
            x => x.BotNpub,
            npub => !string.IsNullOrWhiteSpace(npub) && npub.Trim().Length > 5);

        CreateBotChatCommand = ReactiveCommand.CreateFromTask(CreateBotChatAsync, canCreateBot);

        CancelAddBotCommand = ReactiveCommand.Create(() =>
        {
            ShowAddBotDialog = false;
            AddBotError = null;
        });

        LookupBotNip65Command = ReactiveCommand.CreateFromTask(LookupBotNip65Async);

        DeleteChatCommand = ReactiveCommand.Create<ChatItemViewModel>(chat =>
        {
            _logger.LogInformation("Requesting delete for chat: {ChatId} - {ChatName}", chat.Id, chat.Name);
            ChatToDelete = chat;
            ShowDeleteChatDialog = true;
        });

        ConfirmDeleteChatCommand = ReactiveCommand.CreateFromTask(DeleteChatAsync);

        CancelDeleteChatCommand = ReactiveCommand.Create(() =>
        {
            ShowDeleteChatDialog = false;
            ChatToDelete = null;
        });

        ResetGroupCommand = ReactiveCommand.Create<ChatItemViewModel>(chat =>
        {
            _logger.LogInformation("Requesting reset for group: {ChatId} - {ChatName}", chat.Id, chat.Name);
            GroupToReset = chat;
            ShowResetGroupDialog = true;
        });

        ConfirmResetGroupCommand = ReactiveCommand.CreateFromTask(ResetGroupAsync);

        CancelResetGroupCommand = ReactiveCommand.Create(() =>
        {
            ShowResetGroupDialog = false;
            GroupToReset = null;
        });

        RenameChatCommand = ReactiveCommand.Create<ChatItemViewModel>(chat =>
        {
            _logger.LogInformation("Opening rename dialog for chat: {ChatId} - {ChatName}", chat.Id, chat.Name);
            ChatToRename = chat;
            RenameChatInput = chat.Name;
            ShowRenameChatDialog = true;
        });

        ConfirmRenameChatCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var chat = ChatToRename;
            var newName = RenameChatInput?.Trim() ?? string.Empty;
            if (chat != null && !string.IsNullOrEmpty(newName) && newName != chat.Name)
            {
                await RenameChatAsync(chat, newName);
            }
            ShowRenameChatDialog = false;
            ChatToRename = null;
            RenameChatInput = string.Empty;
        });

        CancelRenameChatCommand = ReactiveCommand.Create(() =>
        {
            ShowRenameChatDialog = false;
            ChatToRename = null;
            RenameChatInput = string.Empty;
        });

        // Filter Following list and validate npub/nprofile (with optional nostr: URI prefix) as the user types
        this.WhenAnyValue(x => x.NewChatParticipantInput)
            .Throttle(TimeSpan.FromMilliseconds(120))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(q =>
            {
                var query = (q ?? string.Empty).Trim();
                var stripped = query.StartsWith("nostr:", StringComparison.OrdinalIgnoreCase)
                    ? query["nostr:".Length..]
                    : query;

                bool isFullNpub = stripped.StartsWith("npub1", StringComparison.OrdinalIgnoreCase) && stripped.Length >= 60;
                bool isFullNprofile = stripped.StartsWith("nprofile1", StringComparison.OrdinalIgnoreCase) && stripped.Length >= 60;

                // Validate: mark invalid if it looks like a complete bech32 entity but fails to decode.
                if (isFullNpub || isFullNprofile)
                {
                    IsParticipantInputInvalid = !Core.Crypto.Nip19.TryDecodePubkey(query, out _);
                }
                else if (query.Length == 64 && query.All(Uri.IsHexDigit))
                {
                    IsParticipantInputInvalid = false;
                }
                else
                {
                    IsParticipantInputInvalid = false;
                }

                foreach (var f in Following)
                {
                    if (isFullNpub)
                    {
                        f.IsVisible = string.Equals(f.Npub, stripped, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.IsNullOrEmpty(query))
                    {
                        f.IsVisible = true;
                    }
                    else
                    {
                        f.IsVisible = (f.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (f.Petname?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                            || f.Npub.Contains(query, StringComparison.OrdinalIgnoreCase);
                    }
                }
            });

        RefreshFollowingCommand = ReactiveCommand.CreateFromTask(async () => await RefreshFollowingAsync());

        ArchiveChatCommand = ReactiveCommand.CreateFromTask<ChatItemViewModel>(async chat =>
        {
            _logger.LogInformation("Archiving chat: {ChatId} - {Name}", chat.Id, chat.Name);
            await _messageService.ArchiveChatAsync(chat.Id);
            Chats.Remove(chat);
            ArchivedChats.Add(chat);
            ArchivedChatsCount = ArchivedChats.Count;
        });

        UnarchiveChatCommand = ReactiveCommand.CreateFromTask<ChatItemViewModel>(async chat =>
        {
            _logger.LogInformation("Unarchiving chat: {ChatId} - {Name}", chat.Id, chat.Name);
            await _messageService.UnarchiveChatAsync(chat.Id);
            ArchivedChats.Remove(chat);
            Chats.Add(chat);
            ArchivedChatsCount = ArchivedChats.Count;
        });

        ToggleArchivedViewCommand = ReactiveCommand.Create(() =>
        {
            ShowArchivedSection = !ShowArchivedSection;
            if (ShowArchivedSection) ShowAgentsSection = false;
        });

        ToggleAgentsViewCommand = ReactiveCommand.Create(() =>
        {
            ShowAgentsSection = !ShowAgentsSection;
            if (ShowAgentsSection) ShowArchivedSection = false;
        });

        ToggleMuteCommand = ReactiveCommand.CreateFromTask<ChatItemViewModel>(async chat =>
        {
            var newMuted = !chat.IsMuted;
            _logger.LogInformation("{Action} chat: {ChatId} - {Name}",
                newMuted ? "Muting" : "Unmuting", chat.Id, chat.Name);
            if (newMuted)
                await _messageService.MuteChatAsync(chat.Id);
            else
                await _messageService.UnmuteChatAsync(chat.Id);
            chat.IsMuted = newMuted;
        });

        var canLookupKeyPackages = this.WhenAnyValue(x => x.IsLookingUpKeyPackages)
            .Select(looking => !looking && NewChatParticipants.Count > 0);

        LookupKeyPackagesCommand = ReactiveCommand.CreateFromTask(LookupKeyPackagesAsync, canLookupKeyPackages);

        AcceptInviteCommand = ReactiveCommand.CreateFromTask<PendingInviteItemViewModel>(AcceptInviteAsync);
        DeclineInviteCommand = ReactiveCommand.CreateFromTask<PendingInviteItemViewModel>(DeclineInviteAsync);

        var canRescan = this.WhenAnyValue(x => x.IsRescanningInvites, scanning => !scanning);
        RescanInvitesCommand = ReactiveCommand.CreateFromTask(RescanInvitesAsync, canRescan);

        var participantsChanged = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => NewChatParticipants.CollectionChanged += h,
                h => NewChatParticipants.CollectionChanged -= h)
            .Select(_ => Unit.Default)
            .StartWith(Unit.Default);

        var canCreateChat = Observable.CombineLatest(
            this.WhenAnyValue(x => x.SelectedRelayCount),
            participantsChanged,
            (relayCount, _) => relayCount > 0 && NewChatParticipants.Count > 0);

        CreateChatCommand = ReactiveCommand.CreateFromTask(CreateNewChatAsync, canCreateChat);

        CancelNewChatCommand = ReactiveCommand.Create(() =>
        {
            ShowNewChatDialog = false;
            NewChatError = null;
            CreateProgress = null;
        });

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadChatsAsync);

        // Subscribe to chat updates
        _chatUpdateSubscription = _messageService.ChatUpdates
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnChatUpdated);

        // Subscribe to new invites
        _inviteSubscription = _messageService.NewInvites
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnNewInvite);

        // Subscribe to MLS decryption errors
        _decryptionErrorSubscription = _messageService.DecryptionErrors
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(error =>
            {
                StatusMessage = $"Failed to decrypt message in \"{error.ChatName}\". Group may need reset.";
            });

        // Subscribe to skipped invite notifications
        _skippedInviteSubscription = _messageService.SkippedInvites
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => SkippedInviteCount++);

        DismissSkippedInviteNoticeCommand = ReactiveCommand.CreateFromTask(DismissSkippedInviteNoticeAsync);

        // Filter chats based on search
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter());
    }

    public async Task LoadChatsAsync()
    {
        IsLoading = true;

        try
        {
            // Cache current user pubkey for self-invite validation
            var currentUser = await _storageService.GetCurrentUserAsync();
            _currentUserPubKeyHex = currentUser?.PublicKeyHex;
            _logger.LogInformation("LoadChats: currentUserPubKey={PubKey}",
                _currentUserPubKeyHex?[..Math.Min(16, _currentUserPubKeyHex?.Length ?? 0)] ?? "null");

            var chats = await _messageService.GetChatsAsync();
            _logger.LogInformation("LoadChats: {Total} chats loaded from DB (IsArchived=0)", chats.Count());

            Chats.Clear();
            AgentChats.Clear();
            foreach (var chat in chats
                .OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.LastActivityAt))
            {
                var isParticipant = _currentUserPubKeyHex == null ||
                    chat.ParticipantPublicKeys.Any(p => string.Equals(p, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase));

                if (!isParticipant)
                {
                    _logger.LogWarning("LoadChats: chat {ChatId} '{ChatName}' (type={Type}) has {Count} participants but current user NOT found. Participants: [{Participants}]. Marking as orphaned.",
                        chat.Id[..Math.Min(8, chat.Id.Length)], chat.Name, chat.Type,
                        chat.ParticipantPublicKeys.Count,
                        string.Join(", ", chat.ParticipantPublicKeys.Select(p => p[..Math.Min(16, p.Length)])));
                }

                var chatItem = new ChatItemViewModel(chat);
                chatItem.NeedsRepair = !isParticipant;

                // Resolve avatar: other participant (DM/bot), or group image / creator / admin (group)
                if (_currentUserPubKeyHex != null)
                {
                    var resolved = await ResolveCachedAvatarAsync(chat);
                    if (resolved != null) chatItem.LocalAvatarPath = resolved;
                }

                if (chat.Type == Core.Models.ChatType.Bot)
                    AgentChats.Add(chatItem);
                else if (chat.Type == Core.Models.ChatType.DeviceSync)
                {
                    // Always cache the device-sync chat item so visibility can be toggled live
                    _deviceSyncChatItem = chatItem;
                    var showSync = await _storageService.GetSettingAsync("show_device_sync_chat");
                    if (showSync != "false") // default: visible
                        Chats.Insert(0, chatItem); // always at top (pinned)
                }
                else
                    Chats.Add(chatItem);
            }
            AgentChatsCount = AgentChats.Count;

            // Load archived chats
            var archivedChats = await _storageService.GetArchivedChatsAsync();
            ArchivedChats.Clear();
            foreach (var chat in archivedChats.OrderByDescending(c => c.LastActivityAt))
            {
                ArchivedChats.Add(new ChatItemViewModel(chat));
            }
            ArchivedChatsCount = ArchivedChats.Count;
            _logger.LogInformation("LoadChats: {Count} archived chats loaded", ArchivedChatsCount);

            // Load pending invites
            var invites = await _messageService.GetPendingInvitesAsync();
            PendingInvites.Clear();
            foreach (var invite in invites)
            {
                PendingInvites.Add(new PendingInviteItemViewModel(invite));
            }
            PendingInviteCount = PendingInvites.Count;
            SkippedInviteCount = await _storageService.GetSkippedInviteCountAsync();

            // Kick off background avatar refresh for DM chats missing profile images
            _ = RefreshAvatarsAsync();

            // Seed contacts table from every participant we know about (DM partners + group members)
            if (_currentUserPubKeyHex != null)
            {
                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var contacts = new List<Contact>();
                    foreach (var chat in chats.Concat(archivedChats))
                    {
                        foreach (var pk in chat.ParticipantPublicKeys)
                        {
                            if (string.IsNullOrEmpty(pk)) continue;
                            if (string.Equals(pk, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!seen.Add(pk)) continue;
                            contacts.Add(new Contact
                            {
                                PublicKeyHex = pk,
                                Source = "group",
                                LastInteractedAt = chat.LastActivityAt
                            });
                        }
                    }
                    if (contacts.Count > 0)
                        await _storageService.UpsertContactsAsync(_currentUserPubKeyHex, contacts);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to seed contacts from chats");
                }
            }

            // Load cached follows + refresh from relays in background
            await LoadFollowingFromCacheAsync();
            _ = RefreshFollowingAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Adds or removes the device-sync ("Private Notes") chat from the visible chat list
    /// without requiring a full reload.
    /// </summary>
    public void SetDeviceSyncChatVisibility(bool show)
    {
        if (_deviceSyncChatItem == null) return;

        var alreadyShown = Chats.Contains(_deviceSyncChatItem);
        if (show && !alreadyShown)
            Chats.Insert(0, _deviceSyncChatItem);
        else if (!show && alreadyShown)
            Chats.Remove(_deviceSyncChatItem);
    }

    private static string AvatarCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Scramble", "avatars");

    private static string? FindGroupAvatarCachePath(string chatId)
    {
        if (!Directory.Exists(AvatarCacheDir)) return null;
        var prefix = $"group_{chatId[..Math.Min(16, chatId.Length)]}.";
        return Directory.EnumerateFiles(AvatarCacheDir)
            .FirstOrDefault(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveCachedAvatarAsync(Chat chat)
    {
        // DeviceSync (Private Notes) is a self-chat — always use the current user's own avatar.
        if (chat.Type == ChatType.DeviceSync)
        {
            if (_currentUserPubKeyHex == null) return null;
            var self = await _storageService.GetUserByPublicKeyAsync(_currentUserPubKeyHex);
            return self?.LocalAvatarPath is { } sp && File.Exists(sp) ? sp : null;
        }

        if (chat.Type != ChatType.Group)
        {
            var otherPubKey = chat.ParticipantPublicKeys.FirstOrDefault(
                p => !string.Equals(p, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase));
            if (otherPubKey == null) return null;
            var otherUser = await _storageService.GetUserByPublicKeyAsync(otherPubKey);
            return otherUser?.LocalAvatarPath is { } p && File.Exists(p) ? p : null;
        }

        var groupPath = FindGroupAvatarCachePath(chat.Id);
        if (groupPath != null) return groupPath;

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(chat.CreatorPublicKey)) candidates.Add(chat.CreatorPublicKey);
        foreach (var a in chat.AdminPublicKeys)
            if (!candidates.Contains(a, StringComparer.OrdinalIgnoreCase)) candidates.Add(a);

        foreach (var key in candidates)
        {
            var user = await _storageService.GetUserByPublicKeyAsync(key);
            if (user?.LocalAvatarPath is { } p && File.Exists(p)) return p;
        }
        return null;
    }

    private async Task<string?> DownloadAvatarToCacheAsync(string imageUrl, string cacheKey)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var avatarUri) ||
            (avatarUri.Scheme != "https" && avatarUri.Scheme != "http"))
            return null;
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(avatarUri.Host);
            foreach (var addr in addresses)
            {
                if (System.Net.IPAddress.IsLoopback(addr)) return null;
                var b = addr.GetAddressBytes();
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) || b[0] == 127 ||
                     (b[0] == 169 && b[1] == 254) || b[0] == 0))
                    return null;
            }
        }
        catch { return null; }

        var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
        if (imageBytes.Length > 2 * 1024 * 1024)
        {
            _logger.LogWarning("Avatar too large for {Key}: {Size} bytes", cacheKey, imageBytes.Length);
            return null;
        }

        var ext = ".jpg";
        var urlPath = avatarUri.AbsolutePath;
        if (urlPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ext = ".png";
        else if (urlPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) ext = ".webp";
        else if (urlPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) ext = ".gif";

        Directory.CreateDirectory(AvatarCacheDir);
        var localPath = Path.Combine(AvatarCacheDir, $"{cacheKey}{ext}");
        await File.WriteAllBytesAsync(localPath, imageBytes);
        return localPath;
    }

    private async Task RefreshAvatarsAsync()
    {
        try
        {
        if (_nostrService == null || _currentUserPubKeyHex == null) return;

        Directory.CreateDirectory(AvatarCacheDir);

        await RefreshGroupAvatarsAsync();

        // Collect DM/bot chats that need avatar refresh
        var chatsToRefresh = Chats
            .Where(c => !c.IsGroup && string.IsNullOrEmpty(c.LocalAvatarPath))
            .ToList();

        foreach (var chatItem in chatsToRefresh)
        {
            try
            {
                var otherPubKey = chatItem.Chat.ParticipantPublicKeys
                    .FirstOrDefault(p => p != _currentUserPubKeyHex);
                if (otherPubKey == null) continue;

                // Check if user already has a cached avatar we missed
                var existingUser = await _storageService.GetUserByPublicKeyAsync(otherPubKey);
                if (existingUser?.LocalAvatarPath != null && File.Exists(existingUser.LocalAvatarPath))
                {
                    RxSchedulers.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = existingUser.LocalAvatarPath);
                    continue;
                }

                // Use cache-first metadata fetch: returns DB/memory cache immediately,
                // fires a background relay refresh that emits MetadataUpdated if changed.
                var metadata = await _messageService.GetCachedOrFetchProfileAsync(otherPubKey);
                if (string.IsNullOrEmpty(metadata?.Picture)) continue;

                var localPath = await DownloadAvatarToCacheAsync(metadata.Picture, otherPubKey[..16]);
                if (localPath == null) continue;

                // Update user record in DB
                if (existingUser != null)
                {
                    existingUser.AvatarUrl = metadata.Picture;
                    existingUser.LocalAvatarPath = localPath;
                    existingUser.DisplayName = metadata.GetDisplayName();
                    existingUser.LastUpdatedAt = DateTime.UtcNow;
                    await _storageService.SaveUserAsync(existingUser);
                }

                // Update the chat item on the UI thread
                RxSchedulers.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = localPath);

                _logger.LogInformation("Avatar cached for {PubKey}: {Path}", otherPubKey[..16], localPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh avatar for chat {ChatId}", chatItem.Id[..Math.Min(8, chatItem.Id.Length)]);
            }
        }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshAvatarsAsync failed");
        }
    }

    private async Task LoadFollowingFromCacheAsync()
    {
        if (_currentUserPubKeyHex == null) return;

        try
        {
            var cached = await _storageService.GetContactsAsync(_currentUserPubKeyHex);
            Following.Clear();
            foreach (var c in cached)
            {
                var user = await _storageService.GetUserByPublicKeyAsync(c.PublicKeyHex);
                var npub = user?.Npub ?? Bech32.Encode("npub", Convert.FromHexString(c.PublicKeyHex));
                Following.Add(new FollowContactViewModel(
                    c.PublicKeyHex, npub, c.Petname,
                    user?.DisplayName,
                    user?.LocalAvatarPath is { } p && File.Exists(p) ? p : null));
            }
            _logger.LogInformation("Loaded {Count} cached contacts", Following.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cached follows");
        }
    }

    private async Task RefreshFollowingAsync()
    {
        if (_nostrService == null || _currentUserPubKeyHex == null) return;

        IsRefreshingFollowing = true;
        try
        {
            var follows = await _nostrService.FetchFollowingListAsync(_currentUserPubKeyHex);
            if (follows.Count == 0)
            {
                _logger.LogInformation("No remote follow list found");
                return;
            }

            await _storageService.SaveFollowsAsync(_currentUserPubKeyHex, follows);

            // Re-read the merged Contacts set (follows + chat/group sources) so
            // refresh doesn't drop contacts that came from chats but aren't in NIP-02.
            var merged = await _storageService.GetContactsAsync(_currentUserPubKeyHex);
            var users = new Dictionary<string, User?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in merged)
                users[c.PublicKeyHex] = await _storageService.GetUserByPublicKeyAsync(c.PublicKeyHex);

            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                var existing = Following.ToDictionary(f => f.PublicKeyHex, StringComparer.OrdinalIgnoreCase);
                Following.Clear();
                foreach (var c in merged)
                {
                    if (existing.TryGetValue(c.PublicKeyHex, out var vm))
                    {
                        vm.Petname = c.Petname ?? vm.Petname;
                        Following.Add(vm);
                    }
                    else
                    {
                        var user = users.GetValueOrDefault(c.PublicKeyHex);
                        var npub = user?.Npub ?? Bech32.Encode("npub", Convert.FromHexString(c.PublicKeyHex));
                        Following.Add(new FollowContactViewModel(
                            c.PublicKeyHex, npub, c.Petname,
                            user?.DisplayName,
                            user?.LocalAvatarPath is { } p && File.Exists(p) ? p : null));
                    }
                }
            });

            _ = RefreshFollowingMetadataAsync(merged.Select(c => c.PublicKeyHex).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh following list");
        }
        finally
        {
            IsRefreshingFollowing = false;
        }
    }

    private async Task RefreshFollowingMetadataAsync(List<string> pubKeys)
    {
        try
        {
        if (_nostrService == null) return;

        Directory.CreateDirectory(AvatarCacheDir);

        // Limit concurrency so we don't hammer relays
        using var gate = new SemaphoreSlim(4);
        var tasks = pubKeys.Select(async pubKey =>
        {
            await gate.WaitAsync();
            try
            {
                var existing = await _storageService.GetUserByPublicKeyAsync(pubKey);
                UserMetadata? metadata = null;

                if (existing == null || string.IsNullOrEmpty(existing.DisplayName))
                {
                    metadata = await _nostrService.FetchUserMetadataAsync(pubKey);
                }

                string? localPath = existing?.LocalAvatarPath is { } p && File.Exists(p) ? p : null;
                if (localPath == null && !string.IsNullOrEmpty(metadata?.Picture))
                {
                    localPath = await DownloadAvatarToCacheAsync(metadata.Picture, pubKey[..16]);
                }

                if (metadata != null || localPath != null)
                {
                    var user = existing ?? new User
                    {
                        Id = pubKey,
                        PublicKeyHex = pubKey,
                        Npub = Bech32.Encode("npub", Convert.FromHexString(pubKey)),
                        CreatedAt = DateTime.UtcNow
                    };
                    if (metadata != null)
                    {
                        user.DisplayName = metadata.GetDisplayName();
                        user.AvatarUrl = metadata.Picture ?? user.AvatarUrl;
                    }
                    if (localPath != null) user.LocalAvatarPath = localPath;
                    user.LastUpdatedAt = DateTime.UtcNow;
                    await _storageService.SaveUserAsync(user);

                    RxSchedulers.MainThreadScheduler.Schedule(() =>
                    {
                        var vm = Following.FirstOrDefault(f => string.Equals(f.PublicKeyHex, pubKey, StringComparison.OrdinalIgnoreCase));
                        if (vm != null)
                        {
                            if (metadata != null) vm.DisplayName = metadata.GetDisplayName();
                            if (localPath != null) vm.LocalAvatarPath = localPath;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Metadata refresh failed for {Pub}", pubKey[..16]);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        _logger.LogInformation("Background metadata refresh complete for {Count} follows", pubKeys.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshFollowingMetadataAsync failed");
        }
    }

    private async Task RefreshGroupAvatarsAsync()
    {
        if (_nostrService == null) return;

        var groupsToRefresh = Chats
            .Where(c => c.IsGroup && string.IsNullOrEmpty(c.LocalAvatarPath))
            .ToList();

        foreach (var chatItem in groupsToRefresh)
        {
            try
            {
                var chat = chatItem.Chat;
                var cacheKey = $"group_{chat.Id[..Math.Min(16, chat.Id.Length)]}";

                // 1. Group image set on the chat itself
                if (!string.IsNullOrEmpty(chat.AvatarUrl))
                {
                    var localPath = await DownloadAvatarToCacheAsync(chat.AvatarUrl, cacheKey);
                    if (localPath != null)
                    {
                        RxSchedulers.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = localPath);
                        continue;
                    }
                }

                // 2. Fall back to creator / admin profile picture
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(chat.CreatorPublicKey)) candidates.Add(chat.CreatorPublicKey);
                foreach (var a in chat.AdminPublicKeys)
                    if (!candidates.Contains(a, StringComparer.OrdinalIgnoreCase)) candidates.Add(a);

                foreach (var pubKey in candidates)
                {
                    var user = await _storageService.GetUserByPublicKeyAsync(pubKey);
                    if (user?.LocalAvatarPath is { } cached && File.Exists(cached))
                    {
                        RxSchedulers.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = cached);
                        break;
                    }

                    var metadata = await _nostrService.FetchUserMetadataAsync(pubKey);
                    if (string.IsNullOrEmpty(metadata?.Picture)) continue;

                    var localPath = await DownloadAvatarToCacheAsync(metadata.Picture, pubKey[..16]);
                    if (localPath == null) continue;

                    if (user != null)
                    {
                        user.AvatarUrl = metadata.Picture;
                        user.LocalAvatarPath = localPath;
                        user.DisplayName = metadata.GetDisplayName();
                        user.LastUpdatedAt = DateTime.UtcNow;
                        await _storageService.SaveUserAsync(user);
                    }

                    RxSchedulers.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = localPath);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh group avatar for chat {ChatId}", chatItem.Id[..Math.Min(8, chatItem.Id.Length)]);
            }
        }
    }

    public void ClearChats()
    {
        Chats.Clear();
        SelectedChat = null;
    }

    private void TryAddChatParticipant(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        var raw = input.Trim();

        // Strip the optional nostr: URI scheme so we can pattern-match the entity prefix.
        var stripped = raw.StartsWith("nostr:", StringComparison.OrdinalIgnoreCase) ? raw["nostr:".Length..] : raw;

        string publicKeyHex;
        if (stripped.StartsWith("npub1", StringComparison.OrdinalIgnoreCase) ||
            stripped.StartsWith("nprofile1", StringComparison.OrdinalIgnoreCase))
        {
            // Single decoder handles npub, nprofile, and any nostr: prefix variant; relay hints
            // (from nprofile TLV type 1) are intentionally discarded here for now — capturing them
            // is the next step once the basic add flow is verified.
            if (!Core.Crypto.Nip19.TryDecodePubkey(raw, out var decoded))
            {
                NewChatError = stripped.StartsWith("nprofile", StringComparison.OrdinalIgnoreCase)
                    ? "Invalid nprofile"
                    : "Invalid npub";
                return;
            }
            publicKeyHex = decoded!.PubkeyHex;
        }
        else if (raw.Length == 64 && raw.All(Uri.IsHexDigit))
        {
            publicKeyHex = raw.ToLowerInvariant();
        }
        else
        {
            NewChatError = "Enter an npub, nprofile, or 64-char hex pubkey";
            return;
        }

        if (string.Equals(publicKeyHex, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase))
        {
            NewChatError = "You're already a member — no need to add yourself";
            return;
        }

        if (NewChatParticipants.Any(p => string.Equals(p.PublicKeyHex, publicKeyHex, StringComparison.OrdinalIgnoreCase)))
        {
            NewChatError = null;
            return;
        }

        var existing = Following.FirstOrDefault(f => string.Equals(f.PublicKeyHex, publicKeyHex, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            NewChatParticipants.Add(new FollowContactViewModel(
                existing.PublicKeyHex, existing.Npub, existing.Petname, existing.DisplayName, existing.LocalAvatarPath));
        }
        else
        {
            var npub = Bech32.Encode("npub", Convert.FromHexString(publicKeyHex));
            NewChatParticipants.Add(new FollowContactViewModel(publicKeyHex, npub, null, null, null));
        }

        NewChatError = null;

        // Auto-trigger key package lookup when a participant is added
        RxSchedulers.MainThreadScheduler.Schedule(() =>
            LookupKeyPackagesCommand.Execute().Subscribe());
    }

    private async Task LookupKeyPackagesAsync()
    {
        if (NewChatParticipants.Count == 0 || _nostrService == null)
        {
            return;
        }

        _logger.LogInformation("Looking up KeyPackages for chat participants");

        IsLookingUpKeyPackages = true;
        KeyPackageStatus = null;

        try
        {
            var foundCount = 0;
            var notFoundCount = 0;

            foreach (var participant in NewChatParticipants.ToList())
            {
                participant.IsLookingUpKey = true;
                participant.KeyPackageStatusText = null;

                try
                {
                    var keyPackages = await _nostrService.FetchKeyPackagesAsync(participant.PublicKeyHex);
                    var keyPackageList = keyPackages.ToList();

                    if (keyPackageList.Count > 0)
                    {
                        var latest = keyPackageList.OrderByDescending(k => k.CreatedAt).First();
                        participant.KeyPackageStatusText = $"KeyPackage found (created {latest.CreatedAt:g})";
                        foundCount++;
                    }
                    else
                    {
                        participant.KeyPackageStatusText = "No KeyPackage found";
                        notFoundCount++;
                    }
                }
                catch (Exception ex)
                {
                    participant.KeyPackageStatusText = $"Lookup failed: {ex.Message}";
                    notFoundCount++;
                }
                finally
                {
                    participant.IsLookingUpKey = false;
                }
            }

            KeyPackageStatus = $"Found: {foundCount}, Missing: {notFoundCount}";
            _logger.LogInformation("KeyPackage lookup complete: {Found} found, {Missing} missing", foundCount, notFoundCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lookup KeyPackages");
            KeyPackageStatus = $"Lookup failed: {ex.Message}";
        }
        finally
        {
            IsLookingUpKeyPackages = false;
        }
    }

    private void PopulateSelectableRelays()
    {
        SelectableRelays.Clear();

        if (_nostrService == null) return;

        // Fallback chain: connected relays > configured relays > default relays
        // This ensures users can still select relays even if connections haven't
        // been established yet (matches the bot dialog pattern).
        var connectedRelays = _nostrService.ConnectedRelayUrls;
        var configuredRelays = _nostrService.ConfiguredRelayUrls;
        var relayUrls = connectedRelays?.Count > 0
            ? (IReadOnlyList<string>)connectedRelays
            : configuredRelays?.Count > 0
                ? (IReadOnlyList<string>)configuredRelays
                : (IReadOnlyList<string>)Core.NostrConstants.DefaultRelays;

        foreach (var url in relayUrls)
        {
            SelectableRelays.Add(new RelaySelectionItemViewModel(url, isSelected: true));
        }

        SelectedRelayCount = SelectableRelays.Count;

        // Subscribe after all items are added to avoid triggering during construction
        foreach (var item in SelectableRelays)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RelaySelectionItemViewModel.IsSelected))
                    SelectedRelayCount = SelectableRelays.Count(r => r.IsSelected);
            };
        }
    }

    private string[] GetSelectedRelayUrls()
    {
        return SelectableRelays
            .Where(r => r.IsSelected)
            .Select(r => r.Url)
            .ToArray();
    }

    private async Task CreateNewChatAsync()
    {
        if (NewChatParticipants.Count == 0)
        {
            NewChatError = "Add at least one participant";
            return;
        }

        if (_mlsService == null || _nostrService == null)
        {
            NewChatError = "MLS or Nostr service not available";
            _logger.LogWarning("CreateNewChat: MLS or Nostr service is null");
            return;
        }

        var participantKeys = NewChatParticipants.Select(p => p.PublicKeyHex).ToList();
        _logger.LogInformation("Creating new chat with {Count} participant(s)", participantKeys.Count);

        try
        {
            var selectedRelays = GetSelectedRelayUrls();
            if (selectedRelays.Length == 0)
            {
                NewChatError = "Select at least one relay";
                return;
            }

            var currentUser = await _storageService.GetCurrentUserAsync();
            if (currentUser == null || string.IsNullOrEmpty(currentUser.PublicKeyHex))
            {
                NewChatError = "Not logged in";
                return;
            }

            // Resolve chat name: user-provided > (1 participant) profile metadata > fallback
            CreateProgress = "Resolving identity...";
            var chatName = NewChatName.Trim();
            if (string.IsNullOrEmpty(chatName) && participantKeys.Count == 1)
            {
                try
                {
                    var metadata = await _messageService.GetCachedOrFetchProfileAsync(participantKeys[0]);
                    chatName = metadata?.DisplayName ?? metadata?.Name ?? string.Empty;
                }
                catch { /* metadata fetch is optional */ }
            }
            if (string.IsNullOrEmpty(chatName))
            {
                chatName = participantKeys.Count == 1
                    ? $"Chat with {participantKeys[0][..Math.Min(12, participantKeys[0].Length)]}..."
                    : $"Chat ({participantKeys.Count + 1} members)";
            }

            CreateProgress = "Creating MLS group...";
            var groupInfo = await _mlsService.CreateGroupAsync(chatName, selectedRelays);
            var nostrGroupId = _mlsService.GetNostrGroupId(groupInfo.GroupId);
            var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

            // Extract admin pubkeys from 0xF2EE extension; fall back to creator
            var adminPubkeys = _mlsService.GetAdminPubkeys(groupInfo.GroupId);
            if (adminPubkeys == null || adminPubkeys.Count == 0)
                adminPubkeys = new List<string> { currentUser.PublicKeyHex.ToLowerInvariant() };

            var chat = new Chat
            {
                Id = groupIdHex,
                Name = chatName,
                Description = string.IsNullOrWhiteSpace(NewChatDescription) ? null : NewChatDescription.Trim(),
                Type = ChatType.Group,
                ParticipantPublicKeys = new List<string>(groupInfo.MemberPublicKeys),
                MlsGroupId = groupInfo.GroupId,
                NostrGroupId = nostrGroupId,
                MlsEpoch = groupInfo.Epoch,
                AdminPublicKeys = adminPubkeys,
                CreatorPublicKey = currentUser.PublicKeyHex,
                RelayUrls = selectedRelays.ToList(),
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };
            _logger.LogInformation("Created MLS group {GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);

            var invited = new List<string>();
            var inviteErrors = new List<string>();

            for (int i = 0; i < participantKeys.Count; i++)
            {
                var publicKeyHex = participantKeys[i];
                CreateProgress = participantKeys.Count > 1
                    ? $"Inviting {i + 1}/{participantKeys.Count}..."
                    : "Inviting...";

                try
                {
                    var keyPackages = await _nostrService.FetchKeyPackagesAsync(publicKeyHex);
                    var keyPackage = keyPackages
                        .OrderByDescending(k => k.CreatedAt)
                        .FirstOrDefault(k => k.IsCipherSuiteSupported)
                        ?? keyPackages.FirstOrDefault();

                    if (keyPackage == null)
                    {
                        inviteErrors.Add($"{publicKeyHex[..Math.Min(12, publicKeyHex.Length)]}... - No KeyPackage");
                        continue;
                    }
                    if (!keyPackage.IsCipherSuiteSupported)
                    {
                        inviteErrors.Add($"{publicKeyHex[..Math.Min(12, publicKeyHex.Length)]}... - Unsupported cipher suite 0x{keyPackage.CiphersuiteId:x4}");
                        continue;
                    }

                    var welcome = await _mlsService.AddMemberAsync(chat.MlsGroupId, keyPackage);

                    // MIP-02: epoch 0 (initial creation) sends Welcome only — existing-group invites publish Commit first.
                    if (groupInfo.Epoch + (ulong)i > 0 && welcome.CommitData != null && welcome.CommitData.Length > 0)
                    {
                        try
                        {
                            var commitEventJson = await _mlsService.EncryptCommitAsync(chat.MlsGroupId, welcome.CommitData);
                            await _nostrService.PublishRawEventJsonAsync(commitEventJson);
                        }
                        catch (NotSupportedException)
                        {
                            await _nostrService.PublishCommitAsync(
                                welcome.CommitData, groupIdHex, currentUser.PrivateKeyHex);
                        }
                    }

                    var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                        welcome.WelcomeData, publicKeyHex, currentUser.PrivateKeyHex, welcome.KeyPackageEventId);
                    _logger.LogInformation("Published Welcome {EventId} for {PubKey}",
                        welcomeEventId[..Math.Min(16, welcomeEventId.Length)],
                        publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);

                    if (!chat.ParticipantPublicKeys.Any(p => string.Equals(p, publicKeyHex, StringComparison.OrdinalIgnoreCase)))
                        chat.ParticipantPublicKeys.Add(publicKeyHex);
                    invited.Add(publicKeyHex[..Math.Min(12, publicKeyHex.Length)] + "...");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to invite {Key}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
                    inviteErrors.Add($"{publicKeyHex[..Math.Min(12, publicKeyHex.Length)]}... - {ex.Message}");
                }
            }

            CreateProgress = "Saving chat...";
            await _storageService.SaveChatAsync(chat);

            var subIdHex = nostrGroupId != null
                ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
                : groupIdHex;
            await _nostrService.SubscribeToGroupMessagesAsync(new[] { subIdHex });

            var chatItem = new ChatItemViewModel(chat);
            Chats.Insert(0, chatItem);
            SelectedChat = chatItem;

            CreateProgress = null;

            if (inviteErrors.Count > 0 && invited.Count == 0)
            {
                NewChatError = $"Chat created but failed to invite participants:\n{string.Join("\n", inviteErrors)}";
            }
            else if (inviteErrors.Count > 0)
            {
                NewChatError = $"Invited {invited.Count}, failed {inviteErrors.Count}:\n{string.Join("\n", inviteErrors)}";
            }
            else
            {
                ShowNewChatDialog = false;
                NewChatError = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create new chat");
            CreateProgress = null;
            NewChatError = $"Failed to create chat: {ex.Message}";
        }
    }

    private async Task JoinGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(JoinGroupId))
        {
            JoinGroupError = "Please enter a group ID or invite link";
            _logger.LogWarning("JoinGroup: empty group ID");
            return;
        }

        var groupId = JoinGroupId.Trim();
        _logger.LogInformation("Joining group: {GroupId}", groupId[..Math.Min(16, groupId.Length)] + "...");

        try
        {
            // Check if we already have this group (case-insensitive comparison)
            var existingChat = Chats.FirstOrDefault(c =>
                c.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

            if (existingChat != null)
            {
                // Select the existing group instead of showing error
                SelectedChat = existingChat;
                ShowJoinGroupDialog = false;
                JoinGroupError = null;
                _logger.LogInformation("Already a member of group: {GroupId}, selecting it", groupId);
                return;
            }

            // Validate group ID format (should be a GUID for our app)
            if (!Guid.TryParse(groupId, out _))
            {
                _logger.LogWarning("Non-GUID group ID provided: {GroupId}", groupId);
                // Still allow it, but log warning - might be from another client
            }

            // Create a placeholder group entry
            // Note: In a full implementation, this would:
            // 1. Subscribe to Nostr events for this group ID
            // 2. Process MLS welcome message from relay
            // 3. Fetch group metadata from relays
            var chat = new Chat
            {
                Id = groupId,
                Name = $"Group {groupId[..Math.Min(8, groupId.Length)]}...",
                Type = ChatType.Group,
                MlsGroupId = Convert.FromHexString(groupId),
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };

            await _storageService.SaveChatAsync(chat);
            _logger.LogInformation("Joined group: {GroupId}", chat.Id);

            // Add to list and select it
            var chatItem = new ChatItemViewModel(chat);
            Chats.Insert(0, chatItem);
            SelectedChat = chatItem;

            // Close dialog
            ShowJoinGroupDialog = false;
            JoinGroupError = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join group");
            JoinGroupError = $"Failed to join group: {ex.Message}";
        }
    }

    private async Task LookupBotNip65Async()
    {
        if (string.IsNullOrWhiteSpace(BotNpub) || _nostrService == null)
        {
            Nip65Status = "Enter a valid npub first.";
            return;
        }

        var input = BotNpub.Trim();
        string hex;
        if (input.StartsWith("npub1"))
        {
            hex = _nostrService.NpubToHex(input) ?? string.Empty;
            if (string.IsNullOrEmpty(hex))
            {
                Nip65Status = "Invalid npub format.";
                return;
            }
        }
        else if (input.Length == 64 && System.Text.RegularExpressions.Regex.IsMatch(input, "^[0-9a-fA-F]+$"))
        {
            hex = input.ToLowerInvariant();
        }
        else
        {
            Nip65Status = "Enter a valid npub or hex key.";
            return;
        }

        IsFetchingNip65 = true;
        Nip65Status = null;
        BotNip65Relays.Clear();

        try
        {
            var relays = await _nostrService.FetchRelayListAsync(hex);
            if (relays.Count == 0)
            {
                Nip65Status = "No NIP-65 relays found for this user.";
                return;
            }

            foreach (var relay in relays)
                BotNip65Relays.Add(new RelayCheckItem(relay.Url, true));

            Nip65Status = $"Found {relays.Count} relay(s).";
            _logger.LogInformation("NIP-65 lookup found {Count} relays for {PubKey}",
                relays.Count, hex[..Math.Min(16, hex.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NIP-65 lookup failed for {PubKey}", hex[..Math.Min(16, hex.Length)]);
            Nip65Status = $"Lookup failed: {ex.Message}";
        }
        finally
        {
            IsFetchingNip65 = false;
        }
    }

    private async Task CreateBotChatAsync()
    {
        if (string.IsNullOrWhiteSpace(BotNpub))
        {
            AddBotError = "Please enter a valid npub";
            return;
        }

        try
        {
            var input = BotNpub.Trim();
            string hex;

            if (input.StartsWith("npub1") && _nostrService != null)
            {
                hex = _nostrService.NpubToHex(input) ?? string.Empty;
                if (string.IsNullOrEmpty(hex))
                {
                    AddBotError = "Invalid npub format";
                    return;
                }
            }
            else if (input.Length == 64 && System.Text.RegularExpressions.Regex.IsMatch(input, "^[0-9a-fA-F]+$"))
            {
                hex = input.ToLowerInvariant();
            }
            else
            {
                AddBotError = "Enter an npub (npub1...) or 64-char hex public key";
                return;
            }

            // Collect relay URLs based on selected mode
            var relayUrls = new List<string>();
            if (BotRelayModeNip65)
            {
                relayUrls.AddRange(BotNip65Relays.Where(r => r.IsChecked).Select(r => r.Url));
                if (relayUrls.Count == 0)
                {
                    AddBotError = "No NIP-65 relays found. Click 'Look up relays' first.";
                    return;
                }
            }
            else if (BotRelayModeList)
            {
                relayUrls.AddRange(BotAvailableRelays.Where(r => r.IsChecked).Select(r => r.Url));
                if (relayUrls.Count == 0)
                {
                    AddBotError = "Select at least one relay.";
                    return;
                }
            }
            else if (BotRelayModeManual)
            {
                var manual = BotManualRelay.Trim();
                if (string.IsNullOrEmpty(manual))
                {
                    AddBotError = "Enter a relay domain (e.g. relay.example.com)";
                    return;
                }
                relayUrls.Add(SettingsViewModel.NormalizeRelayUrl(manual));
            }

            // Try to fetch profile for name resolution
            try
            {
                await _messageService.GetCachedOrFetchProfileAsync(hex);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch profile, continuing with hex name");
            }

            var chat = await _messageService.GetOrCreateBotChatAsync(hex, relayUrls);

            // Connect to bot-specific relays for receiving replies
            if (relayUrls.Count > 0 && _nostrService != null)
            {
                await _nostrService.ConnectBotRelaysAsync(relayUrls);
            }

            ShowAddBotDialog = false;
            AddBotError = null;

            // Add to list if not already present and select it
            var existingItem = Chats.FirstOrDefault(c => c.Id == chat.Id);
            if (existingItem != null)
            {
                SelectedChat = existingItem;
            }
            else
            {
                var chatItem = new ChatItemViewModel(chat);
                Chats.Insert(0, chatItem);
                SelectedChat = chatItem;
            }

            _logger.LogInformation("Linked device chat {ChatId} for {Pub}",
                chat.Id[..Math.Min(8, chat.Id.Length)], hex[..Math.Min(16, hex.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create bot chat");
            AddBotError = $"Failed: {ex.Message}";
        }
    }

    public async Task RenameChatAsync(ChatItemViewModel item, string newName)
    {
        try
        {
            item.Chat.Name = newName;
            await _storageService.SaveChatAsync(item.Chat);
            item.Name = newName;
            _logger.LogInformation("Renamed chat {ChatId} to '{Name}'", item.Id, newName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename chat {ChatId}", item.Id);
        }
    }

    private async Task DeleteChatAsync()
    {
        if (ChatToDelete == null)
        {
            return;
        }

        var chatId = ChatToDelete.Id;
        var chatName = ChatToDelete.Name;
        _logger.LogInformation("Deleting chat: {ChatId} - {ChatName}", chatId, chatName);

        try
        {
            // Delete from storage
            await _storageService.DeleteChatAsync(chatId);

            // Remove from whichever in-memory collection it's in. Bot/Agent chats
            // live in AgentChats, groups/DMs in Chats, archived in ArchivedChats —
            // previously this only checked Chats, so deleting a bot chat left a
            // stale row in AgentChats until the next full reload.
            var chatToRemove = Chats.FirstOrDefault(c => c.Id == chatId);
            if (chatToRemove != null) Chats.Remove(chatToRemove);

            var agentToRemove = AgentChats.FirstOrDefault(c => c.Id == chatId);
            if (agentToRemove != null) AgentChats.Remove(agentToRemove);
            AgentChatsCount = AgentChats.Count;

            var archivedToRemove = ArchivedChats.FirstOrDefault(c => c.Id == chatId);
            if (archivedToRemove != null) ArchivedChats.Remove(archivedToRemove);
            ArchivedChatsCount = ArchivedChats.Count;

            if (SelectedChat?.Id == chatId)
            {
                SelectedChat = null;
            }

            _logger.LogInformation("Chat deleted successfully: {ChatId}", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete chat: {ChatId}", chatId);
        }
        finally
        {
            ShowDeleteChatDialog = false;
            ChatToDelete = null;
        }
    }

    private async Task ResetGroupAsync()
    {
        if (GroupToReset == null) return;

        var chatId = GroupToReset.Id;
        var chatName = GroupToReset.Name;
        _logger.LogInformation("Resetting group: {ChatId} - {ChatName}", chatId, chatName);

        try
        {
            await _messageService.ResetGroupAsync(chatId);

            // Remove from UI
            var chatToRemove = Chats.FirstOrDefault(c => c.Id == chatId);
            if (chatToRemove != null)
            {
                Chats.Remove(chatToRemove);
                if (SelectedChat?.Id == chatId)
                    SelectedChat = null;
            }

            StatusMessage = $"Group \"{chatName}\" reset. Rescanning for invites...";
            _logger.LogInformation("Group reset successful, rescanning for invites");

            // Trigger rescan to find the original welcome
            await RescanInvitesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset group: {ChatId}", chatId);
            StatusMessage = $"Failed to reset group: {ex.Message}";
        }
        finally
        {
            ShowResetGroupDialog = false;
            GroupToReset = null;
        }
    }

    private void OnChatUpdated(Chat chat)
    {
        var existingInChats = Chats.FirstOrDefault(c => c.Id == chat.Id);
        var existingInAgents = AgentChats.FirstOrDefault(c => c.Id == chat.Id);
        var existingInArchived = ArchivedChats.FirstOrDefault(c => c.Id == chat.Id);

        var isParticipant = _currentUserPubKeyHex == null ||
            chat.ParticipantPublicKeys.Any(p => string.Equals(p, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase));

        var isAgent = chat.Type == Core.Models.ChatType.Bot;
        var targetCollection = isAgent ? AgentChats : Chats;
        var existingInTarget = isAgent ? existingInAgents : existingInChats;

        if (chat.IsArchived)
        {
            // Move to archived if it was in main or agents list
            if (existingInChats != null) Chats.Remove(existingInChats);
            if (existingInAgents != null) { AgentChats.Remove(existingInAgents); AgentChatsCount = AgentChats.Count; }

            if (existingInArchived != null)
            {
                existingInArchived.Update(chat);
            }
            else
            {
                ArchivedChats.Add(new ChatItemViewModel(chat));
            }
            ArchivedChatsCount = ArchivedChats.Count;
        }
        else
        {
            // Move from archived if needed
            if (existingInArchived != null)
            {
                ArchivedChats.Remove(existingInArchived);
                ArchivedChatsCount = ArchivedChats.Count;
            }
            // Remove from wrong collection if chat type changed
            if (!isAgent && existingInAgents != null) { AgentChats.Remove(existingInAgents); AgentChatsCount = AgentChats.Count; }
            if (isAgent && existingInChats != null) Chats.Remove(existingInChats);

            if (existingInTarget != null)
            {
                existingInTarget.Update(chat);
                existingInTarget.NeedsRepair = !isParticipant;
                // Re-sort if needed
                var index = targetCollection.IndexOf(existingInTarget);
                var newIndex = GetInsertIndex(chat, targetCollection);
                if (index != newIndex && newIndex < targetCollection.Count)
                {
                    targetCollection.Move(index, newIndex > index ? newIndex - 1 : newIndex);
                }
            }
            else
            {
                var newItem = new ChatItemViewModel(chat);
                newItem.NeedsRepair = !isParticipant;
                var insertIndex = GetInsertIndex(chat, targetCollection);
                targetCollection.Insert(insertIndex, newItem);
                if (isAgent) AgentChatsCount = AgentChats.Count;
            }
        }
    }

    private int GetInsertIndex(Chat chat, ObservableCollection<ChatItemViewModel>? collection = null)
    {
        var target = collection ?? Chats;
        if (chat.IsPinned)
        {
            return target.TakeWhile(c => c.IsPinned && c.LastActivityAt > chat.LastActivityAt).Count();
        }

        var pinnedCount = target.Count(c => c.IsPinned);
        return pinnedCount + target.Skip(pinnedCount).TakeWhile(c => c.LastActivityAt > chat.LastActivityAt).Count();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var chat in Chats)
            {
                chat.IsVisible = true;
            }
        }
        else
        {
            var filter = SearchText.ToLowerInvariant();
            foreach (var chat in Chats)
            {
                chat.IsVisible = chat.Name.ToLowerInvariant().Contains(filter) ||
                                 (chat.LastMessagePreview?.ToLowerInvariant().Contains(filter) ?? false);
            }
        }
    }

    private void OnNewInvite(PendingInvite invite)
    {
        // Deduplicate by NostrEventId (same welcome event arrives from multiple relays)
        if (!string.IsNullOrEmpty(invite.NostrEventId) &&
            PendingInvites.Any(i => i.NostrEventId == invite.NostrEventId))
            return;

        // Fallback: also check by internal Id
        if (PendingInvites.Any(i => i.Id == invite.Id))
            return;

        PendingInvites.Insert(0, new PendingInviteItemViewModel(invite));
        PendingInviteCount = PendingInvites.Count;
    }

    private async Task AcceptInviteAsync(PendingInviteItemViewModel inviteVm)
    {
        _logger.LogInformation("Accepting invite: {InviteId}", inviteVm.Id);

        inviteVm.IsAccepting = true;
        StatusMessage = "Accepting invite...";

        try
        {
            var chat = await _messageService.AcceptInviteAsync(inviteVm.Id);

            // Subscribe to group messages for the newly accepted group
            // Use NostrGroupId for relay subscriptions when available
            if (chat.MlsGroupId != null && chat.MlsGroupId.Length > 0 && _nostrService != null)
            {
                var subGroupId = chat.NostrGroupId != null && chat.NostrGroupId.Length > 0
                    ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
                    : Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                // Subscribe with a since parameter to catch commits published between
                // the Welcome creation and now (MIP-03 epoch sync)
                var since = new DateTimeOffset(chat.CreatedAt, TimeSpan.Zero).AddMinutes(-5);
                await _nostrService.SubscribeToGroupMessagesAsync(new[] { subGroupId }, since);
            }

            inviteVm.IsAccepting = false;
            PendingInvites.Remove(inviteVm);
            PendingInviteCount = PendingInvites.Count;
            StatusMessage = null;

            // Select the new chat
            var chatItem = Chats.FirstOrDefault(c => c.Id == chat.Id);
            if (chatItem != null)
            {
                SelectedChat = chatItem;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept invite: {InviteId}", inviteVm.Id);
            StatusMessage = $"Failed to accept invite: {ex.Message}";
            inviteVm.IsAccepting = false;
        }
    }

    private async Task DeclineInviteAsync(PendingInviteItemViewModel inviteVm)
    {
        _logger.LogInformation("Declining invite: {InviteId}", inviteVm.Id);

        inviteVm.IsDeclining = true;
        StatusMessage = "Declining invite...";

        try
        {
            await _messageService.DeclineInviteAsync(inviteVm.Id);

            PendingInvites.Remove(inviteVm);
            PendingInviteCount = PendingInvites.Count;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decline invite: {InviteId}", inviteVm.Id);
            StatusMessage = $"Failed to decline invite: {ex.Message}";
            inviteVm.IsDeclining = false;
        }
    }

    private async Task DismissSkippedInviteNoticeAsync()
    {
        _logger.LogInformation("Dismissing skipped invite notice (count was {Count})", SkippedInviteCount);
        await _storageService.ResetSkippedInviteCountAsync();
        SkippedInviteCount = 0;
    }

    private async Task RescanInvitesAsync()
    {
        _logger.LogInformation("Rescanning for invites...");

        IsRescanningInvites = true;
        StatusMessage = "Scanning relays for invites...";

        try
        {
            // Re-subscribe to kind 444 and wait for relay responses
            await _messageService.RescanInvitesAsync();

            // Reload invites from storage (includes any newly arrived ones)
            var invites = await _messageService.GetPendingInvitesAsync();
            PendingInvites.Clear();
            foreach (var invite in invites)
            {
                PendingInvites.Add(new PendingInviteItemViewModel(invite));
            }
            PendingInviteCount = PendingInvites.Count;

            StatusMessage = PendingInviteCount > 0
                ? $"Found {PendingInviteCount} invite(s)"
                : "No invites found";

            _logger.LogInformation("Rescan complete: {Count} invite(s)", PendingInviteCount);

            // Clear status after a delay
            await Task.Delay(3000);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rescan invites");
            StatusMessage = $"Rescan failed: {ex.Message}";
        }
        finally
        {
            IsRescanningInvites = false;
        }
    }
}

public partial class ChatItemViewModel : ViewModelBase
{
    public string Id { get; }

    [Reactive] public partial string Name { get; set; } = string.Empty;
    [Reactive] public partial string? AvatarUrl { get; set; }
    [Reactive] public partial string? LocalAvatarPath { get; set; }
    [Reactive] public partial string? LastMessagePreview { get; set; }
    [Reactive] public partial DateTime LastActivityAt { get; set; }
    [Reactive] public partial int UnreadCount { get; set; }
    public bool HasUnread => UnreadCount > 0;
    [Reactive] public partial bool IsPinned { get; set; }
    [Reactive] public partial bool IsMuted { get; set; }
    [Reactive] public partial bool IsGroup { get; set; }
    [Reactive] public partial bool IsBot { get; set; }
    [Reactive] public partial bool IsVisible { get; set; } = true;
    [Reactive] public partial bool NeedsRepair { get; set; }

    public Chat Chat { get; private set; }

    public ChatItemViewModel(Chat chat)
    {
        Chat = chat;
        Id = chat.Id;
        Update(chat);
    }

    public void Update(Chat chat)
    {
        Chat = chat;
        Name = chat.Name;
        AvatarUrl = chat.AvatarUrl;
        LastMessagePreview = chat.LastMessage?.Content;
        LastActivityAt = chat.LastActivityAt.ToLocalTime();
        UnreadCount = chat.UnreadCount;
        this.RaisePropertyChanged(nameof(HasUnread));
        IsPinned = chat.IsPinned;
        IsMuted = chat.IsMuted;
        IsGroup = chat.Type == ChatType.Group &&
            (chat.ParticipantPublicKeys.Count > 2 || !string.IsNullOrEmpty(chat.Description));
        IsBot = chat.Type == ChatType.Bot;
    }
}

public partial class FollowContactViewModel : ViewModelBase
{
    public string PublicKeyHex { get; }
    public string Npub { get; }
    [Reactive] public partial string? DisplayName { get; set; }
    [Reactive] public partial string? Petname { get; set; }
    [Reactive] public partial string? LocalAvatarPath { get; set; }
    [Reactive] public partial bool IsVisible { get; set; } = true;

    /// <summary>Per-participant key package lookup result (e.g. "KeyPackage found" or "No KeyPackage").</summary>
    [Reactive] public partial string? KeyPackageStatusText { get; set; }

    /// <summary>Whether this participant's key package lookup is currently in progress.</summary>
    [Reactive] public partial bool IsLookingUpKey { get; set; }

    public string ShownName => !string.IsNullOrWhiteSpace(Petname) ? Petname!
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
        : $"{Npub[..12]}…";

    public string Initial =>
        !string.IsNullOrWhiteSpace(ShownName) && char.IsLetterOrDigit(ShownName[0])
            ? ShownName[..1].ToUpper()
            : "?";

    public FollowContactViewModel(string publicKeyHex, string npub, string? petname, string? displayName, string? localAvatarPath)
    {
        PublicKeyHex = publicKeyHex;
        Npub = npub;
        Petname = petname;
        DisplayName = displayName;
        LocalAvatarPath = localAvatarPath;

        this.WhenAnyValue(x => x.DisplayName, x => x.Petname)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(ShownName));
                this.RaisePropertyChanged(nameof(Initial));
            });
    }
}

public partial class RelayCheckItem : ViewModelBase
{
    public string Url { get; }
    [Reactive] public partial bool IsChecked { get; set; }

    public RelayCheckItem(string url, bool isChecked = false)
    {
        Url = url;
        IsChecked = isChecked;
    }
}

public partial class PendingInviteItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string NostrEventId { get; }
    public string SenderName { get; }
    public string? GroupId { get; }
    public DateTime ReceivedAt { get; }
    public string SenderPublicKey { get; }
    public string SenderInitial { get; }
    public string TimeAgo { get; }

    [Reactive] public partial bool IsAccepting { get; set; }
    [Reactive] public partial bool IsDeclining { get; set; }

    private ObservableAsPropertyHelper<bool>? _isProcessing;
    public bool IsProcessing => _isProcessing?.Value ?? false;

    public PendingInviteItemViewModel(PendingInvite invite)
    {
        _isProcessing = this.WhenAnyValue(x => x.IsAccepting, x => x.IsDeclining, (a, d) => a || d)
            .ToProperty(this, x => x.IsProcessing);

        Id = invite.Id;
        NostrEventId = invite.NostrEventId;
        SenderPublicKey = invite.SenderPublicKey;
        GroupId = invite.GroupId;
        ReceivedAt = invite.ReceivedAt;

        SenderName = !string.IsNullOrEmpty(invite.SenderDisplayName)
            ? invite.SenderDisplayName
            : "Anonymous";

        SenderInitial = !string.IsNullOrEmpty(invite.SenderDisplayName)
            ? invite.SenderDisplayName[..1].ToUpperInvariant()
            : "?";

        var elapsed = DateTime.UtcNow - invite.ReceivedAt;
        TimeAgo = elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
            : $"{(int)elapsed.TotalDays}d ago";
    }
}

public partial class KeyPackageItemViewModel : ViewModelBase
{
    /// <summary>The underlying KeyPackage model.</summary>
    public KeyPackage KeyPackage { get; }

    /// <summary>Relative time display, e.g. "2h ago".</summary>
    public string CreatedAtDisplay { get; }

    /// <summary>Ciphersuite as hex string, e.g. "0x0001".</summary>
    public string CiphersuiteDisplay { get; }

    /// <summary>Human-readable cipher suite name.</summary>
    public string CiphersuiteName { get; }

    /// <summary>Whether this KeyPackage uses a supported cipher suite.</summary>
    public bool IsSupported { get; }

    /// <summary>First relay URL where this KeyPackage was found.</summary>
    public string RelaySource { get; }

    /// <summary>Truncated event ID for reference.</summary>
    public string KeyPackageRef { get; }

    /// <summary>Compact summary for display, e.g. "2h ago via relay.example.com".</summary>
    public string Summary { get; }

    public KeyPackageItemViewModel(KeyPackage keyPackage)
    {
        KeyPackage = keyPackage;

        var elapsed = DateTime.UtcNow - keyPackage.CreatedAt;
        CreatedAtDisplay = elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
            : $"{(int)elapsed.TotalDays}d ago";

        CiphersuiteDisplay = $"0x{keyPackage.CiphersuiteId:x4}";
        CiphersuiteName = keyPackage.CipherSuiteName;
        IsSupported = keyPackage.IsCipherSuiteSupported;

        RelaySource = keyPackage.RelayUrls.Count > 0
            ? keyPackage.RelayUrls[0]
            : "Unknown";

        KeyPackageRef = !string.IsNullOrEmpty(keyPackage.NostrEventId) && keyPackage.NostrEventId.Length > 12
            ? keyPackage.NostrEventId[..12] + "..."
            : keyPackage.NostrEventId ?? "N/A";

        var relayShort = RelaySource.Replace("wss://", "").Replace("ws://", "").TrimEnd('/');
        Summary = $"{CreatedAtDisplay} via {relayShort}";
    }
}

public partial class RelaySelectionItemViewModel : ViewModelBase
{
    public string Url { get; }
    public string DisplayUrl => Url.Replace("wss://", "").Replace("ws://", "").TrimEnd('/');
    [Reactive] public partial bool IsSelected { get; set; }

    public RelaySelectionItemViewModel(string url, bool isSelected = true)
    {
        Url = url;
        IsSelected = isSelected;
    }
}
