using ArChrono.App.ViewModels.Dialogs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ArChrono.App.Views.Dialogs;

/// <summary>Satırları sürükle-bırak ile ve Alt+↑/↓ ile yeniden sıralama.</summary>
public partial class InteractiveRebaseDialogView : UserControl
{
    private RebaseItemViewModel? _dragging;

    public InteractiveRebaseDialogView()
    {
        InitializeComponent();
        Items.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        Items.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        Items.AddHandler(PointerReleasedEvent, (_, _) => _dragging = null, RoutingStrategies.Tunnel);
        Items.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private InteractiveRebaseDialogViewModel? ViewModel => DataContext as InteractiveRebaseDialogViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<ComboBox>(true) is null && visual.FindAncestorOfType<TextBox>(true) is null)
            _dragging = (visual.FindAncestorOfType<ListBoxItem>(true)?.DataContext) as RebaseItemViewModel;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null || ViewModel is null || !e.GetCurrentPoint(Items).Properties.IsLeftButtonPressed) return;
        var target = Items.InputHitTest(e.GetPosition(Items)) is Visual hit ? hit.FindAncestorOfType<ListBoxItem>(true)?.DataContext as RebaseItemViewModel : null;
        if (target is null || ReferenceEquals(target, _dragging)) return;
        ViewModel.MoveItem(_dragging, ViewModel.Items.IndexOf(target));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null || !e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        if (e.Key == Key.Up) ViewModel.Move(-1);
        else if (e.Key == Key.Down) ViewModel.Move(1);
        else return;
        e.Handled = true;
    }
}
