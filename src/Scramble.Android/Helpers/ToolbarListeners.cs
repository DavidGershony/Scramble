using Android.Views;
using AndroidX.AppCompat.Widget;

namespace Scramble.Android;

/// <summary>
/// Simple View.IOnClickListener that invokes an Action.
/// Used for toolbar navigation click since C# events may not fire reliably on MaterialToolbar.
/// </summary>
public class ActionClickListener : Java.Lang.Object, View.IOnClickListener
{
    private readonly Action _action;

    public ActionClickListener(Action action)
    {
        _action = action;
    }

    public void OnClick(View? v)
    {
        _action();
    }
}

/// <summary>
/// Simple View.IOnLongClickListener that invokes an Action.
/// </summary>
/// <remarks>
/// Exists so adapters can use <c>SetOnLongClickListener</c> instead of <c>LongClick +=</c>.
/// The two are not equivalent inside <c>OnBindViewHolder</c>: setting replaces, adding
/// accumulates, and a recycled holder is bound many times.
/// </remarks>
public class ActionLongClickListener : Java.Lang.Object, View.IOnLongClickListener
{
    private readonly Action _action;

    public ActionLongClickListener(Action action)
    {
        _action = action;
    }

    public bool OnLongClick(View? v)
    {
        _action();
        return true;
    }
}

/// <summary>
/// Simple Toolbar.IOnMenuItemClickListener that invokes a Func.
/// Used for toolbar menu item clicks since C# events may not fire reliably on MaterialToolbar.
/// </summary>
public class ActionMenuItemClickListener : Java.Lang.Object, AndroidX.AppCompat.Widget.Toolbar.IOnMenuItemClickListener
{
    private readonly Func<IMenuItem?, bool> _handler;

    public ActionMenuItemClickListener(Func<IMenuItem?, bool> handler)
    {
        _handler = handler;
    }

    public bool OnMenuItemClick(IMenuItem? item)
    {
        return _handler(item);
    }
}

public class SearchExpandListener : Java.Lang.Object, IMenuItemOnActionExpandListener
{
    private readonly Action _onCollapse;

    public SearchExpandListener(Action onCollapse)
    {
        _onCollapse = onCollapse;
    }

    public bool OnMenuItemActionExpand(IMenuItem? item) => true;

    public bool OnMenuItemActionCollapse(IMenuItem? item)
    {
        _onCollapse();
        return true;
    }
}
