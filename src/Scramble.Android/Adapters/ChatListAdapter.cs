using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.ImageView;
using Scramble.Presentation.ViewModels;

namespace Scramble.Android.Adapters;

public class ChatListAdapter : RecyclerView.Adapter
{
    private List<ChatItemViewModel> _items = new();

    public event EventHandler<ChatItemViewModel>? ItemClick;
    public event EventHandler<ChatItemViewModel>? ItemLongClick;

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<ChatItemViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_chat, parent, false)!;
        return new ChatViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is ChatViewHolder chatHolder)
        {
            var item = _items[position];
            chatHolder.Bind(item);
            chatHolder.ItemView.Click += (s, e) => ItemClick?.Invoke(this, item);
            chatHolder.ItemView.LongClick += (s, e) => ItemLongClick?.Invoke(this, item);
        }
    }

    private class ChatViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _avatar;
        private readonly ShapeableImageView _avatarImage;
        private readonly TextView _name;
        private readonly TextView _lastMessage;
        private readonly TextView _timestamp;
        private readonly TextView _unreadBadge;

        public ChatViewHolder(View itemView) : base(itemView)
        {
            _avatar = itemView.FindViewById<TextView>(Resource.Id.chat_avatar)!;
            _avatarImage = itemView.FindViewById<ShapeableImageView>(Resource.Id.chat_avatar_image)!;
            _name = itemView.FindViewById<TextView>(Resource.Id.chat_name)!;
            _lastMessage = itemView.FindViewById<TextView>(Resource.Id.chat_last_message)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.chat_timestamp)!;
            _unreadBadge = itemView.FindViewById<TextView>(Resource.Id.chat_unread_badge)!;
        }

        public void Bind(ChatItemViewModel item)
        {
            _name.Text = item.Name;
            _lastMessage.Text = item.NeedsRepair
                ? "[Broken link — long-press to reset or delete]"
                : item.LastMessagePreview ?? "";
            _timestamp.Text = FormatRelativeTime(item.LastActivityAt);

            // Avatar: try cached image first, then fall back to text
            if (!string.IsNullOrEmpty(item.LocalAvatarPath) && System.IO.File.Exists(item.LocalAvatarPath))
            {
                var bitmap = BitmapFactory.DecodeFile(item.LocalAvatarPath);
                if (bitmap != null)
                {
                    _avatarImage.SetImageBitmap(bitmap);
                    _avatarImage.Visibility = ViewStates.Visible;
                    _avatar.Visibility = ViewStates.Gone;
                }
                else
                {
                    ShowTextAvatar(item);
                }
            }
            else
            {
                ShowTextAvatar(item);
            }

            // Unread badge
            if (item.UnreadCount > 0)
            {
                _unreadBadge.Text = item.UnreadCount.ToString();
                _unreadBadge.Visibility = ViewStates.Visible;
            }
            else
            {
                _unreadBadge.Visibility = ViewStates.Gone;
            }
        }

        private void ShowTextAvatar(ChatItemViewModel item)
        {
            _avatarImage.Visibility = ViewStates.Gone;
            _avatar.Visibility = ViewStates.Visible;

            if (item.IsBot)
            {
                _avatar.Text = "🤖";
                _avatar.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 30);
                _avatar.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            }
            else
            {
                var initial = string.IsNullOrEmpty(item.Name) ? "?" : item.Name[..1].ToUpper();
                _avatar.Text = initial;
                _avatar.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20);
                _avatar.SetBackgroundResource(Resource.Drawable.avatar_background);
            }
        }

        private static string FormatRelativeTime(DateTime dateTime)
        {
            if (dateTime == default) return "";
            var elapsed = DateTime.Now - dateTime;
            if (elapsed.TotalMinutes < 1) return "now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d";
            return dateTime.ToString("MMM d");
        }
    }
}
