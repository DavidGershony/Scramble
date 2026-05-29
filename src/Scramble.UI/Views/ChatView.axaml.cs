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
