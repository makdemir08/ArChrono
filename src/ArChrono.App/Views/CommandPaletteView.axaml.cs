using ArChrono.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ArChrono.App.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        Results.DoubleTapped += (_, _) => _ = (DataContext as CommandPaletteViewModel)?.Execute(null);
        Results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is CommandPaletteViewModel vm)
            {
                _ = vm.Execute(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            QueryBox.Focus();
            QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm) return;
        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                Results.ScrollIntoView(vm.Selected!);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                Results.ScrollIntoView(vm.Selected!);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = vm.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
