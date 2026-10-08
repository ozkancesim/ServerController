using Avalonia.Controls;
using Avalonia.Input;
using ServerController.ViewModels;

namespace ServerController.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerMovedEvent, (_, _) => (DataContext as MainViewModel)?.NotifyActivity(), handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => (DataContext as MainViewModel)?.NotifyActivity(), handledEventsToo: true);
        Opened += (_, _) => this.FindControl<TextBox>("UserBox")?.Focus();
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnLoginKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm) vm.Login.SubmitCommand.Execute(null);
    }
}
