using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.Snackbar;
using Google.Android.Material.Tabs;
using Google.Android.Material.TextField;
using Scramble.Android.Adapters;
using Scramble.Core.Configuration;
using Scramble.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace Scramble.Android.Fragments;

public class ChatListFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private readonly ShellViewModel _shellViewModel;
    private ChatListViewModel ViewModel => _mainViewModel.ChatListViewModel;
    private CompositeDisposable _disposables = new();
    private ChatListAdapter? _adapter;
    private PendingInviteAdapter? _inviteAdapter;

    public ChatListFragment(MainViewModel mainViewModel, ShellViewModel? shellViewModel = null)
    {
        _mainViewModel = mainViewModel;
        _shellViewModel = shellViewModel!;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_chat_list, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        var recyclerView = view.FindViewById<RecyclerView>(Resource.Id.chat_list_recycler)!;
        var loadingIndicator = view.FindViewById<ProgressBar>(Resource.Id.loading_indicator)!;
        var emptyState = view.FindViewById<LinearLayout>(Resource.Id.empty_state)!;
        var chatTabs = view.FindViewById<TabLayout>(Resource.Id.chat_tabs)!;

        // Set up tab toggle for Chats / Agents (DVM) / Archived
        var chatsTab = chatTabs.NewTab()!.SetText("Chats")!;
        var agentsTab = chatTabs.NewTab()!.SetText("Agents (DVM)")!;
        var archivedTab = chatTabs.NewTab()!.SetText("Archived (0)")!;
        chatTabs.AddTab(chatsTab);
        chatTabs.AddTab(agentsTab);
        chatTabs.AddTab(archivedTab);

        // After back-stack pop (e.g. returning from a chat), the fragment view is
        // recreated and TabLayout defaults to the first tab — but the ViewModel
        // still has the previously-selected section flag. Restore the matching
        // tab so the visible selection lines up with the list contents.
        if (ViewModel.ShowArchivedSection)
            chatTabs.SelectTab(archivedTab);
        else if (ViewModel.ShowAgentsSection)
            chatTabs.SelectTab(agentsTab);

        ViewModel.WhenAnyValue(x => x.ArchivedChatsCount)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(count => archivedTab.SetText($"Archived ({count})"))
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.AgentChatsCount)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(count => agentsTab.SetText($"Agents (DVM){(count > 0 ? $" ({count})" : "")}"))
            .DisposeWith(_disposables);

        chatTabs.TabSelected += (s, e) =>
        {
            ViewModel.ShowArchivedSection = e.Tab == archivedTab;
            ViewModel.ShowAgentsSection = e.Tab == agentsTab;
        };

        // Pending invites views
        var pendingInvitesSection = view.FindViewById<LinearLayout>(Resource.Id.pending_invites_section)!;
        var inviteBadge = view.FindViewById<TextView>(Resource.Id.invite_badge)!;
        var rescanButton = view.FindViewById<MaterialButton>(Resource.Id.rescan_invites_button)!;
        var invitesRecycler = view.FindViewById<RecyclerView>(Resource.Id.pending_invites_recycler)!;

        // Skipped invites notice views
        var skippedNotice = view.FindViewById<LinearLayout>(Resource.Id.skipped_invites_notice)!;
        var skippedText = view.FindViewById<TextView>(Resource.Id.skipped_invites_text)!;
        var skippedRetry = view.FindViewById<MaterialButton>(Resource.Id.skipped_invites_retry)!;
        var skippedDismiss = view.FindViewById<MaterialButton>(Resource.Id.skipped_invites_dismiss)!;

        // Profile avatar (left side of toolbar) — shows user image or default icon
        var profileAvatar = view.FindViewById<ImageView>(Resource.Id.toolbar_profile_avatar)!;
        var relayText = view.FindViewById<TextView>(Resource.Id.toolbar_relay_text)!;

        // Show profile avatar for all logged-in users (including external signer)
        _mainViewModel.WhenAnyValue(x => x.CurrentUser)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(user =>
            {
                profileAvatar.Visibility = user != null ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        // Load profile picture into avatar or show default person icon
        _mainViewModel.WhenAnyValue(x => x.MyPictureUrl)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(url =>
            {
                if (!string.IsNullOrEmpty(url) && Activity != null)
                {
                    // Load the image from URL
                    _ = LoadProfileImageAsync(profileAvatar, url);
                }
                else
                {
                    profileAvatar.SetImageResource(Resource.Drawable.ic_account_circle);
                }
            })
            .DisposeWith(_disposables);

        // Tap profile avatar to open account switcher
        profileAvatar.Click += (s, e) => ShowAccountSwitcher();

        // Relay count text next to avatar
        relayText.Text = _mainViewModel.RelayCountText;
        _mainViewModel.WhenAnyValue(x => x.RelayCountText)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(text => relayText.Text = text)
            .DisposeWith(_disposables);

        // Tint the toolbar action icons
        var newChatItem = toolbar.Menu?.FindItem(Resource.Id.action_new_chat);
        if (newChatItem?.Icon != null)
        {
            var tinted = AndroidX.Core.Graphics.Drawable.DrawableCompat.Wrap(newChatItem.Icon).Mutate();
            var tv = new global::Android.Util.TypedValue();
            if (Context?.Theme?.ResolveAttribute(Resource.Attribute.colorOnSurface, tv, true) == true)
            {
                AndroidX.Core.Graphics.Drawable.DrawableCompat.SetTint(tinted, tv.Data);
            }
            newChatItem.SetIcon(tinted);
        }

        // Set up toolbar menu (use Java listener for reliable click handling)
        toolbar.SetOnMenuItemClickListener(new ActionMenuItemClickListener(item =>
        {
            if (item?.ItemId == Resource.Id.action_new_chat)
            {
                ShowNewOptionsBottomSheet();
                return true;
            }
            else if (item?.ItemId == Resource.Id.action_reconnect_all)
            {
                _mainViewModel.ReconnectCommand.Execute().Subscribe();
                return true;
            }
            else if (item?.ItemId == Resource.Id.action_settings)
            {
                (Activity as MainActivity)?.NavigateToSettings();
                return true;
            }
            return false;
        }));

        // Set up Chat RecyclerView
        _adapter = new ChatListAdapter();
        recyclerView.SetLayoutManager(new LinearLayoutManager(Context));
        recyclerView.SetAdapter(_adapter);

        _adapter.ItemClick += (s, chatItem) =>
        {
            ViewModel.SelectedChat = chatItem;
            (Activity as MainActivity)?.NavigateToChat();
        };

        _adapter.ItemLongClick += (s, chatItem) =>
        {
            ShowChatContextMenu(chatItem);
        };

        // Set up Pending Invites RecyclerView
        _inviteAdapter = new PendingInviteAdapter();
        invitesRecycler.SetLayoutManager(new LinearLayoutManager(Context));
        invitesRecycler.SetAdapter(_inviteAdapter);

        _inviteAdapter.AcceptClick += (s, invite) =>
        {
            ViewModel.AcceptInviteCommand.Execute(invite).Subscribe().DisposeWith(_disposables);
        };

        _inviteAdapter.DeclineClick += (s, invite) =>
        {
            ViewModel.DeclineInviteCommand.Execute(invite).Subscribe().DisposeWith(_disposables);
        };

        // Rescan button
        rescanButton.Click += (s, e) =>
        {
            ViewModel.RescanInvitesCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Search - hooked to the toolbar SearchView action item
        var searchItem = toolbar.Menu?.FindItem(Resource.Id.action_search);
        if (searchItem?.ActionView is AndroidX.AppCompat.Widget.SearchView searchView)
        {
            searchView.QueryHint = "Search chats...";
            searchView.QueryTextChange += (s, e) =>
            {
                ViewModel.SearchText = e.NewText ?? string.Empty;
                e.Handled = true;
            };
            searchItem.SetOnActionExpandListener(new SearchExpandListener(() =>
            {
                ViewModel.SearchText = string.Empty;
            }));
        }

        // Observe chats collection and switch based on active tab
        void RefreshChatList()
        {
            Activity?.RunOnUiThread(() =>
            {
                var items = ViewModel.ShowArchivedSection
                    ? ViewModel.ArchivedChats.ToList()
                    : ViewModel.ShowAgentsSection
                        ? ViewModel.AgentChats.ToList()
                        : ViewModel.Chats.ToList();
                _adapter.UpdateItems(items);
                emptyState.Visibility = items.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        }

        ViewModel.Chats.CollectionChanged += (s, e) => RefreshChatList();
        ViewModel.AgentChats.CollectionChanged += (s, e) => RefreshChatList();
        ViewModel.ArchivedChats.CollectionChanged += (s, e) => RefreshChatList();
        ViewModel.WhenAnyValue(x => x.ShowArchivedSection)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshChatList())
            .DisposeWith(_disposables);
        ViewModel.WhenAnyValue(x => x.ShowAgentsSection)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshChatList())
            .DisposeWith(_disposables);

        // Observe pending invites
        ViewModel.PendingInvites.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                _inviteAdapter.UpdateItems(ViewModel.PendingInvites.ToList());
            });
        };

        // Pending invite count → show/hide section and badge
        ViewModel.WhenAnyValue(x => x.PendingInviteCount)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(count =>
            {
                pendingInvitesSection.Visibility = count > 0 ? ViewStates.Visible : ViewStates.Gone;
                inviteBadge.Text = count.ToString();
            })
            .DisposeWith(_disposables);

        // Rescan loading state
        ViewModel.WhenAnyValue(x => x.IsRescanningInvites)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(scanning =>
            {
                rescanButton.Enabled = !scanning;
                rescanButton.Text = scanning ? "Scanning..." : "Scan";
            })
            .DisposeWith(_disposables);

        // Skipped invite notice → show/hide and update text
        ViewModel.WhenAnyValue(x => x.SkippedInviteCount)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(count =>
            {
                skippedNotice.Visibility = count > 0 ? ViewStates.Visible : ViewStates.Gone;
                skippedText.Text = count == 1
                    ? "1 group invite received — encryption key not available on this device"
                    : $"{count} group invites received — encryption keys not available on this device";
            })
            .DisposeWith(_disposables);

        // Retry, because Dismiss was the only way out of a notice about invites the app
        // had already dismissed for you. Once dismissed they are unreachable: both the
        // live welcome handler and the rescan skip them, and the only other escape
        // needs a chat that a pending invite has not got.
        skippedRetry.Click += (s, e) =>
            ViewModel.RetryDismissedInvitesCommand.Execute().Subscribe().DisposeWith(_disposables);

        skippedDismiss.Click += (s, e) =>
            ViewModel.DismissSkippedInviteNoticeCommand.Execute().Subscribe().DisposeWith(_disposables);

        // Status message → Snackbar
        ViewModel.WhenAnyValue(x => x.StatusMessage)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(msg => !string.IsNullOrEmpty(msg))
            .Subscribe(msg =>
            {
                Snackbar.Make(view, msg!, Snackbar.LengthLong).Show();
                ViewModel.StatusMessage = null;
            })
            .DisposeWith(_disposables);

        // Loading state
        ViewModel.WhenAnyValue(x => x.IsLoading)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(loading =>
            {
                loadingIndicator.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        // Observe dialog states for auto-dismiss
        ViewModel.WhenAnyValue(x => x.ShowDeleteChatDialog)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { /* dialog auto-dismissed by ViewModel */ })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ShowResetGroupDialog)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { /* dialog auto-dismissed by ViewModel */ })
            .DisposeWith(_disposables);

        // Initial load
        _adapter.UpdateItems(ViewModel.Chats.ToList());
        _inviteAdapter.UpdateItems(ViewModel.PendingInvites.ToList());
        emptyState.Visibility = ViewModel.Chats.Count == 0 && !ViewModel.IsLoading ? ViewStates.Visible : ViewStates.Gone;
        pendingInvitesSection.Visibility = ViewModel.PendingInviteCount > 0 ? ViewStates.Visible : ViewStates.Gone;
        inviteBadge.Text = ViewModel.PendingInviteCount.ToString();
        skippedNotice.Visibility = ViewModel.SkippedInviteCount > 0 ? ViewStates.Visible : ViewStates.Gone;
        skippedText.Text = ViewModel.SkippedInviteCount == 1
            ? "1 group invite received — encryption key not available on this device"
            : $"{ViewModel.SkippedInviteCount} group invites received — encryption keys not available on this device";

        // Auto-scan for invites on load
        ViewModel.RescanInvitesCommand.Execute().Subscribe().DisposeWith(_disposables);
    }

    private void ShowNewOptionsBottomSheet()
    {
        if (Context == null) return;

        var items = new[] { "New Chat", "Link Device" };
        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Create")!
            .SetItems(items, (s, e) =>
            {
                switch (e.Which)
                {
                    case 0: (Activity as MainActivity)?.NavigateToNewChat(); break;
                    case 1: (Activity as MainActivity)?.NavigateToAddBot(); break;
                }
            })!
            .Show();
    }

    private void ShowChatContextMenu(ChatItemViewModel chatItem)
    {
        if (Context == null) return;

        var isArchived = ViewModel.ShowArchivedSection;
        var options = new List<(string label, Action action)>();

        if (isArchived)
        {
            options.Add(("Unarchive", () =>
                ViewModel.UnarchiveChatCommand.Execute(chatItem).Subscribe().DisposeWith(_disposables)));
        }
        else
        {
            options.Add(("Archive", () =>
                ViewModel.ArchiveChatCommand.Execute(chatItem).Subscribe().DisposeWith(_disposables)));
        }

        options.Add(("Rename", () => ShowRenameDialog(chatItem)));

        options.Add((chatItem.IsMuted ? "Unmute" : "Mute", () =>
            ViewModel.ToggleMuteCommand.Execute(chatItem).Subscribe().DisposeWith(_disposables)));

        if (chatItem.IsGroup)
            options.Add(("Reset Group", () => ShowResetGroupConfirmation(chatItem)));

        options.Add(("Delete Chat", () => ShowDeleteConfirmation(chatItem)));

        new MaterialAlertDialogBuilder(Context)
            .SetTitle(chatItem.Name)!
            .SetItems(options.Select(o => o.label).ToArray(), (s, e) =>
            {
                options[e.Which].action();
            })!
            .Show();
    }

    private void ShowRenameDialog(ChatItemViewModel chatItem)
    {
        if (Context == null) return;

        var input = new EditText(Context) { Text = chatItem.Name };
        input.SetSelection(input.Text?.Length ?? 0);

        var container = new FrameLayout(Context);
        var padding = (int)(16 * Context.Resources!.DisplayMetrics!.Density);
        container.SetPadding(padding, padding / 2, padding, 0);
        container.AddView(input);

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Rename Chat")!
            .SetView(container)!
            .SetPositiveButton("Save", (s, e) =>
            {
                var newName = input.Text?.Trim();
                if (!string.IsNullOrEmpty(newName) && newName != chatItem.Name)
                {
                    _ = ViewModel.RenameChatAsync(chatItem, newName);
                }
            })!
            .SetNegativeButton("Cancel", (s, e) => { })!
            .Show();
    }

    private void ShowDeleteConfirmation(ChatItemViewModel chatItem)
    {
        if (Context == null) return;

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Delete Chat")!
            .SetMessage($"Are you sure you want to delete the chat with {chatItem.Name}?")!
            .SetPositiveButton("Delete", (s, e) =>
            {
                ViewModel.ConfirmDeleteChatCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .SetNegativeButton("Cancel", (s, e) =>
            {
                ViewModel.CancelDeleteChatCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Show();
    }

    private void ShowResetGroupConfirmation(ChatItemViewModel chatItem)
    {
        if (Context == null) return;

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Reset Group")!
            .SetMessage($"Are you sure you want to reset the group \"{chatItem.Name}\"? This will re-create the MLS group state.")!
            .SetPositiveButton("Reset", (s, e) =>
            {
                ViewModel.ConfirmResetGroupCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .SetNegativeButton("Cancel", (s, e) =>
            {
                ViewModel.CancelResetGroupCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Show();
    }

    private void ShowAccountSwitcher()
    {
        if (Context == null || _shellViewModel == null) return;

        var accounts = AccountRegistryService.GetAccounts();
        var items = new List<string>();

        // Build display items: account names
        foreach (var account in accounts)
        {
            var name = !string.IsNullOrEmpty(account.DisplayName)
                ? account.DisplayName
                : account.Npub?[..Math.Min(16, account.Npub.Length)] + "...";
            var isActive = string.Equals(account.PublicKeyHex,
                _shellViewModel.ActiveAccountEntry?.PublicKeyHex, StringComparison.OrdinalIgnoreCase);
            items.Add(isActive ? $"{name} (active)" : name);
        }

        // Add actions at the bottom
        items.Add("View Profile");
        items.Add("Add Account");

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Accounts")!
            .SetItems(items.ToArray(), (s, e) =>
            {
                var accountCount = accounts.Count;
                if (e.Which < accountCount)
                {
                    // Switch to selected account
                    var selected = accounts[e.Which];
                    _ = _shellViewModel.SwitchAccountAsync(selected.PublicKeyHex);
                }
                else if (e.Which == accountCount)
                {
                    // View Profile
                    ShowMyProfileDialog();
                }
                else if (e.Which == accountCount + 1)
                {
                    // Add Account
                    _ = _shellViewModel.AddAccountAsync();
                }
            })!
            .Show();
    }

    private void ShowMyProfileDialog()
    {
        if (Context == null) return;

        // Trigger profile metadata loading
        _mainViewModel.ShowMyProfileCommand.Execute().Subscribe().DisposeWith(_disposables);

        var dialogView = LayoutInflater.From(Context)!.Inflate(Resource.Layout.dialog_my_profile, null)!;

        var npubText = dialogView.FindViewById<TextView>(Resource.Id.my_npub_text)!;
        var copyNpubButton = dialogView.FindViewById<ImageButton>(Resource.Id.my_copy_npub_button)!;
        var nsecSection = dialogView.FindViewById<LinearLayout>(Resource.Id.my_nsec_section)!;
        var copyNsecButton = dialogView.FindViewById<ImageButton>(Resource.Id.my_copy_nsec_button)!;
        var displayNameText = dialogView.FindViewById<TextView>(Resource.Id.my_display_name_text)!;
        var usernameText = dialogView.FindViewById<TextView>(Resource.Id.my_username_text)!;
        var aboutText = dialogView.FindViewById<TextView>(Resource.Id.my_about_text)!;
        var loadingIndicator = dialogView.FindViewById<ProgressBar>(Resource.Id.my_profile_loading)!;
        var qrImage = dialogView.FindViewById<ImageView>(Resource.Id.my_npub_qr)!;

        // Hide nsec section for remote signer users (e.g. Amber)
        var isRemoteSigner = _mainViewModel.CurrentUser?.IsRemoteSigner == true;
        nsecSection.Visibility = isRemoteSigner ? ViewStates.Gone : ViewStates.Visible;

        var dialog = new MaterialAlertDialogBuilder(Context)
            .SetTitle("My Profile")!
            .SetView(dialogView)!
            .SetPositiveButton("Close", (s, e) =>
            {
                _mainViewModel.CloseMyProfileCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Create()!;

        dialog.Show();

        copyNsecButton.Click += (s, e) =>
        {
            CopyToClipboard("nsec", _mainViewModel.MyNsec);
        };

        copyNpubButton.Click += (s, e) =>
        {
            CopyToClipboard("npub", _mainViewModel.MyNpub);
        };

        // Bind QR code
        _mainViewModel.WhenAnyValue(x => x.MyNpubQrPngBytes)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(bytes =>
            {
                if (bytes != null && bytes.Length > 0)
                {
                    var bitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                    qrImage.SetImageBitmap(bitmap);
                    qrImage.Visibility = ViewStates.Visible;
                }
                else
                {
                    qrImage.Visibility = ViewStates.Gone;
                }
            })
            .DisposeWith(_disposables);

        // Bind profile properties
        _mainViewModel.WhenAnyValue(x => x.MyNpub)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(npub => npubText.Text = npub ?? "No key available")
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.MyDisplayName)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(name =>
            {
                displayNameText.Text = name ?? "";
                displayNameText.Visibility = string.IsNullOrEmpty(name) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.MyName)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(name =>
            {
                usernameText.Text = string.IsNullOrEmpty(name) ? "" : $"@{name}";
                usernameText.Visibility = string.IsNullOrEmpty(name) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.MyAbout)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(about =>
            {
                aboutText.Text = about ?? "";
                aboutText.Visibility = string.IsNullOrEmpty(about) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.IsLoadingProfile)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(loading =>
            {
                loadingIndicator.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.ShowMyProfileDialog)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { if (dialog.IsShowing) dialog.Dismiss(); })
            .DisposeWith(_disposables);
    }

    private void CopyToClipboard(string label, string? text)
    {
        if (string.IsNullOrEmpty(text) || Activity == null) return;

        var clipboard = (ClipboardManager?)Activity.GetSystemService(global::Android.Content.Context.ClipboardService);
        if (clipboard != null)
        {
            var clip = ClipData.NewPlainText(label, text);
            clipboard.PrimaryClip = clip;
            Toast.MakeText(Activity, "Copied!", ToastLength.Short)?.Show();
        }
    }

    private static readonly System.Net.Http.HttpClient _httpClient = new();

    private async Task LoadProfileImageAsync(ImageView imageView, string url)
    {
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url);
            if (bytes.Length > 0 && Activity != null)
            {
                Activity.RunOnUiThread(() =>
                {
                    var bitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                    if (bitmap != null)
                        imageView.SetImageBitmap(bitmap);
                });
            }
        }
        catch
        {
            // Failed to load — keep default icon
        }
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
