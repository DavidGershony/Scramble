using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Scramble.Presentation.ViewModels;

namespace Scramble.Android.Adapters;

public class RelayAdapter : RecyclerView.Adapter
{
    private List<RelayViewModel> _items = new();

    public event EventHandler<RelayViewModel>? RemoveClick;

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<RelayViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_relay, parent, false)!;
        return new RelayViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is RelayViewHolder relayHolder)
        {
            var item = _items[position];
            relayHolder.Bind(item);
            // SetOnClickListener REPLACES the listener. `Click +=` ADDS one, and
            // OnBindViewHolder runs again every time a recycled holder is reused, so the
            // event form accumulated a handler per rebind and one tap fired the event
            // once per rebind.
            relayHolder.RemoveButton.SetOnClickListener(
                new ActionClickListener(() => RemoveClick?.Invoke(this, item)));
        }
    }

    private class RelayViewHolder : RecyclerView.ViewHolder
    {
        private readonly View _statusDot;
        private readonly TextView _url;
        public ImageButton RemoveButton { get; }

        public RelayViewHolder(View itemView) : base(itemView)
        {
            _statusDot = itemView.FindViewById<View>(Resource.Id.relay_status_dot)!;
            _url = itemView.FindViewById<TextView>(Resource.Id.relay_url)!;
            RemoveButton = itemView.FindViewById<ImageButton>(Resource.Id.relay_remove_button)!;
        }

        public void Bind(RelayViewModel item)
        {
            _url.Text = item.Url;
            var colorRes = item.IsConnected ? Resource.Color.status_success : Resource.Color.status_error;
            _statusDot.SetBackgroundColor(new global::Android.Graphics.Color(ContextCompat.GetColor(ItemView.Context!, colorRes)));
        }
    }
}
