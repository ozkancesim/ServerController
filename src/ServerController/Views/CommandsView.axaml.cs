using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ServerController.ViewModels;

namespace ServerController.Views;

public partial class CommandsView : UserControl
{
    public CommandsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CommandsViewModel vm)
                vm.Terminal.CollectionChanged += (_, _) => ScrollDown();
        };
    }

    private void ScrollDown() =>
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("TermScroll")?.ScrollToEnd(), DispatcherPriority.Background);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandsViewModel vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.RunTerminalCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.HistoryUp();
                e.Handled = true;
                break;
            case Key.Down:
                vm.HistoryDown();
                e.Handled = true;
                break;
        }
        if (sender is TextBox tb) tb.CaretIndex = tb.Text?.Length ?? 0;
        ScrollDown();
    }
}
