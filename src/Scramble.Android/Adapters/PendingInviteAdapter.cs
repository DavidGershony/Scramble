using Android.Views;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Scramble.Presentation.ViewModels;

namespace Scramble.Android.Adapters;

public class PendingInviteAdapter : RecyclerView.Adapter
{
    private List<PendingInviteItemViewModel> _items = new();

    public event EventHandler<PendingInviteItemViewModel>? AcceptClick;
    public event EventHandler<PendingInviteItemViewModel>? DeclineClick;

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<PendingInviteItemViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_pending_invite, parent, false)!;
        return new InviteViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is InviteViewHolder inviteHolder)
        {
            var item = _items[position];
            inviteHolder.Bind(item);
            // SetOnClickListener REPLACES the listener. `Click +=` ADDS one, and
            // OnBindViewHolder runs again every time a recycled holder is reused, so the
            // event form accumulated a handler per rebind and one tap fired the event
            // once per rebind.
            //
            // Worst here: a single Accept tap invoked AcceptInviteAsync once per rebind.
            // The first call consumes and deletes the invite, so the rest throw
            // "Invite not found" -- and concurrent accepts also race the read-then-insert
            // guard that is supposed to keep one chat row per MLS group.
            inviteHolder.AcceptButton.SetOnClickListener(
                new ActionClickListener(() => AcceptClick?.Invoke(this, item)));
            inviteHolder.DeclineButton.SetOnClickListener(
                new ActionClickListener(() => DeclineClick?.Invoke(this, item)));
        }
    }

    private class InviteViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _avatar;
        private readonly TextView _senderName;
        private readonly TextView _groupId;
        private readonly TextView _time;
        private readonly ProgressBar _progress;
        public MaterialButton AcceptButton { get; }
        public MaterialButton DeclineButton { get; }

        public InviteViewHolder(View itemView) : base(itemView)
        {
            _avatar = itemView.FindViewById<TextView>(Resource.Id.invite_avatar)!;
            _senderName = itemView.FindViewById<TextView>(Resource.Id.invite_sender_name)!;
            _groupId = itemView.FindViewById<TextView>(Resource.Id.invite_group_id)!;
            _time = itemView.FindViewById<TextView>(Resource.Id.invite_time)!;
            _progress = itemView.FindViewById<ProgressBar>(Resource.Id.invite_progress)!;
            AcceptButton = itemView.FindViewById<MaterialButton>(Resource.Id.invite_accept_button)!;
            DeclineButton = itemView.FindViewById<MaterialButton>(Resource.Id.invite_decline_button)!;
        }

        public void Bind(PendingInviteItemViewModel item)
        {
            _avatar.Text = item.SenderInitial;
            _senderName.Text = item.SenderName;
            _groupId.Text = item.GroupId ?? "Unknown group";
            _time.Text = item.TimeAgo;

            var isProcessing = item.IsProcessing;
            AcceptButton.Visibility = isProcessing ? ViewStates.Gone : ViewStates.Visible;
            DeclineButton.Visibility = isProcessing ? ViewStates.Gone : ViewStates.Visible;
            _progress.Visibility = isProcessing ? ViewStates.Visible : ViewStates.Gone;
        }
    }
}
