using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ServerController.ViewModels;

namespace ServerController.Views;

public partial class OutputDialogView : UserControl
{
    public OutputDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is OutputDialogViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputDialogViewModel.Text))
            Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("Scroller")?.ScrollToEnd(), DispatcherPriority.Background);
    }
}
