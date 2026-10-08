using Avalonia.Controls;
using Avalonia.Input;
using ServerController.ViewModels;

namespace ServerController.Views;

public partial class CrowdSecView : UserControl
{
    public CrowdSecView() => InitializeComponent();

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is CrowdSecViewModel vm) vm.QueryCommand.Execute(null);
    }
}
