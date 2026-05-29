using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Scramble.Presentation.ViewModels;

namespace Scramble.UI.Views;

public partial class ChatView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private ListBox? _listBox;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // When the ChatView transitions from hidden to visible (e.g., switching back
        // from Settings), the ListBox's VirtualizingStackPanel may have lost its viewport
        // metrics. Force a re-measure so items are materialized correctly.
        var isVisibleDesc = IsVisibleProperty.Changed;
        isVisibleDesc.AddClassHandler<ChatView>((view, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                // Clear cached ScrollViewer — visual tree may have been rebuilt
                view._scrollViewer = null;

                // Schedule InvalidateMeasure after the current layout pass so the
                // ScrollViewer viewport is correctly established before the
                // VirtualizingStackPanel tries to materialize items.
                Dispatcher.UIThread.Post(() =>
                {
                    var lb = view._listBox ?? view.FindControl<ListBox>("MessageListBox");
                    view._listBox = lb;
                    lb?.InvalidateMeasure();
                }, DispatcherPriority.Render);
            }
        });
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ChatViewModel vm)
        {
            vm.ScrollToBottomRequested += OnScrollToBottomRequested;
        }
    }

    private void OnScrollToBottomRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollViewer == null)
            {
                _listBox ??= this.FindControl<ListBox>("MessageListBox");
                _scrollViewer = _listBox?.GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault();
            }
            _scrollViewer?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
