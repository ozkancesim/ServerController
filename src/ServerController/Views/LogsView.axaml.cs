using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ServerController.ViewModels;

namespace ServerController.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _vm;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as LogsViewModel;
            if (_vm != null) _vm.PropertyChanged += OnChanged;
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.Content))
            Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("LogScroll")?.ScrollToEnd(), DispatcherPriority.Background);
    }
}
