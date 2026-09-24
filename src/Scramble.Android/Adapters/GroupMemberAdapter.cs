using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Scramble.Presentation.ViewModels;

namespace Scramble.Android.Adapters;

public class GroupMemberAdapter : RecyclerView.Adapter
{
    private List<GroupMemberViewModel> _items = new();

    public event EventHandler<string>? CopyNpubClick;

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<GroupMemberViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_group_member, parent, false)!;
        return new MemberViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is MemberViewHolder memberHolder)
        {
            var member = _items[position];
            memberHolder.Bind(member);
            // SetOnClickListener REPLACES the listener. `Click +=` ADDS one, and
            // OnBindViewHolder runs again every time a recycled holder is reused, so the
            // event form accumulated a handler per rebind and one tap fired the event
            // once per rebind.
            memberHolder.CopyButton.SetOnClickListener(new ActionClickListener(() =>
            {
                var npubText = member.Npub ?? member.PublicKeyHex;
                CopyNpubClick?.Invoke(this, npubText);
            }));
        }
    }

    private class MemberViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _initial;
        private readonly ImageView _avatar;
        private readonly TextView _name;
        private readonly TextView _adminBadge;
        private readonly TextView _youBadge;
        private readonly TextView _npub;
        public readonly ImageButton CopyButton;

        public MemberViewHolder(View itemView) : base(itemView)
        {
            _initial = itemView.FindViewById<TextView>(Resource.Id.member_initial)!;
            _avatar = itemView.FindViewById<ImageView>(Resource.Id.member_avatar)!;
            _name = itemView.FindViewById<TextView>(Resource.Id.member_name)!;
            _adminBadge = itemView.FindViewById<TextView>(Resource.Id.member_admin_badge)!;
            _youBadge = itemView.FindViewById<TextView>(Resource.Id.member_you_badge)!;
            _npub = itemView.FindViewById<TextView>(Resource.Id.member_npub)!;
            CopyButton = itemView.FindViewById<ImageButton>(Resource.Id.member_copy_npub)!;
        }

        public void Bind(GroupMemberViewModel member)
        {
            _name.Text = member.DisplayName;
            _initial.Text = member.Initial;
            _initial.Visibility = string.IsNullOrEmpty(member.Picture) ? ViewStates.Visible : ViewStates.Gone;
            _avatar.Visibility = string.IsNullOrEmpty(member.Picture) ? ViewStates.Gone : ViewStates.Visible;
            _adminBadge.Visibility = member.IsAdmin ? ViewStates.Visible : ViewStates.Gone;
            _youBadge.Visibility = member.IsCurrentUser ? ViewStates.Visible : ViewStates.Gone;
            _npub.Text = member.Npub ?? $"{member.PublicKeyHex[..12]}...";
        }
    }
}
