using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Scramble.Core.Models;
using Scramble.Core.Services;

namespace Scramble.Android.Adapters;

/// <summary>
/// Sectioned RecyclerView adapter for the share target activity.
/// Shows "Devices" header + bot chats, then "Chats" header + DM/group chats.
/// </summary>
public class ShareChatAdapter : RecyclerView.Adapter
{
    private const int TypeHeader = 0;
    private const int TypeChat = 1;

    private readonly List<object> _items = new();

    public event EventHandler<Chat>? ChatClick;

    public override int ItemCount => _items.Count;

    public void Update(ShareChatResult result)
    {
        _items.Clear();

        if (result.DeviceChats.Count > 0)
        {
            _items.Add("Devices");
            _items.AddRange(result.DeviceChats);
        }

        if (result.RegularChats.Count > 0)
        {
            _items.Add("Chats");
            _items.AddRange(result.RegularChats);
        }

        NotifyDataSetChanged();
    }

    public override int GetItemViewType(int position)
        => _items[position] is string ? TypeHeader : TypeChat;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var inflater = LayoutInflater.From(parent.Context)!;
        if (viewType == TypeHeader)
        {
            var view = inflater.Inflate(Resource.Layout.item_share_section_header, parent, false)!;
            return new HeaderViewHolder(view);
        }
        else
        {
            var view = inflater.Inflate(Resource.Layout.item_chat, parent, false)!;
            return new ChatViewHolder(view);
        }
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = _items[position];

        if (holder is HeaderViewHolder headerHolder && item is string title)
        {
            headerHolder.Bind(title);
        }
        else if (holder is ChatViewHolder chatHolder && item is Chat chat)
        {
            chatHolder.Bind(chat);
            // SetOnClickListener REPLACES the listener. `Click +=` ADDS one, and
            // OnBindViewHolder runs again every time a recycled holder is reused, so the
            // event form accumulated a handler per rebind and one tap fired the event
            // once per rebind.
            chatHolder.ItemView.SetOnClickListener(
                new ActionClickListener(() => ChatClick?.Invoke(this, chat)));
        }
    }

    private class HeaderViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _text;

        public HeaderViewHolder(View itemView) : base(itemView)
        {
            _text = itemView.FindViewById<TextView>(Resource.Id.section_header_text)!;
        }

        public void Bind(string title) => _text.Text = title;
    }

    private class ChatViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _avatar;
        private readonly TextView _name;
        private readonly TextView _lastMessage;
        private readonly TextView _timestamp;
        private readonly View _unreadBadge;

        public ChatViewHolder(View itemView) : base(itemView)
        {
            _avatar = itemView.FindViewById<TextView>(Resource.Id.chat_avatar)!;
            _name = itemView.FindViewById<TextView>(Resource.Id.chat_name)!;
            _lastMessage = itemView.FindViewById<TextView>(Resource.Id.chat_last_message)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.chat_timestamp)!;
            _unreadBadge = itemView.FindViewById<TextView>(Resource.Id.chat_unread_badge)!;

            // Hide elements not relevant for share picker
            _unreadBadge.Visibility = ViewStates.Gone;

            // Hide the ShapeableImageView (avatar image) — we only use text avatars here
            var avatarImage = itemView.FindViewById(Resource.Id.chat_avatar_image);
            if (avatarImage != null) avatarImage.Visibility = ViewStates.Gone;
        }

        public void Bind(Chat chat)
        {
            _name.Text = chat.Name;
            _lastMessage.Text = chat.LastMessage?.Content ?? "";
            _timestamp.Text = FormatRelativeTime(chat.LastActivityAt);

            if (chat.Type == ChatType.Bot)
            {
                _avatar.Text = "\U0001F916"; // robot emoji
                _avatar.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 30);
                _avatar.SetBackgroundColor(Color.Transparent);
            }
            else
            {
                var initial = string.IsNullOrEmpty(chat.Name) ? "?" : chat.Name[..1].ToUpper();
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
