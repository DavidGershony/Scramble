using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Scramble.Presentation.ViewModels;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using ReactiveUI;

namespace Scramble.Android.Adapters;

public class MessageAdapter : RecyclerView.Adapter
{
    private const int ViewTypeSent = 0;
    private const int ViewTypeReceived = 1;

    private List<MessageViewModel> _items = new();

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<MessageViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    /// <summary>
    /// The message at <paramref name="position"/>, or null if out of range.
    /// </summary>
    /// <remarks>
    /// For the swipe-to-reply gesture: ItemTouchHelper reports a position, and the list
    /// can have changed between the swipe starting and this being read.
    /// </remarks>
    public MessageViewModel? ItemAt(int position)
        => position >= 0 && position < _items.Count ? _items[position] : null;

    public override int GetItemViewType(int position)
    {
        return _items[position].IsFromCurrentUser ? ViewTypeSent : ViewTypeReceived;
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var layoutId = viewType == ViewTypeSent
            ? Resource.Layout.item_message_sent
            : Resource.Layout.item_message_received;

        var view = LayoutInflater.From(parent.Context)!
            .Inflate(layoutId, parent, false)!;

        return viewType == ViewTypeSent
            ? new SentMessageViewHolder(view)
            : new ReceivedMessageViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = _items[position];

        if (holder is SentMessageViewHolder sent)
        {
            sent.Bind(item);
        }
        else if (holder is ReceivedMessageViewHolder received)
        {
            received.Bind(item);
        }
    }

    private static void BindMediaViews(View itemView, MessageViewModel item, CompositeDisposable disposables)
    {
        var mediaStatus = itemView.FindViewById<TextView>(Resource.Id.media_status)!;
        var loadButton = itemView.FindViewById<MaterialButton>(Resource.Id.load_media_button)!;
        var mediaImage = itemView.FindViewById<ImageView>(Resource.Id.media_image)!;
        var content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
        var audioPlayer = itemView.FindViewById<LinearLayout>(Resource.Id.audio_player)!;
        var audioPlayButton = itemView.FindViewById<ImageButton>(Resource.Id.audio_play_button)!;
        var audioProgress = itemView.FindViewById<SeekBar>(Resource.Id.audio_progress)!;
        var audioDuration = itemView.FindViewById<TextView>(Resource.Id.audio_duration)!;

        if (item.IsTextMessage)
        {
            // Text message — hide all media views
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
            content.Visibility = ViewStates.Visible;
            content.Text = item.Content;
            return;
        }

        // Media message (image or audio) — hide text content
        content.Visibility = ViewStates.Gone;

        if (item.ShowMediaDisabled)
        {
            mediaStatus.Text = "Media loading disabled\nEnable in Settings > Privacy";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (item.IsAudio && item.IsMediaLoaded)
        {
            // Audio loaded — show player
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Visible;

            audioDuration.Text = item.AudioDurationText ?? "0:00";

            audioPlayButton.SetOnClickListener(new ActionClickListener(() =>
            {
                item.ToggleAudioCommand.Execute().Subscribe();
            }));

            // Observe playing state
            item.WhenAnyValue(x => x.IsPlayingAudio)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(playing =>
                {
                    audioPlayButton.SetImageResource(playing
                        ? global::Android.Resource.Drawable.IcMediaPause
                        : global::Android.Resource.Drawable.IcMediaPlay);
                })
                .DisposeWith(disposables);

            // Observe progress
            item.WhenAnyValue(x => x.AudioProgress)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(progress =>
                {
                    audioProgress.Progress = (int)(progress * 100);
                })
                .DisposeWith(disposables);

            // Observe duration text updates
            item.WhenAnyValue(x => x.AudioDurationText)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(dur =>
                {
                    audioDuration.Text = dur ?? "0:00";
                })
                .DisposeWith(disposables);
        }
        else if (item.IsImage && item.IsMediaLoaded && item.DecryptedMediaBytes != null)
        {
            // Image loaded — show bitmap
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Visible;
            audioPlayer.Visibility = ViewStates.Gone;
            var bitmap = BitmapFactory.DecodeByteArray(
                item.DecryptedMediaBytes, 0, item.DecryptedMediaBytes.Length);
            mediaImage.SetImageBitmap(bitmap);
        }
        else if (item.IsFile && item.IsMediaLoaded)
        {
            // File downloaded
            mediaStatus.Text = $"{item.ImageDisplayText}\nDownloaded";
            mediaStatus.SetTextColor(new global::Android.Graphics.Color(ContextCompat.GetColor(itemView.Context!, Resource.Color.status_success)));
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (item.IsLoadingMedia)
        {
            mediaStatus.Text = item.MediaSizeDisplay != null
                ? $"Loading... {item.MediaSizeDisplay}"
                : "Loading...";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (!string.IsNullOrEmpty(item.MediaError))
        {
            mediaStatus.Text = item.MediaError;
            mediaStatus.SetTextColor(new global::Android.Graphics.Color(ContextCompat.GetColor(itemView.Context!, Resource.Color.status_error)));
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (item.ShowTapToLoad)
        {
            var buttonText = item.IsAudio
                ? (item.ImageDisplayText ?? "Load voice message")
                : item.IsFile
                    ? $"Download {item.ImageDisplayText ?? "file"}"
                    : (item.ImageDisplayText ?? "Load image");
            if (item.IsUnknownServer)
                buttonText += $"\nUnknown server: {item.ServerHostname}";
            loadButton.Text = buttonText;
            loadButton.Visibility = ViewStates.Visible;
            mediaStatus.Text = "Your IP will be visible to the host";
            mediaStatus.Visibility = ViewStates.Visible;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;

            loadButton.SetOnClickListener(new ActionClickListener(() =>
            {
                item.LoadMediaCommand.Execute().Subscribe();
            }));
        }
        else
        {
            // Fallback
            mediaStatus.Text = item.ImageDisplayText ?? "[Encrypted media]";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
    }

    private static readonly string[] ReactionEmojis = { "\ud83d\udc4d", "\u2764\ufe0f", "\ud83d\ude02", "\ud83d\ude2e", "\ud83d\ude22", "\ud83d\udd25" };

    // Parallel to ReactionEmojis by index. Kept adjacent so the two cannot drift apart
    // silently -- a mismatch would send the wrong reaction rather than fail.
    private static readonly int[] ReactionItemIds =
    {
        Resource.Id.reaction_0, Resource.Id.reaction_1, Resource.Id.reaction_2,
        Resource.Id.reaction_3, Resource.Id.reaction_4, Resource.Id.reaction_5,
    };

    private static void BindReactions(View itemView, MessageViewModel item, CompositeDisposable disposables)
    {
        var reactionsDisplay = itemView.FindViewById<TextView>(Resource.Id.reactions_display);
        if (reactionsDisplay == null) return;

        item.WhenAnyValue(x => x.ReactionsDisplay)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(display =>
            {
                if (string.IsNullOrEmpty(display))
                {
                    reactionsDisplay.Visibility = ViewStates.Gone;
                }
                else
                {
                    reactionsDisplay.Text = display;
                    reactionsDisplay.Visibility = ViewStates.Visible;
                }
            })
            .DisposeWith(disposables);

        // Long-press to show reaction picker
        itemView.SetOnLongClickListener(new ActionLongClickListener(() =>
        {
            ShowReactionPicker(itemView, item);
        }));
    }

    /// <summary>
    /// Callback for when the user taps "Reply" on a message. Set by ChatFragment.
    /// </summary>
    public static Action<MessageViewModel>? OnReplyRequested { get; set; }

    /// <summary>
    /// Shows the horizontal reaction bar above a message.
    /// </summary>
    /// <remarks>
    /// Was a vertical PopupMenu listing Reply, Copy text and then the six emoji as
    /// separate rows. On a phone the emoji fell below the fold, so reacting needed a
    /// scroll inside the menu and the feature read as absent. A single row shows every
    /// option at once.
    /// </remarks>
    private static void ShowReactionPicker(View anchor, MessageViewModel item)
    {
        var context = anchor.Context;
        if (context == null) return;

        var bar = LayoutInflater.From(context)!.Inflate(Resource.Layout.view_reaction_bar, null)!;

        var popup = new PopupWindow(bar,
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            focusable: true)
        {
            // Without a background the window swallows nothing and an outside tap cannot
            // dismiss it, leaving the bar stranded over the conversation.
            OutsideTouchable = true,
            Elevation = 12f
        };
        popup.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(
            global::Android.Graphics.Color.Transparent));

        for (var i = 0; i < ReactionEmojis.Length; i++)
        {
            var emoji = ReactionEmojis[i];
            var id = ReactionItemIds[i];
            bar.FindViewById<TextView>(id)?.SetOnClickListener(new ActionClickListener(() =>
            {
                item.ReactCommand?.Execute(emoji).Subscribe();
                popup.Dismiss();
            }));
        }

        bar.FindViewById<TextView>(Resource.Id.reaction_reply)?
            .SetOnClickListener(new ActionClickListener(() =>
            {
                OnReplyRequested?.Invoke(item);
                popup.Dismiss();
            }));

        var copyView = bar.FindViewById<TextView>(Resource.Id.reaction_copy);
        if (copyView != null)
        {
            // A message with no text -- a bare image or voice note -- has nothing to copy.
            if (string.IsNullOrWhiteSpace(item.Content))
            {
                copyView.Visibility = ViewStates.Gone;
            }
            else
            {
                copyView.SetOnClickListener(new ActionClickListener(() =>
                {
                    CopyTextToClipboard(context, item.Content);
                    popup.Dismiss();
                }));
            }
        }

        // Measure so the bar can be placed ABOVE the message rather than over it.
        bar.Measure(View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
                    View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        var barHeight = bar.MeasuredHeight;

        // Android clamps a drop-down that would land off-screen, so a message at the very
        // top of the list gets the bar below it instead of half outside the window.
        popup.ShowAsDropDown(anchor, 0, -(anchor.Height + barHeight), GravityFlags.Start);
    }

    private static void CopyTextToClipboard(Context context, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var clipboard = context.GetSystemService(Context.ClipboardService) as ClipboardManager;
        if (clipboard == null)
            return;

        clipboard.PrimaryClip = ClipData.NewPlainText("message", text);
        Toast.MakeText(context, "Message copied", ToastLength.Short)?.Show();
    }

    private static void BindReplyQuote(View itemView, MessageViewModel item)
    {
        var replyQuote = itemView.FindViewById<LinearLayout>(Resource.Id.reply_quote);
        if (replyQuote == null) return;

        if (item.HasReplyTo)
        {
            replyQuote.Visibility = ViewStates.Visible;
            var replySender = itemView.FindViewById<TextView>(Resource.Id.reply_sender);
            var replyContent = itemView.FindViewById<TextView>(Resource.Id.reply_content);
            if (replySender != null) replySender.Text = item.ReplyToSenderName ?? "";
            if (replyContent != null) replyContent.Text = item.ReplyToContent ?? "";
        }
        else
        {
            replyQuote.Visibility = ViewStates.Gone;
        }
    }

    private class SentMessageViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _content;
        private readonly TextView _timestamp;
        private CompositeDisposable _disposables = new();

        public SentMessageViewHolder(View itemView) : base(itemView)
        {
            _content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.message_timestamp)!;
        }

        public void Bind(MessageViewModel item)
        {
            _disposables.Dispose();
            _disposables = new CompositeDisposable();

            BindReplyQuote(ItemView, item);
            BindMediaViews(ItemView, item, _disposables);
            BindReactions(ItemView, item, _disposables);
            _timestamp.Text = item.Timestamp.ToLocalTime().ToString("HH:mm");
        }
    }

    private class ReceivedMessageViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _senderName;
        private readonly TextView _content;
        private readonly TextView _timestamp;
        private CompositeDisposable _disposables = new();

        public ReceivedMessageViewHolder(View itemView) : base(itemView)
        {
            _senderName = itemView.FindViewById<TextView>(Resource.Id.sender_name)!;
            _content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.message_timestamp)!;
        }

        public void Bind(MessageViewModel item)
        {
            _disposables.Dispose();
            _disposables = new CompositeDisposable();

            _senderName.Text = item.SenderName;
            BindReplyQuote(ItemView, item);
            BindMediaViews(ItemView, item, _disposables);
            BindReactions(ItemView, item, _disposables);
            _timestamp.Text = item.Timestamp.ToLocalTime().ToString("HH:mm");
        }
    }

    private class ActionLongClickListener : Java.Lang.Object, View.IOnLongClickListener
    {
        private readonly Action _action;
        public ActionLongClickListener(Action action) => _action = action;
        public bool OnLongClick(View? v) { _action(); return true; }
    }

    private class ActionClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly Action _action;
        public ActionClickListener(Action action) => _action = action;
        public void OnClick(View? v) => _action();
    }
}
